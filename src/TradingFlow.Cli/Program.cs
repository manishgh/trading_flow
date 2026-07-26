using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Json;
using TradingFlow.Backtesting;
using TradingFlow.Backtesting.Optimization;
using TradingFlow.Backtesting.Research;
using TradingFlow.Backtesting.StrategyEvaluation;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Collection;
using TradingFlow.Data.Evidence.Labeling;
using TradingFlow.Data.Evidence.Normalization;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Data.Evidence.Research;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;
using TradingFlow.Research;
using TradingFlow.Research.Momentum;
using TradingFlow.Research.Orchestration;
using TradingFlow.Research.Workflows;

var serializerOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNameCaseInsensitive = true
};
serializerOptions.Converters.Add(new JsonStringEnumConverter());

if (args.Length > 0 &&
    args[0].Equals("evidence-inventory", StringComparison.OrdinalIgnoreCase))
{
    var catalogPath = Path.GetFullPath(RequireStringOption(args, "--catalog"));
    var artifactRoot = Path.GetFullPath(RequireStringOption(args, "--artifact-root"));
    var artifactStore = new FileSystemImmutableArtifactStore(
        new ImmutableArtifactStoreOptions(artifactRoot));
    var catalog = new SqliteEvidenceCatalog(
        new EvidenceCatalogOptions(catalogPath, EvidenceCatalogOpenMode.OpenExisting),
        artifactStore);
    var datasets = await catalog.FindDatasetsAsync(new EvidenceDatasetQuery());
    Console.WriteLine(JsonSerializer.Serialize(
        datasets
            .OrderBy(dataset => dataset.Kind)
            .ThenBy(dataset => dataset.CreatedAtUtc)
            .Select(dataset => new
            {
                dataset.DatasetId,
                dataset.Kind,
                dataset.CreatedAtUtc,
                dataset.DataFeed,
                PartitionCount = dataset.Partitions.Count,
                RowCount = dataset.Partitions.Sum(partition => partition.RowCount),
                MinimumSourceTimestampUtc = dataset.Partitions.Min(
                    partition => partition.MinimumSourceTimestampUtc),
                MaximumSourceTimestampUtc = dataset.Partitions.Max(
                    partition => partition.MaximumSourceTimestampUtc),
                dataset.Attributes
            })
            .ToArray(),
        serializerOptions));
    return;
}

if (args.Length > 0 &&
    args[0].Equals(
        "evidence-collection-status",
        StringComparison.OrdinalIgnoreCase))
{
    var requestPath = Path.GetFullPath(RequireStringOption(args, "--request"));
    var catalogPath = Path.GetFullPath(RequireStringOption(args, "--catalog"));
    var artifactRoot = Path.GetFullPath(RequireStringOption(args, "--artifact-root"));
    var request = JsonSerializer.Deserialize<EvidenceDatasetCollectionRequest>(
                      await File.ReadAllTextAsync(requestPath),
                      serializerOptions)
                  ?? throw new InvalidDataException(
                      "The frozen evidence collection request is empty.");
    var artifactStore = new FileSystemImmutableArtifactStore(
        new ImmutableArtifactStoreOptions(artifactRoot));
    var catalog = new SqliteEvidenceCatalog(
        new EvidenceCatalogOptions(catalogPath, EvidenceCatalogOpenMode.OpenExisting),
        artifactStore);
    var plan = request.CreatePlan();
    var checkpoint = await catalog.GetCollectionCheckpointAsync(plan.JobId);
    var cursors = new List<EvidenceRequestCursorCheckpoint?>();
    foreach (var collectionRequest in plan.Requests)
    {
        cursors.Add(await catalog.GetRequestCursorCheckpointAsync(
            plan.JobId,
            collectionRequest.RequestId));
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        plan.JobId,
        checkpoint,
        Requests = plan.Requests.Select((collectionRequest, index) => new
        {
            collectionRequest.RequestId,
            PageOrdinal = cursors[index]?.PageOrdinal,
            AttemptCount = cursors[index]?.AttemptCount,
            Exhausted = cursors[index]?.Exhausted,
            UpdatedAtUtc = cursors[index]?.UpdatedAtUtc
        })
    }, serializerOptions));
    return;
}

if (args.Length > 0 &&
    args[0].Equals(
        "evidence-recover-normalization",
        StringComparison.OrdinalIgnoreCase))
{
    var requestPath = Path.GetFullPath(RequireStringOption(args, "--request"));
    var catalogPath = Path.GetFullPath(RequireStringOption(args, "--catalog"));
    var artifactRoot = Path.GetFullPath(RequireStringOption(args, "--artifact-root"));
    var actor = RequireStringOption(args, "--actor");
    var reason = RequireStringOption(args, "--reason");
    var request = JsonSerializer.Deserialize<EvidenceDatasetCollectionRequest>(
                      await File.ReadAllTextAsync(requestPath),
                      serializerOptions)
                  ?? throw new InvalidDataException(
                      "The frozen evidence collection request is empty.");
    var artifactStore = new FileSystemImmutableArtifactStore(
        new ImmutableArtifactStoreOptions(artifactRoot));
    var catalog = new SqliteEvidenceCatalog(
        new EvidenceCatalogOptions(catalogPath, EvidenceCatalogOpenMode.OpenExisting),
        artifactStore);
    var plan = request.CreatePlan();
    await catalog.RecoverQuarantinedNormalizationAsync(
        plan.JobId,
        actor,
        reason,
        DateTimeOffset.UtcNow);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        plan.JobId,
        State = EvidenceCollectionState.Normalizing,
        actor,
        reason
    }, serializerOptions));
    return;
}

if (args.Length > 1 &&
    args[0].Equals("ops", StringComparison.OrdinalIgnoreCase) &&
    args[1].Equals("ack", StringComparison.OrdinalIgnoreCase))
{
    var serviceUrl = RequireStringOption(args, "--url").TrimEnd('/');
    var reconciliationId = Guid.Parse(RequireStringOption(args, "--reconciliation-id"));
    var actor = RequireStringOption(args, "--actor");
    var reason = RequireStringOption(args, "--reason");
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    using var response = await client.PostAsJsonAsync(
        $"{serviceUrl}/api/operations/reconciliations/{reconciliationId:D}/ack",
        new { actor, reason });
    var payload = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException(
            $"Reconciliation acknowledgement failed with HTTP {(int)response.StatusCode}: {payload}");
    }

    Console.WriteLine(payload);
    return;
}

if (args.Length > 0 && args[0].Equals("database-backup", StringComparison.OrdinalIgnoreCase))
{
    var databasePath = RequireStringOption(args, "--database");
    var backupRoot = RequireStringOption(args, "--backup-root");
    var operationalDateText = RequireStringOption(args, "--operational-date");
    var operationalDate = DateOnly.ParseExact(
        operationalDateText,
        "yyyy-MM-dd",
        System.Globalization.CultureInfo.InvariantCulture);
    var service = new TradingFlow.Data.Backups.SqliteDatabaseBackupService(
        databasePath,
        backupRoot,
        AtomicFileArtifactWriter.Instance);
    var result = await service.CreateDailyBackupAsync(operationalDate);
    Console.WriteLine(JsonSerializer.Serialize(result, serializerOptions));
    return;
}

