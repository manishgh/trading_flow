using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Governance;
using TradingFlow.Data.Evidence.Labeling;
using TradingFlow.Data.Evidence.Research;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Research.Catalysts;
using TradingFlow.Research.Momentum;

namespace TradingFlow.Research.Workflows;

/// <summary>
/// Composes catalog-only research, immutable packaging, and atomic run registration.
/// Provider clients and arbitrary market-data paths are intentionally absent.
/// </summary>
public sealed partial class CatalogResearchWorkflow
{
    private const string DatasetManifestMediaType =
        "application/vnd.tradingflow.evidence-dataset+json";
    private readonly IEvidenceCatalog catalog;
    private readonly IEvidencePartitionDataReader partitionReader;
    private readonly EvidenceResearchRunArtifactPackager packager;
    private readonly SqliteResearchTrialRegistry? trialRegistry;

    public CatalogResearchWorkflow(
        IEvidenceCatalog catalog,
        IEvidencePartitionDataReader partitionReader,
        EvidenceResearchRunArtifactPackager packager,
        SqliteResearchTrialRegistry? trialRegistry = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.partitionReader = partitionReader ??
            throw new ArgumentNullException(nameof(partitionReader));
        this.packager = packager ?? throw new ArgumentNullException(nameof(packager));
        this.trialRegistry = trialRegistry;
    }

    public async Task<CatalogMomentumWorkflowResult> RunMomentumAsync(
        CatalogMomentumWorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureUtc(request.CreatedAtUtc, nameof(request.CreatedAtUtc));
        ValidateMomentumPartitions(request);

        var adjustedManifest = await RequireDatasetAsync(
            request.Study.ResearchAdjustedBarsDatasetId,
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            cancellationToken);
        var asTradedManifest = await RequireDatasetAsync(
            request.Study.AsTradedBarsDatasetId,
            EvidenceDatasetKind.MarketBarsAsTraded,
            cancellationToken);
        var universeManifest = await RequireDatasetAsync(
            request.Study.UniverseMembershipDatasetId,
            EvidenceDatasetKind.UniverseMembership,
            cancellationToken);
        var inputDatasets = new[]
        {
            DatasetReference(adjustedManifest),
            DatasetReference(asTradedManifest),
            DatasetReference(universeManifest)
        };
        var universeLedger = UniverseLedger(universeManifest);
        var partitionDefinition = Json(request.StudyPartitions);
        var holdout = await packager.PrepareHoldoutAsync(
            request.Study.Definition.StudyName,
            inputDatasets,
            universeManifest.DatasetId,
            request.StudyPartitions,
            universeLedger,
            partitionDefinition,
            cancellationToken);
        var phaseState = await OpenPhaseAsync(
            request.Phase,
            request.FrozenTrial,
            request.ResearchRunId,
            request.CreatedAtUtc,
            inputDatasets.Select(value => value.DatasetId).ToArray(),
            request.StudyPartitions,
            CatalogResearchTrialBinding.ComputeMomentumRequestSha256(request),
            holdout,
            cancellationToken);
        var phaseReader = ReaderForPhase(
            request.Phase,
            request.StudyPartitions);

        var result = await new EvidenceCatalogResearchRunner(catalog, phaseReader)
            .RunMomentumAsync(
                request.Study,
                new TradingFlow.Research.Momentum.MomentumExecutionCostAssumptions(
                    request.Assumptions.Cost.CommissionBps,
                    request.Assumptions.Cost.RegulatoryFeesBps,
                    request.Assumptions.Spread.EstimatedFullSpreadBps,
                    request.Assumptions.Slippage.MaximumSlippageBps),
                cancellationToken);
        var evidenceReady =
            request.Phase == CatalogResearchPhase.Holdout &&
            result.Report.DataEvidenceReady &&
            result.Report.PromotionEligible;
        var readinessFailures = result.Report.PromotionBlockers
                .Distinct(StringComparer.Ordinal)
                .OrderBy(blocker => blocker, StringComparer.Ordinal)
                .ToArray();
        if (request.Phase == CatalogResearchPhase.DevelopmentValidation)
        {
            readinessFailures = readinessFailures
                .Append("holdout_not_executed")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }

        if (!evidenceReady && readinessFailures.Length == 0)
        {
            readinessFailures = ["momentum_evidence_not_ready"];
        }

        var package = await packager.PackageAsync(
            new EvidenceResearchRunPackageRequest(
                request.ResearchRunId,
                request.Study.Definition.StudyName,
                request.CreatedAtUtc,
                request.CodeVersion,
                inputDatasets,
                universeManifest.DatasetId,
                request.StudyPartitions,
                Json(request.Study),
                universeLedger,
                partitionDefinition,
                Assumptions(request.Assumptions),
                [
                    new EvidenceResearchNamedOutput(
                        "momentum-report",
                        Json(result.Report)),
                    new EvidenceResearchNamedOutput(
                        "research-phase",
                        Json(phaseState))
                ],
                evidenceReady,
                readinessFailures),
            cancellationToken);
        var commit = await catalog.RegisterResearchRunAsync(
            package.ManifestDraft,
            cancellationToken);
        var registered = await catalog.GetResearchRunAsync(
            package.ManifestDraft.ResearchRunId,
            cancellationToken)
            ?? throw new InvalidDataException(
                "The evidence catalog did not return the registered research run.");
        if (!EvidenceCanonicalJson.SerializeToUtf8Bytes(registered)
                .AsSpan()
                .SequenceEqual(EvidenceCanonicalJson.SerializeToUtf8Bytes(package.ManifestDraft)))
        {
            throw new InvalidDataException(
                "The registered research run differs from the immutable package manifest.");
        }

        phaseState = await CompleteHoldoutPhaseAsync(
            phaseState,
            registered,
            MomentumResultMetrics(result.Report),
            cancellationToken);
        return new CatalogMomentumWorkflowResult(
            result,
            registered,
            commit.AlreadyCommitted)
        {
            PhaseState = phaseState
        };
    }

