using System.Text.Json.Serialization;

namespace TradingFlow.Domain.Execution;

/// <summary>
/// Canonical entry-rejection vocabulary shared by execution gates, persistence, and APIs.
/// Member names intentionally match the binding specification and persisted wire values.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RejectCode
{
    REJECT_WIDE_SPREAD,
    REJECT_LOW_DOLLAR_VOLUME,
    REJECT_STALE_NEWS,
    REJECT_AMBIGUOUS_DIRECTION,
    REJECT_NO_SIP_DATA,
    REJECT_HALT_OR_LULD,
    REJECT_ALREADY_EXTENDED,
    REJECT_NO_SHORT_AVAILABILITY,
    REJECT_DUPLICATE_EVENT,
    REJECT_SETUP_INVALID,
    REJECT_PDT_LIMIT,
    REJECT_CORPORATE_ACTION,
    REJECT_UNKNOWN_EARNINGS_DATE,
    REJECT_DEGRADED_DATA,
    REJECT_RECONCILE_LOCK,
    REJECT_BUDGET_EXHAUSTED,
    REJECT_PARTICIPATION_CAP
}