if (args.Length > 0 && args[0].Equals("database-restore", StringComparison.OrdinalIgnoreCase))
{
    var backupPath = RequireStringOption(args, "--backup");
    var destinationPath = RequireStringOption(args, "--destination");
    var service = new TradingFlow.Data.Backups.SqliteDatabaseRestoreService();
    var result = await service.RestoreToEmptyAsync(backupPath, destinationPath);
    Console.WriteLine(JsonSerializer.Serialize(result, serializerOptions));
    return;
}

if (args.Length > 0 && args[0].Equals("alpaca-stream-smoke", StringComparison.OrdinalIgnoreCase))
{
    var timeoutSeconds = ParseIntOption(args, "--timeout-seconds") ?? 20;
    if (timeoutSeconds is < 1 or > 120)
    {
        throw new ArgumentOutOfRangeException(
            nameof(timeoutSeconds),
            timeoutSeconds,
            "Stream smoke timeout must be between 1 and 120 seconds.");
    }

    var options = TradingFlow.Alpaca.AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
    {
        KeyId = ResolveSecret("Alpaca", "KeyId", "ALPACA_KEY_ID"),
        SecretKey = ResolveSecret("Alpaca", "SecretKey", "ALPACA_SECRET_KEY"),
        MarketDataFeed = "sip",
        AllowIexFallback = false
    };

    if (String.IsNullOrWhiteSpace(options.KeyId) || String.IsNullOrWhiteSpace(options.SecretKey))
    {
        throw new InvalidOperationException(
            "Alpaca credentials are not configured. Use ALPACA_KEY_ID and ALPACA_SECRET_KEY or the ignored local development secret store.");
    }

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
    using var stream = new TradingFlow.Alpaca.AlpacaStreamClient(options);
    await stream.ConnectAsync(timeout.Token);
    Console.WriteLine("Alpaca SIP market-data stream authentication succeeded.");
    return;
}

if (args.Length > 0 &&
    args[0].Equals("evidence-collect-normalize", StringComparison.OrdinalIgnoreCase))
{
    var requestPath = Path.GetFullPath(RequireStringOption(args, "--request"));
    var catalogPath = Path.GetFullPath(RequireStringOption(args, "--catalog"));
    var artifactRoot = Path.GetFullPath(RequireStringOption(args, "--artifact-root"));
    if (!File.Exists(requestPath))
    {
        throw new FileNotFoundException(
            "The frozen evidence collection request was not found.",
            requestPath);
    }

    var request = JsonSerializer.Deserialize<EvidenceDatasetCollectionRequest>(
                      await File.ReadAllTextAsync(requestPath),
                      serializerOptions)
                  ?? throw new InvalidDataException(
                      "The frozen evidence collection request is empty.");
    var keyId = ResolveSecret("Alpaca", "KeyId", "ALPACA_KEY_ID");
    var secretKey = ResolveSecret("Alpaca", "SecretKey", "ALPACA_SECRET_KEY");
    if (String.IsNullOrWhiteSpace(keyId) || String.IsNullOrWhiteSpace(secretKey))
    {
        throw new InvalidOperationException(
            "Alpaca credentials are not configured. Use ALPACA_KEY_ID and ALPACA_SECRET_KEY or the ignored local development secret store.");
    }

    var artifactStore = new FileSystemImmutableArtifactStore(
        new ImmutableArtifactStoreOptions(artifactRoot));
    var catalogMode = File.Exists(catalogPath)
        ? EvidenceCatalogOpenMode.OpenExisting
        : EvidenceCatalogOpenMode.BootstrapNew;
    var catalog = new SqliteEvidenceCatalog(
        new EvidenceCatalogOptions(catalogPath, catalogMode),
        artifactStore);
    var publicationCoordinator = new EvidencePublicationCoordinator(
        catalog,
        artifactStore);
    var endpoints = TradingFlow.Alpaca.AlpacaEndpointResolver.Resolve(
        ProductionProfile.Paper);
    using var httpClient = new HttpClient
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    httpClient.DefaultRequestHeaders.Add("APCA-API-KEY-ID", keyId);
    httpClient.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", secretKey);
    var collector = new EvidenceHttpCollector(
        httpClient,
        catalog,
        publicationCoordinator,
        [
            new AlpacaBarsEvidenceRequestAdapter(endpoints.MarketDataRest),
            new AlpacaQuotesEvidenceRequestAdapter(endpoints.MarketDataRest),
            new AlpacaNewsEvidenceRequestAdapter(endpoints.MarketDataRest),
            new AlpacaExchangeCalendarEvidenceRequestAdapter(endpoints.TradingRest)
        ],
        new EvidenceHttpCollectionOptions(
            workerCount: ParseIntOption(args, "--workers") ?? 4,
            boundedCapacity: ParseIntOption(args, "--capacity") ?? 16,
            maximumAttempts: ParseIntOption(args, "--maximum-attempts") ?? 4,
            requestTimeout: TimeSpan.FromSeconds(
                ParseIntOption(args, "--request-timeout-seconds") ?? 30)));
    var normalizer = new EvidenceNormalizationOrchestrator(
        catalog,
        artifactStore,
        new EvidenceParquetPartitionPublisher(
            new EvidenceParquetCodec(),
            artifactStore));
    var workflow = new EvidenceDatasetCollectionWorkflow(collector, normalizer);
    var plan = request.CreatePlan();
    using var commandCancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancellationHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        commandCancellation.Cancel();
    };
    Console.CancelKeyPress += cancellationHandler;
    try
    {
        var recovery = await publicationCoordinator.RecoverPendingPublicationsAsync(
            maximumPublications: 10_000,
            commandCancellation.Token);
        if (recovery.Quarantined > 0 || recovery.Failed > 0)
        {
            throw new InvalidDataException(
                "Evidence publication recovery found quarantined or failed entries. " +
                "Inspect the catalog before starting another collection.");
        }

        var result = await workflow.RunAsync(
            plan,
            request.CreateNormalizationJob(plan),
            commandCancellation.Token);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result.JobId,
            result.DatasetId,
            result.AlreadyCommitted,
            result.PartitionCount,
            result.RowCount,
            PublicationRecovery = recovery,
            Catalog = catalogPath,
            ArtifactRoot = artifactRoot
        }, serializerOptions));
    }
    catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
    {
        Environment.ExitCode = 130;
        Console.Error.WriteLine(
            $"Evidence collection '{plan.JobId}' was interrupted and checkpointed for resume.");
    }
    finally
    {
        Console.CancelKeyPress -= cancellationHandler;
    }

    return;
}

