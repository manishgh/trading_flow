using TradingFlow.Domain.Persistence;

namespace TradingFlow.Domain.Execution;

public sealed record AccountLossLimits(decimal DailyPct, decimal WeeklyPct, decimal PeakToTroughPct);
public sealed record AccountLossGuardOptions(string Provider, AccountLossLimits Limits, TimeSpan MaximumSnapshotAge);
public sealed record AccountLossScope(string Provider, string Mode, string AccountId);
public sealed record AccountClosingEquity(DateOnly Session, DateTimeOffset CloseUtc, decimal Equity);
public sealed record AccountLossEvidence(
    DateOnly Session,
    AccountClosingEquity PriorClose,
    AccountClosingEquity WeekAnchor,
    IReadOnlyList<AccountClosingEquity> CompletedSessions,
    string? FundingIssue,
    string? HistoryArchiveId = null,
    DateOnly? LatestFundingDate = null);
public sealed record AccountLossSample(
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ObservedAtUtc,
    decimal Equity,
    AccountLossEvidence? Evidence,
    string? EvidenceIssue);
public sealed record AccountLossBreach(string Code, decimal ObservedPct, decimal LimitPct, DateTimeOffset ObservedAtUtc);
public sealed record AccountLossCancellationReview(
    string ClientOrderId, string BrokerOrderId, string Symbol, decimal FilledQuantity, DateTimeOffset DetectedAtUtc);
public sealed record AccountLossState(
    DateTimeOffset ObservationUtc,
    DateOnly? Session,
    AccountClosingEquity? PriorClose,
    AccountClosingEquity? WeekAnchor,
    DateTimeOffset? PeakEpochStartUtc,
    decimal? PeakEquity,
    AccountLossBreach? DailyBreach = null,
    AccountLossBreach? WeeklyBreach = null,
    AccountLossBreach? PeakBreach = null,
    string? FundingIssue = null,
    string? EvidenceIssue = null,
    AccountLossLimits? EvaluatedLimits = null,
    DateTimeOffset? LastTrustedObservationUtc = null,
    IReadOnlyList<AccountLossCancellationReview>? CancellationReviews = null,
    long CancellationSweepGeneration = 0,
    bool CancellationSweepPending = false,
    DateTimeOffset? LastTrustedRequestUtc = null,
    DateOnly? LatestFundingDate = null)
{
    public bool Breached => DailyBreach is not null || WeeklyBreach is not null || PeakBreach is not null;
}

public interface IAccountLossStateStore
{
    Task<AccountLossState?> GetAsync(AccountLossScope scope, CancellationToken cancellationToken);

    Task<AccountLossState> RecordAsync(
        AccountLossScope scope, ProductionRun run, AccountLossSample sample,
        AccountLossGuardOptions options, CancellationToken cancellationToken);

    Task SetCancellationReviewAsync(
        AccountLossScope scope, ProductionRun run, AccountLossCancellationReview review,
        bool required, CancellationToken cancellationToken);

    Task<bool> CompleteCancellationSweepAsync(
        AccountLossScope scope, long generation, CancellationToken cancellationToken);
}

/// <summary>Evaluates marked portfolio equity without broker, SQLite or input-mode assumptions.</summary>
public static class AccountLossLimitPolicy
{
    // A weekly latch is scoped to the Monday of the verified New York trading week.
    public static DateOnly WeekOf(DateOnly session) =>
        session.AddDays(-((int)session.DayOfWeek + 6) % 7);

