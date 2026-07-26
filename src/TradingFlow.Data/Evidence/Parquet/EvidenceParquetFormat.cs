using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parquet;
using Parquet.Schema;
using Parquet.Serialization;
using Parquet.Serialization.Attributes;
using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence.Parquet;

public sealed class EvidenceParquetOptions
{
    public int MaximumRowsPerPartition { get; init; } = 250_000;

    public long MaximumPartitionBytes { get; init; } = 512L * 1024L * 1024L;

    internal void Validate()
    {
        if (MaximumRowsPerPartition <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRowsPerPartition));
        }

        if (MaximumPartitionBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPartitionBytes));
        }
    }
}

public sealed record EvidenceParquetDocument<T>(
    EvidenceDatasetKind DatasetKind,
    int SchemaVersion,
    IReadOnlyList<T> Rows,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// Deterministic normalized-evidence Parquet codec. Partitions are intentionally bounded and
/// materialized before immutable publication so no partial object can be catalogued.
/// </summary>
public sealed class EvidenceParquetCodec
{
    public const string MediaType = "application/vnd.apache.parquet";
    public const string FormatVersion = "1";
    private const string TimestampEncoding = "unix_microseconds_utc";
    private const int IdentityDecimalScale = 6;
    private const decimal IdentityDecimalScaleFactor = 1_000_000m;
    private readonly EvidenceParquetOptions options;

    public EvidenceParquetCodec(EvidenceParquetOptions? options = null)
    {
        this.options = options ?? new EvidenceParquetOptions();
        this.options.Validate();
    }

    public Task<byte[]> WriteMarketBarsAsTradedAsync(
        IReadOnlyCollection<MarketBarEvidenceRow> rows,
        string normalizerVersion,
        CancellationToken cancellationToken = default) =>
        WriteMarketBarsAsync(
            rows,
            EvidenceDatasetKind.MarketBarsAsTraded,
            normalizerVersion,
            cancellationToken);

    public Task<byte[]> WriteResearchAdjustedMarketBarsAsync(
        IReadOnlyCollection<MarketBarEvidenceRow> rows,
        string normalizerVersion,
        CancellationToken cancellationToken = default) =>
        WriteMarketBarsAsync(
            rows,
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            normalizerVersion,
            cancellationToken);

    public Task<EvidenceParquetDocument<MarketBarEvidenceRow>> ReadMarketBarsAsTradedAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default) =>
        ReadMarketBarsAsync(
            content,
            EvidenceDatasetKind.MarketBarsAsTraded,
            cancellationToken);

    public Task<EvidenceParquetDocument<MarketBarEvidenceRow>> ReadResearchAdjustedMarketBarsAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default) =>
        ReadMarketBarsAsync(
            content,
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            cancellationToken);

    private Task<byte[]> WriteMarketBarsAsync(
        IReadOnlyCollection<MarketBarEvidenceRow> rows,
        EvidenceDatasetKind datasetKind,
        string normalizerVersion,
        CancellationToken cancellationToken) =>
        WriteAsync(
            ValidateAndOrder(
                rows,
                datasetKind,
                row => $"{row.SecurityId}\u001f{row.Timeframe}\u001f{row.BarStartUtc:O}",
                row => (row.SecurityId, row.Timeframe, row.BarStartUtc)),
            datasetKind,
            MarketBarParquetRow.SchemaFingerprint,
            MarketBarParquetRow.FromDomain,
            normalizerVersion,
            cancellationToken);

    private async Task<EvidenceParquetDocument<MarketBarEvidenceRow>> ReadMarketBarsAsync(
        ReadOnlyMemory<byte> content,
        EvidenceDatasetKind datasetKind,
        CancellationToken cancellationToken)
    {
        var result = await ReadAsync<MarketBarParquetRow>(
            content,
            datasetKind,
            MarketBarParquetRow.SchemaFingerprint,
            cancellationToken);
        var rows = MapRows(result.Rows, row => row.ToDomain());
        EnsureProvenanceMetadata(rows, result.Metadata);
        EnsureReadOrderingAndUniqueness(
            rows,
            row => $"{row.SecurityId}\u001f{row.Timeframe}\u001f{row.BarStartUtc:O}",
            row => (row.SecurityId, row.Timeframe, row.BarStartUtc));
        return new(result.DatasetKind, result.SchemaVersion, rows, result.Metadata);
    }

    public Task<byte[]> WriteNewsRevisionsAsync(
        IReadOnlyCollection<NewsRevisionEvidenceRow> rows,
        string normalizerVersion,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            ValidateAndOrder(
                rows,
                EvidenceDatasetKind.NewsRevisions,
                row => $"{row.Provider}\u001f{row.ProviderArticleId}\u001f{row.RevisionId}",
                row => (row.Provider, row.ProviderArticleId, row.ProviderUpdatedAtUtc, row.RevisionId)),
            EvidenceDatasetKind.NewsRevisions,
            NewsRevisionParquetRow.SchemaFingerprint,
            NewsRevisionParquetRow.FromDomain,
            normalizerVersion,
            cancellationToken);

    public async Task<EvidenceParquetDocument<NewsRevisionEvidenceRow>> ReadNewsRevisionsAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var result = await ReadAsync<NewsRevisionParquetRow>(
            content,
            EvidenceDatasetKind.NewsRevisions,
            NewsRevisionParquetRow.SchemaFingerprint,
            cancellationToken);
        var rows = MapRows(result.Rows, row => row.ToDomain());
        EnsureProvenanceMetadata(rows, result.Metadata);
        EnsureReadOrderingAndUniqueness(
            rows,
            row => $"{row.Provider}\u001f{row.ProviderArticleId}\u001f{row.RevisionId}",
            row => (row.Provider, row.ProviderArticleId, row.ProviderUpdatedAtUtc, row.RevisionId));
        return new(result.DatasetKind, result.SchemaVersion, rows, result.Metadata);
    }

    public Task<byte[]> WriteSentimentAssessmentsAsync(
        IReadOnlyCollection<SentimentAssessmentEvidenceRow> rows,
        string normalizerVersion,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            ValidateAndOrder(
                rows,
                EvidenceDatasetKind.SentimentAssessments,
                row => row.AssessmentId,
                row => (
                    row.NewsProvider,
                    row.ProviderArticleId,
                    row.NewsRevisionId,
                    row.AssessmentProvider,
                    row.AssessmentModel,
                    row.AssessmentModelVersion,
                    row.PromptOrStageVersion,
                    row.AssessmentId)),
            EvidenceDatasetKind.SentimentAssessments,
            SentimentAssessmentParquetRow.SchemaFingerprint,
            SentimentAssessmentParquetRow.FromDomain,
            normalizerVersion,
            cancellationToken);

    public async Task<EvidenceParquetDocument<SentimentAssessmentEvidenceRow>>
        ReadSentimentAssessmentsAsync(
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
    {
        var result = await ReadAsync<SentimentAssessmentParquetRow>(
            content,
            EvidenceDatasetKind.SentimentAssessments,
            SentimentAssessmentParquetRow.SchemaFingerprint,
            cancellationToken);
        var rows = MapRows(result.Rows, row => row.ToDomain());
        EnsureProvenanceMetadata(rows, result.Metadata);
        EnsureReadOrderingAndUniqueness(
            rows,
            row => row.AssessmentId,
            row => (
                row.NewsProvider,
                row.ProviderArticleId,
                row.NewsRevisionId,
                row.AssessmentProvider,
                row.AssessmentModel,
                row.AssessmentModelVersion,
                row.PromptOrStageVersion,
                row.AssessmentId));
        return new(result.DatasetKind, result.SchemaVersion, rows, result.Metadata);
    }

    public Task<byte[]> WriteClassifierGroundTruthAsync(
        IReadOnlyCollection<ClassifierGroundTruthEvidenceRow> rows,
        string normalizerVersion,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            ValidateAndOrder(
                rows,
                EvidenceDatasetKind.ClassifierGroundTruth,
                row => row.GroundTruthId,
                row => (
                    row.NewsProvider,
                    row.ProviderArticleId,
                    row.NewsRevisionId,
                    row.WorkItemId,
                    row.GroundTruthId)),
            EvidenceDatasetKind.ClassifierGroundTruth,
            ClassifierGroundTruthParquetRow.SchemaFingerprint,
            ClassifierGroundTruthParquetRow.FromDomain,
            normalizerVersion,
            cancellationToken);

    public async Task<EvidenceParquetDocument<ClassifierGroundTruthEvidenceRow>>
        ReadClassifierGroundTruthAsync(
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
    {
        var result = await ReadAsync<ClassifierGroundTruthParquetRow>(
            content,
            EvidenceDatasetKind.ClassifierGroundTruth,
            ClassifierGroundTruthParquetRow.SchemaFingerprint,
            cancellationToken);
        var rows = MapRows(result.Rows, row => row.ToDomain());
        EnsureProvenanceMetadata(rows, result.Metadata);
        EnsureReadOrderingAndUniqueness(
            rows,
            row => row.GroundTruthId,
            row => (
                row.NewsProvider,
                row.ProviderArticleId,
                row.NewsRevisionId,
                row.WorkItemId,
                row.GroundTruthId));
        return new(result.DatasetKind, result.SchemaVersion, rows, result.Metadata);
    }

    public Task<byte[]> WriteSecurityMasterSnapshotsAsync(
        IReadOnlyCollection<SecurityMasterSnapshotEvidenceRow> rows,
        string normalizerVersion,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            ValidateAndOrder(
                rows,
                EvidenceDatasetKind.SecurityMaster,
                row => $"{row.Provider}\u001f{row.SecurityId}\u001f{row.ObservedAtUtc:O}",
                row => (row.Provider, row.ObservedAtUtc, row.SecurityId)),
            EvidenceDatasetKind.SecurityMaster,
            SecurityMasterSnapshotParquetRow.SchemaFingerprint,
            SecurityMasterSnapshotParquetRow.FromDomain,
            normalizerVersion,
            cancellationToken);

    public async Task<EvidenceParquetDocument<SecurityMasterSnapshotEvidenceRow>>
        ReadSecurityMasterSnapshotsAsync(
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
    {
        var result = await ReadAsync<SecurityMasterSnapshotParquetRow>(
            content,
            EvidenceDatasetKind.SecurityMaster,
            SecurityMasterSnapshotParquetRow.SchemaFingerprint,
            cancellationToken);
        var rows = MapRows(result.Rows, row => row.ToDomain());
        EnsureProvenanceMetadata(rows, result.Metadata);
        EnsureReadOrderingAndUniqueness(
            rows,
            row => $"{row.Provider}\u001f{row.SecurityId}\u001f{row.ObservedAtUtc:O}",
            row => (row.Provider, row.ObservedAtUtc, row.SecurityId));
        return new(result.DatasetKind, result.SchemaVersion, rows, result.Metadata);
    }

    public Task<byte[]> WriteSymbolIntervalsAsync(
        IReadOnlyCollection<SymbolIntervalEvidenceRow> rows,
        string normalizerVersion,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            ValidateAndOrder(
                rows,
                EvidenceDatasetKind.SymbolIntervals,
                row => $"{row.Provider}\u001f{row.SecurityId}\u001f{row.Symbol}\u001f{row.ObservedAtUtc:O}",
                row => (row.Provider, row.ObservedAtUtc, row.Symbol, row.SecurityId)),
            EvidenceDatasetKind.SymbolIntervals,
            SymbolIntervalParquetRow.SchemaFingerprint,
            SymbolIntervalParquetRow.FromDomain,
            normalizerVersion,
            cancellationToken);

    public async Task<EvidenceParquetDocument<SymbolIntervalEvidenceRow>>
        ReadSymbolIntervalsAsync(
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
    {
        var result = await ReadAsync<SymbolIntervalParquetRow>(
            content,
            EvidenceDatasetKind.SymbolIntervals,
            SymbolIntervalParquetRow.SchemaFingerprint,
            cancellationToken);
        var rows = MapRows(result.Rows, row => row.ToDomain());
        EnsureProvenanceMetadata(rows, result.Metadata);
        EnsureReadOrderingAndUniqueness(
            rows,
            row => $"{row.Provider}\u001f{row.SecurityId}\u001f{row.Symbol}\u001f{row.ObservedAtUtc:O}",
            row => (row.Provider, row.ObservedAtUtc, row.Symbol, row.SecurityId));
        return new(result.DatasetKind, result.SchemaVersion, rows, result.Metadata);
    }

    public Task<byte[]> WriteCorporateActionsAsync(
        IReadOnlyCollection<CorporateActionEvidenceRow> rows,
        string normalizerVersion,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            ValidateAndOrder(
                rows,
                EvidenceDatasetKind.CorporateActions,
                row => $"{row.Provider}\u001f{row.ActionType}\u001f{row.ProviderActionId}",
                row => (row.Provider, row.ProcessDate, row.ActionType, row.ProviderActionId)),
            EvidenceDatasetKind.CorporateActions,
            CorporateActionParquetRow.SchemaFingerprint,
            CorporateActionParquetRow.FromDomain,
            normalizerVersion,
            cancellationToken);

    public async Task<EvidenceParquetDocument<CorporateActionEvidenceRow>>
        ReadCorporateActionsAsync(
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
    {
        var result = await ReadAsync<CorporateActionParquetRow>(
            content,
            EvidenceDatasetKind.CorporateActions,
            CorporateActionParquetRow.SchemaFingerprint,
            cancellationToken);
        var rows = MapRows(result.Rows, row => row.ToDomain());
        EnsureProvenanceMetadata(rows, result.Metadata);
        EnsureReadOrderingAndUniqueness(
            rows,
            row => $"{row.Provider}\u001f{row.ActionType}\u001f{row.ProviderActionId}",
            row => (row.Provider, row.ProcessDate, row.ActionType, row.ProviderActionId));
        return new(result.DatasetKind, result.SchemaVersion, rows, result.Metadata);
    }

    public Task<byte[]> WriteSecurityMasterAsync(
        IReadOnlyCollection<SecurityMasterEvidenceRow> rows,
        string normalizerVersion,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            ValidateAndOrder(
                rows,
                EvidenceDatasetKind.SecurityMaster,
                row => $"{row.Provider}\u001f{row.SecurityId}\u001f{row.ValidFromUtc:O}",
                row => (row.Provider, row.SecurityId, row.ValidFromUtc)),
            EvidenceDatasetKind.SecurityMaster,
            SecurityMasterParquetRow.SchemaFingerprint,
            SecurityMasterParquetRow.FromDomain,
            normalizerVersion,
            cancellationToken);

    public async Task<EvidenceParquetDocument<SecurityMasterEvidenceRow>> ReadSecurityMasterAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var result = await ReadAsync<SecurityMasterParquetRow>(
            content,
            EvidenceDatasetKind.SecurityMaster,
            SecurityMasterParquetRow.SchemaFingerprint,
            cancellationToken);
        var rows = MapRows(result.Rows, row => row.ToDomain());
        EnsureProvenanceMetadata(rows, result.Metadata);
        EnsureReadOrderingAndUniqueness(
            rows,
            row => $"{row.Provider}\u001f{row.SecurityId}\u001f{row.ValidFromUtc:O}",
            row => (row.Provider, row.SecurityId, row.ValidFromUtc));
        return new(result.DatasetKind, result.SchemaVersion, rows, result.Metadata);
    }

    public Task<byte[]> WriteUniverseMembershipAsync(
        IReadOnlyCollection<UniverseMembershipEvidenceRow> rows,
        string normalizerVersion,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            ValidateAndOrder(
                rows,
                EvidenceDatasetKind.UniverseMembership,
                row => $"{row.Provider}\u001f{row.UniverseId}\u001f{row.SnapshotId}\u001f{row.SecurityId}",
                row => (row.Provider, row.UniverseId, row.AsOfUtc, row.SnapshotId, row.SecurityId)),
            EvidenceDatasetKind.UniverseMembership,
            UniverseMembershipParquetRow.SchemaFingerprint,
            UniverseMembershipParquetRow.FromDomain,
            normalizerVersion,
            cancellationToken);

    public async Task<EvidenceParquetDocument<UniverseMembershipEvidenceRow>> ReadUniverseMembershipAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var result = await ReadAsync<UniverseMembershipParquetRow>(
            content,
            EvidenceDatasetKind.UniverseMembership,
            UniverseMembershipParquetRow.SchemaFingerprint,
            cancellationToken);
        var rows = MapRows(result.Rows, row => row.ToDomain());
        EnsureProvenanceMetadata(rows, result.Metadata);
        EnsureReadOrderingAndUniqueness(
            rows,
            row => $"{row.Provider}\u001f{row.UniverseId}\u001f{row.SnapshotId}\u001f{row.SecurityId}",
            row => (row.Provider, row.UniverseId, row.AsOfUtc, row.SnapshotId, row.SecurityId));
        return new(result.DatasetKind, result.SchemaVersion, rows, result.Metadata);
    }

    public Task<byte[]> WriteSipQuotesAsync(
        IReadOnlyCollection<SipQuoteEvidenceRow> rows,
        string normalizerVersion,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            ValidateAndOrder(
                rows,
                EvidenceDatasetKind.SipQuotes,
                row => $"{row.SecurityId}\u001f{row.QuoteTimestampUtc:O}",
                row => (row.SecurityId, row.QuoteTimestampUtc)),
            EvidenceDatasetKind.SipQuotes,
            SipQuoteParquetRow.SchemaFingerprint,
            SipQuoteParquetRow.FromDomain,
            normalizerVersion,
            cancellationToken);

    public async Task<EvidenceParquetDocument<SipQuoteEvidenceRow>> ReadSipQuotesAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var result = await ReadAsync<SipQuoteParquetRow>(
            content,
            EvidenceDatasetKind.SipQuotes,
            SipQuoteParquetRow.SchemaFingerprint,
            cancellationToken);
        var rows = MapRows(result.Rows, row => row.ToDomain());
        EnsureProvenanceMetadata(rows, result.Metadata);
        EnsureReadOrderingAndUniqueness(
            rows,
            row => $"{row.SecurityId}\u001f{row.QuoteTimestampUtc:O}",
            row => (row.SecurityId, row.QuoteTimestampUtc));
        return new(result.DatasetKind, result.SchemaVersion, rows, result.Metadata);
    }

    public Task<byte[]> WriteExchangeSessionsAsync(
        IReadOnlyCollection<ExchangeSessionEvidenceRow> rows,
        string normalizerVersion,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            ValidateAndOrder(
                rows,
                EvidenceDatasetKind.ExchangeSessions,
                row => $"{row.Exchange}\u001f{row.TradeDate:yyyy-MM-dd}",
                row => (row.Exchange, row.TradeDate)),
            EvidenceDatasetKind.ExchangeSessions,
            ExchangeSessionParquetRow.SchemaFingerprint,
            ExchangeSessionParquetRow.FromDomain,
            normalizerVersion,
            cancellationToken);

    public async Task<EvidenceParquetDocument<ExchangeSessionEvidenceRow>>
        ReadExchangeSessionsAsync(
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
    {
        var result = await ReadAsync<ExchangeSessionParquetRow>(
            content,
            EvidenceDatasetKind.ExchangeSessions,
            ExchangeSessionParquetRow.SchemaFingerprint,
            cancellationToken);
        var rows = MapRows(result.Rows, row => row.ToDomain());
        EnsureProvenanceMetadata(rows, result.Metadata);
        EnsureReadOrderingAndUniqueness(
            rows,
            row => $"{row.Exchange}\u001f{row.TradeDate:yyyy-MM-dd}",
            row => (row.Exchange, row.TradeDate));
        return new(result.DatasetKind, result.SchemaVersion, rows, result.Metadata);
    }

    private async Task<byte[]> WriteAsync<TDomain, TStorage>(
        IReadOnlyList<TDomain> rows,
        EvidenceDatasetKind datasetKind,
        string schemaFingerprint,
        Func<TDomain, TStorage> map,
        string normalizerVersion,
        CancellationToken cancellationToken)
        where TDomain : INormalizedEvidenceRow
        where TStorage : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var schemaVersion = rows[0].SchemaVersion;
        var metadata = CreateMetadata(
            datasetKind,
            schemaVersion,
            schemaFingerprint,
            rows.Count,
            rows[0],
            normalizerVersion);
        var storageRows = rows.Select(map).ToArray();
        await using var stream = new MemoryStream();
        await ParquetSerializer.SerializeAsync(
            storageRows,
            stream,
            new ParquetOptions
            {
                CompressionMethod = CompressionMethod.None
            },
            metadata,
            cancellationToken);
        if (stream.Length > options.MaximumPartitionBytes)
        {
            throw new InvalidDataException(
                $"Encoded evidence partition is {stream.Length} bytes; maximum is {options.MaximumPartitionBytes}.");
        }

        return stream.ToArray();
    }

    private async Task<EvidenceParquetDocument<TStorage>> ReadAsync<TStorage>(
        ReadOnlyMemory<byte> content,
        EvidenceDatasetKind expectedKind,
        string expectedSchemaFingerprint,
        CancellationToken cancellationToken)
        where TStorage : class, new()
    {
        if (content.IsEmpty)
        {
            throw new InvalidDataException("Parquet evidence content is empty.");
        }

        if (content.Length > options.MaximumPartitionBytes)
        {
            throw new InvalidDataException(
                $"Parquet evidence content is {content.Length} bytes; maximum is {options.MaximumPartitionBytes}.");
        }

        await using var stream = new MemoryStream(content.ToArray(), writable: false);
        DeserializationResult<TStorage> decoded;
        try
        {
            decoded = await ParquetSerializer.DeserializeAsync<TStorage>(
                stream,
                new ParquetOptions(),
                rowGroupIndex: null,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException &&
            exception is not InvalidDataException)
        {
            throw new InvalidDataException("Content is not a valid TradingFlow evidence Parquet partition.", exception);
        }

        var metadata = new ReadOnlyDictionary<string, string>(
            new SortedDictionary<string, string>(
                (decoded.CustomMetadata ?? new Dictionary<string, string>())
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal));
        var schemaVersion = ValidateMetadata(
            metadata,
            expectedKind,
            expectedSchemaFingerprint,
            decoded.Data.Count);
        var actualSchemaFingerprint = ComputeSchemaFingerprint(decoded.Schema);
        if (!actualSchemaFingerprint.Equals(expectedSchemaFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Parquet schema fingerprint '{actualSchemaFingerprint}' does not match expected '{expectedSchemaFingerprint}'.");
        }

        if (decoded.Data.Count == 0)
        {
            throw new InvalidDataException("Evidence partitions cannot be empty.");
        }

        if (decoded.Data.Count > options.MaximumRowsPerPartition)
        {
            throw new InvalidDataException(
                $"Evidence partition has {decoded.Data.Count} rows; maximum is {options.MaximumRowsPerPartition}.");
        }

        return new(expectedKind, schemaVersion, decoded.Data.ToArray(), metadata);
    }

    private IReadOnlyList<T> ValidateAndOrder<T, TOrder>(
        IReadOnlyCollection<T> rows,
        EvidenceDatasetKind datasetKind,
        Func<T, string> identity,
        Func<T, TOrder> order)
        where T : INormalizedEvidenceRow
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            throw new ArgumentException("Evidence partitions cannot be empty.", nameof(rows));
        }

        if (rows.Count > options.MaximumRowsPerPartition)
        {
            throw new ArgumentException(
                $"Evidence partition has {rows.Count} rows; maximum is {options.MaximumRowsPerPartition}.",
                nameof(rows));
        }

        if (rows.Any(row => row is null))
        {
            throw new ArgumentException("Evidence rows cannot contain null.", nameof(rows));
        }

        var versions = rows.Select(row => row.SchemaVersion).Distinct().ToArray();
        if (versions.Length != 1)
        {
            throw new ArgumentException(
                $"{datasetKind} rows must have one schema version.",
                nameof(rows));
        }

        var provenanceValues = rows
            .Select(row => $"{row.RunId}\u001f{row.ConfigHash}\u001f{row.CodeVersion}\u001f{row.DataFeed}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (provenanceValues.Length != 1)
        {
            throw new ArgumentException(
                $"{datasetKind} rows must have one run, config, code version, and data feed.",
                nameof(rows));
        }

        var duplicate = rows
            .GroupBy(identity, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"{datasetKind} contains duplicate row identity '{duplicate.Key}'.",
                nameof(rows));
        }

        return rows.OrderBy(order).ToArray();
    }

    private static void EnsureReadOrderingAndUniqueness<T, TOrder>(
        IReadOnlyList<T> rows,
        Func<T, string> identity,
        Func<T, TOrder> order)
    {
        var duplicate = rows
            .GroupBy(identity, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"Parquet evidence contains duplicate row identity '{duplicate.Key}'.");
        }

        var ordered = rows.OrderBy(order).ToArray();
        if (!rows.SequenceEqual(ordered))
        {
            throw new InvalidDataException(
                "Parquet evidence rows are not in canonical deterministic order.");
        }
    }

    private static TDomain[] MapRows<TStorage, TDomain>(
        IReadOnlyList<TStorage> rows,
        Func<TStorage, TDomain> map)
    {
        try
        {
            return rows.Select(map).ToArray();
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException or
            OverflowException or
            JsonException)
        {
            throw new InvalidDataException(
                "Parquet evidence contains malformed normalized row values.",
                exception);
        }
    }

    private static IReadOnlyDictionary<string, string> CreateMetadata(
        EvidenceDatasetKind kind,
        int schemaVersion,
        string schemaFingerprint,
        int rowCount,
        INormalizedEvidenceRow provenance,
        string normalizerVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizerVersion);
        normalizerVersion = normalizerVersion.Trim();
        return
        new ReadOnlyDictionary<string, string>(
            new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["tradingflow.code_version"] = provenance.CodeVersion,
                ["tradingflow.config_hash"] = provenance.ConfigHash,
                ["tradingflow.data_feed"] = provenance.DataFeed,
                ["tradingflow.dataset_kind"] = kind.ToString(),
                ["tradingflow.format_version"] = FormatVersion,
                ["tradingflow.normalizer_version"] = normalizerVersion,
                ["tradingflow.price_scale"] = EvidenceFixedDecimal.PriceScale.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["tradingflow.row_count"] = rowCount.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["tradingflow.run_id"] = provenance.RunId,
                ["tradingflow.schema_fingerprint"] = schemaFingerprint,
                ["tradingflow.schema_version"] = schemaVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["tradingflow.timestamp_encoding"] = TimestampEncoding
            });
    }

    private static int ValidateMetadata(
        IReadOnlyDictionary<string, string> metadata,
        EvidenceDatasetKind expectedKind,
        string expectedSchemaFingerprint,
        int actualRowCount)
    {
        RequireMetadata(metadata, "tradingflow.format_version", FormatVersion);
        RequireMetadata(metadata, "tradingflow.dataset_kind", expectedKind.ToString());
        RequireMetadata(metadata, "tradingflow.timestamp_encoding", TimestampEncoding);
        RequireMetadata(
            metadata,
            "tradingflow.price_scale",
            EvidenceFixedDecimal.PriceScale.ToString(System.Globalization.CultureInfo.InvariantCulture));
        RequireMetadata(metadata, "tradingflow.schema_fingerprint", expectedSchemaFingerprint);
        RequireNonEmptyMetadata(metadata, "tradingflow.run_id");
        RequireNonEmptyMetadata(metadata, "tradingflow.code_version");
        RequireNonEmptyMetadata(metadata, "tradingflow.data_feed");
        RequireNonEmptyMetadata(metadata, "tradingflow.normalizer_version");
        if (!metadata.TryGetValue("tradingflow.config_hash", out var configHash) ||
            configHash.Length != 64 ||
            configHash.Any(character => !Uri.IsHexDigit(character)) ||
            !configHash.Equals(configHash.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Parquet evidence config hash metadata is missing or invalid.");
        }

        if (!metadata.TryGetValue("tradingflow.schema_version", out var schemaText) ||
            !Int32.TryParse(
                schemaText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var schemaVersion) ||
            schemaVersion <= 0)
        {
            throw new InvalidDataException("Parquet evidence schema version is missing or invalid.");
        }

        if (!metadata.TryGetValue("tradingflow.row_count", out var rowCountText) ||
            !Int32.TryParse(
                rowCountText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var declaredRowCount) ||
            declaredRowCount != actualRowCount)
        {
            throw new InvalidDataException(
                $"Parquet evidence row count metadata does not match actual row count {actualRowCount}.");
        }

        return schemaVersion;
    }

    private static void RequireMetadata(
        IReadOnlyDictionary<string, string> metadata,
        string key,
        string expectedValue)
    {
        if (!metadata.TryGetValue(key, out var value) ||
            !value.Equals(expectedValue, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Parquet evidence metadata '{key}' must equal '{expectedValue}'.");
        }
    }

    private static void RequireNonEmptyMetadata(
        IReadOnlyDictionary<string, string> metadata,
        string key)
    {
        if (!metadata.TryGetValue(key, out var value) || String.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"Parquet evidence metadata '{key}' is required.");
        }
    }

    private static void EnsureProvenanceMetadata<T>(
        IReadOnlyList<T> rows,
        IReadOnlyDictionary<string, string> metadata)
        where T : INormalizedEvidenceRow
    {
        if (rows.Any(row =>
                !row.RunId.Equals(metadata["tradingflow.run_id"], StringComparison.Ordinal) ||
                !row.ConfigHash.Equals(metadata["tradingflow.config_hash"], StringComparison.Ordinal) ||
                !row.CodeVersion.Equals(metadata["tradingflow.code_version"], StringComparison.Ordinal) ||
                !row.DataFeed.Equals(metadata["tradingflow.data_feed"], StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "Parquet row provenance does not match partition provenance metadata.");
        }
    }

    internal static string ComputeSchemaFingerprint(ParquetSchema schema) =>
        ComputeSchemaFingerprint(
            schema.GetDataFields().Select(field =>
                $"{field.Name}|{field.ClrType.FullName}|{field.IsNullable}"));

    internal static string ComputeSchemaFingerprint(IEnumerable<string> fields) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(String.Join("\n", fields))))
        .ToLowerInvariant();

    private static class StorageValue
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false
        };

        public static long ToUnixMicroseconds(DateTimeOffset value)
        {
            if (value.Offset != TimeSpan.Zero || value == default)
            {
                throw new InvalidDataException("Evidence timestamp must be UTC.");
            }

            if (value.Ticks % TimeSpan.TicksPerMicrosecond != 0)
            {
                throw new InvalidDataException(
                    $"Timestamp '{value:O}' has sub-microsecond precision that Parquet cannot represent exactly.");
            }

            return checked((value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) /
                           TimeSpan.TicksPerMicrosecond);
        }

        public static DateTimeOffset FromUnixMicroseconds(long value)
        {
            try
            {
                return new DateTimeOffset(
                    checked(DateTimeOffset.UnixEpoch.UtcTicks +
                            value * TimeSpan.TicksPerMicrosecond),
                    TimeSpan.Zero);
            }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
            {
                throw new InvalidDataException($"Unix microsecond timestamp '{value}' is outside the supported range.", exception);
            }
        }

        public static string SerializeSources(IReadOnlyList<EvidenceRowSourceAddress> sources) =>
            JsonSerializer.Serialize(
                sources.Select(source => new SourceStorage
                {
                    ObservationId = source.ObservationId,
                    ObservationSha256 = source.ObservationSha256
                }),
                JsonOptions);

        public static IReadOnlyList<EvidenceRowSourceAddress> DeserializeSources(string value)
        {
            try
            {
                var sources = JsonSerializer.Deserialize<SourceStorage[]>(value, JsonOptions)
                    ?? throw new InvalidDataException("Source lineage JSON is null.");
                return sources.Select(source => new EvidenceRowSourceAddress(
                    source.ObservationId,
                    source.ObservationSha256)).ToArray();
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                throw new InvalidDataException("Source lineage JSON is malformed.", exception);
            }
        }

        public static string SerializeStrings(IReadOnlyList<string> values) =>
            JsonSerializer.Serialize(values, JsonOptions);

        public static IReadOnlyList<string> DeserializeStrings(string value)
        {
            try
            {
                return JsonSerializer.Deserialize<string[]>(value, JsonOptions)
                    ?? throw new InvalidDataException("String-array JSON is null.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("String-array JSON is malformed.", exception);
            }
        }

        public static string SerializeDictionary(IReadOnlyDictionary<string, string> values) =>
            JsonSerializer.Serialize(
                values
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                JsonOptions);

        public static IReadOnlyDictionary<string, string> DeserializeDictionary(string value)
        {
            try
            {
                var result = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    value,
                    JsonOptions)
                    ?? throw new InvalidDataException("Dictionary JSON is null.");
                return new ReadOnlyDictionary<string, string>(
                    result
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value,
                            StringComparer.Ordinal));
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                throw new InvalidDataException("Dictionary JSON is malformed.", exception);
            }
        }

        private sealed class SourceStorage
        {
            public string ObservationId { get; set; } = String.Empty;
            public string ObservationSha256 { get; set; } = String.Empty;
        }
    }

    private sealed class MarketBarParquetRow
    {
        public static readonly string SchemaFingerprint = Fingerprint(
            "schema_version|System.Int32|False",
            "run_id|System.String|False",
            "config_hash|System.String|False",
            "code_version|System.String|False",
            "data_feed|System.String|False",
            "security_id|System.String|False",
            "symbol|System.String|False",
            "bar_start_utc_us|System.Int64|False",
            "bar_end_utc_us|System.Int64|False",
            "timeframe|System.String|False",
            "price_scale|System.Int32|False",
            "open_price_units|System.Int64|False",
            "high_price_units|System.Int64|False",
            "low_price_units|System.Int64|False",
            "close_price_units|System.Int64|False",
            "vwap_price_units|System.Int64|True",
            "volume|System.Int64|False",
            "trade_count|System.Int64|True",
            "adjustment|System.String|False",
            "currency|System.String|False",
            "provider_timestamp_utc_us|System.Int64|False",
            "received_at_utc_us|System.Int64|False",
            "sources_json|System.String|False");

        [JsonPropertyOrder(0), JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyOrder(1), JsonPropertyName("run_id"), ParquetRequired] public string RunId { get; set; } = String.Empty;
        [JsonPropertyOrder(2), JsonPropertyName("config_hash"), ParquetRequired] public string ConfigHash { get; set; } = String.Empty;
        [JsonPropertyOrder(3), JsonPropertyName("code_version"), ParquetRequired] public string CodeVersion { get; set; } = String.Empty;
        [JsonPropertyOrder(4), JsonPropertyName("data_feed"), ParquetRequired] public string DataFeed { get; set; } = String.Empty;
        [JsonPropertyOrder(5), JsonPropertyName("security_id"), ParquetRequired] public string SecurityId { get; set; } = String.Empty;
        [JsonPropertyOrder(6), JsonPropertyName("symbol"), ParquetRequired] public string Symbol { get; set; } = String.Empty;
        [JsonPropertyOrder(7), JsonPropertyName("bar_start_utc_us")] public long BarStartUtcUs { get; set; }
        [JsonPropertyOrder(8), JsonPropertyName("bar_end_utc_us")] public long BarEndUtcUs { get; set; }
        [JsonPropertyOrder(9), JsonPropertyName("timeframe"), ParquetRequired] public string Timeframe { get; set; } = String.Empty;
        [JsonPropertyOrder(10), JsonPropertyName("price_scale")] public int PriceScale { get; set; }
        [JsonPropertyOrder(11), JsonPropertyName("open_price_units")] public long OpenPriceUnits { get; set; }
        [JsonPropertyOrder(12), JsonPropertyName("high_price_units")] public long HighPriceUnits { get; set; }
        [JsonPropertyOrder(13), JsonPropertyName("low_price_units")] public long LowPriceUnits { get; set; }
        [JsonPropertyOrder(14), JsonPropertyName("close_price_units")] public long ClosePriceUnits { get; set; }
        [JsonPropertyOrder(15), JsonPropertyName("vwap_price_units")] public long? VwapPriceUnits { get; set; }
        [JsonPropertyOrder(16), JsonPropertyName("volume")] public long Volume { get; set; }
        [JsonPropertyOrder(17), JsonPropertyName("trade_count")] public long? TradeCount { get; set; }
        [JsonPropertyOrder(18), JsonPropertyName("adjustment"), ParquetRequired] public string Adjustment { get; set; } = String.Empty;
        [JsonPropertyOrder(19), JsonPropertyName("currency"), ParquetRequired] public string Currency { get; set; } = String.Empty;
        [JsonPropertyOrder(20), JsonPropertyName("provider_timestamp_utc_us")] public long ProviderTimestampUtcUs { get; set; }
        [JsonPropertyOrder(21), JsonPropertyName("received_at_utc_us")] public long ReceivedAtUtcUs { get; set; }
        [JsonPropertyOrder(22), JsonPropertyName("sources_json"), ParquetRequired] public string SourcesJson { get; set; } = String.Empty;

        public static MarketBarParquetRow FromDomain(MarketBarEvidenceRow row) => new()
        {
            SchemaVersion = row.SchemaVersion,
            RunId = row.RunId,
            ConfigHash = row.ConfigHash,
            CodeVersion = row.CodeVersion,
            DataFeed = row.DataFeed,
            SecurityId = row.SecurityId,
            Symbol = row.Symbol,
            BarStartUtcUs = StorageValue.ToUnixMicroseconds(row.BarStartUtc),
            BarEndUtcUs = StorageValue.ToUnixMicroseconds(row.BarEndUtc),
            Timeframe = row.Timeframe,
            PriceScale = EvidenceFixedDecimal.PriceScale,
            OpenPriceUnits = row.OpenPriceUnits,
            HighPriceUnits = row.HighPriceUnits,
            LowPriceUnits = row.LowPriceUnits,
            ClosePriceUnits = row.ClosePriceUnits,
            VwapPriceUnits = row.VwapPriceUnits,
            Volume = row.Volume,
            TradeCount = row.TradeCount,
            Adjustment = row.Adjustment,
            Currency = row.Currency,
            ProviderTimestampUtcUs = StorageValue.ToUnixMicroseconds(row.ProviderTimestampUtc),
            ReceivedAtUtcUs = StorageValue.ToUnixMicroseconds(row.ReceivedAtUtc),
            SourcesJson = StorageValue.SerializeSources(row.Sources)
        };

        public MarketBarEvidenceRow ToDomain()
        {
            EnsurePriceScale(PriceScale);
            return new(
                SchemaVersion,
                RunId,
                ConfigHash,
                CodeVersion,
                DataFeed,
                SecurityId,
                Symbol,
                StorageValue.FromUnixMicroseconds(BarStartUtcUs),
                StorageValue.FromUnixMicroseconds(BarEndUtcUs),
                Timeframe,
                OpenPriceUnits,
                HighPriceUnits,
                LowPriceUnits,
                ClosePriceUnits,
                VwapPriceUnits,
                Volume,
                TradeCount,
                Adjustment,
                Currency,
                StorageValue.FromUnixMicroseconds(ProviderTimestampUtcUs),
                StorageValue.FromUnixMicroseconds(ReceivedAtUtcUs),
                StorageValue.DeserializeSources(SourcesJson));
        }
    }

    private sealed class NewsRevisionParquetRow
    {
        public static readonly string SchemaFingerprint = Fingerprint(
            "schema_version|System.Int32|False",
            "run_id|System.String|False",
            "config_hash|System.String|False",
            "code_version|System.String|False",
            "data_feed|System.String|False",
            "availability_evidence|System.String|False",
            "provider|System.String|False",
            "provider_article_id|System.String|False",
            "revision_id|System.String|False",
            "headline|System.String|False",
            "summary|System.String|True",
            "article_url|System.String|False",
            "symbols_json|System.String|False",
            "categories_json|System.String|False",
            "published_at_utc_us|System.Int64|False",
            "provider_created_at_utc_us|System.Int64|False",
            "provider_updated_at_utc_us|System.Int64|False",
            "received_at_utc_us|System.Int64|False",
            "sources_json|System.String|False");

        [JsonPropertyOrder(0), JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyOrder(1), JsonPropertyName("run_id"), ParquetRequired] public string RunId { get; set; } = String.Empty;
        [JsonPropertyOrder(2), JsonPropertyName("config_hash"), ParquetRequired] public string ConfigHash { get; set; } = String.Empty;
        [JsonPropertyOrder(3), JsonPropertyName("code_version"), ParquetRequired] public string CodeVersion { get; set; } = String.Empty;
        [JsonPropertyOrder(4), JsonPropertyName("data_feed"), ParquetRequired] public string DataFeed { get; set; } = String.Empty;
        [JsonPropertyOrder(5), JsonPropertyName("availability_evidence"), ParquetRequired] public string AvailabilityEvidence { get; set; } = String.Empty;
        [JsonPropertyOrder(6), JsonPropertyName("provider"), ParquetRequired] public string Provider { get; set; } = String.Empty;
        [JsonPropertyOrder(7), JsonPropertyName("provider_article_id"), ParquetRequired] public string ProviderArticleId { get; set; } = String.Empty;
        [JsonPropertyOrder(8), JsonPropertyName("revision_id"), ParquetRequired] public string RevisionId { get; set; } = String.Empty;
        [JsonPropertyOrder(9), JsonPropertyName("headline"), ParquetRequired] public string Headline { get; set; } = String.Empty;
        [JsonPropertyOrder(10), JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyOrder(11), JsonPropertyName("article_url"), ParquetRequired] public string ArticleUrl { get; set; } = String.Empty;
        [JsonPropertyOrder(12), JsonPropertyName("symbols_json"), ParquetRequired] public string SymbolsJson { get; set; } = String.Empty;
        [JsonPropertyOrder(13), JsonPropertyName("categories_json"), ParquetRequired] public string CategoriesJson { get; set; } = String.Empty;
        [JsonPropertyOrder(14), JsonPropertyName("published_at_utc_us")] public long PublishedAtUtcUs { get; set; }
        [JsonPropertyOrder(15), JsonPropertyName("provider_created_at_utc_us")] public long ProviderCreatedAtUtcUs { get; set; }
        [JsonPropertyOrder(16), JsonPropertyName("provider_updated_at_utc_us")] public long ProviderUpdatedAtUtcUs { get; set; }
        [JsonPropertyOrder(17), JsonPropertyName("received_at_utc_us")] public long ReceivedAtUtcUs { get; set; }
        [JsonPropertyOrder(18), JsonPropertyName("sources_json"), ParquetRequired] public string SourcesJson { get; set; } = String.Empty;

        public static NewsRevisionParquetRow FromDomain(NewsRevisionEvidenceRow row) => new()
        {
            SchemaVersion = row.SchemaVersion,
            RunId = row.RunId,
            ConfigHash = row.ConfigHash,
            CodeVersion = row.CodeVersion,
            DataFeed = row.DataFeed,
            AvailabilityEvidence = ToAvailabilityEvidence(row.AvailabilityEvidence),
            Provider = row.Provider,
            ProviderArticleId = row.ProviderArticleId,
            RevisionId = row.RevisionId,
            Headline = row.Headline,
            Summary = row.Summary,
            ArticleUrl = row.ArticleUrl,
            SymbolsJson = StorageValue.SerializeStrings(row.Symbols),
            CategoriesJson = StorageValue.SerializeStrings(row.Categories),
            PublishedAtUtcUs = StorageValue.ToUnixMicroseconds(row.PublishedAtUtc),
            ProviderCreatedAtUtcUs = StorageValue.ToUnixMicroseconds(row.ProviderCreatedAtUtc),
            ProviderUpdatedAtUtcUs = StorageValue.ToUnixMicroseconds(row.ProviderUpdatedAtUtc),
            ReceivedAtUtcUs = StorageValue.ToUnixMicroseconds(row.ReceivedAtUtc),
            SourcesJson = StorageValue.SerializeSources(row.Sources)
        };

        public NewsRevisionEvidenceRow ToDomain() => new(
            SchemaVersion,
            RunId,
            ConfigHash,
            CodeVersion,
            DataFeed,
            FromAvailabilityEvidence(AvailabilityEvidence),
            Provider,
            ProviderArticleId,
            RevisionId,
            Headline,
            Summary,
            ArticleUrl,
            StorageValue.DeserializeStrings(SymbolsJson),
            StorageValue.DeserializeStrings(CategoriesJson),
            StorageValue.FromUnixMicroseconds(PublishedAtUtcUs),
            StorageValue.FromUnixMicroseconds(ProviderCreatedAtUtcUs),
            StorageValue.FromUnixMicroseconds(ProviderUpdatedAtUtcUs),
            StorageValue.FromUnixMicroseconds(ReceivedAtUtcUs),
            StorageValue.DeserializeSources(SourcesJson));
    }

    private sealed class SentimentAssessmentParquetRow
    {
        public static readonly string SchemaFingerprint = Fingerprint(
            "schema_version|System.Int32|False",
            "run_id|System.String|False",
            "config_hash|System.String|False",
            "code_version|System.String|False",
            "data_feed|System.String|False",
            "assessment_id|System.String|False",
            "news_provider|System.String|False",
            "provider_article_id|System.String|False",
            "news_revision_id|System.String|False",
            "input_content|System.String|False",
            "input_content_sha256|System.String|False",
            "assessment_provider|System.String|False",
            "assessment_model|System.String|False",
            "assessment_model_version|System.String|False",
            "model_artifact_id|System.String|False",
            "model_artifact_sha256|System.String|False",
            "model_training_data_cutoff_utc_us|System.Int64|False",
            "prompt_or_stage_version|System.String|False",
            "score_scale|System.Int32|False",
            "score_units|System.Int64|False",
            "assessed_at_utc_us|System.Int64|False",
            "observed_at_utc_us|System.Int64|False",
            "sources_json|System.String|False");

        [JsonPropertyOrder(0), JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyOrder(1), JsonPropertyName("run_id"), ParquetRequired] public string RunId { get; set; } = String.Empty;
        [JsonPropertyOrder(2), JsonPropertyName("config_hash"), ParquetRequired] public string ConfigHash { get; set; } = String.Empty;
        [JsonPropertyOrder(3), JsonPropertyName("code_version"), ParquetRequired] public string CodeVersion { get; set; } = String.Empty;
        [JsonPropertyOrder(4), JsonPropertyName("data_feed"), ParquetRequired] public string DataFeed { get; set; } = String.Empty;
        [JsonPropertyOrder(5), JsonPropertyName("assessment_id"), ParquetRequired] public string AssessmentId { get; set; } = String.Empty;
        [JsonPropertyOrder(6), JsonPropertyName("news_provider"), ParquetRequired] public string NewsProvider { get; set; } = String.Empty;
        [JsonPropertyOrder(7), JsonPropertyName("provider_article_id"), ParquetRequired] public string ProviderArticleId { get; set; } = String.Empty;
        [JsonPropertyOrder(8), JsonPropertyName("news_revision_id"), ParquetRequired] public string NewsRevisionId { get; set; } = String.Empty;
        [JsonPropertyOrder(9), JsonPropertyName("input_content"), ParquetRequired] public string InputContent { get; set; } = String.Empty;
        [JsonPropertyOrder(10), JsonPropertyName("input_content_sha256"), ParquetRequired] public string InputContentSha256 { get; set; } = String.Empty;
        [JsonPropertyOrder(11), JsonPropertyName("assessment_provider"), ParquetRequired] public string AssessmentProvider { get; set; } = String.Empty;
        [JsonPropertyOrder(12), JsonPropertyName("assessment_model"), ParquetRequired] public string AssessmentModel { get; set; } = String.Empty;
        [JsonPropertyOrder(13), JsonPropertyName("assessment_model_version"), ParquetRequired] public string AssessmentModelVersion { get; set; } = String.Empty;
        [JsonPropertyOrder(14), JsonPropertyName("model_artifact_id"), ParquetRequired] public string ModelArtifactId { get; set; } = String.Empty;
        [JsonPropertyOrder(15), JsonPropertyName("model_artifact_sha256"), ParquetRequired] public string ModelArtifactSha256 { get; set; } = String.Empty;
        [JsonPropertyOrder(16), JsonPropertyName("model_training_data_cutoff_utc_us")] public long ModelTrainingDataCutoffUtcUs { get; set; }
        [JsonPropertyOrder(17), JsonPropertyName("prompt_or_stage_version"), ParquetRequired] public string PromptOrStageVersion { get; set; } = String.Empty;
        [JsonPropertyOrder(18), JsonPropertyName("score_scale")] public int ScoreScale { get; set; }
        [JsonPropertyOrder(19), JsonPropertyName("score_units")] public long ScoreUnits { get; set; }
        [JsonPropertyOrder(20), JsonPropertyName("assessed_at_utc_us")] public long AssessedAtUtcUs { get; set; }
        [JsonPropertyOrder(21), JsonPropertyName("observed_at_utc_us")] public long ObservedAtUtcUs { get; set; }
        [JsonPropertyOrder(22), JsonPropertyName("sources_json"), ParquetRequired] public string SourcesJson { get; set; } = String.Empty;

        public static SentimentAssessmentParquetRow FromDomain(
            SentimentAssessmentEvidenceRow row) => new()
        {
            SchemaVersion = row.SchemaVersion,
            RunId = row.RunId,
            ConfigHash = row.ConfigHash,
            CodeVersion = row.CodeVersion,
            DataFeed = row.DataFeed,
            AssessmentId = row.AssessmentId,
            NewsProvider = row.NewsProvider,
            ProviderArticleId = row.ProviderArticleId,
            NewsRevisionId = row.NewsRevisionId,
            InputContent = row.InputContent,
            InputContentSha256 = row.InputContentSha256,
            AssessmentProvider = row.AssessmentProvider,
            AssessmentModel = row.AssessmentModel,
            AssessmentModelVersion = row.AssessmentModelVersion,
            ModelArtifactId = row.ModelArtifactId,
            ModelArtifactSha256 = row.ModelArtifactSha256,
            ModelTrainingDataCutoffUtcUs =
                StorageValue.ToUnixMicroseconds(row.ModelTrainingDataCutoffUtc),
            PromptOrStageVersion = row.PromptOrStageVersion,
            ScoreScale = SentimentAssessmentEvidenceRow.ScoreScale,
            ScoreUnits = row.ScoreUnits,
            AssessedAtUtcUs = StorageValue.ToUnixMicroseconds(row.AssessedAtUtc),
            ObservedAtUtcUs = StorageValue.ToUnixMicroseconds(row.ObservedAtUtc),
            SourcesJson = StorageValue.SerializeSources(row.Sources)
        };

        public SentimentAssessmentEvidenceRow ToDomain()
        {
            if (ScoreScale != SentimentAssessmentEvidenceRow.ScoreScale)
            {
                throw new InvalidDataException(
                    $"Sentiment score scale '{ScoreScale}' does not match required scale " +
                    $"'{SentimentAssessmentEvidenceRow.ScoreScale}'.");
            }

            return new(
                SchemaVersion,
                RunId,
                ConfigHash,
                CodeVersion,
                DataFeed,
                AssessmentId,
                NewsProvider,
                ProviderArticleId,
                NewsRevisionId,
                InputContent,
                InputContentSha256,
                AssessmentProvider,
                AssessmentModel,
                AssessmentModelVersion,
                ModelArtifactId,
                ModelArtifactSha256,
                StorageValue.FromUnixMicroseconds(ModelTrainingDataCutoffUtcUs),
                PromptOrStageVersion,
                ScoreUnits / (decimal)SentimentAssessmentEvidenceRow.ScoreScaleFactor,
                StorageValue.FromUnixMicroseconds(AssessedAtUtcUs),
                StorageValue.FromUnixMicroseconds(ObservedAtUtcUs),
                StorageValue.DeserializeSources(SourcesJson));
        }
    }

    private sealed class ClassifierGroundTruthParquetRow
    {
        public static readonly string SchemaFingerprint = Fingerprint(
            "schema_version|System.Int32|False",
            "run_id|System.String|False",
            "config_hash|System.String|False",
            "code_version|System.String|False",
            "data_feed|System.String|False",
            "ground_truth_id|System.String|False",
            "source_news_dataset_id|System.String|False",
            "work_item_id|System.String|False",
            "news_provider|System.String|False",
            "provider_article_id|System.String|False",
            "news_revision_id|System.String|False",
            "news_revision_content_sha256|System.String|False",
            "category|System.Int32|False",
            "direction|System.Int32|False",
            "direction_is_clear|System.Boolean|False",
            "materiality|System.Int32|False",
            "resolution|System.Int32|False",
            "annotator_ids_json|System.String|False",
            "annotation_timestamps_utc_json|System.String|False",
            "adjudicator_id|System.String|True",
            "adjudicated_at_utc_us|System.Int64|True",
            "adjudication_rationale|System.String|True",
            "news_available_at_utc_us|System.Int64|False",
            "resolved_at_utc_us|System.Int64|False",
            "sources_json|System.String|False");

        [JsonPropertyOrder(0), JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyOrder(1), JsonPropertyName("run_id"), ParquetRequired] public string RunId { get; set; } = String.Empty;
        [JsonPropertyOrder(2), JsonPropertyName("config_hash"), ParquetRequired] public string ConfigHash { get; set; } = String.Empty;
        [JsonPropertyOrder(3), JsonPropertyName("code_version"), ParquetRequired] public string CodeVersion { get; set; } = String.Empty;
        [JsonPropertyOrder(4), JsonPropertyName("data_feed"), ParquetRequired] public string DataFeed { get; set; } = String.Empty;
        [JsonPropertyOrder(5), JsonPropertyName("ground_truth_id"), ParquetRequired] public string GroundTruthId { get; set; } = String.Empty;
        [JsonPropertyOrder(6), JsonPropertyName("source_news_dataset_id"), ParquetRequired] public string SourceNewsDatasetId { get; set; } = String.Empty;
        [JsonPropertyOrder(7), JsonPropertyName("work_item_id"), ParquetRequired] public string WorkItemId { get; set; } = String.Empty;
        [JsonPropertyOrder(8), JsonPropertyName("news_provider"), ParquetRequired] public string NewsProvider { get; set; } = String.Empty;
        [JsonPropertyOrder(9), JsonPropertyName("provider_article_id"), ParquetRequired] public string ProviderArticleId { get; set; } = String.Empty;
        [JsonPropertyOrder(10), JsonPropertyName("news_revision_id"), ParquetRequired] public string NewsRevisionId { get; set; } = String.Empty;
        [JsonPropertyOrder(11), JsonPropertyName("news_revision_content_sha256"), ParquetRequired] public string NewsRevisionContentSha256 { get; set; } = String.Empty;
        [JsonPropertyOrder(12), JsonPropertyName("category")] public int Category { get; set; }
        [JsonPropertyOrder(13), JsonPropertyName("direction")] public int Direction { get; set; }
        [JsonPropertyOrder(14), JsonPropertyName("direction_is_clear")] public bool DirectionIsClear { get; set; }
        [JsonPropertyOrder(15), JsonPropertyName("materiality")] public int Materiality { get; set; }
        [JsonPropertyOrder(16), JsonPropertyName("resolution")] public int Resolution { get; set; }
        [JsonPropertyOrder(17), JsonPropertyName("annotator_ids_json"), ParquetRequired] public string AnnotatorIdsJson { get; set; } = String.Empty;
        [JsonPropertyOrder(18), JsonPropertyName("annotation_timestamps_utc_json"), ParquetRequired] public string AnnotationTimestampsUtcJson { get; set; } = String.Empty;
        [JsonPropertyOrder(19), JsonPropertyName("adjudicator_id")] public string? AdjudicatorId { get; set; }
        [JsonPropertyOrder(20), JsonPropertyName("adjudicated_at_utc_us")] public long? AdjudicatedAtUtcUs { get; set; }
        [JsonPropertyOrder(21), JsonPropertyName("adjudication_rationale")] public string? AdjudicationRationale { get; set; }
        [JsonPropertyOrder(22), JsonPropertyName("news_available_at_utc_us")] public long NewsAvailableAtUtcUs { get; set; }
        [JsonPropertyOrder(23), JsonPropertyName("resolved_at_utc_us")] public long ResolvedAtUtcUs { get; set; }
        [JsonPropertyOrder(24), JsonPropertyName("sources_json"), ParquetRequired] public string SourcesJson { get; set; } = String.Empty;

        public static ClassifierGroundTruthParquetRow FromDomain(
            ClassifierGroundTruthEvidenceRow row) => new()
        {
            SchemaVersion = row.SchemaVersion,
            RunId = row.RunId,
            ConfigHash = row.ConfigHash,
            CodeVersion = row.CodeVersion,
            DataFeed = row.DataFeed,
            GroundTruthId = row.GroundTruthId,
            SourceNewsDatasetId = row.SourceNewsDatasetId,
            WorkItemId = row.WorkItemId,
            NewsProvider = row.NewsProvider,
            ProviderArticleId = row.ProviderArticleId,
            NewsRevisionId = row.NewsRevisionId,
            NewsRevisionContentSha256 = row.NewsRevisionContentSha256,
            Category = (int)row.Category,
            Direction = (int)row.Direction,
            DirectionIsClear = row.DirectionIsClear,
            Materiality = (int)row.Materiality,
            Resolution = (int)row.Resolution,
            AnnotatorIdsJson = StorageValue.SerializeStrings(row.AnnotatorIds),
            AnnotationTimestampsUtcJson = StorageValue.SerializeStrings(
                row.AnnotationTimestampsUtc.Select(value => value.ToString("O")).ToArray()),
            AdjudicatorId = row.AdjudicatorId,
            AdjudicatedAtUtcUs = row.AdjudicatedAtUtc.HasValue
                ? StorageValue.ToUnixMicroseconds(row.AdjudicatedAtUtc.Value)
                : null,
            AdjudicationRationale = row.AdjudicationRationale,
            NewsAvailableAtUtcUs = StorageValue.ToUnixMicroseconds(row.NewsAvailableAtUtc),
            ResolvedAtUtcUs = StorageValue.ToUnixMicroseconds(row.ResolvedAtUtc),
            SourcesJson = StorageValue.SerializeSources(row.Sources)
        };

        public ClassifierGroundTruthEvidenceRow ToDomain()
        {
            var timestamps = StorageValue.DeserializeStrings(AnnotationTimestampsUtcJson)
                .Select(value =>
                {
                    if (!DateTimeOffset.TryParseExact(
                            value,
                            "O",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind,
                            out var timestamp))
                    {
                        throw new InvalidDataException(
                            $"Annotation timestamp '{value}' is invalid.");
                    }

                    return timestamp;
                })
                .ToArray();
            return new(
                SchemaVersion,
                RunId,
                ConfigHash,
                CodeVersion,
                DataFeed,
                GroundTruthId,
                SourceNewsDatasetId,
                WorkItemId,
                NewsProvider,
                ProviderArticleId,
                NewsRevisionId,
                NewsRevisionContentSha256,
                (CatalystNewsCategory)Category,
                (CatalystDirection)Direction,
                DirectionIsClear,
                (CatalystMateriality)Materiality,
                (GroundTruthResolution)Resolution,
                StorageValue.DeserializeStrings(AnnotatorIdsJson),
                timestamps,
                AdjudicatorId,
                AdjudicatedAtUtcUs.HasValue
                    ? StorageValue.FromUnixMicroseconds(AdjudicatedAtUtcUs.Value)
                    : null,
                AdjudicationRationale,
                StorageValue.FromUnixMicroseconds(NewsAvailableAtUtcUs),
                StorageValue.FromUnixMicroseconds(ResolvedAtUtcUs),
                StorageValue.DeserializeSources(SourcesJson));
        }
    }

    private sealed class SipQuoteParquetRow
    {
        public static readonly string SchemaFingerprint = Fingerprint(
            "schema_version|System.Int32|False",
            "run_id|System.String|False",
            "config_hash|System.String|False",
            "code_version|System.String|False",
            "data_feed|System.String|False",
            "security_id|System.String|False",
            "symbol|System.String|False",
            "quote_timestamp_utc_us|System.Int64|False",
            "price_scale|System.Int32|False",
            "bid_price_units|System.Int64|True",
            "ask_price_units|System.Int64|True",
            "bid_size|System.Int64|True",
            "ask_size|System.Int64|True",
            "bid_exchange|System.String|True",
            "ask_exchange|System.String|True",
            "currency|System.String|False",
            "conditions_json|System.String|False",
            "provider_timestamp_utc_us|System.Int64|False",
            "received_at_utc_us|System.Int64|False",
            "sources_json|System.String|False");

        [JsonPropertyOrder(0), JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyOrder(1), JsonPropertyName("run_id"), ParquetRequired] public string RunId { get; set; } = String.Empty;
        [JsonPropertyOrder(2), JsonPropertyName("config_hash"), ParquetRequired] public string ConfigHash { get; set; } = String.Empty;
        [JsonPropertyOrder(3), JsonPropertyName("code_version"), ParquetRequired] public string CodeVersion { get; set; } = String.Empty;
        [JsonPropertyOrder(4), JsonPropertyName("data_feed"), ParquetRequired] public string DataFeed { get; set; } = String.Empty;
        [JsonPropertyOrder(5), JsonPropertyName("security_id"), ParquetRequired] public string SecurityId { get; set; } = String.Empty;
        [JsonPropertyOrder(6), JsonPropertyName("symbol"), ParquetRequired] public string Symbol { get; set; } = String.Empty;
        [JsonPropertyOrder(7), JsonPropertyName("quote_timestamp_utc_us")] public long QuoteTimestampUtcUs { get; set; }
        [JsonPropertyOrder(8), JsonPropertyName("price_scale")] public int PriceScale { get; set; }
        [JsonPropertyOrder(9), JsonPropertyName("bid_price_units")] public long? BidPriceUnits { get; set; }
        [JsonPropertyOrder(10), JsonPropertyName("ask_price_units")] public long? AskPriceUnits { get; set; }
        [JsonPropertyOrder(11), JsonPropertyName("bid_size")] public long? BidSize { get; set; }
        [JsonPropertyOrder(12), JsonPropertyName("ask_size")] public long? AskSize { get; set; }
        [JsonPropertyOrder(13), JsonPropertyName("bid_exchange")] public string? BidExchange { get; set; }
        [JsonPropertyOrder(14), JsonPropertyName("ask_exchange")] public string? AskExchange { get; set; }
        [JsonPropertyOrder(15), JsonPropertyName("currency"), ParquetRequired] public string Currency { get; set; } = String.Empty;
        [JsonPropertyOrder(16), JsonPropertyName("conditions_json"), ParquetRequired] public string ConditionsJson { get; set; } = String.Empty;
        [JsonPropertyOrder(17), JsonPropertyName("provider_timestamp_utc_us")] public long ProviderTimestampUtcUs { get; set; }
        [JsonPropertyOrder(18), JsonPropertyName("received_at_utc_us")] public long ReceivedAtUtcUs { get; set; }
        [JsonPropertyOrder(19), JsonPropertyName("sources_json"), ParquetRequired] public string SourcesJson { get; set; } = String.Empty;

        public static SipQuoteParquetRow FromDomain(SipQuoteEvidenceRow row) => new()
        {
            SchemaVersion = row.SchemaVersion,
            RunId = row.RunId,
            ConfigHash = row.ConfigHash,
            CodeVersion = row.CodeVersion,
            DataFeed = row.DataFeed,
            SecurityId = row.SecurityId,
            Symbol = row.Symbol,
            QuoteTimestampUtcUs = StorageValue.ToUnixMicroseconds(row.QuoteTimestampUtc),
            PriceScale = EvidenceFixedDecimal.PriceScale,
            BidPriceUnits = row.BidPriceUnits,
            AskPriceUnits = row.AskPriceUnits,
            BidSize = row.BidSize,
            AskSize = row.AskSize,
            BidExchange = row.BidExchange,
            AskExchange = row.AskExchange,
            Currency = row.Currency,
            ConditionsJson = StorageValue.SerializeStrings(row.Conditions),
            ProviderTimestampUtcUs = StorageValue.ToUnixMicroseconds(row.ProviderTimestampUtc),
            ReceivedAtUtcUs = StorageValue.ToUnixMicroseconds(row.ReceivedAtUtc),
            SourcesJson = StorageValue.SerializeSources(row.Sources)
        };

        public SipQuoteEvidenceRow ToDomain()
        {
            EnsurePriceScale(PriceScale);
            return new(
                SchemaVersion,
                RunId,
                ConfigHash,
                CodeVersion,
                SecurityId,
                Symbol,
                StorageValue.FromUnixMicroseconds(QuoteTimestampUtcUs),
                BidPriceUnits,
                AskPriceUnits,
                BidSize,
                AskSize,
                BidExchange ?? String.Empty,
                AskExchange ?? String.Empty,
                DataFeed,
                Currency,
                StorageValue.DeserializeStrings(ConditionsJson),
                StorageValue.FromUnixMicroseconds(ProviderTimestampUtcUs),
                StorageValue.FromUnixMicroseconds(ReceivedAtUtcUs),
                StorageValue.DeserializeSources(SourcesJson));
        }
    }

    private sealed class SecurityMasterSnapshotParquetRow
    {
        public static readonly string SchemaFingerprint = Fingerprint(
            "schema_version|System.Int32|False",
            "run_id|System.String|False",
            "config_hash|System.String|False",
            "code_version|System.String|False",
            "data_feed|System.String|False",
            "provider|System.String|False",
            "security_id|System.String|False",
            "issuer_id|System.String|True",
            "symbol|System.String|False",
            "name|System.String|False",
            "exchange|System.String|False",
            "asset_class|System.String|False",
            "currency|System.String|False",
            "status|System.String|False",
            "tradable|System.Boolean|False",
            "marginable|System.Boolean|False",
            "shortable|System.Boolean|False",
            "easy_to_borrow|System.Boolean|False",
            "fractionable|System.Boolean|False",
            "borrow_status|System.String|True",
            "decimal_scale|System.Int32|False",
            "maintenance_margin_requirement_units|System.Int64|True",
            "margin_requirement_long_units|System.Int64|True",
            "margin_requirement_short_units|System.Int64|True",
            "attributes_json|System.String|False",
            "observed_at_utc_us|System.Int64|False",
            "symbol_ambiguous_in_snapshot|System.Boolean|False",
            "sources_json|System.String|False");

        [JsonPropertyOrder(0), JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyOrder(1), JsonPropertyName("run_id"), ParquetRequired] public string RunId { get; set; } = String.Empty;
        [JsonPropertyOrder(2), JsonPropertyName("config_hash"), ParquetRequired] public string ConfigHash { get; set; } = String.Empty;
        [JsonPropertyOrder(3), JsonPropertyName("code_version"), ParquetRequired] public string CodeVersion { get; set; } = String.Empty;
        [JsonPropertyOrder(4), JsonPropertyName("data_feed"), ParquetRequired] public string DataFeed { get; set; } = String.Empty;
        [JsonPropertyOrder(5), JsonPropertyName("provider"), ParquetRequired] public string Provider { get; set; } = String.Empty;
        [JsonPropertyOrder(6), JsonPropertyName("security_id"), ParquetRequired] public string SecurityId { get; set; } = String.Empty;
        [JsonPropertyOrder(7), JsonPropertyName("issuer_id")] public string? IssuerId { get; set; }
        [JsonPropertyOrder(8), JsonPropertyName("symbol"), ParquetRequired] public string Symbol { get; set; } = String.Empty;
        [JsonPropertyOrder(9), JsonPropertyName("name"), ParquetRequired] public string Name { get; set; } = String.Empty;
        [JsonPropertyOrder(10), JsonPropertyName("exchange"), ParquetRequired] public string Exchange { get; set; } = String.Empty;
        [JsonPropertyOrder(11), JsonPropertyName("asset_class"), ParquetRequired] public string AssetClass { get; set; } = String.Empty;
        [JsonPropertyOrder(12), JsonPropertyName("currency"), ParquetRequired] public string Currency { get; set; } = String.Empty;
        [JsonPropertyOrder(13), JsonPropertyName("status"), ParquetRequired] public string Status { get; set; } = String.Empty;
        [JsonPropertyOrder(14), JsonPropertyName("tradable")] public bool Tradable { get; set; }
        [JsonPropertyOrder(15), JsonPropertyName("marginable")] public bool Marginable { get; set; }
        [JsonPropertyOrder(16), JsonPropertyName("shortable")] public bool Shortable { get; set; }
        [JsonPropertyOrder(17), JsonPropertyName("easy_to_borrow")] public bool EasyToBorrow { get; set; }
        [JsonPropertyOrder(18), JsonPropertyName("fractionable")] public bool Fractionable { get; set; }
        [JsonPropertyOrder(19), JsonPropertyName("borrow_status")] public string? BorrowStatus { get; set; }
        [JsonPropertyOrder(20), JsonPropertyName("decimal_scale")] public int DecimalScale { get; set; }
        [JsonPropertyOrder(21), JsonPropertyName("maintenance_margin_requirement_units")] public long? MaintenanceMarginRequirementUnits { get; set; }
        [JsonPropertyOrder(22), JsonPropertyName("margin_requirement_long_units")] public long? MarginRequirementLongUnits { get; set; }
        [JsonPropertyOrder(23), JsonPropertyName("margin_requirement_short_units")] public long? MarginRequirementShortUnits { get; set; }
        [JsonPropertyOrder(24), JsonPropertyName("attributes_json"), ParquetRequired] public string AttributesJson { get; set; } = String.Empty;
        [JsonPropertyOrder(25), JsonPropertyName("observed_at_utc_us")] public long ObservedAtUtcUs { get; set; }
        [JsonPropertyOrder(26), JsonPropertyName("symbol_ambiguous_in_snapshot")] public bool SymbolAmbiguousInSnapshot { get; set; }
        [JsonPropertyOrder(27), JsonPropertyName("sources_json"), ParquetRequired] public string SourcesJson { get; set; } = String.Empty;

        public static SecurityMasterSnapshotParquetRow FromDomain(
            SecurityMasterSnapshotEvidenceRow row) => new()
        {
            SchemaVersion = row.SchemaVersion,
            RunId = row.RunId,
            ConfigHash = row.ConfigHash,
            CodeVersion = row.CodeVersion,
            DataFeed = row.DataFeed,
            Provider = row.Provider,
            SecurityId = row.SecurityId,
            IssuerId = row.IssuerId,
            Symbol = row.Symbol,
            Name = row.Name,
            Exchange = row.Exchange,
            AssetClass = row.AssetClass,
            Currency = row.Currency,
            Status = row.Status,
            Tradable = row.Tradable,
            Marginable = row.Marginable,
            Shortable = row.Shortable,
            EasyToBorrow = row.EasyToBorrow,
            Fractionable = row.Fractionable,
            BorrowStatus = row.BorrowStatus,
            DecimalScale = IdentityDecimalScale,
            MaintenanceMarginRequirementUnits =
                ToIdentityDecimalUnits(row.MaintenanceMarginRequirement),
            MarginRequirementLongUnits = ToIdentityDecimalUnits(row.MarginRequirementLong),
            MarginRequirementShortUnits = ToIdentityDecimalUnits(row.MarginRequirementShort),
            AttributesJson = StorageValue.SerializeStrings(row.Attributes),
            ObservedAtUtcUs = StorageValue.ToUnixMicroseconds(row.ObservedAtUtc),
            SymbolAmbiguousInSnapshot = row.ReadinessFailures.Contains(
                IdentityEvidenceReadinessFailure.SymbolAmbiguousInSnapshot),
            SourcesJson = StorageValue.SerializeSources(row.Sources)
        };

        public SecurityMasterSnapshotEvidenceRow ToDomain()
        {
            EnsureIdentityDecimalScale(DecimalScale);
            return new(
                SchemaVersion,
                RunId,
                ConfigHash,
                CodeVersion,
                DataFeed,
                Provider,
                SecurityId,
                IssuerId,
                Symbol,
                Name,
                Exchange,
                AssetClass,
                Currency,
                Status,
                Tradable,
                Marginable,
                Shortable,
                EasyToBorrow,
                Fractionable,
                BorrowStatus,
                FromIdentityDecimalUnits(MaintenanceMarginRequirementUnits),
                FromIdentityDecimalUnits(MarginRequirementLongUnits),
                FromIdentityDecimalUnits(MarginRequirementShortUnits),
                StorageValue.DeserializeStrings(AttributesJson),
                StorageValue.FromUnixMicroseconds(ObservedAtUtcUs),
                SymbolAmbiguousInSnapshot,
                StorageValue.DeserializeSources(SourcesJson));
        }
    }

    private sealed class SymbolIntervalParquetRow
    {
        public static readonly string SchemaFingerprint = Fingerprint(
            "schema_version|System.Int32|False",
            "run_id|System.String|False",
            "config_hash|System.String|False",
            "code_version|System.String|False",
            "data_feed|System.String|False",
            "provider|System.String|False",
            "security_id|System.String|False",
            "issuer_id|System.String|True",
            "symbol|System.String|False",
            "observed_at_utc_us|System.Int64|False",
            "symbol_ambiguous_in_snapshot|System.Boolean|False",
            "sources_json|System.String|False");

        [JsonPropertyOrder(0), JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyOrder(1), JsonPropertyName("run_id"), ParquetRequired] public string RunId { get; set; } = String.Empty;
        [JsonPropertyOrder(2), JsonPropertyName("config_hash"), ParquetRequired] public string ConfigHash { get; set; } = String.Empty;
        [JsonPropertyOrder(3), JsonPropertyName("code_version"), ParquetRequired] public string CodeVersion { get; set; } = String.Empty;
        [JsonPropertyOrder(4), JsonPropertyName("data_feed"), ParquetRequired] public string DataFeed { get; set; } = String.Empty;
        [JsonPropertyOrder(5), JsonPropertyName("provider"), ParquetRequired] public string Provider { get; set; } = String.Empty;
        [JsonPropertyOrder(6), JsonPropertyName("security_id"), ParquetRequired] public string SecurityId { get; set; } = String.Empty;
        [JsonPropertyOrder(7), JsonPropertyName("issuer_id")] public string? IssuerId { get; set; }
        [JsonPropertyOrder(8), JsonPropertyName("symbol"), ParquetRequired] public string Symbol { get; set; } = String.Empty;
        [JsonPropertyOrder(9), JsonPropertyName("observed_at_utc_us")] public long ObservedAtUtcUs { get; set; }
        [JsonPropertyOrder(10), JsonPropertyName("symbol_ambiguous_in_snapshot")] public bool SymbolAmbiguousInSnapshot { get; set; }
        [JsonPropertyOrder(11), JsonPropertyName("sources_json"), ParquetRequired] public string SourcesJson { get; set; } = String.Empty;

        public static SymbolIntervalParquetRow FromDomain(SymbolIntervalEvidenceRow row) => new()
        {
            SchemaVersion = row.SchemaVersion,
            RunId = row.RunId,
            ConfigHash = row.ConfigHash,
            CodeVersion = row.CodeVersion,
            DataFeed = row.DataFeed,
            Provider = row.Provider,
            SecurityId = row.SecurityId,
            IssuerId = row.IssuerId,
            Symbol = row.Symbol,
            ObservedAtUtcUs = StorageValue.ToUnixMicroseconds(row.ObservedAtUtc),
            SymbolAmbiguousInSnapshot = row.ReadinessFailures.Contains(
                IdentityEvidenceReadinessFailure.SymbolAmbiguousInSnapshot),
            SourcesJson = StorageValue.SerializeSources(row.Sources)
        };

        public SymbolIntervalEvidenceRow ToDomain() => new(
            SchemaVersion,
            RunId,
            ConfigHash,
            CodeVersion,
            DataFeed,
            Provider,
            SecurityId,
            IssuerId,
            Symbol,
            StorageValue.FromUnixMicroseconds(ObservedAtUtcUs),
            SymbolAmbiguousInSnapshot,
            StorageValue.DeserializeSources(SourcesJson));
    }

    private sealed class CorporateActionParquetRow
    {
        public static readonly string SchemaFingerprint = Fingerprint(
            "schema_version|System.Int32|False",
            "run_id|System.String|False",
            "config_hash|System.String|False",
            "code_version|System.String|False",
            "data_feed|System.String|False",
            "provider|System.String|False",
            "action_type|System.Int32|False",
            "provider_action_id|System.String|False",
            "security_id|System.String|True",
            "issuer_id|System.String|True",
            "primary_symbol|System.String|True",
            "primary_cusip|System.String|True",
            "process_day_number|System.Int32|False",
            "effective_day_number|System.Int32|True",
            "ex_day_number|System.Int32|True",
            "record_day_number|System.Int32|True",
            "payable_day_number|System.Int32|True",
            "provider_fields_json|System.String|False",
            "received_at_utc_us|System.Int64|False",
            "sources_json|System.String|False");

        [JsonPropertyOrder(0), JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyOrder(1), JsonPropertyName("run_id"), ParquetRequired] public string RunId { get; set; } = String.Empty;
        [JsonPropertyOrder(2), JsonPropertyName("config_hash"), ParquetRequired] public string ConfigHash { get; set; } = String.Empty;
        [JsonPropertyOrder(3), JsonPropertyName("code_version"), ParquetRequired] public string CodeVersion { get; set; } = String.Empty;
        [JsonPropertyOrder(4), JsonPropertyName("data_feed"), ParquetRequired] public string DataFeed { get; set; } = String.Empty;
        [JsonPropertyOrder(5), JsonPropertyName("provider"), ParquetRequired] public string Provider { get; set; } = String.Empty;
        [JsonPropertyOrder(6), JsonPropertyName("action_type")] public int ActionType { get; set; }
        [JsonPropertyOrder(7), JsonPropertyName("provider_action_id"), ParquetRequired] public string ProviderActionId { get; set; } = String.Empty;
        [JsonPropertyOrder(8), JsonPropertyName("security_id")] public string? SecurityId { get; set; }
        [JsonPropertyOrder(9), JsonPropertyName("issuer_id")] public string? IssuerId { get; set; }
        [JsonPropertyOrder(10), JsonPropertyName("primary_symbol")] public string? PrimarySymbol { get; set; }
        [JsonPropertyOrder(11), JsonPropertyName("primary_cusip")] public string? PrimaryCusip { get; set; }
        [JsonPropertyOrder(12), JsonPropertyName("process_day_number")] public int ProcessDayNumber { get; set; }
        [JsonPropertyOrder(13), JsonPropertyName("effective_day_number")] public int? EffectiveDayNumber { get; set; }
        [JsonPropertyOrder(14), JsonPropertyName("ex_day_number")] public int? ExDayNumber { get; set; }
        [JsonPropertyOrder(15), JsonPropertyName("record_day_number")] public int? RecordDayNumber { get; set; }
        [JsonPropertyOrder(16), JsonPropertyName("payable_day_number")] public int? PayableDayNumber { get; set; }
        [JsonPropertyOrder(17), JsonPropertyName("provider_fields_json"), ParquetRequired] public string ProviderFieldsJson { get; set; } = String.Empty;
        [JsonPropertyOrder(18), JsonPropertyName("received_at_utc_us")] public long ReceivedAtUtcUs { get; set; }
        [JsonPropertyOrder(19), JsonPropertyName("sources_json"), ParquetRequired] public string SourcesJson { get; set; } = String.Empty;

        public static CorporateActionParquetRow FromDomain(CorporateActionEvidenceRow row) => new()
        {
            SchemaVersion = row.SchemaVersion,
            RunId = row.RunId,
            ConfigHash = row.ConfigHash,
            CodeVersion = row.CodeVersion,
            DataFeed = row.DataFeed,
            Provider = row.Provider,
            ActionType = (int)row.ActionType,
            ProviderActionId = row.ProviderActionId,
            SecurityId = row.SecurityId,
            IssuerId = row.IssuerId,
            PrimarySymbol = row.PrimarySymbol,
            PrimaryCusip = row.PrimaryCusip,
            ProcessDayNumber = row.ProcessDate.DayNumber,
            EffectiveDayNumber = row.EffectiveDate?.DayNumber,
            ExDayNumber = row.ExDate?.DayNumber,
            RecordDayNumber = row.RecordDate?.DayNumber,
            PayableDayNumber = row.PayableDate?.DayNumber,
            ProviderFieldsJson = StorageValue.SerializeDictionary(row.ProviderFields),
            ReceivedAtUtcUs = StorageValue.ToUnixMicroseconds(row.ReceivedAtUtc),
            SourcesJson = StorageValue.SerializeSources(row.Sources)
        };

        public CorporateActionEvidenceRow ToDomain() => new(
            SchemaVersion,
            RunId,
            ConfigHash,
            CodeVersion,
            DataFeed,
            Provider,
            ToCorporateActionType(ActionType),
            ProviderActionId,
            SecurityId,
            IssuerId,
            PrimarySymbol,
            PrimaryCusip,
            FromDayNumber(ProcessDayNumber),
            FromNullableDayNumber(EffectiveDayNumber),
            FromNullableDayNumber(ExDayNumber),
            FromNullableDayNumber(RecordDayNumber),
            FromNullableDayNumber(PayableDayNumber),
            StorageValue.DeserializeDictionary(ProviderFieldsJson),
            StorageValue.FromUnixMicroseconds(ReceivedAtUtcUs),
            StorageValue.DeserializeSources(SourcesJson));
    }

    private sealed class SecurityMasterParquetRow
    {
        public static readonly string SchemaFingerprint = Fingerprint(
            "schema_version|System.Int32|False",
            "run_id|System.String|False",
            "config_hash|System.String|False",
            "code_version|System.String|False",
            "data_feed|System.String|False",
            "provider|System.String|False",
            "security_id|System.String|False",
            "issuer_id|System.String|False",
            "symbol|System.String|False",
            "exchange|System.String|False",
            "asset_class|System.String|False",
            "currency|System.String|False",
            "status|System.String|False",
            "valid_from_utc_us|System.Int64|False",
            "valid_to_utc_us|System.Int64|True",
            "provider_updated_at_utc_us|System.Int64|False",
            "received_at_utc_us|System.Int64|False",
            "sources_json|System.String|False");

        [JsonPropertyOrder(0), JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyOrder(1), JsonPropertyName("run_id"), ParquetRequired] public string RunId { get; set; } = String.Empty;
        [JsonPropertyOrder(2), JsonPropertyName("config_hash"), ParquetRequired] public string ConfigHash { get; set; } = String.Empty;
        [JsonPropertyOrder(3), JsonPropertyName("code_version"), ParquetRequired] public string CodeVersion { get; set; } = String.Empty;
        [JsonPropertyOrder(4), JsonPropertyName("data_feed"), ParquetRequired] public string DataFeed { get; set; } = String.Empty;
        [JsonPropertyOrder(5), JsonPropertyName("provider"), ParquetRequired] public string Provider { get; set; } = String.Empty;
        [JsonPropertyOrder(6), JsonPropertyName("security_id"), ParquetRequired] public string SecurityId { get; set; } = String.Empty;
        [JsonPropertyOrder(7), JsonPropertyName("issuer_id"), ParquetRequired] public string IssuerId { get; set; } = String.Empty;
        [JsonPropertyOrder(8), JsonPropertyName("symbol"), ParquetRequired] public string Symbol { get; set; } = String.Empty;
        [JsonPropertyOrder(9), JsonPropertyName("exchange"), ParquetRequired] public string Exchange { get; set; } = String.Empty;
        [JsonPropertyOrder(10), JsonPropertyName("asset_class"), ParquetRequired] public string AssetClass { get; set; } = String.Empty;
        [JsonPropertyOrder(11), JsonPropertyName("currency"), ParquetRequired] public string Currency { get; set; } = String.Empty;
        [JsonPropertyOrder(12), JsonPropertyName("status"), ParquetRequired] public string Status { get; set; } = String.Empty;
        [JsonPropertyOrder(13), JsonPropertyName("valid_from_utc_us")] public long ValidFromUtcUs { get; set; }
        [JsonPropertyOrder(14), JsonPropertyName("valid_to_utc_us")] public long? ValidToUtcUs { get; set; }
        [JsonPropertyOrder(15), JsonPropertyName("provider_updated_at_utc_us")] public long ProviderUpdatedAtUtcUs { get; set; }
        [JsonPropertyOrder(16), JsonPropertyName("received_at_utc_us")] public long ReceivedAtUtcUs { get; set; }
        [JsonPropertyOrder(17), JsonPropertyName("sources_json"), ParquetRequired] public string SourcesJson { get; set; } = String.Empty;

        public static SecurityMasterParquetRow FromDomain(SecurityMasterEvidenceRow row) => new()
        {
            SchemaVersion = row.SchemaVersion,
            RunId = row.RunId,
            ConfigHash = row.ConfigHash,
            CodeVersion = row.CodeVersion,
            DataFeed = row.DataFeed,
            Provider = row.Provider,
            SecurityId = row.SecurityId,
            IssuerId = row.IssuerId,
            Symbol = row.Symbol,
            Exchange = row.Exchange,
            AssetClass = row.AssetClass,
            Currency = row.Currency,
            Status = row.Status,
            ValidFromUtcUs = StorageValue.ToUnixMicroseconds(row.ValidFromUtc),
            ValidToUtcUs = row.ValidToUtc.HasValue
                ? StorageValue.ToUnixMicroseconds(row.ValidToUtc.Value)
                : null,
            ProviderUpdatedAtUtcUs = StorageValue.ToUnixMicroseconds(row.ProviderUpdatedAtUtc),
            ReceivedAtUtcUs = StorageValue.ToUnixMicroseconds(row.ReceivedAtUtc),
            SourcesJson = StorageValue.SerializeSources(row.Sources)
        };

        public SecurityMasterEvidenceRow ToDomain() => new(
            SchemaVersion,
            RunId,
            ConfigHash,
            CodeVersion,
            DataFeed,
            Provider,
            SecurityId,
            IssuerId,
            Symbol,
            Exchange,
            AssetClass,
            Currency,
            Status,
            StorageValue.FromUnixMicroseconds(ValidFromUtcUs),
            ValidToUtcUs.HasValue ? StorageValue.FromUnixMicroseconds(ValidToUtcUs.Value) : null,
            StorageValue.FromUnixMicroseconds(ProviderUpdatedAtUtcUs),
            StorageValue.FromUnixMicroseconds(ReceivedAtUtcUs),
            StorageValue.DeserializeSources(SourcesJson));
    }

    private sealed class UniverseMembershipParquetRow
    {
        public static readonly string SchemaFingerprint = Fingerprint(
            "schema_version|System.Int32|False",
            "run_id|System.String|False",
            "config_hash|System.String|False",
            "code_version|System.String|False",
            "data_feed|System.String|False",
            "provider|System.String|False",
            "universe_id|System.String|False",
            "snapshot_id|System.String|False",
            "provider_query|System.String|False",
            "snapshot_content_sha256|System.String|False",
            "effective_session_day_number|System.Int32|False",
            "security_id|System.String|False",
            "issuer_id|System.String|False",
            "symbol|System.String|False",
            "as_of_utc_us|System.Int64|False",
            "included|System.Boolean|False",
            "rank|System.Int32|True",
            "selection_reason|System.String|False",
            "provider_timestamp_utc_us|System.Int64|False",
            "received_at_utc_us|System.Int64|False",
            "sources_json|System.String|False");

        [JsonPropertyOrder(0), JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyOrder(1), JsonPropertyName("run_id"), ParquetRequired] public string RunId { get; set; } = String.Empty;
        [JsonPropertyOrder(2), JsonPropertyName("config_hash"), ParquetRequired] public string ConfigHash { get; set; } = String.Empty;
        [JsonPropertyOrder(3), JsonPropertyName("code_version"), ParquetRequired] public string CodeVersion { get; set; } = String.Empty;
        [JsonPropertyOrder(4), JsonPropertyName("data_feed"), ParquetRequired] public string DataFeed { get; set; } = String.Empty;
        [JsonPropertyOrder(5), JsonPropertyName("provider"), ParquetRequired] public string Provider { get; set; } = String.Empty;
        [JsonPropertyOrder(6), JsonPropertyName("universe_id"), ParquetRequired] public string UniverseId { get; set; } = String.Empty;
        [JsonPropertyOrder(7), JsonPropertyName("snapshot_id"), ParquetRequired] public string SnapshotId { get; set; } = String.Empty;
        [JsonPropertyOrder(8), JsonPropertyName("provider_query"), ParquetRequired] public string ProviderQuery { get; set; } = String.Empty;
        [JsonPropertyOrder(9), JsonPropertyName("snapshot_content_sha256"), ParquetRequired] public string SnapshotContentSha256 { get; set; } = String.Empty;
        [JsonPropertyOrder(10), JsonPropertyName("effective_session_day_number")] public int EffectiveSessionDayNumber { get; set; }
        [JsonPropertyOrder(11), JsonPropertyName("security_id"), ParquetRequired] public string SecurityId { get; set; } = String.Empty;
        [JsonPropertyOrder(12), JsonPropertyName("issuer_id"), ParquetRequired] public string IssuerId { get; set; } = String.Empty;
        [JsonPropertyOrder(13), JsonPropertyName("symbol"), ParquetRequired] public string Symbol { get; set; } = String.Empty;
        [JsonPropertyOrder(14), JsonPropertyName("as_of_utc_us")] public long AsOfUtcUs { get; set; }
        [JsonPropertyOrder(15), JsonPropertyName("included")] public bool Included { get; set; }
        [JsonPropertyOrder(16), JsonPropertyName("rank")] public int? Rank { get; set; }
        [JsonPropertyOrder(17), JsonPropertyName("selection_reason"), ParquetRequired] public string SelectionReason { get; set; } = String.Empty;
        [JsonPropertyOrder(18), JsonPropertyName("provider_timestamp_utc_us")] public long ProviderTimestampUtcUs { get; set; }
        [JsonPropertyOrder(19), JsonPropertyName("received_at_utc_us")] public long ReceivedAtUtcUs { get; set; }
        [JsonPropertyOrder(20), JsonPropertyName("sources_json"), ParquetRequired] public string SourcesJson { get; set; } = String.Empty;

        public static UniverseMembershipParquetRow FromDomain(UniverseMembershipEvidenceRow row) => new()
        {
            SchemaVersion = row.SchemaVersion,
            RunId = row.RunId,
            ConfigHash = row.ConfigHash,
            CodeVersion = row.CodeVersion,
            DataFeed = row.DataFeed,
            Provider = row.Provider,
            UniverseId = row.UniverseId,
            SnapshotId = row.SnapshotId,
            ProviderQuery = row.ProviderQuery,
            SnapshotContentSha256 = row.SnapshotContentSha256,
            EffectiveSessionDayNumber = row.EffectiveSessionDate.DayNumber,
            SecurityId = row.SecurityId,
            IssuerId = row.IssuerId,
            Symbol = row.Symbol,
            AsOfUtcUs = StorageValue.ToUnixMicroseconds(row.AsOfUtc),
            Included = row.Included,
            Rank = row.Rank,
            SelectionReason = row.SelectionReason,
            ProviderTimestampUtcUs = StorageValue.ToUnixMicroseconds(row.ProviderTimestampUtc),
            ReceivedAtUtcUs = StorageValue.ToUnixMicroseconds(row.ReceivedAtUtc),
            SourcesJson = StorageValue.SerializeSources(row.Sources)
        };

        public UniverseMembershipEvidenceRow ToDomain() => new(
            SchemaVersion,
            RunId,
            ConfigHash,
            CodeVersion,
            DataFeed,
            Provider,
            UniverseId,
            SnapshotId,
            ProviderQuery,
            SnapshotContentSha256,
            FromDayNumber(EffectiveSessionDayNumber),
            SecurityId,
            IssuerId,
            Symbol,
            StorageValue.FromUnixMicroseconds(AsOfUtcUs),
            Included,
            Rank,
            SelectionReason,
            StorageValue.FromUnixMicroseconds(ProviderTimestampUtcUs),
            StorageValue.FromUnixMicroseconds(ReceivedAtUtcUs),
            StorageValue.DeserializeSources(SourcesJson));
    }

    private sealed class ExchangeSessionParquetRow
    {
        public static readonly string SchemaFingerprint = Fingerprint(
            "schema_version|System.Int32|False",
            "run_id|System.String|False",
            "config_hash|System.String|False",
            "code_version|System.String|False",
            "data_feed|System.String|False",
            "provider|System.String|False",
            "exchange|System.String|False",
            "trade_day_number|System.Int32|False",
            "premarket_open_utc_us|System.Int64|False",
            "regular_open_utc_us|System.Int64|False",
            "regular_close_utc_us|System.Int64|False",
            "postmarket_close_utc_us|System.Int64|False",
            "is_early_close|System.Boolean|False",
            "calendar_source|System.String|False",
            "available_at_utc_us|System.Int64|False",
            "sources_json|System.String|False");

        [JsonPropertyOrder(0), JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyOrder(1), JsonPropertyName("run_id"), ParquetRequired] public string RunId { get; set; } = String.Empty;
        [JsonPropertyOrder(2), JsonPropertyName("config_hash"), ParquetRequired] public string ConfigHash { get; set; } = String.Empty;
        [JsonPropertyOrder(3), JsonPropertyName("code_version"), ParquetRequired] public string CodeVersion { get; set; } = String.Empty;
        [JsonPropertyOrder(4), JsonPropertyName("data_feed"), ParquetRequired] public string DataFeed { get; set; } = String.Empty;
        [JsonPropertyOrder(5), JsonPropertyName("provider"), ParquetRequired] public string Provider { get; set; } = String.Empty;
        [JsonPropertyOrder(6), JsonPropertyName("exchange"), ParquetRequired] public string Exchange { get; set; } = String.Empty;
        [JsonPropertyOrder(7), JsonPropertyName("trade_day_number")] public int TradeDayNumber { get; set; }
        [JsonPropertyOrder(8), JsonPropertyName("premarket_open_utc_us")] public long PremarketOpenUtcUs { get; set; }
        [JsonPropertyOrder(9), JsonPropertyName("regular_open_utc_us")] public long RegularOpenUtcUs { get; set; }
        [JsonPropertyOrder(10), JsonPropertyName("regular_close_utc_us")] public long RegularCloseUtcUs { get; set; }
        [JsonPropertyOrder(11), JsonPropertyName("postmarket_close_utc_us")] public long PostmarketCloseUtcUs { get; set; }
        [JsonPropertyOrder(12), JsonPropertyName("is_early_close")] public bool IsEarlyClose { get; set; }
        [JsonPropertyOrder(13), JsonPropertyName("calendar_source"), ParquetRequired] public string CalendarSource { get; set; } = String.Empty;
        [JsonPropertyOrder(14), JsonPropertyName("available_at_utc_us")] public long AvailableAtUtcUs { get; set; }
        [JsonPropertyOrder(15), JsonPropertyName("sources_json"), ParquetRequired] public string SourcesJson { get; set; } = String.Empty;

        public static ExchangeSessionParquetRow FromDomain(ExchangeSessionEvidenceRow row) => new()
        {
            SchemaVersion = row.SchemaVersion,
            RunId = row.RunId,
            ConfigHash = row.ConfigHash,
            CodeVersion = row.CodeVersion,
            DataFeed = row.DataFeed,
            Provider = row.Provider,
            Exchange = row.Exchange,
            TradeDayNumber = row.TradeDate.DayNumber,
            PremarketOpenUtcUs = StorageValue.ToUnixMicroseconds(row.PremarketOpenUtc),
            RegularOpenUtcUs = StorageValue.ToUnixMicroseconds(row.RegularOpenUtc),
            RegularCloseUtcUs = StorageValue.ToUnixMicroseconds(row.RegularCloseUtc),
            PostmarketCloseUtcUs = StorageValue.ToUnixMicroseconds(row.PostmarketCloseUtc),
            IsEarlyClose = row.IsEarlyClose,
            CalendarSource = row.CalendarSource,
            AvailableAtUtcUs = StorageValue.ToUnixMicroseconds(row.AvailableAtUtc),
            SourcesJson = StorageValue.SerializeSources(row.Sources)
        };

        public ExchangeSessionEvidenceRow ToDomain() => new(
            SchemaVersion,
            RunId,
            ConfigHash,
            CodeVersion,
            DataFeed,
            Provider,
            Exchange,
            FromDayNumber(TradeDayNumber),
            StorageValue.FromUnixMicroseconds(PremarketOpenUtcUs),
            StorageValue.FromUnixMicroseconds(RegularOpenUtcUs),
            StorageValue.FromUnixMicroseconds(RegularCloseUtcUs),
            StorageValue.FromUnixMicroseconds(PostmarketCloseUtcUs),
            IsEarlyClose,
            CalendarSource,
            StorageValue.FromUnixMicroseconds(AvailableAtUtcUs),
            StorageValue.DeserializeSources(SourcesJson));
    }

    private static string Fingerprint(params string[] fields) =>
        ComputeSchemaFingerprint(fields);

    private static string ToAvailabilityEvidence(NewsAvailabilityEvidence value) =>
        value switch
        {
            NewsAvailabilityEvidence.ProviderTimestampOnly => "provider_timestamp_only",
            NewsAvailabilityEvidence.ProviderUpdatedTimestampOnly => "provider_updated_timestamp_only",
            NewsAvailabilityEvidence.ObservedReceiptTime => "observed_receipt_time",
            _ => throw new InvalidDataException(
                $"Unsupported news availability evidence '{value}'.")
        };

    private static NewsAvailabilityEvidence FromAvailabilityEvidence(string value) =>
        value switch
        {
            "provider_timestamp_only" => NewsAvailabilityEvidence.ProviderTimestampOnly,
            "provider_updated_timestamp_only" => NewsAvailabilityEvidence.ProviderUpdatedTimestampOnly,
            "observed_receipt_time" => NewsAvailabilityEvidence.ObservedReceiptTime,
            _ => throw new InvalidDataException(
                $"Unsupported news availability evidence '{value}'.")
        };

    private static long? ToIdentityDecimalUnits(decimal? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return checked((long)Decimal.Round(
            value.Value * IdentityDecimalScaleFactor,
            0,
            MidpointRounding.ToEven));
    }

    private static decimal? FromIdentityDecimalUnits(long? value) =>
        value.HasValue
            ? value.Value / IdentityDecimalScaleFactor
            : null;

    private static void EnsureIdentityDecimalScale(int scale)
    {
        if (scale != IdentityDecimalScale)
        {
            throw new InvalidDataException(
                $"Identity decimal scale '{scale}' does not match required scale '{IdentityDecimalScale}'.");
        }
    }

    private static CorporateActionEvidenceType ToCorporateActionType(int value)
    {
        var result = (CorporateActionEvidenceType)value;
        if (!Enum.IsDefined(result))
        {
            throw new InvalidDataException(
                $"Corporate-action type code '{value}' is not supported.");
        }

        return result;
    }

    private static DateOnly? FromNullableDayNumber(int? value) =>
        value.HasValue ? FromDayNumber(value.Value) : null;

    private static DateOnly FromDayNumber(int value)
    {
        try
        {
            return DateOnly.FromDayNumber(value);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(
                $"Effective session day number '{value}' is outside the supported range.",
                exception);
        }
    }

    private static void EnsurePriceScale(int scale)
    {
        if (scale != EvidenceFixedDecimal.PriceScale)
        {
            throw new InvalidDataException(
                $"Price scale '{scale}' does not match required scale '{EvidenceFixedDecimal.PriceScale}'.");
        }
    }
}
