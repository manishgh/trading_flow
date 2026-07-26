using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Data.Evidence.Normalization;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Research.Orchestration;

public sealed record IdentityEvidencePublicationRequest
{
    public IdentityEvidencePublicationRequest(
        string collectionJobId,
        int schemaVersion,
        string normalizerVersion,
        EvidenceObjectNamespace outputNamespace,
        IReadOnlyDictionary<string, string>? attributes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionJobId);
        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(normalizerVersion);
        CollectionJobId = collectionJobId.Trim();
        SchemaVersion = schemaVersion;
        NormalizerVersion = normalizerVersion.Trim();
        OutputNamespace = outputNamespace ??
            throw new ArgumentNullException(nameof(outputNamespace));
        Attributes = new SortedDictionary<string, string>(
            (attributes ?? new Dictionary<string, string>())
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    public string CollectionJobId { get; }

    public int SchemaVersion { get; }

    public string NormalizerVersion { get; }

    public EvidenceObjectNamespace OutputNamespace { get; }

    public IReadOnlyDictionary<string, string> Attributes { get; }
}

public sealed record IdentityEvidencePublicationResult(
    string CollectionJobId,
    string SecurityMasterDatasetId,
    string SymbolIntervalsDatasetId,
    string CorporateActionsDatasetId,
    bool AlreadyCommitted,
    long SecurityCount,
    long SymbolIntervalCount,
    long CorporateActionCount);

/// <summary>
/// Converts one completed Alpaca identity collection into a coherent immutable identity bundle.
/// Every raw receipt is verified before parsing. The three datasets become catalog-visible in
/// one serializable transaction; a crash before that point leaves only recoverable orphan objects.
/// </summary>
public sealed class IdentityEvidencePublicationCoordinator
{
    private readonly IEvidenceCatalog catalog;
    private readonly IAtomicEvidenceDatasetCatalog atomicCatalog;
    private readonly IImmutableArtifactStore artifactStore;
    private readonly EvidenceParquetPartitionPublisher publisher;
    private readonly AlpacaAssetEvidenceNormalizer assetNormalizer;
    private readonly AlpacaCorporateActionEvidenceNormalizer actionNormalizer;
    private readonly VerifiedEvidenceSourceLoader sourceLoader;
    private readonly TimeProvider timeProvider;

    public IdentityEvidencePublicationCoordinator(
        IEvidenceCatalog catalog,
        IAtomicEvidenceDatasetCatalog atomicCatalog,
        IImmutableArtifactStore artifactStore,
        EvidenceParquetPartitionPublisher publisher,
        AlpacaAssetEvidenceNormalizer? assetNormalizer = null,
        AlpacaCorporateActionEvidenceNormalizer? actionNormalizer = null,
        TimeProvider? timeProvider = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.atomicCatalog = atomicCatalog ??
            throw new ArgumentNullException(nameof(atomicCatalog));
        this.artifactStore = artifactStore ??
            throw new ArgumentNullException(nameof(artifactStore));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.assetNormalizer = assetNormalizer ?? new AlpacaAssetEvidenceNormalizer();
        this.actionNormalizer = actionNormalizer ??
            new AlpacaCorporateActionEvidenceNormalizer();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        sourceLoader = new VerifiedEvidenceSourceLoader(catalog, artifactStore);
    }

