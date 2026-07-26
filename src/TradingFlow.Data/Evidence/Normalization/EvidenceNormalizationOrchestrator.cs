using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Data.Evidence.Normalization;

public enum EvidenceNormalizationPipelineFailureCode
{
    CollectionNotReady = 1,
    MissingRawObservation = 2,
    IncompletePagination = 3,
    ConflictingSuccessfulReceipts = 4,
    UnsupportedDatasetKind = 5,
    HeterogeneousCollectionPlan = 6,
    MissingTimeframe = 7,
    DuplicateLogicalRow = 8,
    NoNormalizedRows = 9
}

public sealed class EvidenceNormalizationPipelineException : Exception
{
    public EvidenceNormalizationPipelineException(
        EvidenceNormalizationPipelineFailureCode code,
        string jobId,
        string? requestId,
        string? observationId,
        string message)
        : base(message)
    {
        Code = code;
        JobId = jobId;
        RequestId = requestId;
        ObservationId = observationId;
    }

    public EvidenceNormalizationPipelineFailureCode Code { get; }

    public string JobId { get; }

    public string? RequestId { get; }

    public string? ObservationId { get; }
}

public sealed record EvidenceNormalizationJob
{
    internal const string ContractHashAttribute = "normalization_contract_sha256";

    public EvidenceNormalizationJob(
        string jobId,
        EvidenceDatasetKind datasetKind,
        int schemaVersion,
        string normalizerVersion,
        IReadOnlyCollection<EvidenceSecurityIdentity> securities,
        EvidenceObjectNamespace outputNamespace,
        NewsAvailabilityEvidence? newsAvailabilityEvidence = null,
        IReadOnlyDictionary<string, string>? attributes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        if (!Enum.IsDefined(datasetKind))
        {
            throw new ArgumentOutOfRangeException(nameof(datasetKind));
        }

        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(normalizerVersion);
        JobId = jobId.Trim();
        DatasetKind = datasetKind;
        SchemaVersion = schemaVersion;
        NormalizerVersion = normalizerVersion.Trim();
        Securities = (securities ?? throw new ArgumentNullException(nameof(securities)))
            .Select(security => security ??
                throw new ArgumentException("Security identities cannot contain null.", nameof(securities)))
            .OrderBy(security => security.Symbol, StringComparer.Ordinal)
            .ToArray();
        if (Securities.Count == 0 &&
            DatasetKind != EvidenceDatasetKind.ExchangeSessions)
        {
            throw new ArgumentException("At least one security identity is required.", nameof(securities));
        }

        OutputNamespace = outputNamespace ??
            throw new ArgumentNullException(nameof(outputNamespace));
        NewsAvailabilityEvidence = newsAvailabilityEvidence;
        Attributes = CopySorted(attributes);
        ContractHash = ComputeContractHash(
            DatasetKind,
            SchemaVersion,
            NormalizerVersion,
            Securities,
            OutputNamespace,
            NewsAvailabilityEvidence,
            Attributes);
    }

    public string JobId { get; }

    public EvidenceDatasetKind DatasetKind { get; }

    public int SchemaVersion { get; }

    public string NormalizerVersion { get; }

    public IReadOnlyCollection<EvidenceSecurityIdentity> Securities { get; }

    public EvidenceObjectNamespace OutputNamespace { get; }

    public NewsAvailabilityEvidence? NewsAvailabilityEvidence { get; }

    public IReadOnlyDictionary<string, string> Attributes { get; }

    public string ContractHash { get; }

    public static string ComputeContractHash(
        EvidenceDatasetKind datasetKind,
        int schemaVersion,
        string normalizerVersion,
        IReadOnlyCollection<EvidenceSecurityIdentity> securities,
        EvidenceObjectNamespace outputNamespace,
        NewsAvailabilityEvidence? newsAvailabilityEvidence,
        IReadOnlyDictionary<string, string>? attributes)
    {
        if (!Enum.IsDefined(datasetKind))
        {
            throw new ArgumentOutOfRangeException(nameof(datasetKind));
        }

        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(normalizerVersion);
        var normalizedSecurities =
            (securities ?? throw new ArgumentNullException(nameof(securities)))
            .Select(security => security ??
                throw new ArgumentException(
                    "Security identities cannot contain null.",
                    nameof(securities)))
            .OrderBy(security => security.Symbol, StringComparer.Ordinal)
            .ThenBy(security => security.SecurityId, StringComparer.Ordinal)
            .ToArray();
        var normalizedAttributes = CopySorted(attributes);
        return EvidenceCanonicalJson.ComputeSha256(new
        {
            DatasetKind = datasetKind,
            SchemaVersion = schemaVersion,
            NormalizerVersion = normalizerVersion.Trim(),
            Securities = normalizedSecurities,
            OutputNamespace = outputNamespace ??
                throw new ArgumentNullException(nameof(outputNamespace)),
            NewsAvailabilityEvidence = newsAvailabilityEvidence,
            Attributes = normalizedAttributes
        });
    }

