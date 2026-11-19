using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Alpaca;
using TradingFlow.Application.Jobs;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Collection;
using TradingFlow.Data.Evidence.Screens;
using TradingFlow.Data.Reference;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Web.Services;

/// <summary>HTTP request body for queuing a point-in-time daily research screen build.</summary>
public sealed record BuildResearchScreenHttpRequest(
    string ScreenName,
    string DailyBarsDatasetId,
    string CorporateActionsDatasetId,
    DateOnly From,
    DateOnly To,
    decimal MinPrice = 5m,
    string PriceBasis = "prior_close",
    int AverageSessions = 20,
    decimal? MinAverageShareVolume = null,
    decimal? MinAverageDollarVolume = 20_000_000m,
    int? AtrPeriod = null,
    decimal? MinAtr = null,
    int MinHistorySessions = 253,
    decimal? MinAverageRangePercent = 2.5m,
    int? SplitLookbackSessions = 253,
    string? ExchangeSessionsDatasetId = null,
    string? SecurityMasterDatasetId = null,
    string? ExclusionListKey = null,
    bool IncludeFredVix = true);

/// <summary>HTTP request body for Step 1 dry-run planning of 5-minute regular-session bars for a published screen.</summary>
public sealed record PlanMinuteBarsHttpRequest(
    string ScreenName,
    DateOnly? From = null,
    DateOnly? To = null,
    int PriorSessions = 21,
    bool IncludeHeldSessions = true,
    int BatchSize = 50);

/// <summary>HTTP request body for Step 2 confirmed collection of a previously planned 5-minute bar request file.</summary>
public sealed record CollectMinuteBarsHttpRequest(
    string PlanFileName,
    string ExpectedRequestSha256);

/// <summary>Catalog dataset option returned to the Backtests research-screens panel.</summary>
public sealed record ResearchEvidenceDatasetOption(
    string DatasetId,
    string Kind,
    string Timeframe,
    DateTimeOffset PublishedAtUtc,
    int PartitionCount,
    long RowCount,
    string Summary);

/// <summary>Discovered symbol-exclusion text list that operators may select without providing raw filesystem paths.</summary>
public sealed record ResearchExclusionListOption(
    string Key,
    string Label,
    string Scope);

/// <summary>Metadata summary of one published research screen folder under the host's screens root.</summary>
public sealed record PublishedResearchScreenOption(
    string Name,
    DateOnly From,
    DateOnly To,
    bool IsRanked,
    bool HasFredVix,
    bool HasSecurityMaster,
    string ManifestSha256,
    string RankedListSha256,
    IReadOnlyList<string> FiveMinuteDatasetIds);

/// <summary>Snapshot of one durable <c>research_evidence</c> job (<c>build_screen</c> or <c>collect_5m_bars</c>).</summary>
public sealed record ResearchEvidenceJobSnapshot(
    Guid JobId,
    string Kind,
    string Label,
    string Status,
    string Stage,
    string Message,
    int ProcessedCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? ErrorMessage,
    ResearchScreenBuildJobResult? ScreenResult = null,
    MinuteBarsCollectJobResult? CollectionResult = null);

/// <summary>Published screen identity and counts recorded when a <c>build_screen</c> job completes.</summary>
public sealed record ResearchScreenBuildJobResult(
    string ScreenName,
    string ManifestSha256,
    string RankedListSha256,
    DateOnly From,
    DateOnly To,
    int SessionCount,
    int EligibleStockDays,
    bool HasFredVix);

/// <summary>Committed dataset identity and counts recorded when a <c>collect_5m_bars</c> job completes.</summary>
public sealed record MinuteBarsCollectJobResult(
    string PlanFileName,
    string RequestSha256,
    string DatasetId,
    bool AlreadyCommitted,
    int PartitionCount,
    long RowCount);

/// <summary>Payload returned by <c>GET /api/v1/backtests/research-screens/options</c>.</summary>
public sealed record ResearchScreensOptionsResponse(
    IReadOnlyList<ResearchEvidenceDatasetOption> DailyBars,
    IReadOnlyList<ResearchEvidenceDatasetOption> CorporateActions,
    IReadOnlyList<ResearchEvidenceDatasetOption> ExchangeSessions,
    IReadOnlyList<ResearchEvidenceDatasetOption> SecurityMasters,
    IReadOnlyList<ResearchExclusionListOption> ExclusionLists,
    IReadOnlyList<PublishedResearchScreenOption> PublishedScreens,
    bool AlpacaCredentialsConfigured,
    IReadOnlyList<ResearchEvidenceJobSnapshot> RecentJobs);

