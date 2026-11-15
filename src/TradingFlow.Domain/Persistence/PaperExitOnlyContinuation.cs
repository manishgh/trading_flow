namespace TradingFlow.Domain.Persistence;

public sealed record PaperRunStart(ProductionRun Run, PaperExitOnlyContinuation? ExitOnlyContinuation);

/// <summary>Identifies the build running an exit-only lease while the run retains its original provenance.</summary>
public sealed record PaperExitOnlyContinuation(
    Guid RunId,
    int LeaseAttempt,
    string OriginCodeVersion,
    string ExecutingCodeVersion,
    DateTimeOffset AttemptStartedAtUtc,
    DateTimeOffset EntriesStoppedAtUtc)
{
    public const string EventType = "paper_rebuild_exit_only";
    public const string Reason = "A rebuild set this run to exit-only. New entries require a separately authorized paper run.";

    public string EntryStopReason => Reason;

    // Keep attempt history isolated to the exact durable paper run.
    public static string StreamName(Guid runId) => $"paper-run:{runId:N}";
}