    public async Task<CatalogCatalystWorkflowResult> RunCatalystAsync(
        CatalogCatalystWorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureUtc(request.CreatedAtUtc, nameof(request.CreatedAtUtc));
        ValidateCatalystPartitions(request);

        var barsManifest = await RequireDatasetAsync(
            request.Study.MarketBarsDatasetId,
            EvidenceDatasetKind.MarketBarsAsTraded,
            cancellationToken);
        var newsManifest = await RequireDatasetAsync(
            request.Study.NewsDatasetId,
            EvidenceDatasetKind.NewsRevisions,
            cancellationToken);
        var sentimentManifest = await RequireDatasetAsync(
            request.Study.SentimentAssessmentsDatasetId,
            EvidenceDatasetKind.SentimentAssessments,
            cancellationToken);
        var universeManifest = await RequireDatasetAsync(
            request.Study.UniverseMembershipDatasetId,
            EvidenceDatasetKind.UniverseMembership,
            cancellationToken);
        var groundTruthManifest = await RequireDatasetAsync(
            request.ClassifierGroundTruthDatasetId,
            EvidenceDatasetKind.ClassifierGroundTruth,
            cancellationToken);
        var exchangeSessionsManifest = await RequireDatasetAsync(
            request.ExchangeSessionsDatasetId,
            EvidenceDatasetKind.ExchangeSessions,
            cancellationToken);
        EvidenceDatasetManifest? quotesManifest = null;
        if (!String.IsNullOrWhiteSpace(request.Study.SipQuotesDatasetId))
        {
            quotesManifest = await RequireDatasetAsync(
                request.Study.SipQuotesDatasetId,
                EvidenceDatasetKind.SipQuotes,
                cancellationToken);
        }

        if (!groundTruthManifest.Attributes.TryGetValue(
                "source_news_dataset_id",
                out var labeledNewsDatasetId) ||
            !labeledNewsDatasetId.Equals(
                newsManifest.DatasetId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Classifier ground truth does not reference the exact catalyst news dataset.");
        }

        var study = request.Study with
        {
            StartUtc = request.Phase == CatalogResearchPhase.Holdout
                ? request.StudyPartitions.Holdout.StartUtc
                : request.StudyPartitions.Development.StartUtc,
            EndUtc = request.Phase == CatalogResearchPhase.Holdout
                ? request.StudyPartitions.Holdout.EndUtc
                : request.StudyPartitions.Validation.EndUtc,
            Options = (request.Study.Options ?? new CatalystEventStudyOptions()) with
            {
                ReportGeneratedAtUtc = request.CreatedAtUtc,
                StudyPartitions = request.Phase == CatalogResearchPhase.Holdout
                    ? [Partition(request.StudyPartitions.Holdout)]
                    :
                    [
                        Partition(request.StudyPartitions.Development),
                        Partition(request.StudyPartitions.Validation)
                    ]
            }
        };
        var inputManifests = new List<EvidenceDatasetManifest>
        {
            barsManifest,
            newsManifest,
            sentimentManifest,
            universeManifest,
            groundTruthManifest,
            exchangeSessionsManifest
        };
        if (quotesManifest is not null)
        {
            inputManifests.Add(quotesManifest);
        }

        var inputDatasets = inputManifests.Select(DatasetReference).ToArray();
        var universeLedger = UniverseLedger(universeManifest);
        var partitionDefinition = Json(request.StudyPartitions);
        var holdout = await packager.PrepareHoldoutAsync(
            $"catalyst-{request.Study.CandleTimeframe}",
            inputDatasets,
            universeManifest.DatasetId,
            request.StudyPartitions,
            universeLedger,
            partitionDefinition,
            cancellationToken);
        var phaseState = await OpenPhaseAsync(
            request.Phase,
            request.FrozenTrial,
            request.ResearchRunId,
            request.CreatedAtUtc,
            inputDatasets.Select(value => value.DatasetId).ToArray(),
            request.StudyPartitions,
            CatalogResearchTrialBinding.ComputeCatalystRequestSha256(request),
            holdout,
            cancellationToken);
        var phaseReader = ReaderForPhase(
            request.Phase,
            request.StudyPartitions);
        var groundTruthRows = await phaseReader.ReadClassifierGroundTruthAsync(
            groundTruthManifest,
            cancellationToken);
        var groundTruthVerification =
            CatalystGroundTruthPromotionGate.Evaluate(
                groundTruthManifest,
                groundTruthRows,
                newsManifest.DatasetId);
        var classifierReady =
            groundTruthVerification.CatalystPromotionAllowed;
        var classifierFailures = groundTruthVerification.Failures;

        var resolver = await new CatalogExchangeSessionResolverFactory(
                catalog,
                phaseReader)
            .CreateAsync(
                exchangeSessionsManifest.DatasetId,
                request.CreatedAtUtc,
                cancellationToken: cancellationToken);
        var result = await new EvidenceCatalogResearchRunner(
                catalog,
                phaseReader,
                resolver)
            .RunCatalystAsync(study, cancellationToken);

        var readinessFailures = classifierFailures
            .Concat(result.ReadinessFailures)
            .Append("catalyst_classifier_accuracy_not_validated")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (request.Phase == CatalogResearchPhase.DevelopmentValidation)
        {
            readinessFailures = readinessFailures
                .Append("holdout_not_executed")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }

        var evidenceReady = false;
        if (!evidenceReady && readinessFailures.Length == 0)
        {
            readinessFailures = ["catalyst_evidence_not_ready"];
        }

        var package = await packager.PackageAsync(
            new EvidenceResearchRunPackageRequest(
                request.ResearchRunId,
                $"catalyst-{request.Study.CandleTimeframe}",
                request.CreatedAtUtc,
                request.CodeVersion,
                inputDatasets,
                universeManifest.DatasetId,
                request.StudyPartitions,
                Json(study),
                universeLedger,
                partitionDefinition,
                Assumptions(request.Assumptions),
                [
                    new EvidenceResearchNamedOutput(
                        "catalyst-event-study",
                        Json(result.Report)),
                    new EvidenceResearchNamedOutput(
                        "catalyst-executable-observations",
                        Json(result.ExecutableObservations)),
                    new EvidenceResearchNamedOutput(
                        "catalyst-readiness",
                        Json(new
                        {
                            ClassifierGroundTruthReady = classifierReady,
                            result.ExecutableEvidenceReady,
                            Failures = readinessFailures
                        })),
                    new EvidenceResearchNamedOutput(
                        "research-phase",
                        Json(phaseState))
                ],
                evidenceReady,
                readinessFailures),
            cancellationToken);
        var commit = await catalog.RegisterResearchRunAsync(
            package.ManifestDraft,
            cancellationToken);
        var registered = await catalog.GetResearchRunAsync(
            package.ManifestDraft.ResearchRunId,
            cancellationToken)
            ?? throw new InvalidDataException(
                "The evidence catalog did not return the registered catalyst research run.");
        EnsureManifestMatchesPackage(registered, package.ManifestDraft);

        phaseState = await CompleteHoldoutPhaseAsync(
            phaseState,
            registered,
            CatalystResultMetrics(result),
            cancellationToken);
        return new CatalogCatalystWorkflowResult(
            result,
            registered,
            classifierReady,
            commit.AlreadyCommitted)
        {
            PhaseState = phaseState
        };
    }

    private async Task<EvidenceDatasetManifest> RequireDatasetAsync(
        string datasetId,
        EvidenceDatasetKind expectedKind,
        CancellationToken cancellationToken)
    {
        var manifest = await catalog.GetDatasetAsync(datasetId, cancellationToken)
            ?? throw new KeyNotFoundException(
                $"Committed evidence dataset '{datasetId}' was not found.");
        if (manifest.Kind != expectedKind)
        {
            throw new InvalidDataException(
                $"Dataset '{datasetId}' is {manifest.Kind}; {expectedKind} is required.");
        }

        if (!manifest.Quality.Passed)
        {
            throw new InvalidDataException(
                $"Dataset '{datasetId}' failed its committed quality report.");
        }

        return manifest;
    }

    private IEvidencePartitionDataReader ReaderForPhase(
        CatalogResearchPhase phase,
        EvidenceStudyPartitions partitions) =>
        phase switch
        {
            CatalogResearchPhase.DevelopmentValidation =>
                new DevelopmentValidationPartitionReader(
                    partitionReader,
                    partitions.Holdout.StartUtc),
            CatalogResearchPhase.Holdout => partitionReader,
            _ => throw new ArgumentOutOfRangeException(nameof(phase))
        };

    private async Task<CatalogResearchPhaseState> OpenPhaseAsync(
        CatalogResearchPhase phase,
        CatalogFrozenTrialIdentity? identity,
        string researchRunId,
        DateTimeOffset createdAtUtc,
        IReadOnlyList<string> datasetIds,
        EvidenceStudyPartitions partitions,
        string requestBindingSha256,
        EvidenceHoldoutIdentity holdout,
        CancellationToken cancellationToken)
    {
        if (phase == CatalogResearchPhase.DevelopmentValidation)
        {
            if (identity is not null)
            {
                throw new InvalidOperationException(
                    "A frozen trial identity is accepted only for explicit holdout execution.");
            }

            return new CatalogResearchPhaseState(
                phase,
                null,
                null,
                ResearchHoldoutState.Unopened,
                false,
                false);
        }

        if (phase != CatalogResearchPhase.Holdout)
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        if (identity is null)
        {
            throw new InvalidOperationException(
                "Explicit holdout execution requires a frozen trial identity.");
        }

        if (trialRegistry is null)
        {
            throw new InvalidOperationException(
                "Explicit holdout execution requires the durable research trial registry.");
        }

        var trial = await trialRegistry.GetTrialAsync(
            identity.ExperimentId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Frozen research trial '{identity.ExperimentId}' is not registered.");
        var registeredHash = EvidenceCanonicalJson.ComputeSha256(trial);
        if (!registeredHash.Equals(
                identity.TrialDefinitionSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The supplied frozen trial identity does not match the registered trial.");
        }

        ValidateTrialBinding(
            trial,
            datasetIds,
            partitions,
            requestBindingSha256);
        var state = await trialRegistry.GetHoldoutStateAsync(
            trial.ExperimentId,
            cancellationToken);
        if (state != ResearchHoldoutState.Unopened)
        {
            throw new InvalidOperationException(
                $"Holdout for frozen trial '{trial.ExperimentId}' has already been consumed.");
        }

        var trialConsumption = await trialRegistry.BeginHoldoutEvaluationAsync(
            new ResearchHoldoutConsumptionRecord(
                trial.ExperimentId,
                researchRunId,
                createdAtUtc),
            cancellationToken);
        if (trialConsumption.AlreadyCommitted)
        {
            throw new InvalidOperationException(
                $"Holdout for frozen trial '{trial.ExperimentId}' cannot be replayed.");
        }

        var catalogReservation = await catalog.ReserveHoldoutAsync(
            new EvidenceHoldoutConsumption(
                holdout.HoldoutId,
                researchRunId,
                createdAtUtc),
            cancellationToken);
        if (catalogReservation.AlreadyCommitted)
        {
            throw new InvalidOperationException(
                "The immutable evidence holdout was already reserved.");
        }

        return new CatalogResearchPhaseState(
            phase,
            trial.ExperimentId,
            registeredHash,
            ResearchHoldoutState.Consumed,
            true,
            true);
    }

    private async Task<CatalogResearchPhaseState> CompleteHoldoutPhaseAsync(
        CatalogResearchPhaseState phaseState,
        EvidenceResearchRunManifest registeredRun,
        IReadOnlyDictionary<string, decimal> metrics,
        CancellationToken cancellationToken)
    {
        if (phaseState.Phase != CatalogResearchPhase.Holdout)
        {
            return phaseState;
        }

        if (trialRegistry is null ||
            String.IsNullOrWhiteSpace(phaseState.ExperimentId) ||
            String.IsNullOrWhiteSpace(phaseState.TrialDefinitionSha256))
        {
            throw new InvalidOperationException(
                "A holdout result cannot complete without its durable frozen trial identity.");
        }

        var trial = await trialRegistry.GetTrialAsync(
            phaseState.ExperimentId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Frozen research trial '{phaseState.ExperimentId}' is not registered.");
        if (!metrics.ContainsKey(trial.PrimaryMetric))
        {
            throw new InvalidDataException(
                $"The workflow does not produce frozen primary metric '{trial.PrimaryMetric}'.");
        }

        var outputHashes = registeredRun.Outputs
            .Select(output => output.Content.Sha256)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var resultId = EvidenceCanonicalJson.ComputeSha256(new
        {
            phaseState.ExperimentId,
            registeredRun.ResearchRunId,
            Partition = registeredRun.StudyPartitions.Holdout.Name,
            phaseState.TrialDefinitionSha256,
            OutputHashes = outputHashes
        });
        await trialRegistry.CompleteHoldoutEvaluationAsync(
            new CanonicalResearchResultManifest(
                resultId,
                phaseState.ExperimentId,
                phaseState.TrialDefinitionSha256,
                registeredRun.ResearchRunId,
                registeredRun.CodeVersion,
                registeredRun.StudyPartitions.Holdout.Name,
                trial.PrimaryMetric,
                metrics,
                outputHashes,
                registeredRun.CreatedAtUtc),
            cancellationToken);

        return phaseState with
        {
            HoldoutState = ResearchHoldoutState.Completed
        };
    }

    private static IReadOnlyDictionary<string, decimal> MomentumResultMetrics(
        CrossSectionalMomentumReport report)
    {
        var holdout = report.FormationLedger
            .Where(value =>
                value.Segment.Equals(
                    MomentumStudySegment.Holdout,
                    StringComparison.Ordinal) &&
                value.IsPrimarySelection)
            .ToArray();
        return new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["net_return_pct"] = holdout.Length == 0
                ? 0m
                : holdout.Average(value => value.SlotNetForwardReturnPct),
            ["observation_count"] = holdout.Length,
            ["promotion_eligible"] = report.PromotionEligible ? 1m : 0m
        };
    }

    private static IReadOnlyDictionary<string, decimal> CatalystResultMetrics(
        CatalogCatalystStudyResult result)
    {
        var executableReturns = result.ExecutableObservations
            .Select(value => value.Result)
            .Where(value =>
                value.Status == CatalystExecutableReturnStatus.NetExecutableEstimate &&
                value.NetReturnPercent.HasValue)
            .Select(value => value.NetReturnPercent!.Value)
            .ToArray();
        var meanNetReturn = executableReturns.Length == 0
            ? 0m
            : executableReturns.Average();
        return new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["net_return_pct"] = meanNetReturn,
            ["net_expectancy_pct"] = meanNetReturn,
            ["executable_trade_count"] = executableReturns.Length,
            ["evidence_ready"] = result.ExecutableEvidenceReady ? 1m : 0m
        };
    }

    private static void ValidateTrialBinding(
        ResearchTrialDefinition trial,
        IReadOnlyList<string> datasetIds,
        EvidenceStudyPartitions partitions,
        string requestBindingSha256)
    {
        var actualDatasets = datasetIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (!trial.DatasetIds.SequenceEqual(actualDatasets, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The workflow datasets differ from the frozen registered trial.");
        }

        if (!trial.Formulas.TryGetValue(
                CatalogResearchTrialBinding.RequestBindingFormulaKey,
                out var registeredBinding) ||
            !registeredBinding.Equals(requestBindingSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The workflow configuration differs from the frozen registered trial.");
        }

        var expectedPartitions = new[]
        {
            partitions.Development,
            partitions.Validation,
            partitions.Holdout
        };
        if (trial.Partitions.Count != expectedPartitions.Length ||
            trial.Partitions.Zip(expectedPartitions).Any(pair =>
                !pair.First.Name.Equals(pair.Second.Name, StringComparison.Ordinal) ||
                pair.First.StartUtc != pair.Second.StartUtc ||
                pair.First.EndUtc != pair.Second.EndUtc))
        {
            throw new InvalidDataException(
                "The workflow partitions differ from the frozen registered trial.");
        }
    }

    private static EvidenceResearchJsonPayload UniverseLedger(
        EvidenceDatasetManifest universeManifest) =>
        Json(new
        {
            universeManifest.DatasetId,
            universeManifest.LogicalDatasetKey,
            universeManifest.CreatedAtUtc,
            ManifestSha256 = EvidenceCanonicalJson.ComputeSha256(universeManifest)
        });

    private static EvidenceDatasetReference DatasetReference(
        EvidenceDatasetManifest manifest)
    {
        var bytes = EvidenceCanonicalJson.SerializeToUtf8Bytes(manifest);
        return new EvidenceDatasetReference(
            manifest.DatasetId,
            new EvidenceArtifactReference(
                new EvidenceContentAddress(
                    EvidenceCanonicalJson.ComputeSha256(manifest),
                    bytes.LongLength,
                    DatasetManifestMediaType),
                EvidenceObjectNamespaces.DatasetManifests));
    }

    private static EvidenceResearchAssumptionPayloads Assumptions(
        CatalogResearchAssumptionSet assumptions) =>
        new(
            Json(assumptions.Cost),
            Json(assumptions.Spread),
            Json(assumptions.Slippage),
            Json(assumptions.Borrow),
            Json(assumptions.Benchmark));

    private static CatalystStudyPartition Partition(EvidenceStudyWindow window) =>
        new(window.Name, window.StartUtc, window.EndUtc);

    private static EvidenceResearchJsonPayload Json<T>(T value) =>
        new(EvidenceCanonicalJson.SerializeToUtf8Bytes(value));

    private static void ValidateMomentumPartitions(
        CatalogMomentumWorkflowRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResearchRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CodeVersion);
        ArgumentNullException.ThrowIfNull(request.Study);
        ArgumentNullException.ThrowIfNull(request.Study.Definition);
        ArgumentNullException.ThrowIfNull(request.StudyPartitions);
        ArgumentNullException.ThrowIfNull(request.Assumptions);

        var options = request.Study.Definition.Options;
        var expectedValidationStart = DateOnly.FromDateTime(
            request.StudyPartitions.Validation.StartUtc.UtcDateTime);
        var expectedHoldoutStart = DateOnly.FromDateTime(
            request.StudyPartitions.Holdout.StartUtc.UtcDateTime);
        if (options.ValidationStartDate != expectedValidationStart ||
            options.HoldoutStartDate != expectedHoldoutStart)
        {
            throw new ArgumentException(
                "Momentum definition partition dates must exactly match the frozen study windows.",
                nameof(request));
        }
    }

    private static void ValidateCatalystPartitions(
        CatalogCatalystWorkflowRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResearchRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CodeVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            request.ClassifierGroundTruthDatasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExchangeSessionsDatasetId);
        ArgumentNullException.ThrowIfNull(request.Study);
        ArgumentNullException.ThrowIfNull(request.StudyPartitions);
        ArgumentNullException.ThrowIfNull(request.Assumptions);
        if (request.Study.StartUtc != request.StudyPartitions.Development.StartUtc ||
            request.Study.EndUtc != request.StudyPartitions.Holdout.EndUtc)
        {
            throw new ArgumentException(
                "Catalyst study bounds must span the exact frozen development-through-holdout windows.",
                nameof(request));
        }
    }

    private static void EnsureManifestMatchesPackage(
        EvidenceResearchRunManifest registered,
        EvidenceResearchRunManifest packaged)
    {
        if (!EvidenceCanonicalJson.SerializeToUtf8Bytes(registered)
                .AsSpan()
                .SequenceEqual(EvidenceCanonicalJson.SerializeToUtf8Bytes(packaged)))
        {
            throw new InvalidDataException(
                "The registered research run differs from the immutable package manifest.");
        }
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Timestamp must be a non-default UTC value.",
                parameterName);
        }
    }

