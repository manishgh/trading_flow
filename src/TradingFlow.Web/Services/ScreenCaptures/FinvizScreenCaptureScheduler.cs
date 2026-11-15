using TradingFlow.Application.Preparation;
using TradingFlow.Contracts.Preparation;
using TradingFlow.Data.ScreenCaptures;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.ScreenCaptures;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Engine.Storage;
using TradingFlow.Finviz;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Services.ScreenCaptures;

public sealed record FinvizScreenCaptureOptions(bool Requested, bool MarketFeedsEnabled, bool CredentialAvailable, bool FixtureHost)
{
    public bool Enabled => Requested && MarketFeedsEnabled && CredentialAvailable && !FixtureHost;

    public string DisabledReason => FixtureHost ? "Scheduled capture is disabled in UI fixtures."
        : !Requested ? "Scheduled Finviz capture is disabled."
        : !MarketFeedsEnabled ? "Scheduled capture requires explicitly enabled market feeds."
        : "The Finviz export credential is missing.";
}

/// <summary>Schedules a local observation over the existing exchange calendar and read-only saved screens.</summary>
public sealed class FinvizScreenCaptureScheduler(
    FinvizScreenCaptureOptions options,
    IMarketStreamLeaseRepository leases,
    IPreparationCalendar calendar,
    ScreenerPresetService presets,
    SqliteFinvizCaptureCatalog catalog,
    FinvizClient client,
    RawArchiveOptions archive,
    TimeProvider clock,
    ILogger<FinvizScreenCaptureScheduler> logger)
{
    private static readonly TimeSpan OwnershipDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumRequestDuration = TimeSpan.FromSeconds(30);
    private readonly string ownerId = Guid.NewGuid().ToString("N");
    private readonly HashSet<(DateOnly Date, FinvizSlotKind Kind)> closedRecentSlots = [];

    // Give a single host ownership for this pass. Active attempts are serialized and the lease is renewed before each request.
    public async Task RunOnceAsync(CancellationToken token)
    {
        if (!options.Enabled)
            return;
        var lease = await leases.TryAcquireAsync(SqliteFinvizCaptureCatalog.LeaseResource, ownerId, OwnershipDuration, token);
        if (lease is null)
            return;

        try
        {
            await RunOwnedPassAsync(lease, token);
        }
        catch (FinvizCaptureOwnershipLostException)
        {
            logger.LogInformation("Scheduled Finviz capture ownership ended before the pass completed.");
        }
        finally
        {
            using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
            await leases.TryReleaseAsync(lease.ResourceKey, lease.OwnerId, lease.FencingToken, releaseTimeout.Token);
        }
    }

    // Always serve today's actual session first, then reconcile up to seven previously unclosed calendar dates.
    private async Task RunOwnedPassAsync(MarketStreamLease lease, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, PreparationCalendarAdapter.Exchange).DateTime);
        var schedule = await catalog.OpenScheduleAsync(lease, today, token);
        closedRecentSlots.RemoveWhere(slot => slot.Date != today);
        var current = await TryLoadCalendarAsync(today, today.AddDays(7), token);
        if (current is null)
        {
            await catalog.UpdateScheduleAsync(await RenewAsync(lease, token), now, null,
                "The collector could not read the authoritative exchange calendar.", null, token);
            return;
        }

        var upcoming = current.Sessions.SelectMany(PlansFor).Where(slot => slot.PlannedAtUtc > now)
            .Select(slot => (DateTimeOffset?)slot.PlannedAtUtc).Min();
        await catalog.UpdateScheduleAsync(await RenewAsync(lease, token), current.RetrievedAtUtc, upcoming, null, null, token);
        var saved = await presets.ListAsync(null, token);
        var session = current.Sessions.Single(session => session.Date == today);
        var todayComplete = schedule.NextUnclosedSessionDate > today
            || await ProcessSessionAsync(lease, session, saved, today, token);
        var olderComplete = await ReconcileOlderSessionsAsync(lease, schedule.NextUnclosedSessionDate, today, saved, current, upcoming, token);
        if (todayComplete && olderComplete && schedule.NextUnclosedSessionDate <= today)
            await catalog.UpdateScheduleAsync(await RenewAsync(lease, token), current.RetrievedAtUtc, upcoming, null, today, token);
    }

    // Reconcile bounded prior dates as missed evidence, without requesting any historical Finviz screen.
    private async Task<bool> ReconcileOlderSessionsAsync(MarketStreamLease lease, DateOnly next, DateOnly today,
        IReadOnlyList<ScreenerPreset> saved, PreparationCalendar current, DateTimeOffset? upcoming, CancellationToken token)
    {
        if (next >= today)
            return true;
        var end = next.AddDays(Math.Min(6, today.DayNumber - next.DayNumber - 1));
        var older = await TryLoadCalendarAsync(next, end, token);
        if (older is null)
        {
            await catalog.UpdateScheduleAsync(await RenewAsync(lease, token), current.RetrievedAtUtc, upcoming,
                $"The exchange calendar for unresolved slots from {next:yyyy-MM-dd} is unavailable.", null, token);
            return false;
        }

        foreach (var session in older.Sessions.OrderBy(session => session.Date))
        {
            if (!await ProcessSessionAsync(lease, session, saved, today, token))
                return false;
            await catalog.UpdateScheduleAsync(await RenewAsync(lease, token), current.RetrievedAtUtc, upcoming, null, session.Date, token);
        }
        return end == today.AddDays(-1);
    }

    // Stage only due slots and clear historical work only once their window and receipt recovery have completed.
    private async Task<bool> ProcessSessionAsync(MarketStreamLease lease, PreparationSession session,
        IReadOnlyList<ScreenerPreset> saved, DateOnly today, CancellationToken token)
    {
        var allClosed = true;
        foreach (var plan in PlansFor(session))
        {
            var key = (session.Date, plan.Kind);
            if (closedRecentSlots.Contains(key))
                continue;
            if (clock.GetUtcNow() < plan.PlannedAtUtc)
            {
                allClosed = false;
                continue;
            }
            var finished = await ProcessSlotAsync(lease, plan, saved, token);
            allClosed &= finished;
            if (finished && session.Date == today)
                closedRecentSlots.Add(key);
        }
        return allClosed;
    }

    // Recover a committed receipt before another request. Outside the window this path only records settled evidence.
    private async Task<bool> ProcessSlotAsync(MarketStreamLease lease, FinvizPlannedSlot plan,
        IReadOnlyList<ScreenerPreset> saved, CancellationToken token)
    {
        await catalog.StageAsync(await RenewAsync(lease, token), plan, saved, token);
        foreach (var request in await catalog.GetUnfinishedAsync(plan, token))
        {
            var recovered = await client.RecoverScreenerExportAsync(request, archive, token);
            var outcome = recovered ?? new FinvizExportObservation(FinvizCaptureState.Failed,
                "The earlier attempt ended without a matching committed raw receipt.");
            await catalog.FinishAsync(await RenewAsync(lease, token), request, outcome, token);
        }

        while (clock.GetUtcNow() < plan.DeadlineUtc)
        {
            var request = await catalog.TryBeginAsync(await RenewAsync(lease, token), plan,
                FinvizClient.ScheduledExportFormat, FinvizClient.CanonicalizeScheduledSelection, token);
            if (request is null)
                return false;
            await CaptureAttemptAsync(lease, request, token);
        }

        await catalog.CloseExpiredAsync(await RenewAsync(lease, token), plan, token);
        return true;
    }

    // Give the actual HTTP operation at most the remaining slot window and never more than thirty seconds.
    private async Task CaptureAttemptAsync(MarketStreamLease lease, FinvizExportRequest request, CancellationToken token)
    {
        var remaining = request.DeadlineUtc - clock.GetUtcNow();
        var duration = remaining < MaximumRequestDuration ? remaining : MaximumRequestDuration;
        FinvizExportObservation outcome;
        if (duration <= TimeSpan.Zero)
        {
            outcome = new FinvizExportObservation(FinvizCaptureState.Failed,
                "The capture window closed before the provider request started.");
        }
        else
        {
            using var timeout = new CancellationTokenSource(duration, clock);
            using var requestToken = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, token);
            try
            {
                outcome = await client.CaptureScreenerExportAsync(request, clock, requestToken.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
            {
                outcome = await client.RecoverScreenerExportAsync(request, archive, token)
                    ?? new FinvizExportObservation(FinvizCaptureState.Failed, "Finviz did not deliver an archived response within the request time limit.");
            }
        }
        await catalog.FinishAsync(await RenewAsync(lease, token), request, outcome, token);
    }

    // A failed provider calendar read leaves the dates unresolved and stops requests for those dates.
    private async Task<PreparationCalendar?> TryLoadCalendarAsync(DateOnly start, DateOnly end, CancellationToken token)
    {
        try
        {
            var result = await calendar.LoadAsync(start, end, token);
            var expected = end.DayNumber - start.DayNumber + 1;
            var dates = result.Sessions.Select(session => session.Date).ToHashSet();
            if (result.Sessions.Length != expected || Enumerable.Range(0, expected).Any(offset => !dates.Contains(start.AddDays(offset))))
                throw new InvalidDataException("The exchange calendar did not cover every requested session date.");
            return result;
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Scheduled Finviz capture could not load the exchange calendar from {Start} to {End}.", start, end);
            return null;
        }
    }

    // Refresh the shared lease and stop before further calls if the host has lost its fencing token.
    private async Task<MarketStreamLease> RenewAsync(MarketStreamLease lease, CancellationToken token) =>
        await leases.TryRenewAsync(lease.ResourceKey, lease.OwnerId, lease.FencingToken, OwnershipDuration, token)
            ?? throw new FinvizCaptureOwnershipLostException();

    // The exchange adapter has already applied the New York date, actual open/close and DST for this session.
    public static IReadOnlyList<FinvizPlannedSlot> PlansFor(PreparationSession session) =>
        !session.TradingDay ? [] :
        [
            new(session.Date, FinvizSlotKind.BeforeOpen, session.OpenUtc.AddMinutes(-30)),
            new(session.Date, FinvizSlotKind.AfterClose, session.CloseUtc.AddMinutes(15))
        ];
}

/// <summary>Runs the observational scheduler independently of the order and screener job workers.</summary>
public sealed class FinvizScreenCaptureHostedService(
    FinvizScreenCaptureScheduler scheduler, TimeProvider clock, ILogger<FinvizScreenCaptureHostedService> logger) : BackgroundService
{
    // Poll until host shutdown; an interrupted pass leaves the durable attempt available to the next owner.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        do
        {
            try
            {
                await scheduler.RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Scheduled Finviz capture could not finish the collector pass.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
