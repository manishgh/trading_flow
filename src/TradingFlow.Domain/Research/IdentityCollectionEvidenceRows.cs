using System.Collections.ObjectModel;

namespace TradingFlow.Domain.Research;

public enum IdentityEvidenceReadinessFailure
{
    IssuerIdentityUnavailable = 1,
    SecurityIdentityUnresolved = 2,
    SymbolAmbiguousInSnapshot = 3
}

public enum CorporateActionEvidenceType
{
    ReverseSplit = 1,
    ForwardSplit = 2,
    UnitSplit = 3,
    CashDividend = 4,
    StockDividend = 5,
    SpinOff = 6,
    CashMerger = 7,
    StockMerger = 8,
    StockAndCashMerger = 9,
    Redemption = 10,
    NameChange = 11,
    WorthlessRemoval = 12,
    RightsDistribution = 13,
    PartialCall = 14,
    Reorganization = 15
}

/// <summary>
/// A current provider asset snapshot. Its validity starts at receipt because Alpaca does not
/// publish a historical effective timestamp or issuer identifier on the assets endpoint.
/// </summary>
public sealed record SecurityMasterSnapshotEvidenceRow : INormalizedEvidenceRow
{
    public SecurityMasterSnapshotEvidenceRow(
        int schemaVersion,
        string runId,
        string configHash,
        string codeVersion,
        string dataFeed,
        string provider,
        string securityId,
        string? issuerId,
        string symbol,
        string name,
        string exchange,
        string assetClass,
        string currency,
        string status,
        bool tradable,
        bool marginable,
        bool shortable,
        bool easyToBorrow,
        bool fractionable,
        string? borrowStatus,
        decimal? maintenanceMarginRequirement,
        decimal? marginRequirementLong,
        decimal? marginRequirementShort,
        IReadOnlyList<string> attributes,
        DateTimeOffset observedAtUtc,
        bool symbolAmbiguousInSnapshot,
        IReadOnlyList<EvidenceRowSourceAddress> sources)
    {
        SchemaVersion = EvidenceRowContract.NormalizeSchemaVersion(schemaVersion);
        RunId = EvidenceRowContract.NormalizeRequired(runId, nameof(runId));
        ConfigHash = EvidenceRowContract.NormalizeSha256(configHash);
        CodeVersion = EvidenceRowContract.NormalizeRequired(codeVersion, nameof(codeVersion));
        DataFeed = EvidenceRowContract.NormalizeRequired(dataFeed, nameof(dataFeed)).ToLowerInvariant();
        Provider = EvidenceRowContract.NormalizeRequired(provider, nameof(provider)).ToLowerInvariant();
        SecurityId = EvidenceRowContract.NormalizeRequired(securityId, nameof(securityId));
        IssuerId = String.IsNullOrWhiteSpace(issuerId) ? null : issuerId.Trim();
        Symbol = EvidenceRowContract.NormalizeRequired(symbol, nameof(symbol)).ToUpperInvariant();
        Name = EvidenceRowContract.NormalizeRequired(name, nameof(name));
        Exchange = EvidenceRowContract.NormalizeRequired(exchange, nameof(exchange)).ToUpperInvariant();
        AssetClass = EvidenceRowContract.NormalizeRequired(assetClass, nameof(assetClass)).ToLowerInvariant();
        Currency = EvidenceRowContract.NormalizeRequired(currency, nameof(currency)).ToUpperInvariant();
        Status = EvidenceRowContract.NormalizeRequired(status, nameof(status)).ToLowerInvariant();
        Tradable = tradable;
        Marginable = marginable;
        Shortable = shortable;
        EasyToBorrow = easyToBorrow;
        Fractionable = fractionable;
        BorrowStatus = String.IsNullOrWhiteSpace(borrowStatus)
            ? null
            : borrowStatus.Trim().ToLowerInvariant();
        MaintenanceMarginRequirement = RequireNonNegative(
            maintenanceMarginRequirement,
            nameof(maintenanceMarginRequirement));
        MarginRequirementLong = RequireNonNegative(
            marginRequirementLong,
            nameof(marginRequirementLong));
        MarginRequirementShort = RequireNonNegative(
            marginRequirementShort,
            nameof(marginRequirementShort));
        Attributes = new ReadOnlyCollection<string>(
            EvidenceRowContract.CopyNormalizedStrings(attributes)
                .Select(value => value.ToLowerInvariant())
                .ToArray());
        ObservedAtUtc = EvidenceRowContract.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        ValidFromUtc = ObservedAtUtc;
        Sources = EvidenceRowContract.CopySources(sources);

        var failures = new List<IdentityEvidenceReadinessFailure>();
        if (IssuerId is null)
        {
            failures.Add(IdentityEvidenceReadinessFailure.IssuerIdentityUnavailable);
        }

        if (symbolAmbiguousInSnapshot)
        {
            failures.Add(IdentityEvidenceReadinessFailure.SymbolAmbiguousInSnapshot);
        }

        ReadinessFailures = new ReadOnlyCollection<IdentityEvidenceReadinessFailure>(failures);
    }

