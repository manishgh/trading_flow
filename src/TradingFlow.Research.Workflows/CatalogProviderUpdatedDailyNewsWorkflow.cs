using TradingFlow.Domain.Research;
using TradingFlow.Data.Evidence.Research;
using TradingFlow.Research.Catalysts;

namespace TradingFlow.Research.Workflows;

public sealed partial class CatalogResearchWorkflow
{
    /// <summary>
    /// Runs a conservative, permanently non-promotable daily news diagnostic. Historical
    /// provider updated-at is treated as the earliest available instant; the workflow never
    /// claims that the research system actually observed the story then.
    /// </summary>
    public async Task<CatalogProviderUpdatedDailyNewsDiagnosticResult>
        RunProviderUpdatedDailyNewsDiagnosticAsync(
            CatalogProviderUpdatedDailyNewsDiagnosticRequest request,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureUtc(request.CreatedAtUtc, nameof(request.CreatedAtUtc));
        var symbols = ValidateProviderUpdatedDailyNewsRequest(request);

        var newsManifest = await RequireDatasetAsync(
            request.NewsDatasetId,
            EvidenceDatasetKind.NewsRevisions,
            cancellationToken);
        var barsManifest = await RequireDatasetAsync(
            request.AsTradedBarsDatasetId,
            EvidenceDatasetKind.MarketBarsAsTraded,
            cancellationToken);
        var sessionsManifest = await RequireDatasetAsync(
            request.ExchangeSessionsDatasetId,
            EvidenceDatasetKind.ExchangeSessions,
            cancellationToken);
        RequireProviderUpdatedNewsDataset(newsManifest);
        RequireStaticMomentumDataset(barsManifest, "raw");

        var news = await partitionReader.ReadNewsRevisionsAsync(
            newsManifest,
            cancellationToken);
        var bars = await partitionReader.ReadMarketBarsAsync(
            barsManifest,
            cancellationToken);
        var sessions = await partitionReader.ReadExchangeSessionsAsync(
            sessionsManifest,
            cancellationToken);
        var selected = symbols.Append(request.Options.BenchmarkSymbol)
            .ToHashSet(StringComparer.Ordinal);
        var selectedNews = news
            .Where(row => row.Symbols.Any(selected.Contains))
            .ToArray();
        var selectedBars = bars
            .Where(row => selected.Contains(row.Symbol))
            .ToArray();
        if (selectedNews.Length == 0)
        {
            throw new InvalidDataException(
                "The news dataset contains no rows for the frozen research universe.");
        }

        var inputs = new[]
        {
            DatasetReference(newsManifest),
            DatasetReference(barsManifest),
            DatasetReference(sessionsManifest)
        };
        var universe = new
        {
            UniverseType = "frozen_static_symbol_list",
            Symbols = symbols,
            Benchmark = request.Options.BenchmarkSymbol,
            Warning = request.SurvivorshipSelectionBiasWarning.Trim(),
            PointInTimeMembershipEvidence = false
        };
        var universeLedger = Json(universe);
        var universeLedgerId =
            $"static-news-universe-{EvidenceCanonicalJson.ComputeSha256(universe)}";
        var partitionDefinition = Json(request.StudyPartitions);
        var holdout = await packager.PrepareHoldoutAsync(
            request.StudyName,
            inputs,
            universeLedgerId,
            request.StudyPartitions,
            universeLedger,
            partitionDefinition,
            cancellationToken);
        await catalog.ReserveHoldoutAsync(
            new EvidenceHoldoutConsumption(
                holdout.HoldoutId,
                request.ResearchRunId,
                request.CreatedAtUtc),
            cancellationToken);

        var report = new ProviderUpdatedDailyNewsStudy().Analyze(
            selectedNews,
            selectedBars,
            sessions,
            request.Options,
            request.StudyPartitions);
        var blockers = report.PromotionBlockers
            .Append(StaticUniversePromotionBlocker)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        report = report with
        {
            PromotionEligible = false,
            PromotionBlockers = blockers
        };
        var studyConfig = Json(new
        {
            Workflow = "provider_updated_daily_news_diagnostic",
            request.NewsDatasetId,
            request.AsTradedBarsDatasetId,
            request.ExchangeSessionsDatasetId,
            FrozenSymbols = symbols,
            Benchmark = request.Options.BenchmarkSymbol,
            request.Options.HorizonSessions,
            request.Options.RoundTripCostBps,
            request.Options.MinimumQuietPeriodHours,
            ProviderCreatedAtUsage = "metadata_only",
            EntryPolicy = "first_exchange_open_after_information_is_knowable",
            request.SurvivorshipSelectionBiasWarning
        });
        var package = await packager.PackageAsync(
            new EvidenceResearchRunPackageRequest(
                request.ResearchRunId,
                request.StudyName,
                request.CreatedAtUtc,
                request.CodeVersion,
                inputs,
                universeLedgerId,
                request.StudyPartitions,
                studyConfig,
                universeLedger,
                partitionDefinition,
                Assumptions(request.Assumptions),
                [
                    new EvidenceResearchNamedOutput(
                        "event-observations",
                        Json(report.EventObservations)),
                    new EvidenceResearchNamedOutput(
                        "story-clusters",
                        Json(report.StoryClusters)),
                    new EvidenceResearchNamedOutput(
                        "horizon-returns",
                        Json(report.HorizonReturns)),
                    new EvidenceResearchNamedOutput(
                        "summary-by-timing-and-horizon",
                        Json(report.Summaries)),
                    new EvidenceResearchNamedOutput(
                        "summary-by-category-timing-and-horizon",
                        Json(report.CategorySummaries)),
                    new EvidenceResearchNamedOutput(
                        "exclusions-and-coverage",
                        Json(new
                        {
                            report.Exclusions,
                            StoryCount = report.StoryClusters.Count,
                            EventCount = report.EventObservations.Count,
                            IndependentEpisodeCount =
                                report.EventObservations.Count(
                                    value => value.IsIndependentEpisodeStart),
                            ReturnCount = report.HorizonReturns.Count,
                            CleanReturnCount = report.HorizonReturns.Count(value => value.IsClean)
                        })),
                    new EvidenceResearchNamedOutput(
                        "readiness",
                        Json(new
                        {
                            EvidenceReady = false,
                            PromotionEligible = false,
                            Blockers = blockers
                        }))
                ],
                false,
                blockers),
            cancellationToken);
        var commit = await catalog.RegisterResearchRunAsync(
            package.ManifestDraft,
            cancellationToken);
        var registered = await catalog.GetResearchRunAsync(
            package.ManifestDraft.ResearchRunId,
            cancellationToken)
            ?? throw new InvalidDataException(
                "The evidence catalog did not return the registered daily news diagnostic.");
        EnsureManifestMatchesPackage(registered, package.ManifestDraft);

        return new CatalogProviderUpdatedDailyNewsDiagnosticResult(
            newsManifest.DatasetId,
            barsManifest.DatasetId,
            sessionsManifest.DatasetId,
            symbols,
            report,
            registered,
            commit.AlreadyCommitted);
    }