if (args.Length > 0 &&
    args[0].Equals(
        "evidence-export-catalyst-label-sample",
        StringComparison.OrdinalIgnoreCase))
{
    var sourceDatasetId = RequireStringOption(args, "--dataset-id");
    var sampleSize = ParseIntOption(args, "--sample-size") ?? 500;
    var catalogPath = Path.GetFullPath(RequireStringOption(args, "--catalog"));
    var artifactRoot = Path.GetFullPath(RequireStringOption(args, "--artifact-root"));
    var outputDirectory = Path.GetFullPath(RequireStringOption(args, "--output-dir"));
    var artifactStore = new FileSystemImmutableArtifactStore(
        new ImmutableArtifactStoreOptions(artifactRoot));
    var catalog = new SqliteEvidenceCatalog(
        new EvidenceCatalogOptions(catalogPath, EvidenceCatalogOpenMode.OpenExisting),
        artifactStore);
    var source = await catalog.GetDatasetAsync(sourceDatasetId)
        ?? throw new InvalidOperationException(
            $"Committed news dataset '{sourceDatasetId}' was not found.");
    var partitionReader = new ParquetEvidencePartitionDataReader(
        new EvidenceParquetCodec(),
        artifactStore);
    var news = await partitionReader.ReadNewsRevisionsAsync(source);
    var sampling = CatalystLabelingSampleDefinition.Stratified(sampleSize);
    var workItems = CatalystLabelingWorkItemExporter.Export(source, news, sampling);
    var labelTemplate = CatalystHumanLabelTemplateExporter.Export(workItems);
    var packageId = $"{source.DatasetId[..12]}-{workItems.Sha256[..12]}";
    var packageDirectory = Path.Combine(outputDirectory, packageId);
    var workItemPath = Path.Combine(
        packageDirectory,
        $"catalyst-label-work-items.{workItems.Sha256}.ndjson");
    var labelPath = Path.Combine(
        packageDirectory,
        $"catalyst-human-labels.{workItems.Sha256}.json");
    var manifestPath = Path.Combine(packageDirectory, "labeling-package.json");
    var packageManifest = JsonSerializer.Serialize(new
    {
        FormatVersion = "tradingflow.catalyst-label-package.v1",
        PackageId = packageId,
        SourceNewsDatasetId = source.DatasetId,
        WorkItemFormatVersion = CatalystLabelingWorkItemExporter.FormatVersion,
        HumanLabelFormatVersion = CatalystHumanLabelImporter.FormatVersion,
        Sampling = workItems.Sampling,
        workItems.EligibleCandidateCount,
        SelectedWorkItemCount = workItems.WorkItems.Count,
        WorkItemExportSha256 = workItems.Sha256,
        HumanLabelTemplateSha256 = labelTemplate.Sha256,
        Files = new
        {
            WorkItems = Path.GetFileName(workItemPath),
            HumanLabels = Path.GetFileName(labelPath)
        },
        AllowedValues = new
        {
            Categories = Enum.GetNames<CatalystNewsCategory>(),
            Directions = Enum.GetNames<CatalystDirection>(),
            Materialities = Enum.GetNames<CatalystMateriality>()
        },
        RequiredHumanEvidence = new
        {
            MinimumValidLabels = 500,
            MinimumDoubleLabeledArticles = 100,
            EveryCategoryRequired = true,
            Instruction =
                "Label exact exported revisions only. Do not infer labels automatically. " +
                "Conflicting double labels require explicit adjudication."
        }
    }, serializerOptions);

    await WriteExactOrVerifyAsync(
        workItemPath,
        System.Text.Encoding.UTF8.GetString(workItems.Content.Span));
    await WriteExactOrVerifyAsync(
        labelPath,
        System.Text.Encoding.UTF8.GetString(labelTemplate.Content.Span));
    await WriteExactOrVerifyAsync(manifestPath, packageManifest);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        PackageId = packageId,
        PackageDirectory = packageDirectory,
        WorkItems = workItemPath,
        HumanLabels = labelPath,
        Manifest = manifestPath,
        workItems.EligibleCandidateCount,
        SelectedWorkItemCount = workItems.WorkItems.Count,
        WorkItemExportSha256 = workItems.Sha256,
        HumanLabelTemplateSha256 = labelTemplate.Sha256
    }, serializerOptions));
    return;
}

if (args.Length > 0 &&
    args[0].Equals(
        "evidence-import-catalyst-ground-truth",
        StringComparison.OrdinalIgnoreCase))
{
    var sourceDatasetId = RequireStringOption(args, "--dataset-id");
    var sampleSize = ParseIntOption(args, "--sample-size") ?? 500;
    var labelDocumentPath = Path.GetFullPath(
        RequireStringOption(args, "--label-document"));
    var catalogPath = Path.GetFullPath(RequireStringOption(args, "--catalog"));
    var artifactRoot = Path.GetFullPath(RequireStringOption(args, "--artifact-root"));
    var runId = RequireStringOption(args, "--run-id");
    var configHash = RequireStringOption(args, "--config-hash");
    var codeVersion = RequireStringOption(args, "--code-version");
    var builderVersion = RequireStringOption(args, "--builder-version");
    var createdAtUtc = DateTimeOffset.Parse(
        RequireStringOption(args, "--created-at-utc"),
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.RoundtripKind);
    if (!File.Exists(labelDocumentPath))
    {
        throw new FileNotFoundException(
            "The completed human-label document was not found.",
            labelDocumentPath);
    }

    var artifactStore = new FileSystemImmutableArtifactStore(
        new ImmutableArtifactStoreOptions(artifactRoot));
    var catalog = new SqliteEvidenceCatalog(
        new EvidenceCatalogOptions(catalogPath, EvidenceCatalogOpenMode.OpenExisting),
        artifactStore);
    var codec = new EvidenceParquetCodec();
    var builder = new CatalystGroundTruthDatasetBuilder(
        catalog,
        new ParquetEvidencePartitionDataReader(codec, artifactStore),
        new EvidenceParquetPartitionPublisher(codec, artifactStore),
        artifactStore);
    var result = await builder.BuildAsync(new CatalystGroundTruthBuildRequest(
        sourceDatasetId,
        await File.ReadAllBytesAsync(labelDocumentPath),
        runId,
        configHash,
        codeVersion,
        builderVersion,
        createdAtUtc,
        new CatalystGroundTruthReadinessThresholds(),
        CatalystLabelingSampleDefinition.Stratified(sampleSize)));

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        result.DatasetId,
        result.AlreadyCommitted,
        result.RowCount,
        WorkItemExport = result.WorkItemExportArtifact,
        HumanLabelDocument = result.HumanLabelDocumentArtifact,
        result.Readiness.IsReady,
        result.Readiness.CatalystPromotionAllowed,
        result.Readiness.ValidLabelCount,
        result.Readiness.DoubleLabeledArticleCount,
        result.Readiness.AdjudicatedArticleCount,
        result.Readiness.UnresolvedConflictCount,
        result.Readiness.CategoryCounts,
        result.Readiness.Failures
    }, serializerOptions));
    return;
}

