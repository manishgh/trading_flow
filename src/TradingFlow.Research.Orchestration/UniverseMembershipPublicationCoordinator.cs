using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;
using TradingFlow.Research.Universe;

namespace TradingFlow.Research.Orchestration;

public sealed record UniverseMembershipPublicationRequest
{
    public UniverseMembershipPublicationRequest(
        string securityMasterDatasetId,
        string symbolIntervalsDatasetId,
        string corporateActionsDatasetId,
        int schemaVersion,
        string runId,
        string configHash,
        string codeVersion,
        string normalizerVersion,
        string dataFeed,
        string provider,
        string currency,
        string universeId,
        string snapshotId,
        string providerQuery,
        string snapshotContentSha256,
        DateOnly effectiveSessionDate,
        DateTimeOffset asOfUtc,
        DateTimeOffset snapshotProviderTimestampUtc,
        DateTimeOffset snapshotReceivedAtUtc,
        IReadOnlyList<PointInTimeUniverseCandidate> candidates,
        IReadOnlyList<EvidenceRowSourceAddress> snapshotSources,
        EvidenceObjectNamespace outputNamespace,
        int? maximumMembers = null)
    {
        SecurityMasterDatasetId = Require(
            securityMasterDatasetId,
            nameof(securityMasterDatasetId));
        SymbolIntervalsDatasetId = Require(
            symbolIntervalsDatasetId,
            nameof(symbolIntervalsDatasetId));
        CorporateActionsDatasetId = Require(
            corporateActionsDatasetId,
            nameof(corporateActionsDatasetId));
        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        SchemaVersion = schemaVersion;
        RunId = Require(runId, nameof(runId));
        ConfigHash = RequireHash(configHash, nameof(configHash));
        CodeVersion = Require(codeVersion, nameof(codeVersion));
        NormalizerVersion = Require(normalizerVersion, nameof(normalizerVersion));
        DataFeed = Require(dataFeed, nameof(dataFeed)).ToLowerInvariant();
        Provider = Require(provider, nameof(provider)).ToLowerInvariant();
        Currency = Require(currency, nameof(currency)).ToUpperInvariant();
        UniverseId = Require(universeId, nameof(universeId));
        SnapshotId = Require(snapshotId, nameof(snapshotId));
        ProviderQuery = Require(providerQuery, nameof(providerQuery));
        SnapshotContentSha256 = RequireHash(
            snapshotContentSha256,
            nameof(snapshotContentSha256));
        EffectiveSessionDate = effectiveSessionDate;
        AsOfUtc = RequireUtc(asOfUtc, nameof(asOfUtc));
        SnapshotProviderTimestampUtc = RequireUtc(
            snapshotProviderTimestampUtc,
            nameof(snapshotProviderTimestampUtc));
        SnapshotReceivedAtUtc = RequireUtc(
            snapshotReceivedAtUtc,
            nameof(snapshotReceivedAtUtc));
        if (snapshotReceivedAtUtc < snapshotProviderTimestampUtc)
        {
            throw new ArgumentException(
                "Universe snapshot receipt cannot precede its provider timestamp.",
                nameof(snapshotReceivedAtUtc));
        }

        Candidates = (candidates ?? throw new ArgumentNullException(nameof(candidates)))
            .ToArray();
        SnapshotSources = (snapshotSources ??
                throw new ArgumentNullException(nameof(snapshotSources)))
            .ToArray();
        if (SnapshotSources.Count == 0)
        {
            throw new ArgumentException(
                "Universe publication requires verified raw snapshot lineage.",
                nameof(snapshotSources));
        }

        if (!SnapshotSources.Any(source =>
                source.ObservationSha256.Equals(
                    SnapshotContentSha256,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Snapshot lineage must include the exact snapshot content SHA-256.",
                nameof(snapshotSources));
        }

        if (maximumMembers is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumMembers));
        }

