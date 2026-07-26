using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Data.Evidence;

/// <summary>
/// Reads normalized evidence exclusively from immutable artifacts named by a committed
/// dataset manifest. Every artifact and every row-to-manifest lineage relationship is
/// verified before any rows are returned.
/// </summary>
public sealed class ParquetEvidencePartitionDataReader(
    EvidenceParquetCodec codec,
    IImmutableArtifactStore artifactStore) : IEvidencePartitionDataReader
{
    public Task<IReadOnlyList<MarketBarEvidenceRow>> ReadMarketBarsAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Kind switch
        {
            EvidenceDatasetKind.MarketBarsAsTraded => ReadAsync(
                manifest,
                EvidenceDatasetKind.MarketBarsAsTraded,
                codec.ReadMarketBarsAsTradedAsync,
                MarketBarKey,
                ValidateMarketBar,
                cancellationToken),
            EvidenceDatasetKind.MarketBarsResearchAdjusted => ReadAsync(
                manifest,
                EvidenceDatasetKind.MarketBarsResearchAdjusted,
                codec.ReadResearchAdjustedMarketBarsAsync,
                MarketBarKey,
                ValidateMarketBar,
                cancellationToken),
            _ => throw WrongKind(manifest.Kind, "market bars")
        };
    }

    public Task<IReadOnlyList<NewsRevisionEvidenceRow>> ReadNewsRevisionsAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken = default) =>
        ReadAsync(
            manifest,
            EvidenceDatasetKind.NewsRevisions,
            codec.ReadNewsRevisionsAsync,
            row => $"{row.Provider}\u001f{row.ProviderArticleId}\u001f{row.RevisionId}",
            ValidateNewsRevision,
            cancellationToken);

    public Task<IReadOnlyList<SentimentAssessmentEvidenceRow>> ReadSentimentAssessmentsAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken = default) =>
        ReadAsync(
            manifest,
            EvidenceDatasetKind.SentimentAssessments,
            codec.ReadSentimentAssessmentsAsync,
            row => row.AssessmentId,
            ValidateSentimentAssessment,
            cancellationToken);

    public Task<IReadOnlyList<ClassifierGroundTruthEvidenceRow>>
        ReadClassifierGroundTruthAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) =>
        ReadAsync(
            manifest,
            EvidenceDatasetKind.ClassifierGroundTruth,
            codec.ReadClassifierGroundTruthAsync,
            row => row.GroundTruthId,
            ValidateClassifierGroundTruth,
            cancellationToken);

    public Task<IReadOnlyList<SecurityMasterSnapshotEvidenceRow>>
        ReadSecurityMasterSnapshotsAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) =>
        ReadAsync(
            manifest,
            EvidenceDatasetKind.SecurityMaster,
            codec.ReadSecurityMasterSnapshotsAsync,
            row => $"{row.Provider}\u001f{row.SecurityId}\u001f{row.ObservedAtUtc:O}",
            ValidateSecurityMasterSnapshot,
            cancellationToken);

    public Task<IReadOnlyList<SymbolIntervalEvidenceRow>> ReadSymbolIntervalsAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken = default) =>
        ReadAsync(
            manifest,
            EvidenceDatasetKind.SymbolIntervals,
            codec.ReadSymbolIntervalsAsync,
            row => $"{row.Provider}\u001f{row.SecurityId}\u001f{row.Symbol}\u001f{row.ObservedAtUtc:O}",
            ValidateSymbolInterval,
            cancellationToken);

    public Task<IReadOnlyList<CorporateActionEvidenceRow>> ReadCorporateActionsAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken = default) =>
        ReadAsync(
            manifest,
            EvidenceDatasetKind.CorporateActions,
            codec.ReadCorporateActionsAsync,
            row => $"{row.Provider}\u001f{row.ActionType}\u001f{row.ProviderActionId}",
            ValidateCorporateAction,
            cancellationToken);

    public Task<IReadOnlyList<UniverseMembershipEvidenceRow>> ReadUniverseMembershipAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken = default) =>
        ReadAsync(
            manifest,
            EvidenceDatasetKind.UniverseMembership,
            codec.ReadUniverseMembershipAsync,
            row => $"{row.Provider}\u001f{row.UniverseId}\u001f{row.SnapshotId}\u001f{row.SecurityId}",
            ValidateUniverseMembership,
            cancellationToken);

    public Task<IReadOnlyList<SipQuoteEvidenceRow>> ReadSipQuotesAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken = default) =>
        ReadAsync(
            manifest,
            EvidenceDatasetKind.SipQuotes,
            codec.ReadSipQuotesAsync,
            row => $"{row.SecurityId}\u001f{row.QuoteTimestampUtc:O}",
            ValidateSipQuote,
            cancellationToken);

    public Task<IReadOnlyList<ExchangeSessionEvidenceRow>> ReadExchangeSessionsAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken = default) =>
        ReadAsync(
            manifest,
            EvidenceDatasetKind.ExchangeSessions,
            codec.ReadExchangeSessionsAsync,
            row => $"{row.Exchange}\u001f{row.TradeDate:yyyy-MM-dd}",
            ValidateExchangeSession,
            cancellationToken);

    private async Task<IReadOnlyList<T>> ReadAsync<T>(
        EvidenceDatasetManifest manifest,
        EvidenceDatasetKind supportedKind,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<EvidenceParquetDocument<T>>> decode,
        Func<T, string> logicalKey,
        Action<T, EvidenceDatasetPartitionManifest> validateTypeSpecific,
        CancellationToken cancellationToken)
        where T : INormalizedEvidenceRow
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Kind != supportedKind)
        {
            throw WrongKind(manifest.Kind, supportedKind.ToString());
        }

        var result = new List<T>();
        var logicalKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var partition in manifest.Partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePartitionEnvelope(manifest, partition);

            var verification = await artifactStore.VerifyAsync(
                partition.Artifact,
                cancellationToken);
            if (!verification.IsValid)
            {
                throw new InvalidDataException(
                    $"Evidence partition '{partition.PartitionId}' failed immutable-artifact verification: " +
                    $"{verification.FailureReason ?? "unknown verification failure"}.");
            }

            var content = await ReadVerifiedArtifactAsync(
                partition.Artifact,
                cancellationToken);
            var document = await decode(content, cancellationToken);
            ValidateDocumentMetadata(manifest, partition, document);
            ValidateRows(manifest, partition, document.Rows, validateTypeSpecific);

            foreach (var row in document.Rows)
            {
                var key = logicalKey(row);
                if (!logicalKeys.Add(key))
                {
                    throw new InvalidDataException(
                        $"Dataset '{manifest.DatasetId}' contains duplicate logical row key '{key}'.");
                }

                result.Add(row);
            }
        }

        return result.OrderBy(logicalKey, StringComparer.Ordinal).ToArray();
    }

    private async Task<byte[]> ReadVerifiedArtifactAsync(
        EvidenceArtifactReference artifact,
        CancellationToken cancellationToken)
    {
        if (!artifact.Content.MediaType.Equals(
                EvidenceParquetCodec.MediaType,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Evidence artifact media type '{artifact.Content.MediaType}' is not Parquet.");
        }

        if (artifact.Content.ByteLength <= 0 ||
            artifact.Content.ByteLength > Int32.MaxValue)
        {
            throw new InvalidDataException(
                $"Evidence artifact byte length '{artifact.Content.ByteLength}' cannot be read safely.");
        }

        await using var stream = await artifactStore.OpenReadAsync(
            artifact,
            cancellationToken);
        var content = GC.AllocateUninitializedArray<byte>((int)artifact.Content.ByteLength);
        var offset = 0;
        while (offset < content.Length)
        {
            var read = await stream.ReadAsync(
                content.AsMemory(offset, content.Length - offset),
                cancellationToken);
            if (read == 0)
            {
                throw new InvalidDataException(
                    "Immutable evidence artifact ended before its declared byte length.");
            }

            offset += read;
        }

        if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
        {
            throw new InvalidDataException(
                "Immutable evidence artifact exceeds its declared byte length.");
        }

        return content;
    }

    private static void ValidatePartitionEnvelope(
        EvidenceDatasetManifest manifest,
        EvidenceDatasetPartitionManifest partition)
    {
        if (partition.DatasetKind != manifest.Kind ||
            partition.SchemaVersion != manifest.SchemaVersion ||
            !partition.CodeVersion.Equals(manifest.CodeVersion, StringComparison.Ordinal) ||
            !partition.NormalizerVersion.Equals(manifest.NormalizerVersion, StringComparison.Ordinal) ||
            !partition.Provenance.DataFeed.Equals(manifest.DataFeed, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Partition '{partition.PartitionId}' does not match its dataset envelope.");
        }

        RequireDimension(partition, "config_hash", manifest.ConfigHash);
        RequireDimension(partition, "code_version", manifest.CodeVersion);
        RequireDimension(partition, "normalizer_version", manifest.NormalizerVersion);
        RequireDimension(partition, "data_feed", manifest.DataFeed);
        RequireDimensionValue(partition, "run_id");
    }

    private static void ValidateDocumentMetadata<T>(
        EvidenceDatasetManifest manifest,
        EvidenceDatasetPartitionManifest partition,
        EvidenceParquetDocument<T> document)
    {
        if (document.DatasetKind != manifest.Kind ||
            document.SchemaVersion != partition.SchemaVersion ||
            document.Rows.Count != partition.RowCount)
        {
            throw new InvalidDataException(
                $"Parquet metadata for partition '{partition.PartitionId}' conflicts with its manifest.");
        }

        RequireMetadata(document.Metadata, "tradingflow.run_id", partition.Dimensions["run_id"]);
        RequireMetadata(document.Metadata, "tradingflow.config_hash", manifest.ConfigHash);
        RequireMetadata(document.Metadata, "tradingflow.code_version", manifest.CodeVersion);
        RequireMetadata(document.Metadata, "tradingflow.normalizer_version", manifest.NormalizerVersion);
        RequireMetadata(document.Metadata, "tradingflow.data_feed", manifest.DataFeed);
    }

    private static void ValidateRows<T>(
        EvidenceDatasetManifest manifest,
        EvidenceDatasetPartitionManifest partition,
        IReadOnlyList<T> rows,
        Action<T, EvidenceDatasetPartitionManifest> validateTypeSpecific)
        where T : INormalizedEvidenceRow
    {
        if (rows.Count == 0 || rows.Count != partition.RowCount)
        {
            throw new InvalidDataException(
                $"Partition '{partition.PartitionId}' row count does not match its manifest.");
        }

        var sources = partition.SourceObservations.ToDictionary(
            source => source.ObservationId,
            StringComparer.Ordinal);
        var usedSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.SchemaVersion != manifest.SchemaVersion ||
                !row.ConfigHash.Equals(manifest.ConfigHash, StringComparison.Ordinal) ||
                !row.CodeVersion.Equals(manifest.CodeVersion, StringComparison.Ordinal) ||
                !row.DataFeed.Equals(manifest.DataFeed, StringComparison.Ordinal) ||
                !row.RunId.Equals(partition.Dimensions["run_id"], StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"A row in partition '{partition.PartitionId}' has conflicting schema or lineage metadata.");
            }

            if (row.SourceTimestampUtc < partition.MinimumSourceTimestampUtc ||
                row.SourceTimestampUtc > partition.MaximumSourceTimestampUtc)
            {
                throw new InvalidDataException(
                    $"A row in partition '{partition.PartitionId}' falls outside its source timestamp bounds.");
            }

            foreach (var source in row.Sources)
            {
                if (!sources.TryGetValue(source.ObservationId, out var manifestSource) ||
                    !manifestSource.Artifact.Content.Sha256.Equals(
                        source.ObservationSha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Row source '{source.ObservationId}' is absent from partition " +
                        $"'{partition.PartitionId}' or has a conflicting hash.");
                }

                usedSources.Add(source.ObservationId);
            }

            validateTypeSpecific(row, partition);
        }

        if (rows.Min(row => row.SourceTimestampUtc) != partition.MinimumSourceTimestampUtc ||
            rows.Max(row => row.SourceTimestampUtc) != partition.MaximumSourceTimestampUtc)
        {
            throw new InvalidDataException(
                $"Partition '{partition.PartitionId}' source timestamp extrema do not match its rows.");
        }

        if (!usedSources.SetEquals(sources.Keys))
        {
            throw new InvalidDataException(
                $"Partition '{partition.PartitionId}' contains source observations not used by its rows.");
        }
    }

    private static void ValidateMarketBar(
        MarketBarEvidenceRow row,
        EvidenceDatasetPartitionManifest partition)
    {
        var provenance = partition.Provenance;
        if (!provenance.SecurityIds.Contains(row.SecurityId, StringComparer.Ordinal) ||
            !provenance.Symbols.Contains(row.Symbol, StringComparer.Ordinal) ||
            !provenance.Timeframe.Equals(row.Timeframe, StringComparison.Ordinal) ||
            !provenance.Adjustment.Equals(row.Adjustment, StringComparison.Ordinal) ||
            !provenance.Currency.Equals(row.Currency, StringComparison.Ordinal) ||
            row.BarStartUtc < provenance.RequestedStartUtc ||
            row.BarStartUtc >= provenance.RequestedEndUtc)
        {
            throw new InvalidDataException(
                $"Market-bar row '{MarketBarKey(row)}' conflicts with partition provenance.");
        }
    }

    private static void ValidateNewsRevision(
        NewsRevisionEvidenceRow row,
        EvidenceDatasetPartitionManifest partition)
    {
        var provenance = partition.Provenance;
        if (!row.Provider.Equals(provenance.Provider, StringComparison.Ordinal) ||
            row.Symbols.Any(symbol => !provenance.Symbols.Contains(symbol, StringComparer.Ordinal)) ||
            row.ProviderUpdatedAtUtc < provenance.RequestedStartUtc ||
            row.ProviderUpdatedAtUtc >= provenance.RequestedEndUtc)
        {
            throw new InvalidDataException(
                $"News revision '{row.ProviderArticleId}/{row.RevisionId}' conflicts with partition provenance.");
        }
    }

    private static void ValidateSentimentAssessment(
        SentimentAssessmentEvidenceRow row,
        EvidenceDatasetPartitionManifest partition)
    {
        var provenance = partition.Provenance;
        if (!row.AssessmentProvider.Equals(provenance.Provider, StringComparison.Ordinal) ||
            row.ObservedAtUtc < provenance.RequestedStartUtc ||
            row.ObservedAtUtc >= provenance.RequestedEndUtc ||
            !partition.Dimensions.TryGetValue(
                "model_artifact_id",
                out var modelArtifactId) ||
            !row.ModelArtifactId.Equals(modelArtifactId, StringComparison.Ordinal) ||
            !partition.Dimensions.TryGetValue(
                "model_artifact_sha256",
                out var modelArtifactSha256) ||
            !row.ModelArtifactSha256.Equals(
                modelArtifactSha256,
                StringComparison.Ordinal) ||
            !partition.Dimensions.TryGetValue(
                "model_training_data_cutoff_utc",
                out var cutoffText) ||
            !DateTimeOffset.TryParse(
                cutoffText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var cutoff) ||
            cutoff != row.ModelTrainingDataCutoffUtc)
        {
            throw new InvalidDataException(
                $"Sentiment assessment '{row.AssessmentId}' conflicts with partition provenance.");
        }
    }

    private static void ValidateClassifierGroundTruth(
        ClassifierGroundTruthEvidenceRow row,
        EvidenceDatasetPartitionManifest partition)
    {
        var provenance = partition.Provenance;
        if (!row.DataFeed.Equals(provenance.DataFeed, StringComparison.Ordinal) ||
            row.ResolvedAtUtc < provenance.RequestedStartUtc ||
            row.ResolvedAtUtc >= provenance.RequestedEndUtc ||
            !partition.Dimensions.TryGetValue(
                "source_news_dataset_id",
                out var sourceNewsDatasetId) ||
            !row.SourceNewsDatasetId.Equals(sourceNewsDatasetId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Classifier ground truth '{row.GroundTruthId}' conflicts with partition provenance.");
        }
    }

    private static void ValidateUniverseMembership(
        UniverseMembershipEvidenceRow row,
        EvidenceDatasetPartitionManifest partition)
    {
        var provenance = partition.Provenance;
        if (!row.Provider.Equals(provenance.Provider, StringComparison.Ordinal) ||
            !provenance.SecurityIds.Contains(row.SecurityId, StringComparer.Ordinal) ||
            !provenance.IssuerIds.Contains(row.IssuerId, StringComparer.Ordinal) ||
            !provenance.Symbols.Contains(row.Symbol, StringComparer.Ordinal) ||
            (provenance.AsOfDate.HasValue &&
             row.EffectiveSessionDate != provenance.AsOfDate.Value) ||
            row.ProviderTimestampUtc < provenance.RequestedStartUtc ||
            row.ProviderTimestampUtc >= provenance.RequestedEndUtc)
        {
            throw new InvalidDataException(
                $"Universe-membership row '{row.SnapshotId}/{row.SecurityId}' conflicts with partition provenance.");
        }
    }

    private static void ValidateSecurityMasterSnapshot(
        SecurityMasterSnapshotEvidenceRow row,
        EvidenceDatasetPartitionManifest partition)
    {
        var provenance = partition.Provenance;
        if (!row.Provider.Equals(provenance.Provider, StringComparison.Ordinal) ||
            !provenance.SecurityIds.Contains(row.SecurityId, StringComparer.Ordinal) ||
            (row.IssuerId is not null &&
             !provenance.IssuerIds.Contains(row.IssuerId, StringComparer.Ordinal)) ||
            !provenance.Symbols.Contains(row.Symbol, StringComparer.Ordinal) ||
            !provenance.Currency.Equals(row.Currency, StringComparison.Ordinal) ||
            row.ObservedAtUtc < provenance.RequestedStartUtc ||
            row.ObservedAtUtc >= provenance.RequestedEndUtc)
        {
            throw new InvalidDataException(
                $"Security-master snapshot '{row.Provider}/{row.SecurityId}/{row.ObservedAtUtc:O}' conflicts with partition provenance.");
        }
    }

    private static void ValidateSymbolInterval(
        SymbolIntervalEvidenceRow row,
        EvidenceDatasetPartitionManifest partition)
    {
        var provenance = partition.Provenance;
        if (!row.Provider.Equals(provenance.Provider, StringComparison.Ordinal) ||
            !provenance.SecurityIds.Contains(row.SecurityId, StringComparer.Ordinal) ||
            (row.IssuerId is not null &&
             !provenance.IssuerIds.Contains(row.IssuerId, StringComparer.Ordinal)) ||
            !provenance.Symbols.Contains(row.Symbol, StringComparer.Ordinal) ||
            row.ObservedAtUtc < provenance.RequestedStartUtc ||
            row.ObservedAtUtc >= provenance.RequestedEndUtc)
        {
            throw new InvalidDataException(
                $"Symbol interval '{row.Provider}/{row.SecurityId}/{row.Symbol}/{row.ObservedAtUtc:O}' conflicts with partition provenance.");
        }
    }

    private static void ValidateCorporateAction(
        CorporateActionEvidenceRow row,
        EvidenceDatasetPartitionManifest partition)
    {
        var provenance = partition.Provenance;
        var requestedStartDate = DateOnly.FromDateTime(
            provenance.RequestedStartUtc.UtcDateTime);
        var requestedEndDate = DateOnly.FromDateTime(
            provenance.RequestedEndUtc.UtcDateTime);
        if (!row.Provider.Equals(provenance.Provider, StringComparison.Ordinal) ||
            (row.SecurityId is not null &&
             !provenance.SecurityIds.Contains(row.SecurityId, StringComparer.Ordinal)) ||
            (row.IssuerId is not null &&
             !provenance.IssuerIds.Contains(row.IssuerId, StringComparer.Ordinal)) ||
            (row.PrimarySymbol is not null &&
             !provenance.Symbols.Contains(row.PrimarySymbol, StringComparer.Ordinal)) ||
            row.ProcessDate < requestedStartDate ||
            row.ProcessDate >= requestedEndDate)
        {
            throw new InvalidDataException(
                $"Corporate action '{row.Provider}/{row.ActionType}/{row.ProviderActionId}' conflicts with partition provenance.");
        }
    }

    private static void ValidateSipQuote(
        SipQuoteEvidenceRow row,
        EvidenceDatasetPartitionManifest partition)
    {
        var provenance = partition.Provenance;
        if (!provenance.SecurityIds.Contains(row.SecurityId, StringComparer.Ordinal) ||
            !provenance.Symbols.Contains(row.Symbol, StringComparer.Ordinal) ||
            !provenance.Currency.Equals(row.Currency, StringComparison.Ordinal) ||
            row.QuoteTimestampUtc < provenance.RequestedStartUtc ||
            row.QuoteTimestampUtc >= provenance.RequestedEndUtc)
        {
            throw new InvalidDataException(
                $"SIP quote row '{row.SecurityId}/{row.QuoteTimestampUtc:O}' conflicts with partition provenance.");
        }
    }

    private static void ValidateExchangeSession(
        ExchangeSessionEvidenceRow row,
        EvidenceDatasetPartitionManifest partition)
    {
        var provenance = partition.Provenance;
        var coverageStart = DateOnly.FromDateTime(
            provenance.RequestedStartUtc.UtcDateTime);
        var coverageEndExclusive = DateOnly.FromDateTime(
            provenance.RequestedEndUtc.UtcDateTime);
        if (!row.Provider.Equals(provenance.Provider, StringComparison.Ordinal) ||
            row.TradeDate < coverageStart ||
            row.TradeDate >= coverageEndExclusive)
        {
            throw new InvalidDataException(
                $"Exchange session '{row.Exchange}/{row.TradeDate:yyyy-MM-dd}' conflicts with partition provenance.");
        }
    }

    private static string MarketBarKey(MarketBarEvidenceRow row) =>
        $"{row.SecurityId}\u001f{row.Timeframe}\u001f{row.BarStartUtc:O}";

    private static void RequireDimension(
        EvidenceDatasetPartitionManifest partition,
        string key,
        string expected)
    {
        if (!partition.Dimensions.TryGetValue(key, out var value) ||
            !value.Equals(expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Partition '{partition.PartitionId}' dimension '{key}' conflicts with its dataset.");
        }
    }

    private static void RequireDimensionValue(
        EvidenceDatasetPartitionManifest partition,
        string key)
    {
        if (!partition.Dimensions.TryGetValue(key, out var value) ||
            String.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"Partition '{partition.PartitionId}' dimension '{key}' is required.");
        }
    }

    private static void RequireMetadata(
        IReadOnlyDictionary<string, string> metadata,
        string key,
        string expected)
    {
        if (!metadata.TryGetValue(key, out var value) ||
            !value.Equals(expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Parquet metadata '{key}' does not match the committed manifest.");
        }
    }

    private static InvalidDataException WrongKind(
        EvidenceDatasetKind actual,
        string expected) =>
        new($"Dataset kind '{actual}' is not supported by the {expected} reader.");
}