if (args.Length > 0 &&
    args[0].Equals("research-momentum", StringComparison.OrdinalIgnoreCase))
{
    var requestPath = Path.GetFullPath(RequireStringOption(args, "--request"));
    var catalogPath = Path.GetFullPath(RequireStringOption(args, "--catalog"));
    var artifactRoot = Path.GetFullPath(RequireStringOption(args, "--artifact-root"));
    if (!File.Exists(requestPath))
    {
        throw new FileNotFoundException(
            "The frozen momentum research request was not found.",
            requestPath);
    }

    var request = JsonSerializer.Deserialize<CatalogMomentumWorkflowRequest>(
                      await File.ReadAllTextAsync(requestPath),
                      new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? throw new InvalidDataException(
                      "The frozen momentum research request is empty.");
    var artifactStore = new FileSystemImmutableArtifactStore(
        new ImmutableArtifactStoreOptions(artifactRoot));
    var catalog = new SqliteEvidenceCatalog(
        new EvidenceCatalogOptions(catalogPath, EvidenceCatalogOpenMode.OpenExisting),
        artifactStore);
    var partitionReader = new ParquetEvidencePartitionDataReader(
        new EvidenceParquetCodec(),
        artifactStore);
    var workflow = new CatalogResearchWorkflow(
        catalog,
        partitionReader,
        new EvidenceResearchRunArtifactPackager(artifactStore));
    var result = await workflow.RunMomentumAsync(request);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        result.Manifest.ResearchRunId,
        result.Manifest.StudyId,
        result.Manifest.EvidenceReady,
        result.Manifest.ReadinessFailures,
        result.AlreadyRegistered,
        result.Study.Report.DataEvidenceReady,
        result.Study.Report.PromotionEligible,
        result.Study.Report.PromotionBlockers,
        result.Study.Report.LoadedTickerCount,
        result.Study.Report.DecisionDateCount,
        result.Study.Report.HoldoutDecisionDateCount
    }, serializerOptions));
    return;
}

if (args.Length > 0 &&
    args[0].Equals(
        "research-momentum-static-diagnostic",
        StringComparison.OrdinalIgnoreCase))
{
    var requestPath = Path.GetFullPath(RequireStringOption(args, "--request"));
    var catalogPath = Path.GetFullPath(RequireStringOption(args, "--catalog"));
    var artifactRoot = Path.GetFullPath(RequireStringOption(args, "--artifact-root"));
    if (!File.Exists(requestPath))
    {
        throw new FileNotFoundException(
            "The frozen static-universe momentum diagnostic request was not found.",
            requestPath);
    }

    var request = JsonSerializer.Deserialize<CatalogStaticUniverseMomentumRequest>(
                      await File.ReadAllTextAsync(requestPath),
                      new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? throw new InvalidDataException(
                      "The frozen static-universe momentum diagnostic request is empty.");
    var artifactStore = new FileSystemImmutableArtifactStore(
        new ImmutableArtifactStoreOptions(artifactRoot));
    var catalog = new SqliteEvidenceCatalog(
        new EvidenceCatalogOptions(catalogPath, EvidenceCatalogOpenMode.OpenExisting),
        artifactStore);
    var partitionReader = new ParquetEvidencePartitionDataReader(
        new EvidenceParquetCodec(),
        artifactStore);
    var workflow = new CatalogResearchWorkflow(
        catalog,
        partitionReader,
        new EvidenceResearchRunArtifactPackager(artifactStore));
    var result = await workflow.RunStaticUniverseMomentumDiagnosticAsync(request);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        result.Manifest.ResearchRunId,
        result.Manifest.StudyId,
        result.Manifest.EvidenceReady,
        result.Manifest.ReadinessFailures,
        result.AlreadyRegistered,
        result.Report.DataEvidenceReady,
        result.Report.PromotionEligible,
        result.Report.PromotionBlockers,
        result.Report.LoadedTickerCount,
        result.Report.DecisionDateCount,
        result.Report.HoldoutDecisionDateCount,
        result.Report.ExecutionCosts,
        PrimaryQuantileCohorts = result.Report.Cohorts
            .Where(cohort => cohort.Quantile == 1)
            .OrderBy(cohort => cohort.Cell, StringComparer.Ordinal)
            .ThenBy(cohort => cohort.Segment, StringComparer.Ordinal)
            .ThenBy(cohort => cohort.ForwardHorizonBars)
            .ToArray(),
        result.Report.Comparisons,
        RankObservationCount = result.Report.RankObservations.Count,
        Robustness = result.Audit.Robustness,
        TopTickerContributors = result.Audit.Tickers
            .Where(value =>
                value.Segment == MomentumStudySegment.Full &&
                value.ForwardHorizonBars is 20 or 60)
            .GroupBy(value => new { value.Cell, value.ForwardHorizonBars })
            .SelectMany(group => group
                .OrderByDescending(value => value.TotalNetReturnPct)
                .Take(5))
            .ToArray(),
        BottomTickerContributors = result.Audit.Tickers
            .Where(value =>
                value.Segment == MomentumStudySegment.Full &&
                value.ForwardHorizonBars is 20 or 60)
            .GroupBy(value => new { value.Cell, value.ForwardHorizonBars })
            .SelectMany(group => group
                .OrderBy(value => value.TotalNetReturnPct)
                .Take(5))
            .ToArray(),
        result.FrozenSymbols,
        result.SurvivorshipSelectionBiasWarning
    }, serializerOptions));
    return;
}