        MaximumMembers = maximumMembers;
        OutputNamespace = outputNamespace ??
            throw new ArgumentNullException(nameof(outputNamespace));
    }

    public string SecurityMasterDatasetId { get; }

    public string SymbolIntervalsDatasetId { get; }

    public string CorporateActionsDatasetId { get; }

    public int SchemaVersion { get; }

    public string RunId { get; }

    public string ConfigHash { get; }

    public string CodeVersion { get; }

    public string NormalizerVersion { get; }

    public string DataFeed { get; }

    public string Provider { get; }

    public string Currency { get; }

    public string UniverseId { get; }

    public string SnapshotId { get; }

    public string ProviderQuery { get; }

    public string SnapshotContentSha256 { get; }

    public DateOnly EffectiveSessionDate { get; }

    public DateTimeOffset AsOfUtc { get; }

    public DateTimeOffset SnapshotProviderTimestampUtc { get; }

    public DateTimeOffset SnapshotReceivedAtUtc { get; }

    public IReadOnlyList<PointInTimeUniverseCandidate> Candidates { get; }

    public IReadOnlyList<EvidenceRowSourceAddress> SnapshotSources { get; }

    public int? MaximumMembers { get; }

    public EvidenceObjectNamespace OutputNamespace { get; }

    internal string RequestKey => EvidenceCanonicalJson.ComputeSha256(new
    {
        SecurityMasterDatasetId,
        SymbolIntervalsDatasetId,
        CorporateActionsDatasetId,
        SchemaVersion,
        RunId,
        ConfigHash,
        CodeVersion,
        NormalizerVersion,
        DataFeed,
        Provider,
        Currency,
        UniverseId,
        SnapshotId,
        ProviderQuery,
        SnapshotContentSha256,
        EffectiveSessionDate,
        AsOfUtc,
        SnapshotProviderTimestampUtc,
        SnapshotReceivedAtUtc,
        Candidates,
        SnapshotSources,
        MaximumMembers,
        OutputNamespace
    });

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static string RequireHash(string value, string parameterName)
    {
        var normalized = Require(value, parameterName).ToLowerInvariant();
        if (normalized.Length != 64 ||
            normalized.Any(character => !Char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException(
                "Value must be a SHA-256 hex digest.",
                parameterName);
        }

        return normalized;
    }

    private static DateTimeOffset RequireUtc(
        DateTimeOffset value,
        string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Timestamp must be a non-default UTC value.",
                parameterName);
        }

        return value;
    }
}

public sealed record UniverseMembershipPublicationResult(
    bool Published,
    string? DatasetId,
    bool AlreadyCommitted,
    int MembershipCount,
    PointInTimeUniverseReadinessReport Readiness,
    IReadOnlyList<string> PublicationBlockers);

/// <summary>
/// Builds universe membership from catalog-committed, artifact-verified identity datasets and
/// an exact verified raw snapshot lineage. The builder runs before any membership object is
/// published, so an identity/history blocker leaves zero partial membership visibility.
/// </summary>
public sealed class UniverseMembershipPublicationCoordinator
{
    private readonly IEvidenceCatalog catalog;
    private readonly IEvidencePartitionDataReader dataReader;
    private readonly IImmutableArtifactStore artifactStore;
    private readonly EvidenceParquetPartitionPublisher publisher;
    private readonly PointInTimeUniverseBuilder builder;
    private readonly VerifiedEvidenceSourceLoader sourceLoader;

    public UniverseMembershipPublicationCoordinator(
        IEvidenceCatalog catalog,
        IEvidencePartitionDataReader dataReader,
        IImmutableArtifactStore artifactStore,
        EvidenceParquetPartitionPublisher publisher,
        PointInTimeUniverseBuilder? builder = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.dataReader = dataReader ?? throw new ArgumentNullException(nameof(dataReader));
        this.artifactStore = artifactStore ??
            throw new ArgumentNullException(nameof(artifactStore));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.builder = builder ?? new PointInTimeUniverseBuilder();
        sourceLoader = new VerifiedEvidenceSourceLoader(catalog, artifactStore);
    }

