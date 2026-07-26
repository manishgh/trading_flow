using System.Collections.ObjectModel;

namespace TradingFlow.Domain.Research;

public enum EvidenceDatasetKind
{
    MarketBarsAsTraded = 1,
    MarketBarsResearchAdjusted = 2,
    SipQuotes = 3,
    SipTrades = 4,
    NewsArticles = 5,
    NewsRevisions = 6,
    NewsStorySymbols = 7,
    SentimentAssessments = 8,
    CatalystResults = 9,
    ClassifierGroundTruth = 10,
    ClassifierAdjudications = 11,
    SecurityMaster = 12,
    SymbolIntervals = 13,
    CorporateActions = 14,
    UniverseSnapshots = 15,
    UniverseMembership = 16,
    Benchmarks = 17,
    ResearchDecisionJournal = 18,
    ExchangeSessions = 19
}

public enum EvidenceQualitySeverity
{
    Information = 1,
    Warning = 2,
    Failure = 3
}

public sealed record EvidenceQualityIssue
{
    public EvidenceQualityIssue(string code, EvidenceQualitySeverity severity, string message)
    {
        Code = EvidenceValue.NormalizeRequired(code, nameof(code));
        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity));
        }

        Severity = severity;
        Message = EvidenceValue.NormalizeRequired(message, nameof(message));
    }

    public string Code { get; }

    public EvidenceQualitySeverity Severity { get; }

    public string Message { get; }
}

public sealed class EvidenceQualityReport
{
    public EvidenceQualityReport(IReadOnlyList<EvidenceQualityIssue>? issues = null)
    {
        Issues = new ReadOnlyCollection<EvidenceQualityIssue>(
            (issues ?? Array.Empty<EvidenceQualityIssue>())
            .Select(issue => issue ?? throw new ArgumentException("Quality issues cannot contain null.", nameof(issues)))
            .OrderBy(issue => issue.Severity)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal)
            .ToArray());
    }

    public IReadOnlyList<EvidenceQualityIssue> Issues { get; }

    public bool Passed => Issues.All(issue => issue.Severity != EvidenceQualitySeverity.Failure);
}