    private static IReadOnlyDictionary<string, string> CopySorted(
        IReadOnlyDictionary<string, string>? values)
    {
        var copy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values ?? new Dictionary<string, string>())
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Value);
            copy.Add(pair.Key.Trim(), pair.Value.Trim());
        }

        return copy;
    }
}

public sealed record EvidenceNormalizationResult(
    string JobId,
    string DatasetId,
    bool AlreadyCommitted,
    int PartitionCount,
    long RowCount);

/// <summary>
/// Converts one homogeneous collection job into one immutable normalized dataset. Raw receipts
/// are all verified before parsing starts. Partition objects may be orphaned by a crash, but no
/// normalized dataset becomes discoverable until every partition has passed and its manifest is
/// committed by the catalog.
/// </summary>
public sealed class EvidenceNormalizationOrchestrator
{
    private static readonly ConcurrentDictionary<string, NormalizationJobGate> JobLocks =
        new(StringComparer.Ordinal);

    private readonly IEvidenceCatalog catalog;
    private readonly IImmutableArtifactStore artifactStore;
    private readonly EvidenceParquetPartitionPublisher publisher;
    private readonly AlpacaMarketBarEvidenceNormalizer marketBarNormalizer;
    private readonly AlpacaNewsEvidenceNormalizer newsNormalizer;
    private readonly AlpacaSipQuoteEvidenceNormalizer quoteNormalizer;
    private readonly AlpacaExchangeCalendarEvidenceNormalizer calendarNormalizer;
    private readonly TimeProvider timeProvider;

    public EvidenceNormalizationOrchestrator(
        IEvidenceCatalog catalog,
        IImmutableArtifactStore artifactStore,
        EvidenceParquetPartitionPublisher publisher,
        AlpacaMarketBarEvidenceNormalizer? marketBarNormalizer = null,
        AlpacaNewsEvidenceNormalizer? newsNormalizer = null,
        AlpacaSipQuoteEvidenceNormalizer? quoteNormalizer = null,
        AlpacaExchangeCalendarEvidenceNormalizer? calendarNormalizer = null,
        TimeProvider? timeProvider = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.marketBarNormalizer = marketBarNormalizer ?? new AlpacaMarketBarEvidenceNormalizer();
        this.newsNormalizer = newsNormalizer ?? new AlpacaNewsEvidenceNormalizer();
        this.quoteNormalizer = quoteNormalizer ?? new AlpacaSipQuoteEvidenceNormalizer();
        this.calendarNormalizer =
            calendarNormalizer ?? new AlpacaExchangeCalendarEvidenceNormalizer();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<EvidenceNormalizationResult> NormalizeAsync(
        EvidenceNormalizationJob job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        using var lease = await AcquireJobGateAsync(job.JobId, cancellationToken);
        return await NormalizeCoreAsync(job, cancellationToken);
    }

    private async Task<EvidenceNormalizationResult> NormalizeCoreAsync(
        EvidenceNormalizationJob job,
        CancellationToken cancellationToken)
    {
        var plan = await catalog.GetCollectionPlanAsync(job.JobId, cancellationToken) ??
            throw Failure(
                EvidenceNormalizationPipelineFailureCode.CollectionNotReady,
                job.JobId,
                null,
                null,
                "The collection plan does not exist.");
        ValidatePlan(job, plan);

        var checkpoint = await catalog.GetCollectionCheckpointAsync(job.JobId, cancellationToken) ??
            throw Failure(
                EvidenceNormalizationPipelineFailureCode.CollectionNotReady,
                job.JobId,
                null,
                null,
                "The collection checkpoint does not exist.");
        if (checkpoint.State == EvidenceCollectionState.Committed)
        {
            return await FindCommittedResultAsync(job, plan, cancellationToken);
        }

        if (checkpoint.State == EvidenceCollectionState.Collecting)
        {
            await EnsureEveryRequestExhaustedAsync(plan, cancellationToken);
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
            throw Failure(
                EvidenceNormalizationPipelineFailureCode.CollectionNotReady,
                job.JobId,
                null,
                null,
                $"Collection state '{checkpoint.State}' cannot be normalized.");
        }

        var allObservations =
            new Dictionary<string, IReadOnlyList<EvidenceSourceObservation>>(StringComparer.Ordinal);
        foreach (var request in plan.Requests)
        {
            var observations = await catalog.FindSourceObservationsAsync(
                plan.JobId,
                request.RequestId,
                cancellationToken);
            if (observations.Count == 0)
            {
                throw Failure(
                    EvidenceNormalizationPipelineFailureCode.MissingRawObservation,
                    plan.JobId,
                    request.RequestId,
                    null,
                    "No committed raw observations were found.");
            }

            allObservations.Add(request.RequestId, observations);
        }

        try
        {
            var partitions = new List<EvidenceDatasetPartitionManifest>(plan.Requests.Count);
            var rawReceiptCount = 0;
            var retryReceiptCount = 0;
            foreach (var request in plan.Requests)
            {
                // Bound memory to one request's response chain. Historical jobs can
                // contain hundreds of requests, so retaining every raw body until
                // final manifest publication would scale with the entire dataset.
                var observations = allObservations[request.RequestId];
                var receipts = new List<RawReceipt>(observations.Count);
                foreach (var observation in observations)
                {
                    receipts.Add(new RawReceipt(
                        observation,
                        await ReadVerifiedRawAsync(observation, cancellationToken)));
                }

                rawReceiptCount += receipts.Count;
                var authoritative = await SelectAuthoritativePagesAsync(
                    plan,
                    request,
                    receipts,
                    cancellationToken);
                retryReceiptCount += receipts.Count - authoritative.Count;
                partitions.Add(await NormalizeRequestAsync(
                    job,
                    plan,
                    request,
                    authoritative,
                    cancellationToken));
            }

            var current = await catalog.GetCollectionCheckpointAsync(plan.JobId, cancellationToken) ??
                throw new InvalidOperationException("Collection checkpoint disappeared.");
            if (current.State == EvidenceCollectionState.Normalizing)
            {
                await SaveCheckpointAsync(
                    plan,
                    EvidenceCollectionState.Validating,
                    current.CompletedRequestIds,
                    cancellationToken);
            }
            else if (current.State == EvidenceCollectionState.Committed)
            {
                return await FindCommittedResultAsync(job, plan, cancellationToken);
            }
            else if (current.State != EvidenceCollectionState.Validating)
            {
                throw Failure(
                    EvidenceNormalizationPipelineFailureCode.CollectionNotReady,
                    plan.JobId,
                    null,
                    null,
                    $"Collection moved to unexpected state '{current.State}'.");
            }

            var attributes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in job.Attributes)
            {
                attributes.Add(pair.Key, pair.Value);
            }

            attributes.Add(
                "raw_receipt_count",
                rawReceiptCount.ToString(CultureInfo.InvariantCulture));
            attributes.Add(
                "retry_receipt_count",
                retryReceiptCount.ToString(CultureInfo.InvariantCulture));
            attributes.Add("normalization_atomic_visibility", "manifest_commit");
            attributes.Add("raw_verified_before_parse", "true");
            attributes[EvidenceNormalizationJob.ContractHashAttribute] = job.ContractHash;
            var manifest = EvidenceParquetPartitionPublisher.BuildDatasetManifest(
                job.DatasetKind,
                job.SchemaVersion,
                plan.CreatedAtUtc,
                plan.JobId,
                plan.LogicalPlanHash,
                plan.ConfigHash,
                plan.CodeVersion,
                job.NormalizerVersion,
                plan.Requests[0].DataFeed,
                partitions,
                new EvidenceQualityReport(),
                attributes);
            var commit = await catalog.CommitDatasetAsync(manifest, cancellationToken);
            var committedManifest = await catalog.GetDatasetAsync(commit.Id, cancellationToken) ??
                throw new InvalidDataException(
                    $"Committed dataset '{commit.Id}' is not readable.");
            await SaveCheckpointAsync(
                plan,
                EvidenceCollectionState.Committed,
                plan.Requests.Select(request => request.RequestId).ToArray(),
                cancellationToken);
            return new EvidenceNormalizationResult(
                plan.JobId,
                committedManifest.DatasetId,
                commit.AlreadyCommitted,
                committedManifest.Partitions.Count,
                committedManifest.Partitions.Sum(partition => partition.RowCount));
        }
        catch (EvidenceNormalizationException exception)
        {
            await QuarantineAsync(
                plan,
                allObservations,
                $"normalization_{exception.Code.ToString().ToLowerInvariant()}",
                exception.ObservationId,
                exception.Message,
                cancellationToken);
            throw;
        }
        catch (EvidenceNormalizationPipelineException exception)
        {
            await QuarantineAsync(
                plan,
                allObservations,
                $"normalization_{exception.Code.ToString().ToLowerInvariant()}",
                exception.ObservationId,
                exception.Message,
                cancellationToken);
            throw;
        }
    }