if (args.Length > 0 &&
    args[0].Equals(
        "research-momentum-audit",
        StringComparison.OrdinalIgnoreCase))
{
    var researchRunId = RequireStringOption(args, "--run-id");
    var catalogPath = Path.GetFullPath(RequireStringOption(args, "--catalog"));
    var artifactRoot = Path.GetFullPath(RequireStringOption(args, "--artifact-root"));
    var auditCell = ParseStringOption(args, "--cell");
    var auditSegment = ParseStringOption(args, "--segment")
        ?? MomentumStudySegment.Full;
    var auditHorizonText = ParseStringOption(args, "--horizon");
    var auditHorizon = auditHorizonText is null
        ? (int?)null
        : Int32.Parse(
            auditHorizonText,
            System.Globalization.CultureInfo.InvariantCulture);
    var auditSummaryOnly = ParseFlag(args, "--summary-only");
    var artifactStore = new FileSystemImmutableArtifactStore(
        new ImmutableArtifactStoreOptions(artifactRoot));
    var catalog = new SqliteEvidenceCatalog(
        new EvidenceCatalogOptions(catalogPath, EvidenceCatalogOpenMode.OpenExisting),
        artifactStore);
    var manifest = await catalog.GetResearchRunAsync(researchRunId)
        ?? throw new InvalidDataException(
            $"Research run '{researchRunId}' is not registered.");
    var reportReference = manifest.Outputs.SingleOrDefault(output =>
        output.ObjectNamespace.Value.Equals(
            "research/reports/momentum-report",
            StringComparison.Ordinal))
        ?? throw new InvalidDataException(
            $"Research run '{researchRunId}' has no momentum-report output.");
    await using var reportStream = await artifactStore.OpenReadAsync(reportReference);
    using var reportBuffer = new MemoryStream();
    await reportStream.CopyToAsync(reportBuffer);
    var report = EvidenceCanonicalJson.Deserialize<CrossSectionalMomentumReport>(
        reportBuffer.ToArray());
    var audit = new MomentumResearchAuditAnalyzer().Analyze(
        report.RankObservations,
        report.Options.DecisionCadenceBars);
    bool MatchesAuditFilter(
        string cell,
        string segment,
        int horizon) =>
        (auditCell is null ||
         cell.Equals(auditCell, StringComparison.OrdinalIgnoreCase)) &&
        (auditSegment.Equals("all", StringComparison.OrdinalIgnoreCase) ||
         segment.Equals(auditSegment, StringComparison.OrdinalIgnoreCase)) &&
        (auditHorizon is null || horizon == auditHorizon.Value);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        manifest.ResearchRunId,
        manifest.StudyId,
        manifest.CreatedAtUtc,
        manifest.EvidenceReady,
        manifest.ReadinessFailures,
        report.ExecutionCosts,
        Filters = new
        {
            Cell = auditCell ?? "all",
            Segment = auditSegment,
            Horizon = auditHorizonText ?? "all"
        },
        Robustness = audit.Robustness
            .Where(value => MatchesAuditFilter(
                value.Cell,
                value.Segment,
                value.ForwardHorizonBars))
            .ToArray(),
        TopTickerContributors = audit.Tickers
            .Where(value => MatchesAuditFilter(
                value.Cell,
                value.Segment,
                value.ForwardHorizonBars))
            .GroupBy(value => new { value.Cell, value.ForwardHorizonBars })
            .SelectMany(group => group
                .OrderByDescending(value => value.TotalNetReturnPct)
                .Take(auditSummaryOnly ? 0 : 5))
            .ToArray(),
        BottomTickerContributors = audit.Tickers
            .Where(value => MatchesAuditFilter(
                value.Cell,
                value.Segment,
                value.ForwardHorizonBars))
            .GroupBy(value => new { value.Cell, value.ForwardHorizonBars })
            .SelectMany(group => group
                .OrderBy(value => value.TotalNetReturnPct)
                .Take(auditSummaryOnly ? 0 : 5))
            .ToArray(),
        TopFormations = audit.Formations
            .Where(value => MatchesAuditFilter(
                value.Cell,
                value.Segment,
                value.ForwardHorizonBars))
            .GroupBy(value => new { value.Cell, value.ForwardHorizonBars })
            .SelectMany(group => group
                .OrderByDescending(value => value.MeanNetReturnPct)
                .Take(auditSummaryOnly ? 0 : 5))
            .ToArray(),
        BottomFormations = audit.Formations
            .Where(value => MatchesAuditFilter(
                value.Cell,
                value.Segment,
                value.ForwardHorizonBars))
            .GroupBy(value => new { value.Cell, value.ForwardHorizonBars })
            .SelectMany(group => group
                .OrderBy(value => value.MeanNetReturnPct)
                .Take(auditSummaryOnly ? 0 : 5))
            .ToArray()
    }, serializerOptions));
    return;
}

if (args.Length > 0 &&
    args[0].Equals(
        "research-daily-news-diagnostic",
        StringComparison.OrdinalIgnoreCase))
{
    var requestPath = Path.GetFullPath(RequireStringOption(args, "--request"));
    var catalogPath = Path.GetFullPath(RequireStringOption(args, "--catalog"));
    var artifactRoot = Path.GetFullPath(RequireStringOption(args, "--artifact-root"));
    if (!File.Exists(requestPath))
    {
        throw new FileNotFoundException(
            "The frozen provider-updated daily-news diagnostic request was not found.",
            requestPath);
    }

    var request =
        JsonSerializer.Deserialize<CatalogProviderUpdatedDailyNewsDiagnosticRequest>(
            await File.ReadAllTextAsync(requestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException(
            "The frozen provider-updated daily-news diagnostic request is empty.");
    var artifactStore = new FileSystemImmutableArtifactStore(
        new ImmutableArtifactStoreOptions(artifactRoot));
    var catalog = new SqliteEvidenceCatalog(
        new EvidenceCatalogOptions(catalogPath, EvidenceCatalogOpenMode.OpenExisting),
        artifactStore);
    var partitionReader = new ParquetEvidencePartitionDataReader(
        new EvidenceParquetCodec(),
        artifactStore);
    var workflow = new CatalogResearchWorkflow(
        catalog,
        partitionReader,
        new EvidenceResearchRunArtifactPackager(artifactStore));
    var result =
        await workflow.RunProviderUpdatedDailyNewsDiagnosticAsync(request);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        result.Manifest.ResearchRunId,
        result.Manifest.StudyId,
        result.Manifest.EvidenceReady,
        result.Manifest.ReadinessFailures,
        result.AlreadyRegistered,
        result.Report.PromotionEligible,
        result.Report.PromotionBlockers,
        StoryCount = result.Report.StoryClusters.Count,
        EventCount = result.Report.EventObservations.Count,
        IndependentEpisodeCount = result.Report.EventObservations.Count(
            value => value.IsIndependentEpisodeStart),
        ReturnCount = result.Report.HorizonReturns.Count,
        CleanReturnCount = result.Report.HorizonReturns.Count(value => value.IsClean),
        result.Report.Exclusions,
        result.Report.Summaries,
        CategorySummaryCount = result.Report.CategorySummaries.Count,
        result.FrozenSymbols
    }, serializerOptions));
    return;
}

