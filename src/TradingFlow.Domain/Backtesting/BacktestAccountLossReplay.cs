using TradingFlow.Domain.Execution;

namespace TradingFlow.Domain.Backtesting;

/// <summary>Frozen settings for the optional replay account-loss policy. The policy identity fixes the observation cadence.</summary>
public sealed record BacktestAccountLossReplayConfig(bool Enabled, string PolicyId, AccountLossLimits Limits)
{
    public const string FiveMinutePolicyId = "daily-rth5m-sampled-account-loss-v1";
    public const string EvidenceBasis = "simulated_cash_and_filled_positions_at_completed_regular_five_minute_marks";
    public const string InitialAnchorBasis = "synthetic_flat_starting_capital";
    public const string Limitations = "Five-minute sampled simulation with a synthetic flat starting-capital anchor. " +
        "The live guard checks 60-second freshness. Intrabar breaches, broker equity and resting opening-order cancellation are outside this replay.";
    public static TimeSpan MaximumSnapshotAge => TimeSpan.FromMinutes(5);

    /// <summary>Reject a different sampling policy or invalid thresholds before a replay can start.</summary>
    public void Validate(string mode, string? executionReplayPolicyId)
    {
        if (PolicyId != FiveMinutePolicyId)
            throw new InvalidOperationException($"Unsupported backtest account-loss policy: {PolicyId}.");
        if (Limits is null || Limits.DailyPct is <= 0m or > 100m || Limits.WeeklyPct is <= 0m or > 100m ||
            Limits.PeakToTroughPct is <= 0m or > 100m)
            throw new InvalidOperationException("Backtest account-loss thresholds must each be greater than zero and at most 100 percent.");
        if (Enabled && (mode != "backtest" || executionReplayPolicyId != ExecutionReplayPolicy.DailyRthFiveMinuteV1))
            throw new InvalidOperationException($"Enabled backtest account-loss limits require {ExecutionReplayPolicy.DailyRthFiveMinuteV1}.");
    }
}

/// <summary>Run-level disclosure of the exact account-loss policy used for a new result.</summary>
public sealed record BacktestAccountLossReplayReport(bool Applied, BacktestAccountLossReplayConfig? Settings = null,
    string? EvidenceBasis = null, string? InitialAnchorBasis = null, int? MaximumSnapshotAgeSeconds = null,
    string? Limitations = null)
{
    /// <summary>Disclose the evidence and limits of the sampled model on every new publication.</summary>
    public static BacktestAccountLossReplayReport From(BacktestAccountLossReplayConfig? settings) => settings is not { Enabled: true }
        ? new(false, settings)
        : new(true, settings, BacktestAccountLossReplayConfig.EvidenceBasis, BacktestAccountLossReplayConfig.InitialAnchorBasis,
            (int)BacktestAccountLossReplayConfig.MaximumSnapshotAge.TotalSeconds, BacktestAccountLossReplayConfig.Limitations);
}

/// <summary>Compact observation and sweep evidence for one isolated replay account.</summary>
public sealed record BacktestAccountLossBookSummary(long ObservationCount, long UnavailableObservationCount,
    long CompletedCancellationSweeps, DateTimeOffset? LastMarkedAtUtc, decimal? LastMarkedEquity,
    AccountLossState? FinalState);