    public async Task<IdentityEvidencePublicationResult> PublishAsync(
        IdentityEvidencePublicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var lease = await AsyncRequestGate.AcquireAsync(
            $"identity:{request.CollectionJobId}",
            cancellationToken);

        var plan = await catalog.GetCollectionPlanAsync(
            request.CollectionJobId,
            cancellationToken) ??
            throw new InvalidOperationException(
                $"Identity collection plan '{request.CollectionJobId}' was not found.");
        var layout = ValidatePlan(plan);
        var checkpoint = await catalog.GetCollectionCheckpointAsync(
            plan.JobId,
            cancellationToken) ??
            throw new InvalidOperationException(
                $"Identity collection '{plan.JobId}' has no lifecycle checkpoint.");
        if (checkpoint.State == EvidenceCollectionState.Committed)
        {
            return await FindCommittedResultAsync(
                request,
                plan,
                layout,
                cancellationToken);
        }

        if (checkpoint.State == EvidenceCollectionState.Collecting)
        {
            await RequireExhaustedRequestsAsync(plan, cancellationToken);
            await SaveCheckpointAsync(
                plan,
                EvidenceCollectionState.Normalizing,
                checkpoint.CompletedRequestIds,
                cancellationToken);
        }
        else if (checkpoint.State is not (
                     EvidenceCollectionState.Normalizing or
                     EvidenceCollectionState.Validating))
        {
            throw new InvalidOperationException(
                $"Identity collection state '{checkpoint.State}' cannot be published.");
        }

        var verifiedByRequest =
            new Dictionary<string, IReadOnlyList<VerifiedSourceObservation>>(
                StringComparer.Ordinal);
        foreach (var collectionRequest in plan.Requests)
        {
            var observations = await catalog.FindSourceObservationsAsync(
                plan.JobId,
                collectionRequest.RequestId,
                cancellationToken);
            if (observations.Count == 0)
            {
                throw new InvalidDataException(
                    $"Identity request '{collectionRequest.RequestId}' has no committed raw observations.");
            }

            var verified = new List<VerifiedSourceObservation>(observations.Count);
            foreach (var observation in observations)
            {
                verified.Add(await sourceLoader.LoadAsync(
                    observation,
                    cancellationToken));
            }

            verifiedByRequest.Add(collectionRequest.RequestId, verified);
        }

        var authoritativeByRequest =
            new Dictionary<string, IReadOnlyList<VerifiedSourceObservation>>(
                StringComparer.Ordinal);
        foreach (var collectionRequest in plan.Requests)
        {
            authoritativeByRequest.Add(
                collectionRequest.RequestId,
                await SelectAuthoritativePagesAsync(
                    plan,
                    collectionRequest,
                    verifiedByRequest[collectionRequest.RequestId],
                    cancellationToken));
        }

        var assetPage = authoritativeByRequest[layout.AssetRequest.RequestId].Single();
        var identity = assetNormalizer.Normalize(
            assetPage.Observation,
            assetPage.Bytes,
            request.SchemaVersion);
        var corporateRowsByRequest =
            new Dictionary<string, IReadOnlyList<CorporateActionEvidenceRow>>(
                StringComparer.Ordinal);
        var actionKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var actionRequest in layout.ActionRequests)
        {
            var rows = authoritativeByRequest[actionRequest.RequestId]
                .SelectMany(page => actionNormalizer.Normalize(
                    page.Observation,
                    page.Bytes,
                    request.SchemaVersion))
                .OrderBy(row => row.ProcessDate)
                .ThenBy(row => row.ActionType)
                .ThenBy(row => row.ProviderActionId, StringComparer.Ordinal)
                .ToArray();
            foreach (var row in rows)
            {
                var key = $"{row.ActionType}|{row.ProviderActionId}";
                if (!actionKeys.Add(key))
                {
                    throw new InvalidDataException(
                        $"Corporate action '{key}' is duplicated across authoritative pages.");
                }
            }

            corporateRowsByRequest.Add(actionRequest.RequestId, rows);
        }

        if (corporateRowsByRequest.Values.Sum(rows => rows.Count) == 0)
        {
            throw new InvalidDataException(
                "The identity collection produced no corporate-action rows; an empty normalized dataset cannot be committed.");
        }

        // Validation begins before any normalized partition or manifest is published.
        // This makes crash recovery accurately distinguish normalization from publication.
        await SaveCheckpointAsync(
            plan,
            EvidenceCollectionState.Validating,
            checkpoint.CompletedRequestIds,
            cancellationToken);

        var manifests = await BuildManifestsAsync(
            request,
            plan,
            layout,
            assetPage,
            identity,
            authoritativeByRequest,
            corporateRowsByRequest,
            cancellationToken);
        var commits = await atomicCatalog.CommitDatasetsAtomicallyAsync(
            manifests,
            cancellationToken);
        foreach (var commit in commits)
        {
            _ = await catalog.GetDatasetAsync(commit.Id, cancellationToken) ??
                throw new InvalidDataException(
                    $"Committed identity dataset '{commit.Id}' is not readable.");
        }