if (args.Length > 0 &&
    args[0].Equals("research-catalyst", StringComparison.OrdinalIgnoreCase))
{
    var requestPath = Path.GetFullPath(RequireStringOption(args, "--request"));
    var catalogPath = Path.GetFullPath(RequireStringOption(args, "--catalog"));
    var artifactRoot = Path.GetFullPath(RequireStringOption(args, "--artifact-root"));
    if (!File.Exists(requestPath))
    {
        throw new FileNotFoundException(
            "The frozen catalyst research request was not found.",
            requestPath);
    }

    var request = JsonSerializer.Deserialize<CatalogCatalystWorkflowRequest>(
                      await File.ReadAllTextAsync(requestPath),
                      new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? throw new InvalidDataException(
                      "The frozen catalyst research request is empty.");
    var artifactStore = new FileSystemImmutableArtifactStore(
        new ImmutableArtifactStoreOptions(artifactRoot));
    var catalog = new SqliteEvidenceCatalog(
        new EvidenceCatalogOptions(catalogPath, EvidenceCatalogOpenMode.OpenExisting),
        artifactStore);
    var partitionReader = new ParquetEvidencePartitionDataReader(
        new EvidenceParquetCodec(),
        artifactStore);
    var workflow = new CatalogResearchWorkflow(
        catalog,
        partitionReader,
        new EvidenceResearchRunArtifactPackager(artifactStore));
    var result = await workflow.RunCatalystDiagnosticAsync(request);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        result.Manifest.ResearchRunId,
        result.Manifest.StudyId,
        result.Manifest.EvidenceReady,
        result.Manifest.ReadinessFailures,
        result.AlreadyRegistered,
        result.ClassifierGroundTruthReady,
        result.Study.ExecutableEvidenceReady,
        StudyReadinessFailures = result.Study.ReadinessFailures,
        ObservationCount = result.Study.Report.Observations.Count,
        ExecutableObservationCount = result.Study.ExecutableObservations.Count
    }, serializerOptions));
    return;
}

if (args.Length > 0 && args[0].Equals("optimize", StringComparison.OrdinalIgnoreCase))
{
    var optConfigPath = args.Length > 1
        ? args[1]
        : throw new ArgumentException("Optimization config path required.");

    var reader = new SimpleYamlReader();
    var runner = new BacktestRunner(reader, rawArchiveWriter: CreateRawArchiveWriter());
    var optimizer = new StrategyOptimizer(reader, runner);

    var progress = new Progress<BacktestProgress>(update =>
    {
        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {update.Stage}: {update.Message}");
    });
    var optimizationProgress = new Progress<TradingFlow.Domain.Optimization.OptimizationProgress>(update =>
    {
        if (update.Kind.Equals("completed", StringComparison.OrdinalIgnoreCase) && update.CompletedRun is not null)
        {
            Console.WriteLine(
                $"{DateTimeOffset.Now:HH:mm:ss} optimization_result: {update.CurrentPermutation}/{update.TotalPermutations} " +
                $"{update.StrategyName} return={update.CompletedRun.TotalReturnPct:0.00}% daily={update.CompletedRun.AverageDailyReturnPct:0.0000}% net={update.CompletedRun.NetProfit:0.00} " +
                $"drawdown={update.CompletedRun.MaxDrawdownPct:0.00}%");
        }
    });

    var optResult = await optimizer.OptimizeAsync(optConfigPath, CancellationToken.None, progress, optimizationProgress);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        optResult.RunName,
        optResult.TopRuns.Count,
        TopPermutations = optResult.TopRuns.Select(r => new
        {
            r.Rank,
            r.MetricValue,
            r.TotalReturnPct,
            r.AverageDailyReturnPct,
            r.NetProfit,
            r.MaxDrawdownPct,
            Trades = r.WinningTradeCount + r.LosingTradeCount,
            WinRate = r.WinningTradeCount + r.LosingTradeCount == 0
                ? 0
                : Math.Round((decimal)r.WinningTradeCount / (r.WinningTradeCount + r.LosingTradeCount) * 100, 2),
            r.ParameterValues
        })
    }, serializerOptions));
    return;
}

if (args.Length > 0 && args[0].Equals("warm-candles", StringComparison.OrdinalIgnoreCase))
{
    var configPathForWarmup = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
        ? args[1]
        : Path.Combine("configs", "backtest", "finviz-reddit-ross-gapgo-bullflag-8-180d-10k-api-v2-confirmed-entry.yaml");
    var reader = new SimpleYamlReader();
    var run = reader.ReadBacktestRun(configPathForWarmup);
    var lookbackDays = ParseIntOption(args, "--days") ?? ResolveWarmupDataDays(run);
    var outputRoot = ParseStringOption(args, "--output") ?? ResolveWarmOutputRoot(run.NormalizedRoot, lookbackDays);
    var end = ParseDateOption(args, "--end") ?? DateTimeOffset.UtcNow;

    var warmer = new TradingFlow.Data.Csv.RollingCandleCacheWarmer();
    var result = await warmer.WarmAsync(
        new TradingFlow.Alpaca.AlpacaMarketDataProvider(
            new HttpClient(),
            ResolveAlpacaOptions(run)),
        new TradingFlow.Data.Csv.RollingCandleWarmRequest(
            run.Tickers,
            run.Intervals,
            outputRoot,
            lookbackDays,
            end),
        CancellationToken.None);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        result.OutputRoot,
        result.Start,
        result.End,
        result.LookbackDays,
        result.TickerCount,
        result.TimeframeCount,
        result.BarCount,
        Files = result.Files.Select(file => new
        {
            file.Ticker,
            file.Timeframe,
            file.BarCount,
            file.FirstTimestamp,
            file.LastTimestamp,
            file.Path,
            file.CalculatedPath
        })
    }, serializerOptions));
    return;
}

if (args.Length > 0 && args[0].Equals("warm-catalysts", StringComparison.OrdinalIgnoreCase))
{
    var configPathForWarmup = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
        ? args[1]
        : Path.Combine("configs", "backtest", "finviz-reddit-ross-gapgo-bullflag-8-180d-10k-api-v2-confirmed-entry.yaml");
    var reader = new SimpleYamlReader();
    var run = reader.ReadBacktestRun(configPathForWarmup);
    if (!run.News.Enabled)
    {
        throw new InvalidOperationException("news.enabled must be true to warm catalyst cache.");
    }

    var lookbackDays = ParseIntOption(args, "--days") ?? ResolveWarmupDataDays(run);
    var outputRoot = ParseStringOption(args, "--output") ?? ResolveWarmOutputRoot(run.NormalizedRoot, lookbackDays);
    var end = ParseDateOption(args, "--end") ?? DateTimeOffset.UtcNow;
    var start = end.AddDays(-lookbackDays);
    var tickerTimeoutSeconds = ParseIntOption(args, "--ticker-timeout-seconds") ?? run.Engine.TickerTimeoutSeconds;
    var refresh = ParseFlag(args, "--refresh");
    var rawProvider = CreateRawNewsProvider(run) ??
        throw new InvalidOperationException($"No news provider was created for {run.News.ProviderName}.");
    var provider = new TradingFlow.Data.Catalysts.CachedCatalystProvider(
        rawProvider,
        outputRoot,
        refresh ? "refresh" : run.CachePolicy);

    var warmed = new List<CatalystWarmTickerResult>();
    foreach (var ticker in run.Tickers)
    {
        using var tickerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, tickerTimeoutSeconds)));
        try
        {
            var catalysts = await provider.GetCatalystsAsync(ticker, start, end, tickerTimeout.Token);
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} warm_catalysts: {ticker} cached {catalysts.Count} catalyst(s).");
            warmed.Add(new CatalystWarmTickerResult(ticker, true, catalysts.Count, null));
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} warm_catalysts: {ticker} timed out after {tickerTimeoutSeconds}s.");
            warmed.Add(new CatalystWarmTickerResult(ticker, false, 0, $"Timed out after {tickerTimeoutSeconds}s."));
        }
        catch (Exception exception)
        {
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} warm_catalysts: {ticker} failed: {exception.Message}");
            warmed.Add(new CatalystWarmTickerResult(ticker, false, 0, exception.Message));
        }
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        OutputRoot = Path.GetFullPath(outputRoot),
        Provider = rawProvider.ProviderName,
        Start = start,
        End = end,
        LookbackDays = lookbackDays,
        TickerCount = run.Tickers.Count,
        Succeeded = warmed.Count(x => x.Succeeded),
        Failed = warmed.Count(x => !x.Succeeded),
        Tickers = warmed
    }, serializerOptions));
    return;
}