    private async Task<EvidenceDatasetPartitionManifest> NormalizeRequestAsync(
        EvidenceNormalizationJob job,
        EvidenceCollectionPlan plan,
        EvidenceCollectionRequest request,
        IReadOnlyList<RawReceipt> pages,
        CancellationToken cancellationToken)
    {
        var asOfDate = request.AsOfDate ??
            throw Failure(
                EvidenceNormalizationPipelineFailureCode.HeterogeneousCollectionPlan,
                plan.JobId,
                request.RequestId,
                pages[0].Observation.ObservationId,
                "Alpaca normalization requires the plan's point-in-time as-of date.");
        var context = new AlpacaEvidenceNormalizationContext(
            job.SchemaVersion,
            request.DataFeed,
            request.Currency,
            asOfDate,
            job.Securities,
            allowEmptySecurities: job.DatasetKind == EvidenceDatasetKind.ExchangeSessions);
        var sourceReferences = pages
            .Select(page => page.Observation.ToReference())
            .ToArray();
        var timeframe = ResolveTimeframe(job.DatasetKind, request, plan.JobId);
        var providerTimeframe = job.DatasetKind is
            EvidenceDatasetKind.MarketBarsAsTraded or
            EvidenceDatasetKind.MarketBarsResearchAdjusted
                ? request.Parameters["timeframe"].Trim()
                : timeframe;
        var provenanceSymbols = job.DatasetKind == EvidenceDatasetKind.ExchangeSessions
            ? ["US_EQUITIES"]
            : request.Symbols;
        var provenance = new EvidencePartitionProvenance(
            request.Provider,
            request.Endpoint,
            request.DataFeed,
            request.Adjustment,
            request.Currency,
            request.AsOfDate,
            request.Symbols.Select(symbol =>
                context.SecuritiesBySymbol[symbol].SecurityId).ToArray(),
            [],
            provenanceSymbols,
            timeframe,
            request.RequestedStartUtc!.Value,
            request.RequestedEndUtc!.Value);
        var partitionRequest = new EvidenceParquetPartitionRequest(
            $"request-{request.RequestId}",
            job.DatasetKind,
            job.SchemaVersion,
            provenance,
            sourceReferences,
            plan.RunId,
            plan.ConfigHash,
            job.NormalizerVersion,
            plan.CodeVersion,
            new EvidenceQualityReport(),
            job.OutputNamespace,
            Dimensions: BuildPartitionDimensions(job.DatasetKind, request, pages.Count));

        return job.DatasetKind switch
        {
            EvidenceDatasetKind.MarketBarsAsTraded =>
                await publisher.PublishMarketBarsAsTradedAsync(
                    partitionRequest,
                    NormalizeBars(
                        pages,
                        context,
                        request.Adjustment,
                        providerTimeframe,
                        plan.JobId,
                        request.RequestId),
                    cancellationToken),
            EvidenceDatasetKind.MarketBarsResearchAdjusted =>
                await publisher.PublishResearchAdjustedMarketBarsAsync(
                    partitionRequest,
                    NormalizeBars(
                        pages,
                        context,
                        request.Adjustment,
                        providerTimeframe,
                        plan.JobId,
                        request.RequestId),
                    cancellationToken),
            EvidenceDatasetKind.NewsRevisions =>
                await publisher.PublishNewsRevisionsAsync(
                    partitionRequest,
                    NormalizeNews(pages, context, job, plan.JobId, request.RequestId),
                    cancellationToken),
            EvidenceDatasetKind.SipQuotes =>
                await publisher.PublishSipQuotesAsync(
                    partitionRequest,
                    NormalizeQuotes(pages, context, plan.JobId, request.RequestId),
                    cancellationToken),
            EvidenceDatasetKind.ExchangeSessions =>
                await publisher.PublishExchangeSessionsAsync(
                    partitionRequest,
                    NormalizeExchangeSessions(
                        pages,
                        context,
                        plan.JobId,
                        request.RequestId),
                    cancellationToken),
            _ => throw Failure(
                EvidenceNormalizationPipelineFailureCode.UnsupportedDatasetKind,
                plan.JobId,
                request.RequestId,
                pages[0].Observation.ObservationId,
                $"Dataset kind '{job.DatasetKind}' is not supported by Alpaca normalization.")
        };
    }

