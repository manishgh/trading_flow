using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Discovery;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Web.Services.Charts;

/// <summary>Owns the single read-only invocation, its fixed deadline and independent scope cleanup.</summary>
public sealed class ChartAcceptanceCoordinator(ChartAcceptanceOptions options, IChartAcceptanceAdmission admission,
    IDiscoverySubscriptionSink subscriptions, AlpacaMarketStateStreamService stream, IRawArchiveWriter archive,
    TimeProvider clock, IHostApplicationLifetime host, ILogger<ChartAcceptanceCoordinator> logger) : IHostedService
{
    private static readonly TimeSpan ScopeCleanupBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ResultWriteBudget = TimeSpan.FromSeconds(15);
    private static readonly ChartAcceptanceStatus Idle = new(null, null, "idle", null, null, false, false, null);
    private readonly SemaphoreSlim startGate = new(1, 1);
    private ChartAcceptanceSession? session;
    private Task run = Task.CompletedTask;

    // Activation uses the administrator endpoint; the hosted hook only supplies a shutdown drain.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Give bounded finalization time during host shutdown instead of abandoning queued evidence.
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Read(ref session)?.Fail("The host stopped during the observation.");
        await Volatile.Read(ref run).WaitAsync(cancellationToken);
    }

    // Serialize preflight and accept at most one invocation during this host enablement.
    public async Task<(ChartAcceptanceStart? Start, string? Denial)> StartAsync(string owner, CancellationToken requestToken)
    {
        if (!options.Enabled || !options.RuntimeEnabled)
            return (null, "The chart check requires an enabled paper host.");
        if (!await startGate.WaitAsync(0, requestToken))
            return (null, "Another chart check is starting.");
        try
        {
            if (Volatile.Read(ref session) is not null)
                return (null, "This host enablement already used its one chart check.");
            using var preflight = CancellationTokenSource.CreateLinkedTokenSource(requestToken, host.ApplicationStopping);
            preflight.CancelAfter(TimeSpan.FromSeconds(20));
            var check = await admission.VerifyAsync(preflight.Token);
            var acceptedAt = clock.GetUtcNow();
            if (check.Denial is not null) return (null, check.Denial);
            if (check.RegularCloseUtc is not { } close || close - acceptedAt < ChartAcceptanceSession.Lifetime)
                return (null, "The verified regular close is less than five minutes away.");

            var candidate = new ChartAcceptanceSession(owner, acceptedAt, clock, archive);
            if (!await stream.ReserveChartDiagnosticAsync(candidate, preflight.Token))
                return (null, "The leased SIP stream cannot provide an exclusive AAPL chart check right now.");

            // After reservation, client abandonment cannot release the one-invocation bound.
            Volatile.Write(ref session, candidate);
            Volatile.Write(ref run, RunAsync(candidate));
            return (candidate.StartResponse, null);
        }
        catch (Exception exception) when (!requestToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Chart acceptance preflight could not verify the paper host.");
            return (null, "The paper account or live market-data prerequisites could not be verified.");
        }
        finally
        {
            startGate.Release();
        }
    }

    // Read only the requesting administrator's own invocation; never return the one-use correlation again.
    public ChartAcceptanceStatus GetStatus(string owner) =>
        Volatile.Read(ref session) is { } current && current.Owner == owner ? current.Status : Idle;

    // Stop is idempotent and never acts on another administrator's invocation.
    public ChartAcceptanceStatus Stop(string owner)
    {
        if (Volatile.Read(ref session) is not { } current || current.Owner != owner) return Idle;
        current.End("stopped", "The initiating administrator stopped the observation.");
        return current.Status;
    }

    // Only the explicit chart watch can consume the capability and establish its exact epoch.
    internal ChartAcceptanceSession? AttachWatch(string user, ChartSeriesKey key, string correlation,
        string connection, string epoch) =>
        Volatile.Read(ref session) is { } current && current.TryAttachWatch(user, key, correlation, connection, epoch)
            ? current : null;

    // Race an independent hard expiry and host shutdown against scope/capture faults.
    private async Task RunAsync(ChartAcceptanceSession current)
    {
        var remaining = current.DeadlineUtc - clock.GetUtcNow();
        using var expired = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, clock);
        using var expiryRegistration = expired.Token.Register(() =>
            current.End("expired", "The five-minute observation deadline elapsed."));
        using var shutdownRegistration = host.ApplicationStopping.Register(() =>
            current.Fail("The host stopped during the observation."));
        using var writing = new CancellationTokenSource(ChartAcceptanceSession.Lifetime + ResultWriteBudget, clock);
        var writer = current.Archive.DrainAsync(writing.Token);
        try
        {
            await subscriptions.ReplaceScopeAsync(current.Id, [ChartAcceptanceSession.Symbol], current.CaptureToken);
            if (await Task.WhenAny(writer, current.Ended) == writer && !writer.IsCompletedSuccessfully)
                current.Fail("The chart evidence could not be written.");
        }
        catch (Exception exception)
        {
            current.Fail("The exclusive AAPL scope could not start.");
            logger.LogWarning(exception, "Chart acceptance scope start failed. SessionId={SessionId}", current.Id);
        }
        var (state, reason) = await current.Ended;
        var removalOutcome = await RemoveScopeAsync(current);
        await PublishAsync(current, writer, state, reason, removalOutcome);
    }

    // Release only this scope using a token independent of the expired capture.
    private async Task<ChartAcceptanceRemoval> RemoveScopeAsync(ChartAcceptanceSession current)
    {
        using var cleanup = new CancellationTokenSource(ScopeCleanupBudget, clock);
        try
        {
            await subscriptions.RemoveScopeAsync(current.Id, cleanup.Token);
            current.MarkRemoval(true, null);
            var outcome = await stream.WaitForChartDiagnosticRemovalAsync(current.Id, cleanup.Token);
            current.MarkRemoval(true, outcome.Outcome switch
            {
                "acknowledged" => true,
                "retained-by-normal-owner" or "not-subscribed" => null,
                _ => false
            });
            return outcome;
        }
        catch (Exception exception)
        {
            current.MarkRemoval(current.Status.ScopeRemovalRequested, false);
            logger.LogWarning(exception, "Chart acceptance scope cleanup did not verify removal. SessionId={SessionId}", current.Id);
            return new("cleanup-unverified");
        }
    }

    // Publish bounded result evidence even if one archive write or the subscription cleanup failed.
    private async Task PublishAsync(ChartAcceptanceSession current, Task<IReadOnlyList<ChartAcceptanceChunk>> writer,
        string state, string reason, ChartAcceptanceRemoval removalOutcome)
    {
        IReadOnlyList<ChartAcceptanceChunk> chunks;
        try { chunks = await writer; }
        catch (Exception exception)
        {
            state = "incomplete";
            reason = "The chart evidence could not be written within its archive limits.";
            chunks = current.Archive.CommittedChunks;
            logger.LogWarning(exception, "Chart acceptance archive failed. SessionId={SessionId}", current.Id);
        }

        if (!current.Status.ScopeRemovalRequested || current.Status.RemovalAcknowledged == false)
        {
            state = "incomplete";
            reason = "Scope removal or the provider's removal acknowledgement could not be verified.";
        }

        var result = current.BuildResult(state, reason, removalOutcome, chunks);
        try
        {
            using var writing = new CancellationTokenSource(ResultWriteBudget, clock);
            var resultId = await current.Archive.WriteResultAsync(result, clock.GetUtcNow(), writing.Token);
            current.PublishStatus(result.State, result.Reason, resultId);
        }
        catch (Exception exception)
        {
            current.PublishStatus("incomplete", "The bounded chart evidence result could not be written.", null);
            logger.LogWarning(exception, "Chart acceptance result write failed. SessionId={SessionId}", current.Id);
        }
    }
}