if (args.Length > 0 && args[0].Equals("evaluate-entry", StringComparison.OrdinalIgnoreCase))
{
    var entryConfigPath = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
        ? args[1]
        : throw new ArgumentException("Run config path required.");
    var strategyPath = ParseStringOption(args, "--strategy")
        ?? throw new ArgumentException("--strategy path is required.");
    var tickersCsv = ParseStringOption(args, "--tickers");
    var ticker = ParseStringOption(args, "--ticker");
    var end = ParseDateOption(args, "--end") ?? DateTimeOffset.UtcNow;
    var lookbackDays = ParseIntOption(args, "--days") ?? 0;

    var reader = new SimpleYamlReader();
    var entryRunConfig = reader.ReadBacktestRun(entryConfigPath);
    var strategy = reader.ReadStrategy(strategyPath);
    var requestedTickers = !String.IsNullOrWhiteSpace(ticker)
        ? new[] { ticker.Trim().ToUpperInvariant() }
        : tickersCsv?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToUpperInvariant())
            .ToArray();

    var evaluationEngine = new StrategyEvaluationEngine();
    var response = await evaluationEngine.EvaluateAsync(
        new StrategyEvaluationRequest(
            entryConfigPath,
            strategyPath,
            requestedTickers,
            lookbackDays <= 0 ? Math.Max(entryRunConfig.TimeWindow.LookbackDays, entryRunConfig.TimeWindow.WarmupLookbackDays) : lookbackDays,
            null,
            end),
        CreateProvider(entryRunConfig),
        entryRunConfig,
        strategy,
        strategyPath,
        CancellationToken.None);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        response.RunName,
        response.StrategyId,
        response.StrategyName,
        response.StrategyPath,
        response.Start,
        response.End,
        Results = response.Results.Select(result => new
        {
            result.Ticker,
            result.Timeframe,
            result.Decision,
            result.Reason,
            result.Timestamp,
            result.Close,
            result.Rsi,
            result.Atr,
            result.Volume,
            result.SlotAverageVolume,
            result.RelativeVolume,
            result.Vwap,
            result.Ema20,
            result.Ema50,
            result.MacdHistogram,
            result.Signal
        }),
        response.Profiler
    }, serializerOptions));
    return;
}

var configPath = ResolveRunConfigPath(args);

var readerInstance = new SimpleYamlReader();
var runConfig = readerInstance.ReadBacktestRun(configPath);
if (runConfig.Mode.Equals("paper", StringComparison.OrdinalIgnoreCase) ||
    runConfig.Mode.Equals("live", StringComparison.OrdinalIgnoreCase))
{
    var runner = new LiveRunner(
        CreateProvider(runConfig),
        CreateNewsProvider(runConfig),
        new TradingFlow.Engine.Execution.PseudoBroker(),
        null,
        null,
        null,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<LiveRunner>.Instance,
        rawArchiveWriter: CreateRawArchiveWriter());

    var strategies = runConfig.Strategies.Select(readerInstance.ReadStrategy).ToArray();
    await runner.RunAsync(runConfig, strategies, CancellationToken.None);
    Console.WriteLine("Live runner execution finished.");
}
else
{
    var runner = new BacktestRunner(readerInstance, rawArchiveWriter: CreateRawArchiveWriter());
    var result = await runner.RunAsync(configPath, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        result.RunName,
        result.ResultPath,
        result.StartingCapital,
        result.EndingCapital,
        result.NetProfit,
        result.TotalReturnPct,
        result.AverageDailyReturnPct,
        result.TradingDayCount,
        result.MaxDrawdownPct,
        Winner = result.Winner is null
            ? null
            : new
            {
                result.Winner.StrategyName,
                result.Winner.TotalReturnPct,
                result.Winner.AverageDailyReturnPct,
                result.Winner.NetProfit,
                result.Winner.MaxDrawdownPct,
                result.Winner.AcceptedTradeCount,
                result.Winner.RejectedTradeCount
            },
        Strategies = result.StrategyResults.Select(strategy => new
        {
            strategy.StrategyName,
            strategy.TotalReturnPct,
            strategy.AverageDailyReturnPct,
            strategy.TradingDayCount,
            strategy.NetProfit,
            strategy.MaxDrawdownPct,
            strategy.CandidateTradeCount,
            strategy.AcceptedTradeCount,
            strategy.RejectedTradeCount,
            strategy.WinningTradeCount,
            strategy.LosingTradeCount
        }),
        Tickers = new
        {
            Succeeded = result.TickerResults.Count(ticker => ticker.Succeeded),
            Failed = result.TickerResults.Count(ticker => !ticker.Succeeded)
        }
    }, serializerOptions));
}