    private sealed class DevelopmentValidationPartitionReader(
        IEvidencePartitionDataReader inner,
        DateTimeOffset holdoutStartUtc) : IEvidencePartitionDataReader
    {
        public Task<IReadOnlyList<MarketBarEvidenceRow>> ReadMarketBarsAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) =>
            inner.ReadMarketBarsAsync(BeforeHoldout(manifest), cancellationToken);

        public Task<IReadOnlyList<NewsRevisionEvidenceRow>> ReadNewsRevisionsAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) =>
            inner.ReadNewsRevisionsAsync(BeforeHoldout(manifest), cancellationToken);

        public Task<IReadOnlyList<SentimentAssessmentEvidenceRow>>
            ReadSentimentAssessmentsAsync(
                EvidenceDatasetManifest manifest,
                CancellationToken cancellationToken = default) =>
            inner.ReadSentimentAssessmentsAsync(
                BeforeHoldout(manifest),
                cancellationToken);

        public Task<IReadOnlyList<ClassifierGroundTruthEvidenceRow>>
            ReadClassifierGroundTruthAsync(
                EvidenceDatasetManifest manifest,
                CancellationToken cancellationToken = default) =>
            inner.ReadClassifierGroundTruthAsync(
                BeforeHoldout(manifest),
                cancellationToken);

        public Task<IReadOnlyList<SecurityMasterSnapshotEvidenceRow>>
            ReadSecurityMasterSnapshotsAsync(
                EvidenceDatasetManifest manifest,
                CancellationToken cancellationToken = default) =>
            inner.ReadSecurityMasterSnapshotsAsync(
                BeforeHoldout(manifest),
                cancellationToken);

        public Task<IReadOnlyList<SymbolIntervalEvidenceRow>> ReadSymbolIntervalsAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) =>
            inner.ReadSymbolIntervalsAsync(BeforeHoldout(manifest), cancellationToken);

        public Task<IReadOnlyList<CorporateActionEvidenceRow>> ReadCorporateActionsAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) =>
            inner.ReadCorporateActionsAsync(BeforeHoldout(manifest), cancellationToken);

        public Task<IReadOnlyList<UniverseMembershipEvidenceRow>>
            ReadUniverseMembershipAsync(
                EvidenceDatasetManifest manifest,
                CancellationToken cancellationToken = default) =>
            inner.ReadUniverseMembershipAsync(BeforeHoldout(manifest), cancellationToken);

        public Task<IReadOnlyList<SipQuoteEvidenceRow>> ReadSipQuotesAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) =>
            inner.ReadSipQuotesAsync(BeforeHoldout(manifest), cancellationToken);

        public Task<IReadOnlyList<ExchangeSessionEvidenceRow>> ReadExchangeSessionsAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) =>
            inner.ReadExchangeSessionsAsync(manifest, cancellationToken);

        private EvidenceDatasetManifest BeforeHoldout(
            EvidenceDatasetManifest manifest)
        {
            var crossing = manifest.Partitions
                .Where(partition =>
                    partition.MinimumSourceTimestampUtc < holdoutStartUtc &&
                    partition.MaximumSourceTimestampUtc >= holdoutStartUtc)
                .Select(partition => partition.PartitionId)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (crossing.Length > 0)
            {
                throw new InvalidDataException(
                    $"Dataset '{manifest.DatasetId}' has physical partition(s) crossing " +
                    $"the holdout boundary: {String.Join(", ", crossing)}.");
            }

            var allowed = manifest.Partitions
                .Where(partition =>
                    partition.MaximumSourceTimestampUtc < holdoutStartUtc)
                .ToArray();
            if (allowed.Length == 0)
            {
                throw new InvalidDataException(
                    $"Dataset '{manifest.DatasetId}' has no development/validation " +
                    "partition wholly before the holdout boundary.");
            }

            return new EvidenceDatasetManifest(
                manifest.Kind,
                manifest.SchemaVersion,
                manifest.CreatedAtUtc,
                manifest.CollectionJobId,
                manifest.CollectionPlanHash,
                manifest.ConfigHash,
                manifest.CodeVersion,
                manifest.NormalizerVersion,
                manifest.DataFeed,
                allowed,
                manifest.Quality,
                manifest.Attributes);
        }
    }
}
