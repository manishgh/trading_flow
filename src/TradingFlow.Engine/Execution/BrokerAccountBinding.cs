namespace TradingFlow.Engine.Execution;

public sealed record BrokerAccountBindingOptions
{
    public BrokerAccountBindingOptions(string expectedAccountId, bool enforcementEnabled = true)
    {
        if (enforcementEnabled && String.IsNullOrWhiteSpace(expectedAccountId))
        {
            throw new ArgumentException(
                "An expected broker account ID is required when account binding is enabled.",
                nameof(expectedAccountId));
        }

        ExpectedAccountId = (expectedAccountId ?? String.Empty).Trim();
        EnforcementEnabled = enforcementEnabled;
    }

    public string ExpectedAccountId { get; }
    public bool EnforcementEnabled { get; }
}

public sealed class BrokerAccountMismatchException(
    string expectedAccountId,
    string observedAccountId) : InvalidOperationException(
        $"Connected broker account '{observedAccountId}' does not match configured account '{expectedAccountId}'.")
{
    public string ExpectedAccountId { get; } = expectedAccountId;
    public string ObservedAccountId { get; } = observedAccountId;
}

public interface IBrokerAccountBindingService
{
    Task<BrokerAccountSnapshot> ValidateAsync(
        IBrokerClient broker,
        CancellationToken cancellationToken = default);

    void Validate(BrokerAccountSnapshot account);
}

/// <summary>
/// Prevents credentials for one broker account from operating state owned by another.
/// The expected account is operator configuration; it is never learned implicitly.
/// </summary>
public sealed class BrokerAccountBindingService(BrokerAccountBindingOptions options)
    : IBrokerAccountBindingService
{
    public async Task<BrokerAccountSnapshot> ValidateAsync(
        IBrokerClient broker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);
        var account = await broker.GetAccountSnapshotAsync(cancellationToken);
        Validate(account);
        return account;
    }

    public void Validate(BrokerAccountSnapshot account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (options.EnforcementEnabled &&
            !account.AccountId.Equals(options.ExpectedAccountId, StringComparison.Ordinal))
        {
            throw new BrokerAccountMismatchException(
                options.ExpectedAccountId,
                account.AccountId);
        }
    }
}