    private IReadOnlyList<MarketBarEvidenceRow> NormalizeBars(
        IReadOnlyList<RawReceipt> pages,
        AlpacaEvidenceNormalizationContext context,
        string adjustment,
        string timeframe,
        string jobId,
        string requestId)
    {
        var rows = pages
            .SelectMany(page => marketBarNormalizer.Normalize(
                page.Observation,
                page.Bytes,
                context,
                adjustment,
                timeframe))
            .OrderBy(row => row.SecurityId, StringComparer.Ordinal)
            .ThenBy(row => row.BarStartUtc)
            .ToArray();
        EnsureRows(
            rows,
            row => $"{row.SecurityId}|{row.Timeframe}|{row.BarStartUtc:O}",
            jobId,
            requestId,
            pages[0].Observation.ObservationId);
        return rows;
    }

    private IReadOnlyList<NewsRevisionEvidenceRow> NormalizeNews(
        IReadOnlyList<RawReceipt> pages,
        AlpacaEvidenceNormalizationContext context,
        EvidenceNormalizationJob job,
        string jobId,
        string requestId)
    {
        if (job.NewsAvailabilityEvidence is null)
        {
            throw Failure(
                EvidenceNormalizationPipelineFailureCode.HeterogeneousCollectionPlan,
                jobId,
                requestId,
                pages[0].Observation.ObservationId,
                "News normalization requires explicit historical availability evidence.");
        }

        var rows = pages
            .SelectMany(page => newsNormalizer.Normalize(
                page.Observation,
                page.Bytes,
                context,
                job.NewsAvailabilityEvidence.Value))
            .OrderBy(row => row.ProviderArticleId, StringComparer.Ordinal)
            .ThenBy(row => row.ProviderUpdatedAtUtc)
            .ThenBy(row => row.RevisionId, StringComparer.Ordinal)
            .ToArray();
        EnsureRows(
            rows,
            row => $"{row.Provider}|{row.ProviderArticleId}|{row.RevisionId}",
            jobId,
            requestId,
            pages[0].Observation.ObservationId);
        return rows;
    }