        await SaveCheckpointAsync(
            plan,
            EvidenceCollectionState.Committed,
            plan.Requests.Select(value => value.RequestId).ToArray(),
            cancellationToken);
        return new IdentityEvidencePublicationResult(
            plan.JobId,
            commits[0].Id,
            commits[1].Id,
            commits[2].Id,
            commits.All(commit => commit.AlreadyCommitted),
            identity.Securities.Count,
            identity.SymbolIntervals.Count,
            corporateRowsByRequest.Values.Sum(rows => (long)rows.Count));
    }

    private async Task<IReadOnlyList<EvidenceDatasetManifest>> BuildManifestsAsync(
        IdentityEvidencePublicationRequest request,
        EvidenceCollectionPlan plan,
        IdentityCollectionLayout layout,
        VerifiedSourceObservation assetPage,
        SecurityMasterSnapshotNormalizationResult identity,
        IReadOnlyDictionary<string, IReadOnlyList<VerifiedSourceObservation>>
            authoritativeByRequest,
        IReadOnlyDictionary<string, IReadOnlyList<CorporateActionEvidenceRow>>
            corporateRowsByRequest,
        CancellationToken cancellationToken)
    {
        var assetSource = new[] { assetPage.Observation.ToReference() };
        var assetStart = assetPage.Observation.ReceivedAtUtc;
        var assetEnd = assetStart.AddTicks(1);
        var assetProvenance = new EvidencePartitionProvenance(
            "alpaca",
            layout.AssetRequest.Endpoint,
            layout.AssetRequest.DataFeed,
            layout.AssetRequest.Adjustment,
            layout.AssetRequest.Currency,
            layout.AssetRequest.AsOfDate,
            identity.Securities.Select(row => row.SecurityId).ToArray(),
            identity.Securities
                .Where(row => row.IssuerId is not null)
                .Select(row => row.IssuerId!)
                .ToArray(),
            identity.Securities.Select(row => row.Symbol).ToArray(),
            "snapshot",
            assetStart,
            assetEnd);
        var baseDimensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["collection_request_id"] = layout.AssetRequest.RequestId,
            ["identity_bundle_job_id"] = plan.JobId
        };
        var securityPartition = await publisher.PublishSecurityMasterSnapshotsAsync(
            PartitionRequest(
                request,
                plan,
                "security-master-current",
                EvidenceDatasetKind.SecurityMaster,
                assetProvenance,
                assetSource,
                baseDimensions),
            identity.Securities,
            cancellationToken);
        var intervalPartition = await publisher.PublishSymbolIntervalsAsync(
            PartitionRequest(
                request,
                plan,
                "symbol-intervals-current",
                EvidenceDatasetKind.SymbolIntervals,
                assetProvenance,
                assetSource,
                baseDimensions),
            identity.SymbolIntervals,
            cancellationToken);