    public async Task<UniverseMembershipPublicationResult> PublishAsync(
        UniverseMembershipPublicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var lease = await AsyncRequestGate.AcquireAsync(
            $"universe:{request.RequestKey}",
            cancellationToken);

        var securityManifest = await RequireDatasetAsync(
            request.SecurityMasterDatasetId,
            EvidenceDatasetKind.SecurityMaster,
            cancellationToken);
        var intervalManifest = await RequireDatasetAsync(
            request.SymbolIntervalsDatasetId,
            EvidenceDatasetKind.SymbolIntervals,
            cancellationToken);
        var actionManifest = await RequireDatasetAsync(
            request.CorporateActionsDatasetId,
            EvidenceDatasetKind.CorporateActions,
            cancellationToken);
        var securityRows = await dataReader.ReadSecurityMasterSnapshotsAsync(
            securityManifest,
            cancellationToken);
        var intervalRows = await dataReader.ReadSymbolIntervalsAsync(
            intervalManifest,
            cancellationToken);
        var actionRows = await dataReader.ReadCorporateActionsAsync(
            actionManifest,
            cancellationToken);

        var snapshotObservations = await ResolveSnapshotObservationsAsync(
            request,
            cancellationToken);
        var buildResult = builder.Build(new PointInTimeUniverseBuildRequest(
            request.SchemaVersion,
            request.RunId,
            request.ConfigHash,
            request.CodeVersion,
            request.DataFeed,
            request.Provider,
            request.UniverseId,
            request.SnapshotId,
            request.ProviderQuery,
            request.SnapshotContentSha256,
            request.EffectiveSessionDate,
            request.AsOfUtc,
            request.SnapshotProviderTimestampUtc,
            request.SnapshotReceivedAtUtc,
            request.Candidates,
            securityRows,
            intervalRows,
            actionRows,
            request.SnapshotSources,
            request.MaximumMembers));
        if (!buildResult.Readiness.IsReady)
        {
            return Blocked(buildResult.Readiness, buildResult.Readiness.Failures.Select(
                failure =>
                    $"{failure.Code}:{failure.Subject}:{failure.Detail}"));
        }

        if (buildResult.Memberships.Count == 0)
        {
            return Blocked(
                buildResult.Readiness,
                ["No universe membership rows were produced."]);
        }

        // Raw row lineage is scoped to the universe snapshot collection attempt.
        // Identity lineage is anchored by the exact committed dataset IDs recorded
        // on both the partition and dataset manifests.
        var publicationRows = buildResult.Memberships
            .Select(row => new UniverseMembershipEvidenceRow(
                row.SchemaVersion,
                row.RunId,
                row.ConfigHash,
                row.CodeVersion,
                row.DataFeed,
                row.Provider,
                row.UniverseId,
                row.SnapshotId,
                row.ProviderQuery,
                row.SnapshotContentSha256,
                row.EffectiveSessionDate,
                row.SecurityId,
                row.IssuerId,
                row.Symbol,
                row.AsOfUtc,
                row.Included,
                row.Rank,
                row.SelectionReason,
                row.ProviderTimestampUtc,
                row.ReceivedAtUtc,
                request.SnapshotSources))
            .ToArray();
        var sourceReferences = await sourceLoader.ResolveAsync(
            publicationRows.SelectMany(row => row.Sources),
            cancellationToken);
        var minimumProviderTimestamp = publicationRows
            .Min(row => row.ProviderTimestampUtc);
        var maximumProviderTimestamp = publicationRows
            .Max(row => row.ProviderTimestampUtc);
        var provenance = new EvidencePartitionProvenance(
            request.Provider,
            snapshotObservations[0].Endpoint,
            request.DataFeed,
            "raw",
            request.Currency,
            request.EffectiveSessionDate,
            publicationRows.Select(row => row.SecurityId).ToArray(),
            publicationRows.Select(row => row.IssuerId).ToArray(),
            publicationRows.Select(row => row.Symbol).ToArray(),
            "session",
            minimumProviderTimestamp,
            maximumProviderTimestamp.AddTicks(1));
        var partition = await publisher.PublishUniverseMembershipAsync(
            new EvidenceParquetPartitionRequest(
                $"universe-{request.UniverseId}-{request.EffectiveSessionDate:yyyyMMdd}",
                EvidenceDatasetKind.UniverseMembership,
                request.SchemaVersion,
                provenance,
                sourceReferences,
                request.RunId,
                request.ConfigHash,
                request.NormalizerVersion,
                request.CodeVersion,
                new EvidenceQualityReport(),
                request.OutputNamespace,
                Dimensions: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["security_master_dataset_id"] = securityManifest.DatasetId,
                    ["symbol_intervals_dataset_id"] = intervalManifest.DatasetId,
                    ["corporate_actions_dataset_id"] = actionManifest.DatasetId,
                    ["snapshot_id"] = request.SnapshotId
                }),
            publicationRows,
            cancellationToken);
        var manifest = EvidenceParquetPartitionPublisher.BuildDatasetManifest(
            EvidenceDatasetKind.UniverseMembership,
            request.SchemaVersion,
            request.SnapshotReceivedAtUtc,
            snapshotObservations[0].CollectionJobId,
            snapshotObservations[0].CollectionPlanHash,
            request.ConfigHash,
            request.CodeVersion,
            request.NormalizerVersion,
            request.DataFeed,
            [partition],
            new EvidenceQualityReport(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["corporate_actions_dataset_id"] = actionManifest.DatasetId,
                ["identity_readiness"] = "ready",
                ["security_master_dataset_id"] = securityManifest.DatasetId,
                ["snapshot_id"] = request.SnapshotId,
                ["symbol_intervals_dataset_id"] = intervalManifest.DatasetId,
                ["zero_partial_visibility"] = "true"
            });
        var commit = await catalog.CommitDatasetAsync(manifest, cancellationToken);
        var committed = await catalog.GetDatasetAsync(commit.Id, cancellationToken) ??
            throw new InvalidDataException(
                $"Committed universe dataset '{commit.Id}' is not readable.");
        return new UniverseMembershipPublicationResult(
            true,
            committed.DatasetId,
            commit.AlreadyCommitted,
            publicationRows.Length,
            buildResult.Readiness,
            []);
    }

    private async Task<EvidenceDatasetManifest> RequireDatasetAsync(
        string datasetId,
        EvidenceDatasetKind expectedKind,
        CancellationToken cancellationToken)
    {
        var manifest = await catalog.GetDatasetAsync(datasetId, cancellationToken) ??
            throw new InvalidDataException(
                $"Required committed dataset '{datasetId}' was not found.");
        if (manifest.Kind != expectedKind || !manifest.Quality.Passed)
        {
            throw new InvalidDataException(
                $"Dataset '{datasetId}' is not passed '{expectedKind}' evidence.");
        }

        return manifest;
    }

    private async Task<IReadOnlyList<EvidenceSourceObservation>>
        ResolveSnapshotObservationsAsync(
            UniverseMembershipPublicationRequest request,
            CancellationToken cancellationToken)
    {
        var observations = new List<EvidenceSourceObservation>();
        foreach (var address in request.SnapshotSources
                     .Distinct()
                     .OrderBy(value => value.ObservationId, StringComparer.Ordinal))
        {
            var observation = await catalog.GetSourceObservationAsync(
                address.ObservationId,
                cancellationToken) ??
                throw new InvalidDataException(
                    $"Universe snapshot observation '{address.ObservationId}' is not catalog committed.");
            if (!observation.Artifact.Content.Sha256.Equals(
                    address.ObservationSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Universe snapshot observation '{address.ObservationId}' has a conflicting hash.");
            }

            _ = await sourceLoader.LoadAsync(observation, cancellationToken);
            observations.Add(observation);
        }

        if (observations.Select(value => value.CollectionJobId)
                .Distinct(StringComparer.Ordinal)
                .Count() != 1 ||
            observations.Select(value => value.CollectionPlanHash)
                .Distinct(StringComparer.Ordinal)
                .Count() != 1 ||
            observations.Any(value =>
                !value.Provider.Equals(request.Provider, StringComparison.Ordinal)) ||
            !observations.Any(value =>
                value.Artifact.Content.Sha256.Equals(
                    request.SnapshotContentSha256,
                    StringComparison.Ordinal) &&
                value.ReceivedAtUtc == request.SnapshotReceivedAtUtc))
        {
            throw new InvalidDataException(
                "Universe snapshot lineage does not identify one exact provider observation set.");
        }

        return observations;
    }

    private static UniverseMembershipPublicationResult Blocked(
        PointInTimeUniverseReadinessReport readiness,
        IEnumerable<string> blockers) =>
        new(
            false,
            null,
            false,
            0,
            readiness,
            blockers
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray());
}