public sealed class EvidencePartitionProvenance
{
    public EvidencePartitionProvenance(
        string provider,
        string endpoint,
        string dataFeed,
        string adjustment,
        string currency,
        DateOnly? asOfDate,
        IReadOnlyList<string>? securityIds,
        IReadOnlyList<string>? issuerIds,
        IReadOnlyList<string> symbols,
        string timeframe,
        DateTimeOffset requestedStartUtc,
        DateTimeOffset requestedEndUtc)
    {
        Provider = EvidenceValue.NormalizeRequired(provider, nameof(provider)).ToLowerInvariant();
        Endpoint = EvidenceValue.NormalizeRequired(endpoint, nameof(endpoint));
        DataFeed = EvidenceValue.NormalizeRequired(dataFeed, nameof(dataFeed)).ToLowerInvariant();
        Adjustment = EvidenceValue.NormalizeRequired(adjustment, nameof(adjustment)).ToLowerInvariant();
        Currency = EvidenceValue.NormalizeRequired(currency, nameof(currency)).ToUpperInvariant();
        AsOfDate = asOfDate;
        SecurityIds = CopyIdentifiers(securityIds, nameof(securityIds));
        IssuerIds = CopyIdentifiers(issuerIds, nameof(issuerIds));
        Symbols = new ReadOnlyCollection<string>(
            (symbols ?? throw new ArgumentNullException(nameof(symbols)))
            .Select(symbol => EvidenceValue.NormalizeRequired(symbol, nameof(symbols)).ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(symbol => symbol, StringComparer.Ordinal)
            .ToArray());
        if (Symbols.Count == 0)
        {
            throw new ArgumentException("Partition provenance requires at least one symbol.", nameof(symbols));
        }

        Timeframe = EvidenceValue.NormalizeRequired(timeframe, nameof(timeframe)).ToLowerInvariant();
        EvidenceValue.EnsureUtc(requestedStartUtc, nameof(requestedStartUtc));
        EvidenceValue.EnsureUtc(requestedEndUtc, nameof(requestedEndUtc));
        if (requestedEndUtc <= requestedStartUtc)
        {
            throw new ArgumentException("Requested end must follow requested start.");
        }

        RequestedStartUtc = requestedStartUtc;
        RequestedEndUtc = requestedEndUtc;
    }

    public string Provider { get; }

    public string Endpoint { get; }

    public string DataFeed { get; }

    public string Adjustment { get; }

    public string Currency { get; }

    public DateOnly? AsOfDate { get; }

    public IReadOnlyList<string> SecurityIds { get; }

    public IReadOnlyList<string> IssuerIds { get; }

    public IReadOnlyList<string> Symbols { get; }

    public string Timeframe { get; }

    public DateTimeOffset RequestedStartUtc { get; }

    public DateTimeOffset RequestedEndUtc { get; }

    private static IReadOnlyList<string> CopyIdentifiers(
        IEnumerable<string>? values,
        string parameterName) =>
        new ReadOnlyCollection<string>(
            (values ?? Array.Empty<string>())
            .Select(value => EvidenceValue.NormalizeRequired(value, parameterName))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray());
}

public sealed class EvidenceDatasetPartitionManifest
{
    public EvidenceDatasetPartitionManifest(
        string partitionId,
        EvidenceDatasetKind datasetKind,
        int schemaVersion,
        EvidencePartitionProvenance provenance,
        DateTimeOffset minimumSourceTimestampUtc,
        DateTimeOffset maximumSourceTimestampUtc,
        long rowCount,
        EvidenceArtifactReference artifact,
        IReadOnlyList<EvidenceSourceReference> sourceObservations,
        string normalizerVersion,
        string codeVersion,
        EvidenceQualityReport quality,
        EvidenceArtifactReference? quarantineLedger = null,
        IReadOnlyDictionary<string, string>? dimensions = null)
    {
        PartitionId = EvidenceValue.NormalizeRequired(partitionId, nameof(partitionId));
        if (!Enum.IsDefined(datasetKind))
        {
            throw new ArgumentOutOfRangeException(nameof(datasetKind));
        }

        DatasetKind = datasetKind;
        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        SchemaVersion = schemaVersion;
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        if (RequiresMarketIdentity(datasetKind) &&
            (Provenance.AsOfDate is null ||
             Provenance.SecurityIds.Count == 0 ||
             Provenance.Symbols.Count == 0))
        {
            throw new ArgumentException(
                "Market evidence requires as-of date, security identity, and symbol provenance.",
                nameof(provenance));
        }

        if (datasetKind == EvidenceDatasetKind.CorporateActions &&
            (Provenance.AsOfDate is null || Provenance.Symbols.Count == 0))
        {
            throw new ArgumentException(
                "Corporate-action evidence requires as-of date and symbol provenance; exact security and issuer identity may remain unavailable.",
                nameof(provenance));
        }

        if (datasetKind == EvidenceDatasetKind.UniverseMembership &&
            Provenance.IssuerIds.Count == 0)
        {
            throw new ArgumentException(
                "Universe membership evidence requires issuer identity provenance.",
                nameof(provenance));
        }

        EvidenceValue.EnsureUtc(minimumSourceTimestampUtc, nameof(minimumSourceTimestampUtc));
        EvidenceValue.EnsureUtc(maximumSourceTimestampUtc, nameof(maximumSourceTimestampUtc));
        if (maximumSourceTimestampUtc < minimumSourceTimestampUtc)
        {
            throw new ArgumentException("Partition maximum timestamp cannot precede its minimum timestamp.");
        }

        MinimumSourceTimestampUtc = minimumSourceTimestampUtc;
        MaximumSourceTimestampUtc = maximumSourceTimestampUtc;
        if (rowCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rowCount));
        }

        RowCount = rowCount;
        Artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
        SourceObservations = new EvidenceRowLineage(sourceObservations).Sources;
        NormalizerVersion = EvidenceValue.NormalizeRequired(normalizerVersion, nameof(normalizerVersion));
        CodeVersion = EvidenceValue.NormalizeRequired(codeVersion, nameof(codeVersion));
        Quality = quality ?? throw new ArgumentNullException(nameof(quality));
        QuarantineLedger = quarantineLedger;
        Dimensions = EvidenceValue.CopySorted(dimensions);
    }

    public string PartitionId { get; }

    public EvidenceDatasetKind DatasetKind { get; }

    public int SchemaVersion { get; }

    public EvidencePartitionProvenance Provenance { get; }

    public DateTimeOffset MinimumSourceTimestampUtc { get; }

    public DateTimeOffset MaximumSourceTimestampUtc { get; }

    public long RowCount { get; }

    public EvidenceArtifactReference Artifact { get; }

    public IReadOnlyList<EvidenceSourceReference> SourceObservations { get; }

    public string NormalizerVersion { get; }

    public string CodeVersion { get; }

    public EvidenceQualityReport Quality { get; }

    public EvidenceArtifactReference? QuarantineLedger { get; }

    public IReadOnlyDictionary<string, string> Dimensions { get; }

    private static bool RequiresMarketIdentity(EvidenceDatasetKind datasetKind) =>
        datasetKind is EvidenceDatasetKind.MarketBarsAsTraded or
            EvidenceDatasetKind.MarketBarsResearchAdjusted or
            EvidenceDatasetKind.SipQuotes or
            EvidenceDatasetKind.SipTrades or
            EvidenceDatasetKind.Benchmarks or
            EvidenceDatasetKind.SecurityMaster or
            EvidenceDatasetKind.SymbolIntervals or
            EvidenceDatasetKind.UniverseMembership;
}