    public int SchemaVersion { get; }
    public string RunId { get; }
    public string ConfigHash { get; }
    public string CodeVersion { get; }
    public string DataFeed { get; }
    public string Provider { get; }
    public string SecurityId { get; }
    public string? IssuerId { get; }
    public string Symbol { get; }
    public string Name { get; }
    public string Exchange { get; }
    public string AssetClass { get; }
    public string Currency { get; }
    public string Status { get; }
    public bool Tradable { get; }
    public bool Marginable { get; }
    public bool Shortable { get; }
    public bool EasyToBorrow { get; }
    public bool Fractionable { get; }
    public string? BorrowStatus { get; }
    public decimal? MaintenanceMarginRequirement { get; }
    public decimal? MarginRequirementLong { get; }
    public decimal? MarginRequirementShort { get; }
    public IReadOnlyList<string> Attributes { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public DateTimeOffset ValidFromUtc { get; }
    public DateTimeOffset? ValidToUtc => null;
    public IReadOnlyList<IdentityEvidenceReadinessFailure> ReadinessFailures { get; }
    public bool IsPointInTimeReady => ReadinessFailures.Count == 0;
    public IReadOnlyList<EvidenceRowSourceAddress> Sources { get; }
    public DateTimeOffset SourceTimestampUtc => ObservedAtUtc;

    private static decimal? RequireNonNegative(decimal? value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}

/// <summary>
/// A symbol-to-security interval observed in a current asset snapshot. The interval cannot begin
/// before receipt and remains open until a later snapshot provides contrary evidence.
/// </summary>
public sealed record SymbolIntervalEvidenceRow : INormalizedEvidenceRow
{
    public SymbolIntervalEvidenceRow(
        int schemaVersion,
        string runId,
        string configHash,
        string codeVersion,
        string dataFeed,
        string provider,
        string securityId,
        string? issuerId,
        string symbol,
        DateTimeOffset observedAtUtc,
        bool symbolAmbiguousInSnapshot,
        IReadOnlyList<EvidenceRowSourceAddress> sources)
    {
        SchemaVersion = EvidenceRowContract.NormalizeSchemaVersion(schemaVersion);
        RunId = EvidenceRowContract.NormalizeRequired(runId, nameof(runId));
        ConfigHash = EvidenceRowContract.NormalizeSha256(configHash);
        CodeVersion = EvidenceRowContract.NormalizeRequired(codeVersion, nameof(codeVersion));
        DataFeed = EvidenceRowContract.NormalizeRequired(dataFeed, nameof(dataFeed)).ToLowerInvariant();
        Provider = EvidenceRowContract.NormalizeRequired(provider, nameof(provider)).ToLowerInvariant();
        SecurityId = EvidenceRowContract.NormalizeRequired(securityId, nameof(securityId));
        IssuerId = String.IsNullOrWhiteSpace(issuerId) ? null : issuerId.Trim();
        Symbol = EvidenceRowContract.NormalizeRequired(symbol, nameof(symbol)).ToUpperInvariant();
        ObservedAtUtc = EvidenceRowContract.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        ValidFromUtc = ObservedAtUtc;
        Sources = EvidenceRowContract.CopySources(sources);

        var failures = new List<IdentityEvidenceReadinessFailure>();
        if (IssuerId is null)
        {
            failures.Add(IdentityEvidenceReadinessFailure.IssuerIdentityUnavailable);
        }

        if (symbolAmbiguousInSnapshot)
        {
            failures.Add(IdentityEvidenceReadinessFailure.SymbolAmbiguousInSnapshot);
        }

        ReadinessFailures = new ReadOnlyCollection<IdentityEvidenceReadinessFailure>(failures);
    }

    public int SchemaVersion { get; }
    public string RunId { get; }
    public string ConfigHash { get; }
    public string CodeVersion { get; }
    public string DataFeed { get; }
    public string Provider { get; }
    public string SecurityId { get; }
    public string? IssuerId { get; }
    public string Symbol { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public DateTimeOffset ValidFromUtc { get; }
    public DateTimeOffset? ValidToUtc => null;
    public IReadOnlyList<IdentityEvidenceReadinessFailure> ReadinessFailures { get; }
    public bool IsPointInTimeReady => ReadinessFailures.Count == 0;
    public IReadOnlyList<EvidenceRowSourceAddress> Sources { get; }
    public DateTimeOffset SourceTimestampUtc => ObservedAtUtc;
}

/// <summary>
/// A provider corporate-action observation. Provider dates describe processing/effect, while
/// AvailabilityTimestampUtc is always the local receipt time because Alpaca does not guarantee
/// when an action first became available.
/// </summary>
public sealed record CorporateActionEvidenceRow : INormalizedEvidenceRow
{
    public CorporateActionEvidenceRow(
        int schemaVersion,
        string runId,
        string configHash,
        string codeVersion,
        string dataFeed,
        string provider,
        CorporateActionEvidenceType actionType,
        string providerActionId,
        string? securityId,
        string? issuerId,
        string? primarySymbol,
        string? primaryCusip,
        DateOnly processDate,
        DateOnly? effectiveDate,
        DateOnly? exDate,
        DateOnly? recordDate,
        DateOnly? payableDate,
        IReadOnlyDictionary<string, string> providerFields,
        DateTimeOffset receivedAtUtc,
        IReadOnlyList<EvidenceRowSourceAddress> sources)
    {
        SchemaVersion = EvidenceRowContract.NormalizeSchemaVersion(schemaVersion);
        RunId = EvidenceRowContract.NormalizeRequired(runId, nameof(runId));
        ConfigHash = EvidenceRowContract.NormalizeSha256(configHash);
        CodeVersion = EvidenceRowContract.NormalizeRequired(codeVersion, nameof(codeVersion));
        DataFeed = EvidenceRowContract.NormalizeRequired(dataFeed, nameof(dataFeed)).ToLowerInvariant();
        Provider = EvidenceRowContract.NormalizeRequired(provider, nameof(provider)).ToLowerInvariant();
        if (!Enum.IsDefined(actionType))
        {
            throw new ArgumentOutOfRangeException(nameof(actionType));
        }

        ActionType = actionType;
        ProviderActionId = EvidenceRowContract.NormalizeRequired(
            providerActionId,
            nameof(providerActionId));
        SecurityId = String.IsNullOrWhiteSpace(securityId) ? null : securityId.Trim();
        IssuerId = String.IsNullOrWhiteSpace(issuerId) ? null : issuerId.Trim();
        PrimarySymbol = String.IsNullOrWhiteSpace(primarySymbol)
            ? null
            : primarySymbol.Trim().ToUpperInvariant();
        PrimaryCusip = String.IsNullOrWhiteSpace(primaryCusip)
            ? null
            : primaryCusip.Trim().ToUpperInvariant();
        if (PrimarySymbol is null && PrimaryCusip is null)
        {
            throw new ArgumentException(
                "Corporate-action evidence requires a provider symbol or CUSIP identity reference.");
        }

        ProcessDate = processDate;
        EffectiveDate = effectiveDate;
        ExDate = exDate;
        RecordDate = recordDate;
        PayableDate = payableDate;
        ProviderFields = CopyProviderFields(providerFields);
        ReceivedAtUtc = EvidenceRowContract.RequireUtc(receivedAtUtc, nameof(receivedAtUtc));
        AvailabilityTimestampUtc = ReceivedAtUtc;
        Sources = EvidenceRowContract.CopySources(sources);

        var failures = new List<IdentityEvidenceReadinessFailure>();
        if (SecurityId is null)
        {
            failures.Add(IdentityEvidenceReadinessFailure.SecurityIdentityUnresolved);
        }

        if (IssuerId is null)
        {
            failures.Add(IdentityEvidenceReadinessFailure.IssuerIdentityUnavailable);
        }

        ReadinessFailures = new ReadOnlyCollection<IdentityEvidenceReadinessFailure>(failures);
    }

    public int SchemaVersion { get; }
    public string RunId { get; }
    public string ConfigHash { get; }
    public string CodeVersion { get; }
    public string DataFeed { get; }
    public string Provider { get; }
    public CorporateActionEvidenceType ActionType { get; }
    public string ProviderActionId { get; }
    public string? SecurityId { get; }
    public string? IssuerId { get; }
    public string? PrimarySymbol { get; }
    public string? PrimaryCusip { get; }
    public DateOnly ProcessDate { get; }
    public DateOnly? EffectiveDate { get; }
    public DateOnly? ExDate { get; }
    public DateOnly? RecordDate { get; }
    public DateOnly? PayableDate { get; }
    public IReadOnlyDictionary<string, string> ProviderFields { get; }
    public DateTimeOffset ReceivedAtUtc { get; }
    public DateTimeOffset AvailabilityTimestampUtc { get; }
    public IReadOnlyList<IdentityEvidenceReadinessFailure> ReadinessFailures { get; }
    public bool IsPointInTimeReady => ReadinessFailures.Count == 0;
    public IReadOnlyList<EvidenceRowSourceAddress> Sources { get; }
    public DateTimeOffset SourceTimestampUtc => AvailabilityTimestampUtc;

    private static IReadOnlyDictionary<string, string> CopyProviderFields(
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var normalized = values.ToDictionary(
            pair => EvidenceRowContract.NormalizeRequired(pair.Key, nameof(values)),
            pair => pair.Value ?? throw new ArgumentException(
                "Corporate-action provider fields cannot contain null values.",
                nameof(values)),
            StringComparer.Ordinal);
        return new ReadOnlyDictionary<string, string>(
            normalized
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }
}

public sealed record SecurityMasterSnapshotNormalizationResult
{
    public SecurityMasterSnapshotNormalizationResult(
        IReadOnlyList<SecurityMasterSnapshotEvidenceRow> securities,
        IReadOnlyList<SymbolIntervalEvidenceRow> symbolIntervals)
    {
        Securities = new ReadOnlyCollection<SecurityMasterSnapshotEvidenceRow>(
            (securities ?? throw new ArgumentNullException(nameof(securities)))
                .Select(row => row ?? throw new ArgumentException(
                    "Security snapshots cannot contain null rows.",
                    nameof(securities)))
                .ToArray());
        SymbolIntervals = new ReadOnlyCollection<SymbolIntervalEvidenceRow>(
            (symbolIntervals ?? throw new ArgumentNullException(nameof(symbolIntervals)))
                .Select(row => row ?? throw new ArgumentException(
                    "Symbol intervals cannot contain null rows.",
                    nameof(symbolIntervals)))
                .ToArray());
        if (Securities.Count != SymbolIntervals.Count)
        {
            throw new ArgumentException(
                "Every security snapshot must have one observed symbol interval.");
        }
    }

    public IReadOnlyList<SecurityMasterSnapshotEvidenceRow> Securities { get; }
    public IReadOnlyList<SymbolIntervalEvidenceRow> SymbolIntervals { get; }
}