        var actionPartitions = new List<EvidenceDatasetPartitionManifest>();
        foreach (var actionRequest in layout.ActionRequests)
        {
            var rows = corporateRowsByRequest[actionRequest.RequestId];
            if (rows.Count == 0)
            {
                continue;
            }

            var pages = authoritativeByRequest[actionRequest.RequestId];
            var symbols = rows
                .Select(row => row.PrimarySymbol)
                .Where(symbol => symbol is not null)
                .Select(symbol => symbol!)
                .Concat(actionRequest.Symbols)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(symbol => symbol, StringComparer.Ordinal)
                .ToArray();
            if (symbols.Length == 0)
            {
                throw new InvalidDataException(
                    $"Corporate-action request '{actionRequest.RequestId}' has no symbol provenance.");
            }

            var provenance = new EvidencePartitionProvenance(
                "alpaca",
                actionRequest.Endpoint,
                actionRequest.DataFeed,
                actionRequest.Adjustment,
                actionRequest.Currency,
                actionRequest.AsOfDate,
                [],
                [],
                symbols,
                "event",
                actionRequest.RequestedStartUtc!.Value,
                actionRequest.RequestedEndUtc!.Value);
            actionPartitions.Add(await publisher.PublishCorporateActionsAsync(
                PartitionRequest(
                    request,
                    plan,
                    $"corporate-actions-{actionRequest.RequestId}",
                    EvidenceDatasetKind.CorporateActions,
                    provenance,
                    pages.Select(page => page.Observation.ToReference()).ToArray(),
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["collection_request_id"] = actionRequest.RequestId,
                        ["identity_bundle_job_id"] = plan.JobId,
                        ["authoritative_page_count"] =
                            pages.Count.ToString(CultureInfo.InvariantCulture)
                    }),
                rows,
                cancellationToken));
        }

        var attributes = new SortedDictionary<string, string>(
            request.Attributes.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal),
            StringComparer.Ordinal)
        {
            ["identity_bundle_job_id"] = plan.JobId,
            ["raw_verified_before_parse"] = "true",
            ["atomic_catalog_visibility"] = "true"
        };
        var quality = new EvidenceQualityReport();
        return
        [
            EvidenceParquetPartitionPublisher.BuildDatasetManifest(
                EvidenceDatasetKind.SecurityMaster,
                request.SchemaVersion,
                plan.CreatedAtUtc,
                plan.JobId,
                plan.LogicalPlanHash,
                plan.ConfigHash,
                plan.CodeVersion,
                request.NormalizerVersion,
                layout.AssetRequest.DataFeed,
                [securityPartition],
                quality,
                attributes),
            EvidenceParquetPartitionPublisher.BuildDatasetManifest(
                EvidenceDatasetKind.SymbolIntervals,
                request.SchemaVersion,
                plan.CreatedAtUtc,
                plan.JobId,
                plan.LogicalPlanHash,
                plan.ConfigHash,
                plan.CodeVersion,
                request.NormalizerVersion,
                layout.AssetRequest.DataFeed,
                [intervalPartition],
                quality,
                attributes),
            EvidenceParquetPartitionPublisher.BuildDatasetManifest(
                EvidenceDatasetKind.CorporateActions,
                request.SchemaVersion,
                plan.CreatedAtUtc,
                plan.JobId,
                plan.LogicalPlanHash,
                plan.ConfigHash,
                plan.CodeVersion,
                request.NormalizerVersion,
                layout.ActionRequests[0].DataFeed,
                actionPartitions,
                quality,
                attributes)
        ];
    }

    private static EvidenceParquetPartitionRequest PartitionRequest(
        IdentityEvidencePublicationRequest request,
        EvidenceCollectionPlan plan,
        string partitionId,
        EvidenceDatasetKind kind,
        EvidencePartitionProvenance provenance,
        IReadOnlyList<EvidenceSourceReference> sources,
        IReadOnlyDictionary<string, string> dimensions) =>
        new(
            partitionId,
            kind,
            request.SchemaVersion,
            provenance,
            sources,
            plan.RunId,
            plan.ConfigHash,
            request.NormalizerVersion,
            plan.CodeVersion,
            new EvidenceQualityReport(),
            request.OutputNamespace,
            Dimensions: dimensions);

    private async Task<IdentityEvidencePublicationResult> FindCommittedResultAsync(
        IdentityEvidencePublicationRequest request,
        EvidenceCollectionPlan plan,
        IdentityCollectionLayout layout,
        CancellationToken cancellationToken)
    {
        async Task<EvidenceDatasetManifest> FindAsync(
            EvidenceDatasetKind kind,
            string feed)
        {
            var matches = (await catalog.FindDatasetsAsync(
                    new EvidenceDatasetQuery(kind, feed),
                    cancellationToken))
                .Where(dataset =>
                    dataset.CollectionJobId.Equals(plan.JobId, StringComparison.Ordinal) &&
                    dataset.CollectionPlanHash.Equals(
                        plan.LogicalPlanHash,
                        StringComparison.Ordinal) &&
                    dataset.NormalizerVersion.Equals(
                        request.NormalizerVersion,
                        StringComparison.Ordinal))
                .ToArray();
            return matches.Length == 1
                ? matches[0]
                : throw new InvalidDataException(
                    $"Committed identity collection '{plan.JobId}' has {matches.Length} " +
                    $"matching '{kind}' datasets.");
        }

        var security = await FindAsync(
            EvidenceDatasetKind.SecurityMaster,
            layout.AssetRequest.DataFeed);
        var intervals = await FindAsync(
            EvidenceDatasetKind.SymbolIntervals,
            layout.AssetRequest.DataFeed);
        var actions = await FindAsync(
            EvidenceDatasetKind.CorporateActions,
            layout.ActionRequests[0].DataFeed);
        return new IdentityEvidencePublicationResult(
            plan.JobId,
            security.DatasetId,
            intervals.DatasetId,
            actions.DatasetId,
            true,
            security.Partitions.Sum(partition => partition.RowCount),
            intervals.Partitions.Sum(partition => partition.RowCount),
            actions.Partitions.Sum(partition => partition.RowCount));
    }

    private async Task<IReadOnlyList<VerifiedSourceObservation>>
        SelectAuthoritativePagesAsync(
            EvidenceCollectionPlan plan,
            EvidenceCollectionRequest request,
            IReadOnlyList<VerifiedSourceObservation> receipts,
            CancellationToken cancellationToken)
    {
        var cursor = await catalog.GetRequestCursorCheckpointAsync(
            plan.JobId,
            request.RequestId,
            cancellationToken);
        if (cursor?.Exhausted != true)
        {
            throw new InvalidDataException(
                $"Identity request '{request.RequestId}' has not exhausted pagination.");
        }

        var byPage = receipts
            .GroupBy(receipt => PageOrdinal(receipt.Observation))
            .ToDictionary(group => group.Key, group => group.ToArray());
        if (byPage.Keys.Any(page => page < 1 || page > cursor.PageOrdinal) ||
            Enumerable.Range(1, cursor.PageOrdinal).Any(page => !byPage.ContainsKey(page)))
        {
            throw new InvalidDataException(
                $"Identity request '{request.RequestId}' does not match its exhausted page chain.");
        }

        var authoritative = new List<VerifiedSourceObservation>(cursor.PageOrdinal);
        for (var page = 1; page <= cursor.PageOrdinal; page++)
        {
            var successful = byPage[page]
                .Where(receipt => receipt.Observation.TransportStatusCode is >= 200 and < 300)
                .ToArray();
            var hashes = successful
                .Select(receipt => receipt.Observation.Artifact.Content.Sha256)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (hashes.Length != 1)
            {
                throw new InvalidDataException(
                    $"Identity request '{request.RequestId}' page {page} has no single authoritative response.");
            }

            var selected = successful
                .OrderBy(receipt => receipt.Observation.ReceivedAtUtc)
                .ThenBy(receipt => receipt.Observation.ObservationId, StringComparer.Ordinal)
                .First();
            if (page == cursor.PageOrdinal)
            {
                selected = successful.SingleOrDefault(receipt =>
                    receipt.Observation.ObservationId.Equals(
                        cursor.ObservationId,
                        StringComparison.Ordinal)) ??
                    throw new InvalidDataException(
                        $"Identity request '{request.RequestId}' terminal cursor observation is missing.");
            }

            if (request.Endpoint.EndsWith(
                    "/v1/corporate-actions",
                    StringComparison.OrdinalIgnoreCase))
            {
                var nextPageToken = ReadNextPageToken(selected);
                if (page < cursor.PageOrdinal)
                {
                    if (nextPageToken is null ||
                        !ComputeSha256(nextPageToken).Equals(
                            cursor.ConsumedPageTokenHashes[page - 1],
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Identity request '{request.RequestId}' page {page} breaks the cursor chain.");
                    }
                }
                else if (nextPageToken is not null)
                {
                    throw new InvalidDataException(
                        $"Identity request '{request.RequestId}' terminal page still has a cursor.");
                }
            }
            else if (cursor.PageOrdinal != 1)
            {
                throw new InvalidDataException(
                    "The Alpaca current asset snapshot must be one authoritative page.");
            }

            authoritative.Add(selected);
        }

        return authoritative;
    }

    private async Task RequireExhaustedRequestsAsync(
        EvidenceCollectionPlan plan,
        CancellationToken cancellationToken)
    {
        foreach (var request in plan.Requests)
        {
            var cursor = await catalog.GetRequestCursorCheckpointAsync(
                plan.JobId,
                request.RequestId,
                cancellationToken);
            if (cursor?.Exhausted != true)
            {
                throw new InvalidOperationException(
                    $"Identity request '{request.RequestId}' has not exhausted pagination.");
            }
        }
    }

    private async Task SaveCheckpointAsync(
        EvidenceCollectionPlan plan,
        EvidenceCollectionState state,
        IReadOnlyList<string> completedRequestIds,
        CancellationToken cancellationToken)
    {
        var current = await catalog.GetCollectionCheckpointAsync(
            plan.JobId,
            cancellationToken);
        if (current?.State == state || current?.State == EvidenceCollectionState.Committed)
        {
            return;
        }

        await catalog.SaveCollectionCheckpointAsync(
            new EvidenceCollectionCheckpoint(
                plan.JobId,
                state,
                timeProvider.GetUtcNow(),
                completedRequestIds),
            cancellationToken);
    }

    private static IdentityCollectionLayout ValidatePlan(EvidenceCollectionPlan plan)
    {
        var assetRequests = plan.Requests
            .Where(request => request.Endpoint.EndsWith(
                "/v2/assets",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var actionRequests = plan.Requests
            .Where(request => request.Endpoint.EndsWith(
                "/v1/corporate-actions",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(request => request.RequestedStartUtc)
            .ThenBy(request => request.RequestId, StringComparer.Ordinal)
            .ToArray();
        if (assetRequests.Length != 1 ||
            actionRequests.Length == 0 ||
            assetRequests.Length + actionRequests.Length != plan.Requests.Count)
        {
            throw new InvalidOperationException(
                "An identity collection requires exactly one Alpaca asset request and at least one corporate-action request.");
        }

        var asset = assetRequests[0];
        if (!asset.Provider.Equals("alpaca", StringComparison.Ordinal) ||
            asset.RequestedStartUtc is not null ||
            asset.RequestedEndUtc is not null ||
            asset.Symbols.Count != 0 ||
            !asset.DataFeed.Equals("alpaca-trading", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Alpaca current asset request has invalid point-in-time semantics.");
        }

        if (actionRequests.Any(request =>
                !request.Provider.Equals("alpaca", StringComparison.Ordinal) ||
                request.RequestedStartUtc is null ||
                request.RequestedEndUtc is null ||
                !request.DataFeed.Equals(
                    actionRequests[0].DataFeed,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Corporate-action requests must be homogeneous ranged Alpaca evidence.");
        }

        return new IdentityCollectionLayout(asset, actionRequests);
    }

    private static int PageOrdinal(EvidenceSourceObservation observation)
    {
        if (!observation.RequestAttributes.TryGetValue("page_ordinal", out var value) ||
            !Int32.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var page) ||
            page <= 0)
        {
            throw new InvalidDataException(
                $"Observation '{observation.ObservationId}' has no valid page ordinal.");
        }

        return page;
    }

    private static string? ReadNextPageToken(VerifiedSourceObservation page)
    {
        using var document = JsonDocument.Parse(page.Bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Observation '{page.Observation.ObservationId}' must contain an object payload with pagination metadata.");
        }

        if (!document.RootElement.TryGetProperty("next_page_token", out var token))
        {
            throw new InvalidDataException(
                $"Observation '{page.Observation.ObservationId}' has no next-page token field.");
        }

        return token.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String when !String.IsNullOrWhiteSpace(token.GetString()) =>
                token.GetString()!.Trim(),
            _ => throw new InvalidDataException(
                $"Observation '{page.Observation.ObservationId}' has invalid pagination metadata.")
        };
    }

    private static string ComputeSha256(string value) =>
        Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed record IdentityCollectionLayout(
        EvidenceCollectionRequest AssetRequest,
        IReadOnlyList<EvidenceCollectionRequest> ActionRequests);
}