public sealed class EvidenceDatasetManifest
{
    public EvidenceDatasetManifest(
        EvidenceDatasetKind kind,
        int schemaVersion,
        DateTimeOffset createdAtUtc,
        string collectionJobId,
        string collectionPlanHash,
        string configHash,
        string codeVersion,
        string normalizerVersion,
        string dataFeed,
        IReadOnlyList<EvidenceDatasetPartitionManifest> partitions,
        EvidenceQualityReport quality,
        IReadOnlyDictionary<string, string>? attributes = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        SchemaVersion = schemaVersion;
        EvidenceValue.EnsureUtc(createdAtUtc, nameof(createdAtUtc));
        CreatedAtUtc = createdAtUtc;
        CollectionJobId = EvidenceValue.NormalizeRequired(collectionJobId, nameof(collectionJobId));
        CollectionPlanHash = EvidenceValue.NormalizeSha256(collectionPlanHash);
        ConfigHash = EvidenceValue.NormalizeSha256(configHash);
        CodeVersion = EvidenceValue.NormalizeRequired(codeVersion, nameof(codeVersion));
        NormalizerVersion = EvidenceValue.NormalizeRequired(normalizerVersion, nameof(normalizerVersion));
        DataFeed = EvidenceValue.NormalizeRequired(dataFeed, nameof(dataFeed)).ToLowerInvariant();

        var partitionArray = (partitions ?? throw new ArgumentNullException(nameof(partitions)))
            .Select(partition => partition ?? throw new ArgumentException("Partitions cannot contain null.", nameof(partitions)))
            .OrderBy(partition => partition.PartitionId, StringComparer.Ordinal)
            .ToArray();
        if (partitionArray.Length == 0)
        {
            throw new ArgumentException("A committed dataset requires at least one partition.", nameof(partitions));
        }

        if (partitionArray.Any(partition =>
                partition.DatasetKind != kind ||
                partition.SchemaVersion != schemaVersion ||
                !partition.CodeVersion.Equals(CodeVersion, StringComparison.Ordinal) ||
                !partition.NormalizerVersion.Equals(NormalizerVersion, StringComparison.Ordinal) ||
                !partition.Provenance.DataFeed.Equals(DataFeed, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Every partition must match the dataset kind, schema, feed, code, and normalizer.",
                nameof(partitions));
        }

        if (partitionArray.Select(partition => partition.PartitionId).Distinct(StringComparer.Ordinal).Count() != partitionArray.Length)
        {
            throw new ArgumentException("Partition identifiers must be unique.", nameof(partitions));
        }

        Partitions = new ReadOnlyCollection<EvidenceDatasetPartitionManifest>(partitionArray);
        Quality = quality ?? throw new ArgumentNullException(nameof(quality));
        if (!Quality.Passed || partitionArray.Any(partition => !partition.Quality.Passed))
        {
            throw new ArgumentException("A committed dataset cannot contain failed quality evidence.", nameof(quality));
        }

        Attributes = EvidenceValue.CopySorted(attributes);
        LogicalDatasetKey = EvidenceCanonicalJson.ComputeSha256(new
        {
            Kind,
            SchemaVersion,
            CollectionPlanHash,
            ConfigHash,
            CodeVersion,
            NormalizerVersion,
            DataFeed,
            Partitions = Partitions.Select(partition => new
            {
                partition.PartitionId,
                partition.DatasetKind,
                partition.SchemaVersion,
                partition.Provenance,
                partition.MinimumSourceTimestampUtc,
                partition.MaximumSourceTimestampUtc,
                partition.RowCount,
                partition.Artifact,
                partition.NormalizerVersion,
                partition.CodeVersion,
                partition.Quality,
                partition.QuarantineLedger,
                partition.Dimensions
            }),
            Quality,
            Attributes
        });
        DatasetId = EvidenceCanonicalJson.ComputeSha256(new
        {
            LogicalDatasetKey,
            CreatedAtUtc,
            CollectionJobId,
            Partitions,
            Quality,
            Attributes
        });
    }

    public string DatasetId { get; }

    public string LogicalDatasetKey { get; }

    public EvidenceDatasetKind Kind { get; }

    public int SchemaVersion { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public string CollectionJobId { get; }

    public string CollectionPlanHash { get; }

    public string ConfigHash { get; }

    public string CodeVersion { get; }

    public string NormalizerVersion { get; }

    public string DataFeed { get; }

    public IReadOnlyList<EvidenceDatasetPartitionManifest> Partitions { get; }

    public EvidenceQualityReport Quality { get; }

    public IReadOnlyDictionary<string, string> Attributes { get; }
}

public sealed record EvidenceDatasetReference
{
    public EvidenceDatasetReference(
        string datasetId,
        EvidenceArtifactReference manifestArtifact)
    {
        DatasetId = EvidenceValue.NormalizeRequired(datasetId, nameof(datasetId));
        ManifestArtifact = manifestArtifact ?? throw new ArgumentNullException(nameof(manifestArtifact));
    }

    public string DatasetId { get; }

    public EvidenceArtifactReference ManifestArtifact { get; }

    public string ManifestSha256 => ManifestArtifact.Content.Sha256;
}