    private IReadOnlyList<SipQuoteEvidenceRow> NormalizeQuotes(
        IReadOnlyList<RawReceipt> pages,
        AlpacaEvidenceNormalizationContext context,
        string jobId,
        string requestId)
    {
        var rows = pages
            .SelectMany(page => quoteNormalizer.Normalize(page.Observation, page.Bytes, context))
            .OrderBy(row => row.SecurityId, StringComparer.Ordinal)
            .ThenBy(row => row.QuoteTimestampUtc)
            .ToArray();
        EnsureRows(
            rows,
            row => $"{row.SecurityId}|{row.QuoteTimestampUtc:O}",
            jobId,
            requestId,
            pages[0].Observation.ObservationId);
        return rows;
    }

    private IReadOnlyList<ExchangeSessionEvidenceRow> NormalizeExchangeSessions(
        IReadOnlyList<RawReceipt> pages,
        AlpacaEvidenceNormalizationContext context,
        string jobId,
        string requestId)
    {
        var rows = pages
            .SelectMany(page => calendarNormalizer.Normalize(
                page.Observation,
                page.Bytes,
                context))
            .OrderBy(row => row.TradeDate)
            .ToArray();
        EnsureRows(
            rows,
            row => $"{row.Exchange}|{row.TradeDate:yyyy-MM-dd}",
            jobId,
            requestId,
            pages[0].Observation.ObservationId);
        return rows;
    }

    private static IReadOnlyDictionary<string, string> BuildPartitionDimensions(
        EvidenceDatasetKind datasetKind,
        EvidenceCollectionRequest request,
        int authoritativePageCount)
    {
        var dimensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["collection_request_id"] = request.RequestId,
            ["authoritative_page_count"] =
                authoritativePageCount.ToString(CultureInfo.InvariantCulture)
        };
        if (datasetKind != EvidenceDatasetKind.ExchangeSessions)
        {
            return dimensions;
        }