    // A breach is inclusive. Incoming equity already includes realized and unrealized P/L.
    public static decimal LossPercent(decimal referenceEquity, decimal markedEquity)
    {
        if (referenceEquity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(referenceEquity), "A loss limit requires positive reference equity.");
        }
        return Math.Max(0m, (referenceEquity - markedEquity) / referenceEquity * 100m);
    }

    // Preserve breach latches within verified periods, and the peak across live marks and restarts.
    public static AccountLossState Observe(
        AccountLossState? previous, AccountLossSample sample, AccountLossLimits limits)
    {
        if (previous is not null && sample.ObservedAtUtc <= previous.ObservationUtc)
        {
            return previous;
        }

        var state = previous ?? new AccountLossState(default, null, null, null, null, null);
        var evidence = sample.Evidence;
        if (sample.EvidenceIssue is not null || evidence is null)
        {
            return state with { ObservationUtc = sample.ObservedAtUtc, EvidenceIssue = sample.EvidenceIssue ?? "Account loss evidence is unavailable." };
        }

        if (evidence.PriorClose.Equity <= 0m || evidence.WeekAnchor.Equity <= 0m ||
            evidence.PriorClose.Session >= evidence.Session || evidence.WeekAnchor.Session >= WeekOf(evidence.Session) ||
            evidence.PriorClose.CloseUtc >= sample.ObservedAtUtc || evidence.WeekAnchor.CloseUtc >= sample.ObservedAtUtc)
        {
            return state with { ObservationUtc = sample.ObservedAtUtc, EvidenceIssue = "Broker prior-close equity is incomplete or not known yet." };
        }

        // Funding changes invalidate the prior peak basis until a reviewed rebaseline.
        var fundingIssue = state.FundingIssue ?? evidence.FundingIssue;
        var latestFunding = new[] { state.LatestFundingDate, evidence.LatestFundingDate }.Max();
        if (fundingIssue is not null)
        {
            return state with { ObservationUtc = sample.ObservedAtUtc, FundingIssue = fundingIssue, LatestFundingDate = latestFunding };
        }
        if (state.Session is { } session && evidence.Session < session)
        {
            return state with { ObservationUtc = sample.ObservedAtUtc, EvidenceIssue = "Broker exchange session moved backwards." };
        }

        var sameDay = state.Session == evidence.Session;
        var sameWeek = state.Session is { } observed && WeekOf(observed) == WeekOf(evidence.Session);
        if ((sameDay && state.PriorClose != evidence.PriorClose) ||
            (sameWeek && state.WeekAnchor != evidence.WeekAnchor))
        {
            return state with { ObservationUtc = sample.ObservedAtUtc, EvidenceIssue = "A frozen broker equity baseline changed." };
        }

        var epoch = state.PeakEpochStartUtc ?? evidence.WeekAnchor.CloseUtc;
        var (peak, weeklyBreach, peakBreach) = ReplayCompletedCloses(state, evidence, limits, epoch, sample.ObservedAtUtc);
        peak = Math.Max(peak, sample.Equity);

        var next = new AccountLossState(
            sample.ObservedAtUtc, evidence.Session, evidence.PriorClose, evidence.WeekAnchor, epoch, peak,
            (sameDay ? state.DailyBreach : null) ??
                Breach("daily", evidence.PriorClose.Equity, limits.DailyPct, sample.Equity, sample.ObservedAtUtc),
            weeklyBreach ?? Breach("weekly", evidence.WeekAnchor.Equity, limits.WeeklyPct, sample.Equity, sample.ObservedAtUtc),
            peakBreach ?? Breach("peak_to_trough", peak, limits.PeakToTroughPct, sample.Equity, sample.ObservedAtUtc),
            EvaluatedLimits: limits, LastTrustedObservationUtc: sample.ObservedAtUtc,
            CancellationReviews: state.CancellationReviews,
            CancellationSweepGeneration: state.CancellationSweepGeneration,
            CancellationSweepPending: state.CancellationSweepPending,
            LastTrustedRequestUtc: sample.RequestedAtUtc, LatestFundingDate: latestFunding);
        return NewBreaches(state, next).Count == 0
            ? next
            : next with
            {
                CancellationSweepGeneration = checked(state.CancellationSweepGeneration + 1),
                CancellationSweepPending = true
            };
    }

    // Use the same refusal reason at reservation and first dispatch.
    public static string? Refusal(AccountLossState? state, AccountLossGuardOptions options, DateOnly session, DateTimeOffset now)
    {
        if (state is null)
        {
            return "No authoritative account loss observation is recorded.";
        }
        if (state.CancellationReviews is { Count: > 0 })
        {
            return "An opening order is awaiting cancellation confirmation or linked protection review. Resolve it before resuming entries.";
        }
        if (state.PeakBreach is { } peak)
        {
            return $"Peak-to-trough loss {peak.ObservedPct:F2}% reached its {peak.LimitPct:F2}% limit and requires review.";
        }
        if (state.FundingIssue is { } funding)
        {
            return funding;
        }
        if (state.EvidenceIssue is { } issue)
        {
            return issue;
        }
        if (state.Session != session || state.EvaluatedLimits != options.Limits ||
            state.ObservationUtc > now || state.LastTrustedRequestUtc is not { } requestedAt ||
            requestedAt > state.ObservationUtc || now - requestedAt > options.MaximumSnapshotAge)
        {
            return "The account loss observation is stale, belongs to another session, or used different limits.";
        }
        if (state.DailyBreach is { } daily)
        {
            return $"Daily loss {daily.ObservedPct:F2}% reached its {daily.LimitPct:F2}% limit.";
        }
        if (state.WeeklyBreach is { } weekly)
        {
            return $"Weekly loss {weekly.ObservedPct:F2}% reached its {weekly.LimitPct:F2}% limit.";
        }
        return state.CancellationSweepPending
            ? "The broker opening-order sweep after an account loss breach is still pending."
            : null;
    }

    // Share one definition of newly latched losses for sweep generation and risk journaling.
    public static IReadOnlyList<AccountLossBreach> NewBreaches(AccountLossState? previous, AccountLossState next) =>
        new[] { next.DailyBreach, next.WeeklyBreach, next.PeakBreach }
            .Except(new[] { previous?.DailyBreach, previous?.WeeklyBreach, previous?.PeakBreach })
            .OfType<AccountLossBreach>()
            .ToArray();

    // Replay only closes that became available after the last trusted mark. A
    // current-week breach or a peak breach survives a rebound during downtime.
    private static (decimal Peak, AccountLossBreach? Weekly, AccountLossBreach? PeakBreach) ReplayCompletedCloses(
        AccountLossState state, AccountLossEvidence evidence, AccountLossLimits limits,
        DateTimeOffset epoch, DateTimeOffset observedAt)
    {
        var peak = state.PeakEquity ?? evidence.WeekAnchor.Equity;
        var week = WeekOf(evidence.Session);
        var weekly = state.Session is { } previous && WeekOf(previous) == week ? state.WeeklyBreach : null;
        var peakBreach = state.PeakBreach;
        var cutoff = state.LastTrustedObservationUtc ?? evidence.WeekAnchor.CloseUtc;
        foreach (var close in evidence.CompletedSessions.OrderBy(item => item.CloseUtc))
        {
            if (close.CloseUtc <= cutoff || close.CloseUtc >= observedAt || close.Session >= evidence.Session)
            {
                continue;
            }

            // A reviewed peak epoch excludes old peak marks; weekly replay retains its trusted cutoff.
            if (close.CloseUtc > epoch)
            {
                peak = Math.Max(peak, close.Equity);
                peakBreach ??= Breach("peak_to_trough", peak, limits.PeakToTroughPct, close.Equity, close.CloseUtc);
            }
            if (close.Session >= week)
            {
                weekly ??= Breach("weekly", evidence.WeekAnchor.Equity, limits.WeeklyPct, close.Equity, close.CloseUtc);
            }
        }

        return (peak, weekly, peakBreach);
    }

    // Keep the first qualifying loss in each period for stable audit evidence.
    private static AccountLossBreach? Breach(string code, decimal baseline, decimal limit, decimal equity, DateTimeOffset at)
    {
        var loss = LossPercent(baseline, equity);
        return loss >= limit ? new AccountLossBreach(code, loss, limit, at) : null;
    }
}
