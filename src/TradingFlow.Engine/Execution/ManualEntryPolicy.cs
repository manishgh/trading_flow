namespace TradingFlow.Engine.Execution;

public enum ManualEntryPolicy
{
    StrategyGated,
    OperatorDirect
}

public sealed record ManualEntryOptions(ManualEntryPolicy Policy)
{
    public static ManualEntryOptions Parse(string value) => new(value.Trim().ToLowerInvariant() switch
    {
        "strategy_gated" => ManualEntryPolicy.StrategyGated,
        "operator_direct" => ManualEntryPolicy.OperatorDirect,
        _ => throw new InvalidOperationException(
            "manual_entry_policy must be strategy_gated or operator_direct.")
    });
}

/// <summary>
/// Server-issued proof that an operator intentionally bypassed strategy admission.
/// It is deliberately unavailable for live execution and cannot be constructed
/// unless the active server policy explicitly permits direct operator entries.
/// </summary>
public sealed record OperatorOverrideAuthorization
{
    private OperatorOverrideAuthorization(string actor, string reason, DateTimeOffset issuedAtUtc)
    {
        Actor = actor;
        Reason = reason;
        IssuedAtUtc = issuedAtUtc;
    }

    public string Actor { get; }
    public string Reason { get; }
    public DateTimeOffset IssuedAtUtc { get; }

    public static OperatorOverrideAuthorization Issue(
        ManualEntryOptions options,
        string actor,
        string reason,
        DateTimeOffset issuedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (options.Policy != ManualEntryPolicy.OperatorDirect)
        {
            throw new InvalidOperationException(
                "Operator-direct entry is disabled by manual_entry_policy.");
        }

        return new OperatorOverrideAuthorization(
            actor.Trim(),
            reason.Trim(),
            issuedAtUtc.ToUniversalTime());
    }
}
