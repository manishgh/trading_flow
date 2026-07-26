using System.Security.Cryptography;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Data.Evidence.Parquet;

public sealed record EvidenceParquetPartitionRequest(
    string PartitionId,
    EvidenceDatasetKind DatasetKind,
    int SchemaVersion,
    EvidencePartitionProvenance Provenance,
    IReadOnlyList<EvidenceSourceReference> SourceObservations,
    string RunId,
    string ConfigHash,
    string NormalizerVersion,
    string CodeVersion,
    EvidenceQualityReport Quality,
    EvidenceObjectNamespace ObjectNamespace,
    EvidenceArtifactReference? QuarantineLedger = null,
    IReadOnlyDictionary<string, string>? Dimensions = null);

/// <summary>
/// Publishes a fully encoded Parquet partition to the immutable store and constructs the
/// catalog-native partition manifest. No provider access or mutable file publication occurs.
/// </summary>
public sealed class EvidenceParquetPartitionPublisher(
    EvidenceParquetCodec codec,
    IImmutableArtifactStore artifactStore)
{
    public Task<EvidenceDatasetPartitionManifest> PublishMarketBarsAsTradedAsync(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<MarketBarEvidenceRow> rows,
        CancellationToken cancellationToken = default)
    {
        ValidateMarketBarProvenance(request, rows);
        return PublishAsync(
            request,
            rows,
            EvidenceDatasetKind.MarketBarsAsTraded,
            codec.WriteMarketBarsAsTradedAsync,
            cancellationToken);
    }

    public Task<EvidenceDatasetPartitionManifest> PublishResearchAdjustedMarketBarsAsync(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<MarketBarEvidenceRow> rows,
        CancellationToken cancellationToken = default)
    {
        ValidateMarketBarProvenance(request, rows);
        return PublishAsync(
            request,
            rows,
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            codec.WriteResearchAdjustedMarketBarsAsync,
            cancellationToken);
    }

    public Task<EvidenceDatasetPartitionManifest> PublishNewsRevisionsAsync(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<NewsRevisionEvidenceRow> rows,
        CancellationToken cancellationToken = default)
    {
        ValidateNewsProvenance(request, rows);
        return PublishAsync(
            request,
            rows,
            EvidenceDatasetKind.NewsRevisions,
            codec.WriteNewsRevisionsAsync,
            cancellationToken);
    }

    public Task<EvidenceDatasetPartitionManifest> PublishSentimentAssessmentsAsync(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<SentimentAssessmentEvidenceRow> rows,
        CancellationToken cancellationToken = default)
    {
        ValidateSentimentProvenance(request, rows);
        return PublishAsync(
            request,
            rows,
            EvidenceDatasetKind.SentimentAssessments,
            codec.WriteSentimentAssessmentsAsync,
            cancellationToken);
    }

    public Task<EvidenceDatasetPartitionManifest> PublishClassifierGroundTruthAsync(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<ClassifierGroundTruthEvidenceRow> rows,
        CancellationToken cancellationToken = default)
    {
        ValidateClassifierGroundTruthProvenance(request, rows);
        return PublishAsync(
            request,
            rows,
            EvidenceDatasetKind.ClassifierGroundTruth,
            codec.WriteClassifierGroundTruthAsync,
            cancellationToken);
    }

    public Task<EvidenceDatasetPartitionManifest> PublishSecurityMasterSnapshotsAsync(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<SecurityMasterSnapshotEvidenceRow> rows,
        CancellationToken cancellationToken = default)
    {
        ValidateSecurityMasterSnapshotProvenance(request, rows);
        return PublishAsync(
            request,
            rows,
            EvidenceDatasetKind.SecurityMaster,
            codec.WriteSecurityMasterSnapshotsAsync,
            cancellationToken);
    }

    public Task<EvidenceDatasetPartitionManifest> PublishSymbolIntervalsAsync(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<SymbolIntervalEvidenceRow> rows,
        CancellationToken cancellationToken = default)
    {
        ValidateSymbolIntervalProvenance(request, rows);
        return PublishAsync(
            request,
            rows,
            EvidenceDatasetKind.SymbolIntervals,
            codec.WriteSymbolIntervalsAsync,
            cancellationToken);
    }

    public Task<EvidenceDatasetPartitionManifest> PublishCorporateActionsAsync(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<CorporateActionEvidenceRow> rows,
        CancellationToken cancellationToken = default)
    {
        ValidateCorporateActionProvenance(request, rows);
        return PublishAsync(
            request,
            rows,
            EvidenceDatasetKind.CorporateActions,
            codec.WriteCorporateActionsAsync,
            cancellationToken);
    }

    public Task<EvidenceDatasetPartitionManifest> PublishSecurityMasterAsync(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<SecurityMasterEvidenceRow> rows,
        CancellationToken cancellationToken = default)
    {
        ValidateSecurityMasterProvenance(request, rows);
        return PublishAsync(
            request,
            rows,
            EvidenceDatasetKind.SecurityMaster,
            codec.WriteSecurityMasterAsync,
            cancellationToken);
    }

    public Task<EvidenceDatasetPartitionManifest> PublishUniverseMembershipAsync(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<UniverseMembershipEvidenceRow> rows,
        CancellationToken cancellationToken = default)
    {
        ValidateUniverseProvenance(request, rows);
        return PublishAsync(
            request,
            rows,
            EvidenceDatasetKind.UniverseMembership,
            codec.WriteUniverseMembershipAsync,
            cancellationToken);
    }

    public Task<EvidenceDatasetPartitionManifest> PublishSipQuotesAsync(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<SipQuoteEvidenceRow> rows,
        CancellationToken cancellationToken = default)
    {
        ValidateSipQuoteProvenance(request, rows);
        return PublishAsync(
            request,
            rows,
            EvidenceDatasetKind.SipQuotes,
            codec.WriteSipQuotesAsync,
            cancellationToken);
    }

    public Task<EvidenceDatasetPartitionManifest> PublishExchangeSessionsAsync(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<ExchangeSessionEvidenceRow> rows,
        CancellationToken cancellationToken = default)
    {
        ValidateExchangeSessionProvenance(request, rows);
        return PublishAsync(
            request,
            rows,
            EvidenceDatasetKind.ExchangeSessions,
            codec.WriteExchangeSessionsAsync,
            cancellationToken);
    }

    public static EvidenceDatasetManifest BuildDatasetManifest(
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
        if (partitions.Any(partition =>
                !partition.Dimensions.TryGetValue("config_hash", out var partitionConfigHash) ||
                !partitionConfigHash.Equals(configHash, StringComparison.Ordinal) ||
                !partition.Dimensions.TryGetValue("data_feed", out var partitionDataFeed) ||
                !partitionDataFeed.Equals(dataFeed, StringComparison.Ordinal) ||
                !partition.Dimensions.ContainsKey("run_id")))
        {
            throw new ArgumentException(
                "Every partition must preserve matching run, config, and data-feed provenance.",
                nameof(partitions));
        }

        return new(
            kind,
            schemaVersion,
            createdAtUtc,
            collectionJobId,
            collectionPlanHash,
            configHash,
            codeVersion,
            normalizerVersion,
            dataFeed,
            partitions,
            quality,
            attributes);
    }

    private async Task<EvidenceDatasetPartitionManifest> PublishAsync<T>(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<T> rows,
        EvidenceDatasetKind expectedKind,
        Func<IReadOnlyCollection<T>, string, CancellationToken, Task<byte[]>> encode,
        CancellationToken cancellationToken)
        where T : INormalizedEvidenceRow
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rows);
        if (request.DatasetKind != expectedKind)
        {
            throw new ArgumentException(
                $"Partition request kind '{request.DatasetKind}' does not match '{expectedKind}'.",
                nameof(request));
        }

        if (rows.Count == 0 || rows.Any(row => row.SchemaVersion != request.SchemaVersion))
        {
            throw new ArgumentException(
                "Partition rows must be non-empty and match the requested schema version.",
                nameof(rows));
        }

        if (rows.Any(row =>
                !row.RunId.Equals(request.RunId, StringComparison.Ordinal) ||
                !row.ConfigHash.Equals(request.ConfigHash, StringComparison.Ordinal) ||
                !row.CodeVersion.Equals(request.CodeVersion, StringComparison.Ordinal) ||
                !row.DataFeed.Equals(request.Provenance.DataFeed, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Partition rows must match the requested run, config, code version, and data feed.",
                nameof(rows));
        }

        var sourceObservations = request.SourceObservations
            ?? throw new ArgumentException(
                "Partition source observations are required.",
                nameof(request));
        var sourceById = sourceObservations.ToDictionary(
            source => source.ObservationId,
            StringComparer.Ordinal);
        var usedIds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var source in row.Sources)
            {
                if (!sourceById.TryGetValue(source.ObservationId, out var reference) ||
                    !reference.Artifact.Content.Sha256.Equals(
                        source.ObservationSha256,
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Row source '{source.ObservationId}' is absent or has a conflicting hash.",
                        nameof(rows));
                }

                usedIds.Add(source.ObservationId);
            }
        }

        var bytes = await encode(rows, request.NormalizerVersion, cancellationToken);
        var artifact = new EvidenceArtifactReference(
            new EvidenceContentAddress(
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.LongLength,
                EvidenceParquetCodec.MediaType),
            request.ObjectNamespace);
        await artifactStore.PutIfAbsentAsync(
            new ImmutableArtifactWriteRequest(artifact),
            bytes,
            cancellationToken);
        var verification = await artifactStore.VerifyAsync(artifact, cancellationToken);
        if (!verification.IsValid)
        {
            throw new InvalidDataException(
                $"Published Parquet artifact failed verification: {verification.FailureReason}");
        }

        var minimumTimestamp = rows.Min(row => row.SourceTimestampUtc);
        var maximumTimestamp = rows.Max(row => row.SourceTimestampUtc);
        return new EvidenceDatasetPartitionManifest(
            request.PartitionId,
            request.DatasetKind,
            request.SchemaVersion,
            request.Provenance,
            minimumTimestamp,
            maximumTimestamp,
            rows.Count,
            artifact,
            usedIds.Select(id => sourceById[id]).ToArray(),
            request.NormalizerVersion,
            request.CodeVersion,
            request.Quality,
            request.QuarantineLedger,
            BuildDimensions(request));
    }

    private static void ValidateMarketBarProvenance(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<MarketBarEvidenceRow> rows)
    {
        ValidateInputs(request, rows);
        if (rows.Any(row =>
                !request.Provenance.SecurityIds.Contains(row.SecurityId, StringComparer.Ordinal) ||
                !request.Provenance.Symbols.Contains(row.Symbol, StringComparer.Ordinal) ||
                !request.Provenance.Timeframe.Equals(row.Timeframe, StringComparison.Ordinal) ||
                !request.Provenance.Adjustment.Equals(row.Adjustment, StringComparison.Ordinal) ||
                !request.Provenance.Currency.Equals(row.Currency, StringComparison.Ordinal) ||
                row.BarStartUtc < request.Provenance.RequestedStartUtc ||
                row.BarStartUtc >= request.Provenance.RequestedEndUtc))
        {
            throw new ArgumentException(
                "Market-bar rows do not match partition security, symbol, timeframe, adjustment, currency, or requested range.",
                nameof(rows));
        }
    }

    private static void ValidateNewsProvenance(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<NewsRevisionEvidenceRow> rows)
    {
        ValidateInputs(request, rows);
        if (rows.Any(row =>
                !row.Provider.Equals(request.Provenance.Provider, StringComparison.Ordinal) ||
                row.Symbols.Any(symbol =>
                    !request.Provenance.Symbols.Contains(symbol, StringComparer.Ordinal)) ||
                row.AvailabilityTimestampUtc < request.Provenance.RequestedStartUtc ||
                row.AvailabilityTimestampUtc >= request.Provenance.RequestedEndUtc))
        {
            throw new ArgumentException(
                "News revision rows do not match partition provider, symbols, or requested range.",
                nameof(rows));
        }
    }

    private static void ValidateSentimentProvenance(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<SentimentAssessmentEvidenceRow> rows)
    {
        ValidateInputs(request, rows);
        if (rows.Any(row =>
                !row.AssessmentProvider.Equals(
                    request.Provenance.Provider,
                    StringComparison.Ordinal) ||
                row.ObservedAtUtc < request.Provenance.RequestedStartUtc ||
                row.ObservedAtUtc >= request.Provenance.RequestedEndUtc))
        {
            throw new ArgumentException(
                "Sentiment assessment rows do not match partition provider or requested range.",
                nameof(rows));
        }
    }

    private static void ValidateClassifierGroundTruthProvenance(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<ClassifierGroundTruthEvidenceRow> rows)
    {
        ValidateInputs(request, rows);
        if (rows.Any(row =>
                !row.DataFeed.Equals(request.Provenance.DataFeed, StringComparison.Ordinal) ||
                row.ResolvedAtUtc < request.Provenance.RequestedStartUtc ||
                row.ResolvedAtUtc >= request.Provenance.RequestedEndUtc))
        {
            throw new ArgumentException(
                "Classifier ground-truth rows do not match the requested labeling feed or range.",
                nameof(rows));
        }
    }

    private static void ValidateSecurityMasterProvenance(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<SecurityMasterEvidenceRow> rows)
    {
        ValidateInputs(request, rows);
        if (rows.Any(row =>
                !row.Provider.Equals(request.Provenance.Provider, StringComparison.Ordinal) ||
                !request.Provenance.SecurityIds.Contains(row.SecurityId, StringComparer.Ordinal) ||
                !request.Provenance.IssuerIds.Contains(row.IssuerId, StringComparer.Ordinal) ||
                !request.Provenance.Symbols.Contains(row.Symbol, StringComparer.Ordinal) ||
                !request.Provenance.Currency.Equals(row.Currency, StringComparison.Ordinal) ||
                row.ProviderUpdatedAtUtc < request.Provenance.RequestedStartUtc ||
                row.ProviderUpdatedAtUtc >= request.Provenance.RequestedEndUtc))
        {
            throw new ArgumentException(
                "Security-master rows do not match partition provider, identity, currency, or requested range.",
                nameof(rows));
        }
    }

    private static void ValidateSecurityMasterSnapshotProvenance(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<SecurityMasterSnapshotEvidenceRow> rows)
    {
        ValidateInputs(request, rows);
        if (rows.Any(row =>
                !row.Provider.Equals(request.Provenance.Provider, StringComparison.Ordinal) ||
                !request.Provenance.SecurityIds.Contains(row.SecurityId, StringComparer.Ordinal) ||
                (row.IssuerId is not null &&
                 !request.Provenance.IssuerIds.Contains(row.IssuerId, StringComparer.Ordinal)) ||
                !request.Provenance.Symbols.Contains(row.Symbol, StringComparer.Ordinal) ||
                !request.Provenance.Currency.Equals(row.Currency, StringComparison.Ordinal) ||
                row.ObservedAtUtc < request.Provenance.RequestedStartUtc ||
                row.ObservedAtUtc >= request.Provenance.RequestedEndUtc))
        {
            throw new ArgumentException(
                "Security-master snapshot rows do not match partition provider, identity, currency, or requested range.",
                nameof(rows));
        }
    }

    private static void ValidateSymbolIntervalProvenance(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<SymbolIntervalEvidenceRow> rows)
    {
        ValidateInputs(request, rows);
        if (rows.Any(row =>
                !row.Provider.Equals(request.Provenance.Provider, StringComparison.Ordinal) ||
                !request.Provenance.SecurityIds.Contains(row.SecurityId, StringComparer.Ordinal) ||
                (row.IssuerId is not null &&
                 !request.Provenance.IssuerIds.Contains(row.IssuerId, StringComparer.Ordinal)) ||
                !request.Provenance.Symbols.Contains(row.Symbol, StringComparer.Ordinal) ||
                row.ObservedAtUtc < request.Provenance.RequestedStartUtc ||
                row.ObservedAtUtc >= request.Provenance.RequestedEndUtc))
        {
            throw new ArgumentException(
                "Symbol-interval rows do not match partition provider, identity, symbol, or requested range.",
                nameof(rows));
        }
    }

    private static void ValidateCorporateActionProvenance(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<CorporateActionEvidenceRow> rows)
    {
        ValidateInputs(request, rows);
        var requestedStartDate = DateOnly.FromDateTime(
            request.Provenance.RequestedStartUtc.UtcDateTime);
        var requestedEndDate = DateOnly.FromDateTime(
            request.Provenance.RequestedEndUtc.UtcDateTime);
        if (rows.Any(row =>
                !row.Provider.Equals(request.Provenance.Provider, StringComparison.Ordinal) ||
                (row.SecurityId is not null &&
                 !request.Provenance.SecurityIds.Contains(row.SecurityId, StringComparer.Ordinal)) ||
                (row.IssuerId is not null &&
                 !request.Provenance.IssuerIds.Contains(row.IssuerId, StringComparer.Ordinal)) ||
                (row.PrimarySymbol is not null &&
                 !request.Provenance.Symbols.Contains(row.PrimarySymbol, StringComparer.Ordinal)) ||
                row.ProcessDate < requestedStartDate ||
                row.ProcessDate >= requestedEndDate))
        {
            throw new ArgumentException(
                "Corporate-action rows do not match partition provider, available identity, symbol, or requested range.",
                nameof(rows));
        }
    }

    private static void ValidateUniverseProvenance(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<UniverseMembershipEvidenceRow> rows)
    {
        ValidateInputs(request, rows);
        if (rows.Any(row =>
                !row.Provider.Equals(request.Provenance.Provider, StringComparison.Ordinal) ||
                !request.Provenance.SecurityIds.Contains(row.SecurityId, StringComparer.Ordinal) ||
                !request.Provenance.IssuerIds.Contains(row.IssuerId, StringComparer.Ordinal) ||
                !request.Provenance.Symbols.Contains(row.Symbol, StringComparer.Ordinal) ||
                (request.Provenance.AsOfDate.HasValue &&
                 row.EffectiveSessionDate != request.Provenance.AsOfDate.Value) ||
                row.ProviderTimestampUtc < request.Provenance.RequestedStartUtc ||
                row.ProviderTimestampUtc >= request.Provenance.RequestedEndUtc))
        {
            throw new ArgumentException(
                "Universe-membership rows do not match partition provider, point-in-time identity, or requested range.",
                nameof(rows));
        }
    }

    private static void ValidateSipQuoteProvenance(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<SipQuoteEvidenceRow> rows)
    {
        ValidateInputs(request, rows);
        if (rows.Any(row =>
                !request.Provenance.SecurityIds.Contains(row.SecurityId, StringComparer.Ordinal) ||
                !request.Provenance.Symbols.Contains(row.Symbol, StringComparer.Ordinal) ||
                !request.Provenance.Currency.Equals(row.Currency, StringComparison.Ordinal) ||
                row.QuoteTimestampUtc < request.Provenance.RequestedStartUtc ||
                row.QuoteTimestampUtc >= request.Provenance.RequestedEndUtc))
        {
            throw new ArgumentException(
                "SIP quote rows do not match partition security, symbol, currency, or requested range.",
                nameof(rows));
        }
    }

    private static void ValidateExchangeSessionProvenance(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<ExchangeSessionEvidenceRow> rows)
    {
        ValidateInputs(request, rows);
        var coverageStart = DateOnly.FromDateTime(
            request.Provenance.RequestedStartUtc.UtcDateTime);
        var coverageEndExclusive = DateOnly.FromDateTime(
            request.Provenance.RequestedEndUtc.UtcDateTime);
        if (rows.Any(row =>
                !row.Provider.Equals(request.Provenance.Provider, StringComparison.Ordinal) ||
                row.TradeDate < coverageStart ||
                row.TradeDate >= coverageEndExclusive))
        {
            throw new ArgumentException(
                "Exchange-session rows do not match partition provider or requested calendar coverage.",
                nameof(rows));
        }
    }

    private static void ValidateInputs<T>(
        EvidenceParquetPartitionRequest request,
        IReadOnlyCollection<T> rows)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(request.Provenance);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CodeVersion);
        var configHash = request.ConfigHash?.Trim().ToLowerInvariant();
        if (configHash is null ||
            configHash.Length != 64 ||
            configHash.Any(character => !Uri.IsHexDigit(character)) ||
            !configHash.Equals(request.ConfigHash, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Partition config hash must be a canonical lowercase SHA-256 value.",
                nameof(request));
        }
    }

    private static IReadOnlyDictionary<string, string> BuildDimensions(
        EvidenceParquetPartitionRequest request)
    {
        var dimensions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in request.Dimensions ??
                     new Dictionary<string, string>())
        {
            if (!dimensions.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException(
                    $"Duplicate partition dimension '{pair.Key}'.",
                    nameof(request));
            }
        }

        AddRequiredDimension(dimensions, "run_id", request.RunId);
        AddRequiredDimension(dimensions, "config_hash", request.ConfigHash);
        AddRequiredDimension(dimensions, "code_version", request.CodeVersion);
        AddRequiredDimension(dimensions, "normalizer_version", request.NormalizerVersion);
        AddRequiredDimension(dimensions, "data_feed", request.Provenance.DataFeed);
        return dimensions;
    }

    private static void AddRequiredDimension(
        IDictionary<string, string> dimensions,
        string key,
        string expectedValue)
    {
        if (dimensions.TryGetValue(key, out var value))
        {
            if (!value.Equals(expectedValue, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Partition dimension '{key}' conflicts with normalized row provenance.");
            }

            return;
        }

        dimensions.Add(key, expectedValue);
    }
}