        var start = request.RequestedStartUtc!.Value;
        var end = request.RequestedEndUtc!.Value;
        dimensions["calendar_coverage_start_date"] =
            DateOnly.FromDateTime(start.UtcDateTime)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        dimensions["calendar_coverage_end_date_exclusive"] =
            DateOnly.FromDateTime(end.UtcDateTime)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        dimensions["calendar_source"] = "alpaca_official_market_calendar";
        dimensions["calendar_source_complete"] = "true";
        return dimensions;
    }

    private static void EnsureRows<T>(
        IReadOnlyCollection<T> rows,
        Func<T, string> logicalKey,
        string jobId,
        string requestId,
        string observationId)
    {
        if (rows.Count == 0)
        {
            throw Failure(
                EvidenceNormalizationPipelineFailureCode.NoNormalizedRows,
                jobId,
                requestId,
                observationId,
                "A normalized partition cannot be empty.");
        }

        var duplicate = rows
            .GroupBy(logicalKey, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw Failure(
                EvidenceNormalizationPipelineFailureCode.DuplicateLogicalRow,
                jobId,
                requestId,
                observationId,
                $"Logical row '{duplicate.Key}' appears in more than one page.");
        }
    }

    private async Task<IReadOnlyList<RawReceipt>> SelectAuthoritativePagesAsync(
        EvidenceCollectionPlan plan,
        EvidenceCollectionRequest request,
        IReadOnlyList<RawReceipt> receipts,
        CancellationToken cancellationToken)
    {
        var cursor = await catalog.GetRequestCursorCheckpointAsync(
            plan.JobId,
            request.RequestId,
            cancellationToken);
        if (cursor is null || !cursor.Exhausted)
        {
            throw Failure(
                EvidenceNormalizationPipelineFailureCode.IncompletePagination,
                plan.JobId,
                request.RequestId,
                cursor?.ObservationId,
                "The request cursor is absent or has not reached an exhausted terminal page.");
        }

        var byPage = receipts
            .GroupBy(receipt => PageOrdinal(receipt.Observation))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var authoritative = new List<RawReceipt>(cursor.PageOrdinal);
        for (var page = 1; page <= cursor.PageOrdinal; page++)
        {
            if (!byPage.TryGetValue(page, out var pageReceipts))
            {
                throw Failure(
                    EvidenceNormalizationPipelineFailureCode.IncompletePagination,
                    plan.JobId,
                    request.RequestId,
                    null,
                    $"Committed raw page {page} is missing.");
            }

            var successful = pageReceipts
                .Where(receipt => receipt.Observation.TransportStatusCode is >= 200 and < 300)
                .ToArray();
            if (successful.Length == 0)
            {
                throw Failure(
                    EvidenceNormalizationPipelineFailureCode.IncompletePagination,
                    plan.JobId,
                    request.RequestId,
                    pageReceipts[0].Observation.ObservationId,
                    $"Page {page} has no successful archived receipt.");
            }

            var hashes = successful
                .Select(receipt => receipt.Observation.Artifact.Content.Sha256)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (hashes.Length != 1)
            {
                throw Failure(
                    EvidenceNormalizationPipelineFailureCode.ConflictingSuccessfulReceipts,
                    plan.JobId,
                    request.RequestId,
                    successful[0].Observation.ObservationId,
                    $"Page {page} has conflicting successful response bodies.");
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
                        StringComparison.Ordinal)) ?? throw Failure(
                    EvidenceNormalizationPipelineFailureCode.IncompletePagination,
                    plan.JobId,
                    request.RequestId,
                    cursor.ObservationId,
                    "The exhausted cursor does not reference its committed terminal receipt.");
            }

            var nextPageToken = request.Endpoint.EndsWith(
                "/v2/calendar",
                StringComparison.OrdinalIgnoreCase)
                ? null
                : ReadNextPageToken(selected, plan.JobId, request.RequestId);
            if (page < cursor.PageOrdinal)
            {
                if (nextPageToken is null ||
                    cursor.ConsumedPageTokenHashes.Count < page ||
                    !ComputeSha256(nextPageToken).Equals(
                        cursor.ConsumedPageTokenHashes[page - 1],
                        StringComparison.Ordinal))
                {
                    throw Failure(
                        EvidenceNormalizationPipelineFailureCode.IncompletePagination,
                        plan.JobId,
                        request.RequestId,
                        selected.Observation.ObservationId,
                        $"Page {page} does not connect to the archived cursor chain.");
                }
            }
            else if (nextPageToken is not null)
            {
                throw Failure(
                    EvidenceNormalizationPipelineFailureCode.IncompletePagination,
                    plan.JobId,
                    request.RequestId,
                    selected.Observation.ObservationId,
                    "The terminal page still contains a next-page token.");
            }

            authoritative.Add(selected);
        }

        if (byPage.Keys.Any(page => page < 1 || page > cursor.PageOrdinal))
        {
            throw Failure(
                EvidenceNormalizationPipelineFailureCode.IncompletePagination,
                plan.JobId,
                request.RequestId,
                null,
                "Raw receipts contain a page outside the exhausted cursor range.");
        }

        return authoritative;
    }

    private async Task EnsureEveryRequestExhaustedAsync(
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
                throw Failure(
                    EvidenceNormalizationPipelineFailureCode.CollectionNotReady,
                    plan.JobId,
                    request.RequestId,
                    cursor?.ObservationId,
                    "Every collection request must be exhausted before normalization starts.");
            }
        }
    }

    private async Task<byte[]> ReadVerifiedRawAsync(
        EvidenceSourceObservation observation,
        CancellationToken cancellationToken)
    {
        var verification = await artifactStore.VerifyAsync(
            observation.Artifact,
            cancellationToken);
        if (!verification.IsValid)
        {
            throw new EvidenceNormalizationException(
                EvidenceNormalizationFailureCode.ArtifactMismatch,
                observation.ObservationId,
                $"Raw artifact verification failed: {verification.FailureReason}");
        }

        var length = observation.Artifact.Content.ByteLength;
        if (length > Int32.MaxValue)
        {
            throw new EvidenceNormalizationException(
                EvidenceNormalizationFailureCode.ArtifactMismatch,
                observation.ObservationId,
                "Raw artifact exceeds the in-memory normalization boundary.");
        }

        var bytes = new byte[(int)length];
        await using var stream = await artifactStore.OpenReadAsync(
            observation.Artifact,
            cancellationToken);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(
                bytes.AsMemory(offset, bytes.Length - offset),
                cancellationToken);
            if (read == 0)
            {
                throw new EvidenceNormalizationException(
                    EvidenceNormalizationFailureCode.ArtifactMismatch,
                    observation.ObservationId,
                    "Raw artifact ended before its declared byte length.");
            }

            offset += read;
        }

        if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
        {
            throw new EvidenceNormalizationException(
                EvidenceNormalizationFailureCode.ArtifactMismatch,
                observation.ObservationId,
                "Raw artifact exceeds its declared byte length.");
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!actualHash.Equals(
                observation.Artifact.Content.Sha256,
                StringComparison.Ordinal))
        {
            throw new EvidenceNormalizationException(
                EvidenceNormalizationFailureCode.ArtifactMismatch,
                observation.ObservationId,
                "Raw artifact bytes do not match the observation SHA-256.");
        }

        return bytes;
    }

    private async Task QuarantineAsync(
        EvidenceCollectionPlan plan,
        IReadOnlyDictionary<string, IReadOnlyList<EvidenceSourceObservation>> observations,
        string reasonCode,
        string? observationId,
        string diagnostic,
        CancellationToken cancellationToken)
    {
        var flattened = observations.Values.SelectMany(value => value).ToArray();
        if (flattened.Length == 0)
        {
            return;
        }

        var diagnosticObservation = flattened.FirstOrDefault(observation =>
            observation.ObservationId.Equals(
                observationId,
                StringComparison.Ordinal)) ?? flattened[0];
        var quarantineId =
            $"quarantine-{ComputeSha256(
                $"{plan.JobId}|{reasonCode}|{observationId ?? "none"}")[..24]}";
        await catalog.RegisterQuarantineAsync(
            new EvidenceQuarantineRecord(
                quarantineId,
                EvidenceQuarantineScope.Dataset,
                reasonCode,
                plan.JobId,
                new EvidenceRowLineage(
                    flattened.Select(observation => observation.ToReference()).ToArray()),
                diagnosticObservation.Artifact,
                retryable: false,
                timeProvider.GetUtcNow()),
            cancellationToken);

        var checkpoint = await catalog.GetCollectionCheckpointAsync(plan.JobId, cancellationToken);
        if (checkpoint is not null &&
            checkpoint.State is EvidenceCollectionState.Collecting or
                EvidenceCollectionState.Normalizing or
                EvidenceCollectionState.Validating)
        {
            await catalog.SaveCollectionCheckpointAsync(
                new EvidenceCollectionCheckpoint(
                    plan.JobId,
                    EvidenceCollectionState.Quarantined,
                    timeProvider.GetUtcNow(),
                    checkpoint.CompletedRequestIds,
                    $"{reasonCode}: {diagnostic}"),
                cancellationToken);
        }
    }

    private async Task SaveCheckpointAsync(
        EvidenceCollectionPlan plan,
        EvidenceCollectionState state,
        IReadOnlyList<string> completedRequestIds,
        CancellationToken cancellationToken)
    {
        var current = await catalog.GetCollectionCheckpointAsync(plan.JobId, cancellationToken);
        if (current?.State == state)
        {
            return;
        }

        if (current?.State == EvidenceCollectionState.Committed)
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

    private async Task<EvidenceNormalizationResult> FindCommittedResultAsync(
        EvidenceNormalizationJob job,
        EvidenceCollectionPlan plan,
        CancellationToken cancellationToken)
    {
        var matches = (await catalog.FindDatasetsAsync(
                new EvidenceDatasetQuery(job.DatasetKind, plan.Requests[0].DataFeed),
                cancellationToken))
            .Where(dataset =>
                dataset.CollectionJobId.Equals(plan.JobId, StringComparison.Ordinal) &&
                dataset.CollectionPlanHash.Equals(plan.LogicalPlanHash, StringComparison.Ordinal) &&
                dataset.NormalizerVersion.Equals(job.NormalizerVersion, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"Committed collection '{plan.JobId}' has {matches.Length} matching normalized datasets.");
        }

        var dataset = matches[0];
        if (!dataset.Attributes.TryGetValue(
                EvidenceNormalizationJob.ContractHashAttribute,
                out var committedContractHash) ||
            !committedContractHash.Equals(job.ContractHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Committed collection '{plan.JobId}' does not match the requested " +
                "normalization contract.");
        }

        return new EvidenceNormalizationResult(
            plan.JobId,
            dataset.DatasetId,
            true,
            dataset.Partitions.Count,
            dataset.Partitions.Sum(partition => partition.RowCount));
    }

    private static void ValidatePlan(
        EvidenceNormalizationJob job,
        EvidenceCollectionPlan plan)
    {
        if (!plan.JobId.Equals(job.JobId, StringComparison.Ordinal))
        {
            throw Failure(
                EvidenceNormalizationPipelineFailureCode.CollectionNotReady,
                job.JobId,
                null,
                null,
                "Normalization job does not match the collection plan.");
        }

        var first = plan.Requests[0];
        if (plan.Requests.Any(request =>
                !request.Provider.Equals("alpaca", StringComparison.Ordinal) ||
                !request.Provider.Equals(first.Provider, StringComparison.Ordinal) ||
                !request.Endpoint.Equals(first.Endpoint, StringComparison.Ordinal) ||
                !request.DataFeed.Equals(first.DataFeed, StringComparison.Ordinal) ||
                !request.Adjustment.Equals(first.Adjustment, StringComparison.Ordinal) ||
                !request.Currency.Equals(first.Currency, StringComparison.Ordinal) ||
                request.RequestedStartUtc is null ||
                request.RequestedEndUtc is null ||
                request.AsOfDate is null))
        {
            throw Failure(
                EvidenceNormalizationPipelineFailureCode.HeterogeneousCollectionPlan,
                plan.JobId,
                null,
                null,
                "A normalization job requires homogeneous ranged Alpaca requests.");
        }

        var valid = job.DatasetKind switch
        {
            EvidenceDatasetKind.MarketBarsAsTraded =>
                first.Endpoint.EndsWith("/v2/stocks/bars", StringComparison.OrdinalIgnoreCase) &&
                first.Adjustment.Equals("raw", StringComparison.Ordinal),
            EvidenceDatasetKind.MarketBarsResearchAdjusted =>
                first.Endpoint.EndsWith("/v2/stocks/bars", StringComparison.OrdinalIgnoreCase) &&
                first.Adjustment.Equals("all", StringComparison.Ordinal),
            EvidenceDatasetKind.NewsRevisions =>
                first.Endpoint.EndsWith("/v1beta1/news", StringComparison.OrdinalIgnoreCase),
            EvidenceDatasetKind.SipQuotes =>
                first.Endpoint.EndsWith("/v2/stocks/quotes", StringComparison.OrdinalIgnoreCase) &&
                first.DataFeed.Equals("sip", StringComparison.Ordinal),
            EvidenceDatasetKind.ExchangeSessions =>
                first.Endpoint.EndsWith("/v2/calendar", StringComparison.OrdinalIgnoreCase) &&
                first.DataFeed.Equals("alpaca-trading", StringComparison.Ordinal) &&
                first.Adjustment.Equals("raw", StringComparison.Ordinal) &&
                first.Symbols.Count == 0,
            _ => false
        };
        if (!valid)
        {
            throw Failure(
                EvidenceNormalizationPipelineFailureCode.UnsupportedDatasetKind,
                plan.JobId,
                null,
                null,
                $"Dataset kind '{job.DatasetKind}' conflicts with the collection endpoint or semantics.");
        }
    }

    private static string ResolveTimeframe(
        EvidenceDatasetKind kind,
        EvidenceCollectionRequest request,
        string jobId)
    {
        if (kind == EvidenceDatasetKind.NewsRevisions)
        {
            return "event";
        }

        if (kind == EvidenceDatasetKind.SipQuotes)
        {
            return "tick";
        }

        if (kind == EvidenceDatasetKind.ExchangeSessions)
        {
            return "session";
        }

        if (!request.Parameters.TryGetValue("timeframe", out var timeframe) ||
            String.IsNullOrWhiteSpace(timeframe))
        {
            throw Failure(
                EvidenceNormalizationPipelineFailureCode.MissingTimeframe,
                jobId,
                request.RequestId,
                null,
                "Market-bar normalization requires the collected provider timeframe.");
        }

        var value = timeframe.Trim();
        if (value.EndsWith("Min", StringComparison.Ordinal) &&
            Int32.TryParse(
                value[..^3],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var minutes) &&
            minutes > 0)
        {
            return $"{minutes}m";
        }

        if (value.EndsWith("Hour", StringComparison.Ordinal) &&
            Int32.TryParse(
                value[..^4],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var hours) &&
            hours > 0)
        {
            return $"{hours}h";
        }

        if (value.EndsWith("Day", StringComparison.Ordinal) &&
            Int32.TryParse(
                value[..^3],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var days) &&
            days > 0)
        {
            return $"{days}d";
        }

        throw Failure(
            EvidenceNormalizationPipelineFailureCode.MissingTimeframe,
            jobId,
            request.RequestId,
            null,
            $"Unsupported Alpaca timeframe '{timeframe}'.");
    }

    private static string? ReadNextPageToken(
        RawReceipt receipt,
        string jobId,
        string requestId)
    {
        try
        {
            using var document = JsonDocument.Parse(receipt.Bytes);
            if (!document.RootElement.TryGetProperty("next_page_token", out var token))
            {
                throw new JsonException("Missing next_page_token.");
            }

            return token.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String when !String.IsNullOrWhiteSpace(token.GetString()) =>
                    token.GetString()!.Trim(),
                _ => throw new JsonException("Invalid next_page_token.")
            };
        }
        catch (JsonException exception)
        {
            throw Failure(
                EvidenceNormalizationPipelineFailureCode.IncompletePagination,
                jobId,
                requestId,
                receipt.Observation.ObservationId,
                $"Pagination metadata is invalid: {exception.Message}");
        }
    }

    private static int PageOrdinal(EvidenceSourceObservation observation)
    {
        if (!observation.RequestAttributes.TryGetValue("page_ordinal", out var text) ||
            !Int32.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value) ||
            value <= 0)
        {
            throw Failure(
                EvidenceNormalizationPipelineFailureCode.IncompletePagination,
                observation.CollectionJobId,
                observation.IngestionRequestId,
                observation.ObservationId,
                "Raw observation has no valid page ordinal.");
        }

        return value;
    }

    private static EvidenceNormalizationPipelineException Failure(
        EvidenceNormalizationPipelineFailureCode code,
        string jobId,
        string? requestId,
        string? observationId,
        string message) =>
        new(code, jobId, requestId, observationId, message);

    private static string ComputeSha256(string value) =>
        Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static async Task<NormalizationJobGateLease> AcquireJobGateAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var gate = JobLocks.GetOrAdd(jobId, _ => new NormalizationJobGate());
            lock (gate)
            {
                if (gate.Retired)
                {
                    continue;
                }

                gate.ReferenceCount++;
            }

            try
            {
                await gate.Semaphore.WaitAsync(cancellationToken);
                return new NormalizationJobGateLease(jobId, gate);
            }
            catch
            {
                ReleaseJobGateReference(jobId, gate, releaseSemaphore: false);
                throw;
            }
        }
    }

    private static void ReleaseJobGateReference(
        string jobId,
        NormalizationJobGate gate,
        bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            gate.Semaphore.Release();
        }

        var remove = false;
        lock (gate)
        {
            gate.ReferenceCount--;
            if (gate.ReferenceCount == 0)
            {
                gate.Retired = true;
                remove = true;
            }
        }

        if (remove)
        {
            JobLocks.TryRemove(
                new KeyValuePair<string, NormalizationJobGate>(jobId, gate));
        }
    }

    private sealed class NormalizationJobGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }

        public bool Retired { get; set; }
    }

    private sealed class NormalizationJobGateLease(
        string jobId,
        NormalizationJobGate gate) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                ReleaseJobGateReference(jobId, gate, releaseSemaphore: true);
            }
        }
    }

    private sealed record RawReceipt(
        EvidenceSourceObservation Observation,
        byte[] Bytes);
}