/// <summary>
/// Durable job service and workflow coordinator for Web UI research screen builds and two-step 5-minute bar
/// collection. Shares <see cref="ResearchDailyScreenPublisher"/> and <see cref="IntradayBarsCollectionPlanner"/> with
/// <c>TradingFlow.Cli</c>, confines all file operations to approved roots, and never overwrites existing screens.
/// </summary>
public sealed class ResearchEvidenceJobService : IDurableJobHandler
{
    public const string Type = "research_evidence";
    public const string BuildScreenKind = "build_screen";
    public const string CollectFiveMinuteBarsKind = "collect_5m_bars";
    private const int MaximumPendingJobs = 8;

    private static readonly Regex SafeScreenNamePattern = new("^[A-Za-z0-9][A-Za-z0-9_-]{1,95}$", RegexOptions.Compiled);
    private static readonly Regex SafePlanFileNamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{1,180}\.json$", RegexOptions.Compiled);
    private static readonly Regex Sha256HexPattern = new("^[a-f0-9]{64}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IDurableJobRepository jobs;
    private readonly ProjectPaths paths;
    private readonly IEvidenceCatalog catalog;
    private readonly IImmutableArtifactStore artifactStore;
    private readonly AlpacaCredentialProvider credentials;
    private readonly IEvidenceDatasetCollectionRunner collectionRunner;
    private readonly Func<HttpClient>? fredHttpClientFactory;
    private readonly TimeProvider clock;
    private readonly ILogger<ResearchEvidenceJobService> logger;

    private sealed record WorkPayload(
        string Kind,
        string Label,
        BuildScreenWork? BuildScreen = null,
        CollectMinuteBarsWork? CollectMinuteBars = null);

    private sealed record BuildScreenWork(
        string ScreenName,
        string DailyBarsDatasetId,
        string CorporateActionsDatasetId,
        DateOnly From,
        DateOnly To,
        decimal MinPrice,
        string PriceBasis,
        int AverageSessions,
        decimal? MinAverageShareVolume,
        decimal? MinAverageDollarVolume,
        int? AtrPeriod,
        decimal? MinAtr,
        int MinHistorySessions,
        decimal? MinAverageRangePercent,
        int? SplitLookbackSessions,
        string? ExchangeSessionsDatasetId,
        string? SecurityMasterDatasetId,
        string? ExclusionListKey,
        bool IncludeFredVix);

    private sealed record CollectMinuteBarsWork(
        string PlanFileName,
        string ExpectedRequestSha256);

    private sealed record ProgressSnapshot(
        string Kind,
        string Label,
        string Stage,
        string Message,
        int ProcessedCount,
        ResearchScreenBuildJobResult? ScreenResult = null,
        MinuteBarsCollectJobResult? CollectionResult = null);

    /// <summary>Creates the durable research-evidence handler backed by the shared evidence catalog and job repository.</summary>
    public ResearchEvidenceJobService(
        IDurableJobRepository jobs,
        ProjectPaths paths,
        IEvidenceCatalog catalog,
        IImmutableArtifactStore artifactStore,
        AlpacaCredentialProvider credentials,
        IEvidenceDatasetCollectionRunner collectionRunner,
        IHttpClientFactory? httpClientFactory = null,
        TimeProvider? clock = null,
        ILogger<ResearchEvidenceJobService>? logger = null,
        Func<HttpClient>? fredHttpClientFactory = null)
    {
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        this.collectionRunner = collectionRunner ?? throw new ArgumentNullException(nameof(collectionRunner));
        this.fredHttpClientFactory = fredHttpClientFactory ??
            (httpClientFactory is null ? null : new Func<HttpClient>(() => httpClientFactory.CreateClient(FredVixCloseSource.HttpClientName)));
        this.clock = clock ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<ResearchEvidenceJobService>.Instance;
    }

    public string JobType => Type;
    public DurableJobRecoveryMode RecoveryMode => DurableJobRecoveryMode.ReplayFromStart;
    public string RequestsRoot => Path.Combine(paths.EvidenceRoot, "requests");

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RecoverAsync(PersistedJob job, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task OnTerminalPublishedAsync(PersistedJob job, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Lists cataloged input datasets, discovered symbol-exclusion files, published research screens with their bound
    /// 5-minute dataset IDs, credential availability, and recent <c>research_evidence</c> jobs.
    /// </summary>
    public async Task<ResearchScreensOptionsResponse> GetOptionsAsync(CancellationToken cancellationToken)
    {
        var barDatasets = await catalog.FindDatasetsAsync(new EvidenceDatasetQuery(EvidenceDatasetKind.MarketBarsAsTraded), cancellationToken);
        var corporateActions = await catalog.FindDatasetsAsync(new EvidenceDatasetQuery(EvidenceDatasetKind.CorporateActions), cancellationToken);
        var sessions = await catalog.FindDatasetsAsync(new EvidenceDatasetQuery(EvidenceDatasetKind.ExchangeSessions), cancellationToken);
        var securityMasters = await catalog.FindDatasetsAsync(new EvidenceDatasetQuery(EvidenceDatasetKind.SecurityMaster), cancellationToken);

        var dailyBars = barDatasets
            .Where(dataset => dataset.Partitions.Count > 0 && dataset.Partitions.All(p => String.Equals(p.Provenance.Timeframe, "1d", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(dataset => dataset.CreatedAtUtc)
            .Select(ToDatasetOption)
            .ToArray();

        var publishedScreens = DiscoverPublishedScreens(barDatasets);
        var exclusionLists = DiscoverExclusionLists().Select(item => item.Option).ToArray();
        var recentJobs = (await jobs.GetJobsAsync(Type, cancellationToken))
            .OrderByDescending(job => job.CreatedAt)
            .Take(10)
            .Select(ToSnapshot)
            .ToArray();

        return new ResearchScreensOptionsResponse(
            dailyBars,
            corporateActions.OrderByDescending(d => d.CreatedAtUtc).Select(ToDatasetOption).ToArray(),
            sessions.OrderByDescending(d => d.CreatedAtUtc).Select(ToDatasetOption).ToArray(),
            securityMasters.OrderByDescending(d => d.CreatedAtUtc).Select(ToDatasetOption).ToArray(),
            exclusionLists,
            publishedScreens,
            credentials.IsConfigured,
            recentJobs);
    }

    /// <summary>
    /// Validates screen name confinement, core-data non-overwrite rules, catalog dataset IDs, exclusion-list key, and
    /// screening thresholds before queuing a durable <c>build_screen</c> job.
    /// </summary>
    public async Task<ResearchEvidenceJobSnapshot> StartBuildScreenAsync(
        BuildResearchScreenHttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var screenName = ValidateScreenName(request.ScreenName);
        var screenFolder = ResolveScreenFolder(screenName);
        EnsureTargetScreenFolderUnused(screenFolder, screenName);

        if (request.From > request.To)
            throw new ArgumentException($"Start date {request.From:yyyy-MM-dd} cannot follow end date {request.To:yyyy-MM-dd}.");
        if (ResearchObservationBoundary.IncludesDocumentedHoldout(request.From, request.To))
        {
            throw new InvalidOperationException(
                $"The requested screen window ({request.From:yyyy-MM-dd} to {request.To:yyyy-MM-dd}) overlaps the protected " +
                "holdout period (2024-01-01 to 2025-10-31). Choose dates outside the documented holdout window so the published screen can be previewed and backtested.");
        }
        if (request.SplitLookbackSessions is <= 0)
            throw new ArgumentException("SplitLookbackSessions must be positive when specified.");

        var rules = BuildDailyScreenRules(request);
        rules.Validate();

        await RequireDatasetKindAsync(request.DailyBarsDatasetId, EvidenceDatasetKind.MarketBarsAsTraded, "daily bars", cancellationToken, requiredTimeframe: "1d");
        await RequireDatasetKindAsync(request.CorporateActionsDatasetId, EvidenceDatasetKind.CorporateActions, "corporate actions", cancellationToken);
        await RequireDatasetKindAsync(request.ExchangeSessionsDatasetId, EvidenceDatasetKind.ExchangeSessions, "exchange sessions", cancellationToken);
        if (!String.IsNullOrWhiteSpace(request.SecurityMasterDatasetId))
            await RequireDatasetKindAsync(request.SecurityMasterDatasetId, EvidenceDatasetKind.SecurityMaster, "security master", cancellationToken);

        var normalizedExclusionKey = String.IsNullOrWhiteSpace(request.ExclusionListKey) ? null : request.ExclusionListKey.Trim();
        if (normalizedExclusionKey is not null)
        {
            _ = ResolveExclusionListPath(normalizedExclusionKey);
        }

        var work = new WorkPayload(
            BuildScreenKind,
            screenName,
            BuildScreen: new BuildScreenWork(
                screenName,
                request.DailyBarsDatasetId.Trim(),
                request.CorporateActionsDatasetId.Trim(),
                request.From,
                request.To,
                request.MinPrice,
                request.PriceBasis.Trim(),
                request.AverageSessions,
                request.MinAverageShareVolume,
                request.MinAverageDollarVolume,
                request.AtrPeriod,
                request.MinAtr,
                request.MinHistorySessions,
                request.MinAverageRangePercent,
                request.SplitLookbackSessions,
                String.IsNullOrWhiteSpace(request.ExchangeSessionsDatasetId) ? null : request.ExchangeSessionsDatasetId.Trim(),
                String.IsNullOrWhiteSpace(request.SecurityMasterDatasetId) ? null : request.SecurityMasterDatasetId.Trim(),
                normalizedExclusionKey,
                request.IncludeFredVix));

        return await EnqueueWorkAsync(work, $"Queued point-in-time screen build for '{screenName}'.", cancellationToken);
    }

    /// <summary>
    /// Executes Step 1 dry-run 5-minute bar planning synchronously without issuing any provider HTTP calls, writing the
    /// frozen request file when missing sessions exist and returning exact bar/size estimates.
    /// </summary>
    public async Task<MinuteBarsCollectionPlanSummary> PlanFiveMinuteBarsAsync(
        PlanMinuteBarsHttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var screenName = ValidateScreenName(request.ScreenName);
        var screenFolder = ResolveScreenFolder(screenName);
        if (!PublishedResearchScreen.IsPublished(screenFolder))
        {
            throw new InvalidDataException($"Published research screen '{screenName}' was not found under {paths.ResearchScreensRoot}.");
        }

        var planner = new IntradayBarsCollectionPlanner(catalog, artifactStore);
        var (summary, _) = await planner.PlanScreenFiveMinuteBarsAsync(
            new MinuteBarsCollectionPlanRequest(
                screenName,
                screenFolder,
                request.From,
                request.To,
                request.PriorSessions,
                request.IncludeHeldSessions,
                request.BatchSize,
                RequestsRoot),
            cancellationToken);
        return summary;
    }

    /// <summary>
    /// Validates Step 2 confirmation inputs (safe single-segment plan filename, matching on-disk SHA-256, valid frozen
    /// request JSON, and configured Alpaca credentials) before queuing a durable <c>collect_5m_bars</c> job.
    /// </summary>
    public async Task<ResearchEvidenceJobSnapshot> StartCollectFiveMinuteBarsAsync(
        CollectMinuteBarsHttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (planFileName, expectedSha, _) = await LoadAndVerifyFrozenRequestAsync(
            request.PlanFileName,
            request.ExpectedRequestSha256,
            cancellationToken);

        RequireAlpacaCredentialsConfigured();

        var work = new WorkPayload(
            CollectFiveMinuteBarsKind,
            planFileName,
            CollectMinuteBars: new CollectMinuteBarsWork(planFileName, expectedSha));
        return await EnqueueWorkAsync(work, $"Queued confirmed 5-minute bar collection for '{planFileName}'.", cancellationToken);
    }

    /// <summary>Returns the current durable snapshot of one <c>research_evidence</c> job, or null if not found.</summary>
    public async Task<ResearchEvidenceJobSnapshot?> GetJobAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await jobs.GetJobAsync(id, cancellationToken);
        return job is null || !String.Equals(job.JobType, Type, StringComparison.OrdinalIgnoreCase)
            ? null
            : ToSnapshot(job);
    }

    /// <summary>Executes one claimed <c>build_screen</c> or <c>collect_5m_bars</c> job under its durable lease.</summary>
    public async Task<DurableJobCompletion> ExecuteAsync(
        DurableJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<WorkPayload>(context.Job.RequestJson, JsonOptions)
            ?? throw new InvalidDataException("Research evidence job payload is empty or invalid.");

        return payload.Kind switch
        {
            BuildScreenKind when payload.BuildScreen is not null =>
                await ExecuteBuildScreenAsync(context, payload.BuildScreen, cancellationToken),
            CollectFiveMinuteBarsKind when payload.CollectMinuteBars is not null =>
                await ExecuteCollectMinuteBarsAsync(context, payload.CollectMinuteBars, cancellationToken),
            _ => throw new InvalidDataException($"Unsupported research evidence job kind '{payload.Kind}'.")
        };
    }

    // Executes ResearchDailyScreenPublisher and records structured progress and completion metadata.
    private async Task<DurableJobCompletion> ExecuteBuildScreenAsync(
        DurableJobExecutionContext context,
        BuildScreenWork work,
        CancellationToken cancellationToken)
    {
        var screenName = ValidateScreenName(work.ScreenName);
        var screenFolder = ResolveScreenFolder(screenName);
        EnsureTargetScreenFolderUnused(screenFolder, screenName);

        var exclusionPath = work.ExclusionListKey is null ? null : ResolveExclusionListPath(work.ExclusionListKey);
        var rules = new DailyScreenRules(
            work.MinPrice,
            DailyScreenRules.ParsePriceBasis(work.PriceBasis),
            work.AverageSessions,
            MinAverageShareVolume: work.MinAverageShareVolume,
            MinAverageDollarVolume: work.MinAverageDollarVolume,
            AtrPeriod: work.AtrPeriod,
            MinAtr: work.MinAtr,
            MinHistorySessions: work.MinHistorySessions,
            MinAverageRangePercent: work.MinAverageRangePercent);

        var screenRequest = new ResearchDailyScreenRequest(
            work.DailyBarsDatasetId,
            work.CorporateActionsDatasetId,
            work.From,
            work.To,
            screenFolder,
            rules,
            ExcludedSymbolsFile: exclusionPath,
            SplitLookbackSessions: work.SplitLookbackSessions,
            ExchangeSessionsDatasetId: work.ExchangeSessionsDatasetId,
            SecurityMasterDatasetId: work.SecurityMasterDatasetId,
            FredVixSeries: work.IncludeFredVix ? RegimeRules.FredVixSeries : null);

        await SaveProgressAsync(context, BuildScreenKind, screenName, "running", $"Building point-in-time screen '{screenName}'...", 0, cancellationToken);
        var lastStage = "running";
        var lastLoggedCount = -1;
        var publisher = new ResearchDailyScreenPublisher(catalog, artifactStore, fredHttpClientFactory, () => clock.GetUtcNow());
        var result = await publisher.PublishAsync(
            screenRequest,
            onProgress: async (stage, processed, ct) =>
            {
                if (stage == lastStage && processed - lastLoggedCount < 500) return;
                lastStage = stage;
                lastLoggedCount = processed;
                try
                {
                    await context.SaveSnapshotAsync(
                        SerializeProgress(BuildScreenKind, screenName, stage, FormatScreenStageMessage(stage, screenName, processed), processed),
                        ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (DurableJobLeaseLostException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Non-fatal progress snapshot update failed for research screen '{ScreenName}'.", screenName);
                }
            },
            cancellationToken: cancellationToken);

        var summary = new ResearchScreenBuildJobResult(
            screenName,
            result.ManifestSha256,
            result.RankedListSha256,
            result.From,
            result.To,
            result.SessionCount,
            result.EligibleStockDays,
            result.HasFredVix);
        var completedJson = SerializeProgress(
            BuildScreenKind,
            screenName,
            "completed",
            $"Published screen '{screenName}' covering {result.SessionCount} sessions ({result.EligibleStockDays:N0} eligible stock-days).",
            result.EligibleStockDays,
            screenResult: summary);
        return DurableJobCompletion.Completed(completedJson, result.ManifestSha256);
    }

    // Re-verifies the frozen request file and credentials, then invokes the shared collection runner.
    private async Task<DurableJobCompletion> ExecuteCollectMinuteBarsAsync(
        DurableJobExecutionContext context,
        CollectMinuteBarsWork work,
        CancellationToken cancellationToken)
    {
        var (planFileName, verifiedSha, frozenRequest) = await LoadAndVerifyFrozenRequestAsync(
            work.PlanFileName,
            work.ExpectedRequestSha256,
            cancellationToken);
        RequireAlpacaCredentialsConfigured();

        await SaveProgressAsync(
            context,
            CollectFiveMinuteBarsKind,
            planFileName,
            "collecting",
            $"Collecting and normalizing {frozenRequest.Requests.Count} Alpaca 5-minute bar batch request(s) for '{planFileName}'...",
            0,
            cancellationToken);

        var endpoints = AlpacaEndpointResolver.Resolve(ProductionProfile.Paper);
        var outcome = await collectionRunner.RunAsync(
            frozenRequest,
            credentials.KeyId,
            credentials.SecretKey,
            endpoints.MarketDataRest,
            endpoints.TradingRest,
            cancellationToken);

        var summary = new MinuteBarsCollectJobResult(
            planFileName,
            verifiedSha,
            outcome.DatasetId,
            outcome.AlreadyCommitted,
            outcome.PartitionCount,
            outcome.RowCount);
        var completedJson = SerializeProgress(
            CollectFiveMinuteBarsKind,
            planFileName,
            "completed",
            outcome.AlreadyCommitted
                ? $"Dataset '{outcome.DatasetId}' was already committed ({outcome.RowCount:N0} rows across {outcome.PartitionCount} partition(s))."
                : $"Committed 5-minute bar dataset '{outcome.DatasetId}' ({outcome.RowCount:N0} rows across {outcome.PartitionCount} partition(s)).",
            outcome.PartitionCount,
            collectionResult: summary);
        return DurableJobCompletion.Completed(completedJson, outcome.DatasetId);
    }

    // Enqueues one durable research_evidence job under the bounded queue limit and returns its initial snapshot.
    private async Task<ResearchEvidenceJobSnapshot> EnqueueWorkAsync(
        WorkPayload work,
        string initialMessage,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var job = new PersistedJob
        {
            Id = Guid.NewGuid(),
            JobType = Type,
            RunName = work.Label,
            RequestJson = JsonSerializer.Serialize(work, JsonOptions),
            SnapshotJson = SerializeProgress(work.Kind, work.Label, "queued", initialMessage, 0),
            Status = "queued",
            CreatedAt = now
        };
        if (!await jobs.TryEnqueueAsync(job, MaximumPendingJobs, cancellationToken))
        {
            throw new InvalidOperationException(
                $"The research evidence job queue is full ({MaximumPendingJobs} active/pending jobs). Wait for an active job to complete before starting another.");
        }

        return ToSnapshot(job);
    }

    // Validates single-segment plan filename confinement, verifies exact SHA-256 match, and deserializes the frozen request.
    private async Task<(string FileName, string Sha256, FrozenEvidenceCollectionRequest Request)> LoadAndVerifyFrozenRequestAsync(
        string planFileName,
        string expectedRequestSha256,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(planFileName) ||
            !SafePlanFileNamePattern.IsMatch(planFileName.Trim()) ||
            planFileName.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("PlanFileName must be a single .json filename created by Step 1 planning (no directory separators or '..').");
        }

        var normalizedName = planFileName.Trim();
        var normalizedExpected = (expectedRequestSha256 ?? String.Empty).Trim().ToLowerInvariant();
        if (!Sha256HexPattern.IsMatch(normalizedExpected))
        {
            throw new ArgumentException("ExpectedRequestSha256 must be the 64-character lowercase SHA-256 hash returned by Step 1 planning.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(RequestsRoot, normalizedName));
        if (ProjectPaths.RelativeInside(RequestsRoot, fullPath) is null ||
            !String.Equals(Path.GetFileName(fullPath), normalizedName, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Plan file '{normalizedName}' escapes the approved requests directory.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Planned 5-minute bar request '{normalizedName}' was not found. Run Step 1 estimate first.", fullPath);
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        var actualSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!String.Equals(actualSha, normalizedExpected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Request fingerprint mismatch for '{normalizedName}': expected SHA-256 '{normalizedExpected}', found '{actualSha}'. Re-run Step 1 estimate before collecting.");
        }

        var parsed = JsonSerializer.Deserialize<FrozenEvidenceCollectionRequest>(bytes, JsonOptions)
            ?? throw new InvalidDataException($"Frozen request '{normalizedName}' is empty or unreadable.");
        return (normalizedName, actualSha, parsed);
    }

    // Ensures Alpaca API keys are configured before queueing or executing a live 5-minute bar collection.
    private void RequireAlpacaCredentialsConfigured()
    {
        if (!credentials.IsConfigured)
        {
            throw new InvalidOperationException(
                "Alpaca API credentials are missing (ALPACA_KEY_ID / ALPACA_SECRET_KEY or Alpaca:KeyId / Alpaca:SecretKey). " +
                "Configure credentials in appsettings.local.json or environment variables before collecting 5-minute bars; dry-run planning remains available.");
        }
    }

    // Validates that a screen name is a single safe folder token with no path traversal characters.
    private static string ValidateScreenName(string? screenName)
    {
        if (String.IsNullOrWhiteSpace(screenName))
            throw new ArgumentException("ScreenName is required.");
        var trimmed = screenName.Trim();
        if (!SafeScreenNamePattern.IsMatch(trimmed) || trimmed.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "ScreenName must be 2–96 characters using only letters, digits, hyphens, or underscores, starting with a letter or digit.");
        }

        return trimmed;
    }

    // Resolves the confined output folder for a validated screen name under ResearchScreensRoot.
    private string ResolveScreenFolder(string validatedScreenName)
    {
        var full = Path.GetFullPath(Path.Combine(paths.ResearchScreensRoot, validatedScreenName));
        if (ProjectPaths.RelativeInside(paths.ResearchScreensRoot, full) is null)
            throw new ArgumentException($"Screen folder for '{validatedScreenName}' lies outside the research screens root.");
        return full;
    }

    // Refuses to reuse or overwrite a screen directory that already contains any files or protected screen evidence.
    private static void EnsureTargetScreenFolderUnused(string screenFolder, string screenName)
    {
        ResearchDailyScreenPublisher.EnsureNoProtectedScreenFilesExist(screenFolder);
        if (Directory.Exists(screenFolder) && Directory.EnumerateFileSystemEntries(screenFolder).Any())
        {
            throw new InvalidOperationException(
                $"Research screen folder '{screenName}' already exists and is not empty. Choose a new screen name; existing screens are never overwritten.");
        }
    }

    // Validates that a dataset ID exists in the catalog, matches its expected kind, and optionally matches a timeframe.
    private async Task RequireDatasetKindAsync(
        string? datasetId,
        EvidenceDatasetKind expectedKind,
        string label,
        CancellationToken cancellationToken,
        string? requiredTimeframe = null)
    {
        if (String.IsNullOrWhiteSpace(datasetId))
            throw new ArgumentException($"A {label} dataset ID is required.");
        var manifest = await catalog.GetDatasetAsync(datasetId.Trim(), cancellationToken)
            ?? throw new InvalidDataException($"Catalog dataset '{datasetId.Trim()}' ({label}) was not found.");
        if (manifest.Kind != expectedKind)
            throw new InvalidDataException($"Dataset '{manifest.DatasetId}' is a {manifest.Kind} dataset, not {label}.");
        if (requiredTimeframe is not null &&
            (manifest.Partitions.Count == 0 || !manifest.Partitions.All(p => String.Equals(p.Provenance.Timeframe, requiredTimeframe, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidDataException($"Dataset '{manifest.DatasetId}' does not contain '{requiredTimeframe}' partitions.");
        }
    }

    // Builds the shared DailyScreenRules value object from an HTTP build request.
    private static DailyScreenRules BuildDailyScreenRules(BuildResearchScreenHttpRequest request) => new(
        request.MinPrice,
        DailyScreenRules.ParsePriceBasis(request.PriceBasis),
        request.AverageSessions,
        MinAverageShareVolume: request.MinAverageShareVolume,
        MinAverageDollarVolume: request.MinAverageDollarVolume,
        AtrPeriod: request.AtrPeriod,
        MinAtr: request.MinAtr,
        MinHistorySessions: request.MinHistorySessions,
        MinAverageRangePercent: request.MinAverageRangePercent);

    // Discovers approved .txt exclusion lists under ResearchScreensRoot and ConfigsRoot/research without accepting raw paths.
    private IReadOnlyList<(ResearchExclusionListOption Option, string FullPath)> DiscoverExclusionLists()
    {
        var results = new List<(ResearchExclusionListOption Option, string FullPath)>();
        void Scan(string root, string scopePrefix, string scopeLabel)
        {
            if (!Directory.Exists(root)) return;
            foreach (var file in Directory.EnumerateFiles(root, "*.txt", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(file);
                if (String.IsNullOrWhiteSpace(fileName)) continue;
                results.Add((
                    new ResearchExclusionListOption($"{scopePrefix}:{fileName}", $"{fileName} ({scopeLabel})", scopeLabel),
                    Path.GetFullPath(file)));
            }
        }

        Scan(paths.ResearchScreensRoot, "screens", "screens");
        Scan(Path.Combine(paths.ConfigsRoot, "research"), "configs", "configs/research");
        return results;
    }

    // Resolves an ExclusionListKey strictly against discovered .txt files, rejecting arbitrary or traversal paths.
    private string ResolveExclusionListPath(string key)
    {
        var discovered = DiscoverExclusionLists();
        var match = discovered.FirstOrDefault(item => String.Equals(item.Option.Key, key, StringComparison.Ordinal));
        if (match.FullPath is null)
        {
            throw new ArgumentException(
                $"Unknown exclusion list key '{key}'. Select one of the discovered symbol exclusion files; raw filesystem paths are not accepted.");
        }

        return match.FullPath;
    }

    // Reads published screen manifests under ResearchScreensRoot and maps any bound 5-minute datasets.
    private IReadOnlyList<PublishedResearchScreenOption> DiscoverPublishedScreens(
        IReadOnlyList<EvidenceDatasetManifest> allAsTradedBars)
    {
        if (!Directory.Exists(paths.ResearchScreensRoot)) return [];
        var screens = new List<PublishedResearchScreenOption>();
        foreach (var dir in Directory.EnumerateDirectories(paths.ResearchScreensRoot).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            if (!PublishedResearchScreen.IsPublished(dir)) continue;
            try
            {
                var (manifest, manifestSha, listSha) = PublishedResearchScreen.ReadDefinition(dir);
                var fiveMinuteIds = allAsTradedBars
                    .Where(dataset => String.Equals(dataset.Attributes.GetValueOrDefault("ranked_list_sha256"), listSha, StringComparison.OrdinalIgnoreCase) &&
                                      IntradayBarsCollectionPlanner.IsFullRegularSessionFiveMinuteDataset(dataset))
                    .Select(dataset => dataset.DatasetId)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                screens.Add(new PublishedResearchScreenOption(
                    Path.GetFileName(dir),
                    manifest.From,
                    manifest.To,
                    IsRanked: manifest.RankingRules is not null,
                    HasFredVix: manifest.Outputs.ContainsKey(ResearchScreenManifest.FredVixFileName),
                    HasSecurityMaster: !String.IsNullOrWhiteSpace(manifest.Inputs.SecurityMasterDatasetId),
                    manifestSha,
                    listSha,
                    fiveMinuteIds));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Skipping unreadable research screen directory '{Directory}'.", dir);
            }
        }

        return screens;
    }

    // Projects a catalog dataset manifest into a concise dropdown option with timeframe and row counts.
    private static ResearchEvidenceDatasetOption ToDatasetOption(EvidenceDatasetManifest manifest)
    {
        var timeframes = manifest.Partitions
            .Select(p => p.Provenance.Timeframe)
            .Where(tf => !String.IsNullOrWhiteSpace(tf))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var timeframe = timeframes.Length == 0 ? "n/a" : String.Join(",", timeframes);
        var rows = manifest.Partitions.Sum(p => p.RowCount);
        var summary = $"{manifest.DatasetId[..Math.Min(12, manifest.DatasetId.Length)]} · {manifest.CreatedAtUtc:yyyy-MM-dd} · {rows:N0} rows";
        return new ResearchEvidenceDatasetOption(
            manifest.DatasetId,
            manifest.Kind.ToString(),
            timeframe,
            manifest.CreatedAtUtc,
            manifest.Partitions.Count,
            rows,
            summary);
    }

    // Projects a PersistedJob row into the API-facing job snapshot record.
    private static ResearchEvidenceJobSnapshot ToSnapshot(PersistedJob job)
    {
        ProgressSnapshot? progress = null;
        if (!String.IsNullOrWhiteSpace(job.SnapshotJson))
        {
            try { progress = JsonSerializer.Deserialize<ProgressSnapshot>(job.SnapshotJson, JsonOptions); }
            catch (JsonException) { progress = null; }
        }

        WorkPayload? work = null;
        if (progress is null && !String.IsNullOrWhiteSpace(job.RequestJson))
        {
            try { work = JsonSerializer.Deserialize<WorkPayload>(job.RequestJson, JsonOptions); }
            catch (JsonException) { work = null; }
        }

        var kind = progress?.Kind ?? work?.Kind ?? "unknown";
        var label = progress?.Label ?? work?.Label ?? job.RunName;
        var stage = progress?.Stage ?? job.Status;
        var message = job.Status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            ? job.ErrorMessage ?? "Research evidence job failed."
            : progress?.Message ?? $"Job is {job.Status}.";

        return new ResearchEvidenceJobSnapshot(
            job.Id,
            kind,
            label,
            job.Status,
            stage,
            message,
            progress?.ProcessedCount ?? 0,
            job.CreatedAt,
            job.StartedAt,
            job.FinishedAt,
            job.ErrorMessage,
            progress?.ScreenResult,
            progress?.CollectionResult);
    }

    // Persists a progress snapshot under the active durable lease.
    private static Task SaveProgressAsync(
        DurableJobExecutionContext context,
        string kind,
        string label,
        string stage,
        string message,
        int processedCount,
        CancellationToken cancellationToken) =>
        context.SaveSnapshotAsync(SerializeProgress(kind, label, stage, message, processedCount), cancellationToken);

    // Serializes structured job progress metadata to JSON for durable persistence.
    private static string SerializeProgress(
        string kind,
        string label,
        string stage,
        string message,
        int processedCount,
        ResearchScreenBuildJobResult? screenResult = null,
        MinuteBarsCollectJobResult? collectionResult = null) =>
        JsonSerializer.Serialize(
            new ProgressSnapshot(kind, label, stage, message, processedCount, screenResult, collectionResult),
            JsonOptions);

    // Translates internal publisher stage tokens into plain-language operator status messages.
    private static string FormatScreenStageMessage(string stage, string screenName, int processed) => stage switch
    {
        "loading_datasets" => $"Loading daily bars, corporate actions, and calendars for '{screenName}'...",
        "screening_symbols" => $"Screening point-in-time rules for '{screenName}' ({processed:N0} symbols processed)...",
        "finalizing_outputs" => $"Writing eligible lists and daily counts for '{screenName}'...",
        "downloading_fred_vix" => $"Downloading and fingerprinting frozen FRED VIXCLS closes for '{screenName}'...",
        "publishing_manifest" => $"Publishing fingerprinted screen-manifest.json for '{screenName}'...",
        _ => $"Processing '{screenName}' ({stage})..."
    };
}
