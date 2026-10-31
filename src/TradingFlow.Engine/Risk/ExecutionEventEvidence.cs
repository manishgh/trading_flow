using System.Collections.Frozen;

namespace TradingFlow.Engine.Risk;

/// <summary>Explicit workflow identity; per-fill research protection is not a native bracket claim.</summary>
public enum ExecutionProtectionActivation { Unspecified, NativeParentFullyFilled, CustomPerFill }

public sealed record ExecutionQuoteTick(string EventId, long EventTimeNanoseconds, decimal Bid,
    decimal Ask, long BidSizeShares, long AskSizeShares, bool IsEligible);
public sealed record ExecutionTradeTick(string EventId, long EventTimeNanoseconds, decimal Price,
    long SizeShares, bool IsEligible);

/// <summary>All same-symbol observations at one nanosecond; separate feeds do not prove within-batch order.</summary>
public sealed record ExecutionEventBatch(string Symbol, long EventTimeNanoseconds, long KnownAtNanoseconds,
    long Sequence, IReadOnlyList<ExecutionQuoteTick> Quotes, IReadOnlyList<ExecutionTradeTick> Trades,
    bool CoverageComplete);

public sealed record ExecutionEventOrderCommand(SimulatedOrderCommand Command, long KnownAtNanoseconds,
    string? ParentOrderId = null, bool IsProtection = false, bool IsRepair = false,
    long? ExpiresAtNanoseconds = null, bool NativeParentCommitted = false);

/// <summary>Broker-held child terms committed before any physical parent fill; not a client fill receipt.</summary>
public sealed record ExecutionNativeChildCommitment(string EntryParentId, string ParentContractId,
    int ParentQuantity, long CommittedAtNanoseconds);

/// <summary>Inputs must already be converted to raw execution prices and share quantities.</summary>
public sealed record ExecutionEventSimulationPolicy(ExecutionSimulationPolicy Costs,
    ExecutionProtectionActivation ProtectionActivation, long MaximumQuoteAgeNanoseconds,
    long FillReportLatencyNanoseconds, bool RawPriceAndQuantityBasisVerified,
    decimal PriceFactorToRaw, decimal QuantityFactorToRaw, string EvidenceVersion)
{
    public const string ModelVersion = "sip-trade-prior-nbbo-event-v1";

    /// <summary>Optional entry-only spread ceiling; protective reductions remain executable when spread widens.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public decimal? MaximumEntrySpreadBps { get; init; }
}

public sealed record ExecutionEventQuoteState(ExecutionQuoteTick Quote, long RemainingBidShares,
    long RemainingAskShares, long KnownAtNanoseconds);

public sealed record ExecutionEventFillReceipt(int Quantity, long KnownAtNanoseconds);

public sealed record ExecutionEventBookState(ExecutionBookState Book,
    IReadOnlyDictionary<string, ExecutionEventQuoteState> Quotes,
    IReadOnlyDictionary<string, long> LastEventTimes,
    IReadOnlyDictionary<string, long> LastKnownTimes,
    IReadOnlyDictionary<string, long> LastSequences,
    IReadOnlyDictionary<string, string> LastFingerprints,
    IReadOnlyDictionary<string, long> OrderActivationTimes,
    IReadOnlyDictionary<string, int> FilledQuantities,
    IReadOnlyDictionary<string, long> FillKnownTimes,
    string? PolicyFingerprint = null,
    IReadOnlyDictionary<string, string>? OrderMetadataFingerprints = null,
    IReadOnlyDictionary<string, System.Collections.Immutable.ImmutableArray<ExecutionEventFillReceipt>>? FillReceipts = null,
    IReadOnlyDictionary<string, string>? OrderEntryParents = null,
    IReadOnlyDictionary<string, System.Collections.Immutable.ImmutableArray<ExecutionEventFillReceipt>>? ParentEntryReceipts = null,
    IReadOnlyDictionary<string, System.Collections.Immutable.ImmutableArray<ExecutionEventFillReceipt>>? ParentReductionReceipts = null,
    IReadOnlyDictionary<string, long>? OrderExpiries = null,
    IReadOnlyDictionary<string, ExecutionNativeChildCommitment>? NativeChildCommitments = null)
{
    public static ExecutionEventBookState Empty { get; } = new(ExecutionBookState.Empty,
        new Dictionary<string, ExecutionEventQuoteState>().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
        EmptyTimes(), EmptyTimes(), EmptyTimes(),
        new Dictionary<string, string>().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
        EmptyOrderTimes(), new Dictionary<string, int>().ToFrozenDictionary(StringComparer.Ordinal),
        EmptyOrderTimes());

    private static FrozenDictionary<string, long> EmptyTimes() =>
        new Dictionary<string, long>().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    private static FrozenDictionary<string, long> EmptyOrderTimes() =>
        new Dictionary<string, long>().ToFrozenDictionary(StringComparer.Ordinal);
}

public sealed record TimedSimulatedExecutionEvent(SimulatedExecutionEvent Event,
    long OccurredAtNanoseconds, long KnownAtNanoseconds);
public sealed record ExecutionEventStepRequest(ExecutionEventBookState PriorState, ExecutionEventBatch Market,
    IReadOnlyList<ExecutionEventOrderCommand> Commands, ExecutionEventSimulationPolicy Policy);
public sealed record ExecutionEventStepResult(ExecutionEventBookState State,
    IReadOnlyList<TimedSimulatedExecutionEvent> Events, IReadOnlyList<string> UnavailableReasons);

public static class ExecutionEventTime
{
    /// <summary>Display/legacy timestamp only; event ordering retains the separate full nanoseconds.</summary>
    public static DateTimeOffset ToUtc(long unixNanoseconds) =>
        DateTimeOffset.UnixEpoch.AddTicks(unixNanoseconds / 100);
}