static string ResolveRunConfigPath(string[] args)
{
    if (args.Length == 0)
    {
        return Path.Combine("configs", "backtest", "finviz-reddit-ross-gapgo-bullflag-8-180d-10k-api-v2-confirmed-entry.yaml");
    }

    if (args[0].Equals("backtest", StringComparison.OrdinalIgnoreCase) ||
        args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
    {
        return args.Length > 1
            ? args[1]
            : Path.Combine("configs", "backtest", "finviz-reddit-ross-gapgo-bullflag-8-180d-10k-api-v2-confirmed-entry.yaml");
    }

    return args[0];
}

static TradingFlow.Engine.Abstractions.ICatalystProvider? CreateNewsProvider(TradingFlow.Domain.Backtesting.BacktestRunConfig run)
{
    if (!run.News.Enabled)
    {
        return null;
    }

    return CreateRawNewsProvider(run);
}

static TradingFlow.Engine.Abstractions.ICatalystProvider? CreateRawNewsProvider(TradingFlow.Domain.Backtesting.BacktestRunConfig run)
{
    return run.News.ProviderName.ToLowerInvariant() switch
    {
        "alpaca" => new TradingFlow.Alpaca.AlpacaNewsProvider(
            new HttpClient(),
            ResolveAlpacaOptions(run),
            CreateRawArchiveWriter(),
            sentimentAnalyzer: CreateSentimentAnalyzer(run.News.SentimentTimeoutSeconds),
            maxArticlesPerTicker: run.News.MaxArticlesPerTicker),
        "finviz" => new TradingFlow.Finviz.FinvizNewsProvider(
            new TradingFlow.Finviz.FinvizClient(
                new HttpClient(),
                TradingFlow.Finviz.FinvizOptions.CreateDefault() with
                {
                    AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? ""
                },
                CreateRawArchiveWriter())),
        "none" => null,
        _ => throw new NotSupportedException($"Unsupported news provider: {run.News.ProviderName}")
    };
}

static TradingFlow.Engine.Abstractions.ISentimentAnalyzer CreateSentimentAnalyzer(int? timeoutSeconds = null)
{
    var endpoint = Environment.GetEnvironmentVariable("FINBERT_SENTIMENT_URL");
    if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
    {
        return new TradingFlow.Alpaca.FinbertHttpSentimentAnalyzer(
            new HttpClient(),
            uri,
            requestTimeout: TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds ?? 3)));
    }

    return new TradingFlow.Alpaca.VaderSentimentAnalyzer();
}

static TradingFlow.Engine.Abstractions.IMarketDataProvider CreateProvider(TradingFlow.Domain.Backtesting.BacktestRunConfig run)
{
    return run.Provider.ToLowerInvariant() switch
    {
        "csv" => new TradingFlow.Data.Csv.CsvMarketDataProvider(run.NormalizedRoot),
        "alpaca" => new TradingFlow.Alpaca.AlpacaMarketDataProvider(
            new HttpClient(),
            ResolveAlpacaOptions(run)),
        "finviz" => new TradingFlow.Finviz.FinvizMarketDataProvider(
            new TradingFlow.Finviz.FinvizClient(
                new HttpClient(),
                TradingFlow.Finviz.FinvizOptions.CreateDefault() with
                {
                    AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? ""
                },
                CreateRawArchiveWriter())),
        _ => throw new NotSupportedException($"Unsupported market data provider: {run.Provider}")
    };
}

static int? ParseIntOption(string[] args, string name)
{
    var value = ParseStringOption(args, name);
    return value is null ? null : Int32.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}

static DateTimeOffset? ParseDateOption(string[] args, string name)
{
    var value = ParseStringOption(args, name);
    return value is null ? null : DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}

static string? ParseStringOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

static string RequireStringOption(string[] args, string name) =>
    ParseStringOption(args, name)
    ?? throw new ArgumentException($"Required option {name} was not provided.");

static bool ParseFlag(string[] args, string name)
{
    return args.Any(arg => arg.Equals(name, StringComparison.OrdinalIgnoreCase));
}

static async Task WriteExactOrVerifyAsync(
    string path,
    string content,
    CancellationToken cancellationToken = default)
{
    if (File.Exists(path))
    {
        var existing = await File.ReadAllTextAsync(path, cancellationToken);
        if (!existing.Equals(content, StringComparison.Ordinal))
        {
            throw new IOException(
                $"Refusing to overwrite non-matching labeling artifact '{path}'.");
        }

        return;
    }

    await AtomicFileArtifactWriter.Instance.WriteTextExclusiveAsync(
        path,
        content,
        cancellationToken);
}

static string ResolveWarmOutputRoot(string normalizedRoot, int lookbackDays)
{
    var fullRoot = Path.GetFullPath(normalizedRoot);
    var leaf = Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    var segment = $"{lookbackDays}d";
    if (leaf.Equals(segment, StringComparison.OrdinalIgnoreCase))
    {
        return fullRoot;
    }

    if (leaf.EndsWith("d", StringComparison.OrdinalIgnoreCase) &&
        Int32.TryParse(leaf[..^1], out _))
    {
        return Path.Combine(Path.GetDirectoryName(fullRoot)!, segment);
    }

    return Path.Combine(fullRoot, segment);
}

static int ResolveWarmupDataDays(TradingFlow.Domain.Backtesting.BacktestRunConfig run)
{
    var warmupDays = Math.Max(run.TimeWindow.WarmupLookbackDays, 0);
    if (run.Mode.Equals("backtest", StringComparison.OrdinalIgnoreCase))
    {
        return Math.Max(1, run.TimeWindow.LookbackDays + warmupDays);
    }

    return Math.Max(1, warmupDays > 0 ? warmupDays : run.TimeWindow.LookbackDays);
}

static TradingFlow.Alpaca.AlpacaOptions ResolveAlpacaOptions(TradingFlow.Domain.Backtesting.BacktestRunConfig run)
{
    return TradingFlow.Alpaca.AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
    {
        KeyId = ResolveSecret("Alpaca", "KeyId", "ALPACA_KEY_ID"),
        SecretKey = ResolveSecret("Alpaca", "SecretKey", "ALPACA_SECRET_KEY"),
        MarketDataFeed = run.Providers.Alpaca.DataFeed
    };
}

static string ResolveSecret(string section, string key, string environmentVariable)
{
    var environmentValue = Environment.GetEnvironmentVariable(environmentVariable);
    if (!String.IsNullOrWhiteSpace(environmentValue))
    {
        return environmentValue;
    }

    var settingsPath = FindRepositoryFile(Path.Combine("src", "TradingFlow.Web", "appsettings.local.json"));
    if (settingsPath is not null)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        if (document.RootElement.TryGetProperty(section, out var sectionElement) &&
            sectionElement.TryGetProperty(key, out var keyElement))
        {
            var localValue = keyElement.GetString();
            if (!String.IsNullOrWhiteSpace(localValue))
            {
                return localValue;
            }
        }
    }

    return String.Empty;
}

static IRawArchiveWriter CreateRawArchiveWriter()
{
    var dataRoot = Environment.GetEnvironmentVariable("TRADINGFLOW_DATA_ROOT");
    if (String.IsNullOrWhiteSpace(dataRoot))
    {
        var solutionPath = FindRepositoryFile("TradingFlow.sln")
            ?? throw new InvalidOperationException(
                "Cannot resolve the raw archive root. Set TRADINGFLOW_DATA_ROOT when running outside the repository.");
        dataRoot = Path.Combine(Path.GetDirectoryName(solutionPath)!, "data");
    }

    return new FileSystemRawArchiveWriter(new RawArchiveOptions(Path.Combine(dataRoot, "raw")));
}

static string? FindRepositoryFile(string relativePath)
{
    foreach (var startPath in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
    {
        var directory = new DirectoryInfo(startPath);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }
    }

    return null;
}

public sealed record CatalystWarmTickerResult(
    string Ticker,
    bool Succeeded,
    int CatalystCount,
    string? Error);