    private static string[] ValidateProviderUpdatedDailyNewsRequest(
        CatalogProviderUpdatedDailyNewsDiagnosticRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResearchRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StudyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CodeVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.NewsDatasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AsTradedBarsDatasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExchangeSessionsDatasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SurvivorshipSelectionBiasWarning);
        ArgumentNullException.ThrowIfNull(request.Options);
        ArgumentNullException.ThrowIfNull(request.StudyPartitions);
        ArgumentNullException.ThrowIfNull(request.Assumptions);
        var symbols = (request.FrozenSymbols ??
                throw new ArgumentNullException(nameof(request.FrozenSymbols)))
            .Select(symbol =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
                return symbol.Trim().ToUpperInvariant();
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(symbol => symbol, StringComparer.Ordinal)
            .ToArray();
        if (symbols.Length == 0)
        {
            throw new ArgumentException(
                "The daily news diagnostic requires a frozen symbol universe.",
                nameof(request));
        }

        if (symbols.Contains(request.Options.BenchmarkSymbol, StringComparer.Ordinal))
        {
            symbols = symbols
                .Where(symbol => symbol != request.Options.BenchmarkSymbol)
                .ToArray();
        }

        var frozenRoundTripCostBps =
            ToMomentumExecutionCosts(request.Assumptions).RoundTripCostBps;
        if (frozenRoundTripCostBps <= 0m)
        {
            throw new ArgumentException(
                "The daily news diagnostic requires positive frozen round-trip execution costs.",
                nameof(request));
        }

        if (request.Options.RoundTripCostBps != frozenRoundTripCostBps)
        {
            throw new ArgumentException(
                "Daily-news execution costs must exactly match the frozen catalog assumptions.",
                nameof(request));
        }

        if (!request.Options.BenchmarkSymbol.Equals(
                request.Assumptions.Benchmark.Ticker.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The daily-news benchmark must exactly match the frozen catalog benchmark.",
                nameof(request));
        }

        return symbols;
    }

    private static void RequireProviderUpdatedNewsDataset(
        EvidenceDatasetManifest manifest)
    {
        if (manifest.Partitions.Count == 0 ||
            manifest.Attributes.TryGetValue(
                "historical_availability_proven",
                out var proven) &&
            Boolean.TryParse(proven, out var isProven) &&
            isProven)
        {
            throw new InvalidDataException(
                "This diagnostic requires non-promotable historical REST news evidence.");
        }

        if (!manifest.Attributes.TryGetValue(
                "provider_updated_timestamp_only",
                out var updatedOnly) ||
            !Boolean.TryParse(updatedOnly, out var isUpdatedOnly) ||
            !isUpdatedOnly)
        {
            throw new InvalidDataException(
                "Historical REST news must be normalized using provider updated-at availability.");
        }
    }
}
