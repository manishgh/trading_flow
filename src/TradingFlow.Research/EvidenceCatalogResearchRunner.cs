using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Research.Catalysts;
using TradingFlow.Research.Momentum;

namespace TradingFlow.Research;

public sealed record CatalogMomentumStudyRequest(
    string ResearchAdjustedBarsDatasetId,
    string AsTradedBarsDatasetId,
    string UniverseMembershipDatasetId,
    CrossSectionalMomentumStudyDefinition Definition);

public sealed record CatalogMomentumStudyResult(
    string ResearchAdjustedBarsDatasetId,
    string AsTradedBarsDatasetId,
    string UniverseMembershipDatasetId,
    CrossSectionalMomentumReport Report,
    MomentumResearchAuditReport Audit);

public sealed record CatalogCatalystStudyRequest(
    string MarketBarsDatasetId,
    string NewsDatasetId,
    string SentimentAssessmentsDatasetId,
    string UniverseMembershipDatasetId,
    string? SipQuotesDatasetId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string CandleTimeframe,
    CatalystEventStudyOptions? Options = null,
    CatalogCatalystExecutionOptions? Execution = null);

public sealed record CatalogCatalystExecutionOptions(
    ExecutablePositionDirection Direction,
    long RequestedQuantity,
    TimeSpan MaximumQuoteDelay,
    SipQuoteEligibilityPolicy QuoteEligibilityPolicy,
    CatalystExecutionCostModel CostModel,
    CatalystExecutionCalibrationPolicy CalibrationPolicy);

public sealed record CatalogCatalystExecutableObservation(
    string StoryId,
    string RevisionId,
    string StudyPartition,
    string Horizon,
    CatalystExecutableReturnResult Result);

public sealed record CatalogCatalystStudyResult(
    string MarketBarsDatasetId,
    string NewsDatasetId,
    string SentimentAssessmentsDatasetId,
    string UniverseMembershipDatasetId,
    string? SipQuotesDatasetId,
    bool ExecutableEvidenceReady,
    IReadOnlyList<string> ReadinessFailures,
    CatalystEventStudyReport Report,
    IReadOnlyList<CatalogCatalystExecutableObservation> ExecutableObservations);

/// <summary>
/// Runs research only against datasets that are discoverable through the evidence catalog.
/// The injected partition reader is an offline immutable-object reader; provider clients are
/// intentionally absent from this assembly and this execution path.
/// </summary>
public sealed class EvidenceCatalogResearchRunner
{
    private const int MinimumIndependentCatalystEvents = 300;
    private const int MinimumIndependentEventsPerObservedCohort = 100;

    private readonly IEvidenceCatalog catalog;
    private readonly IEvidencePartitionDataReader dataReader;
    private readonly IExchangeSessionResolver? exchangeSessionResolver;

    public EvidenceCatalogResearchRunner(
        IEvidenceCatalog catalog,
        IEvidencePartitionDataReader dataReader,
        IExchangeSessionResolver? exchangeSessionResolver = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.dataReader = dataReader ?? throw new ArgumentNullException(nameof(dataReader));
        this.exchangeSessionResolver = exchangeSessionResolver;
    }

    public async Task<CatalogMomentumStudyResult> RunMomentumAsync(
        CatalogMomentumStudyRequest request,
        MomentumExecutionCostAssumptions? executionCosts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Definition);

        var adjustedBarsManifest = await RequireDatasetAsync(
            request.ResearchAdjustedBarsDatasetId,
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            cancellationToken);
        var asTradedBarsManifest = await RequireDatasetAsync(
            request.AsTradedBarsDatasetId,
            EvidenceDatasetKind.MarketBarsAsTraded,
            cancellationToken);
        var universeManifest = await RequireDatasetAsync(
            request.UniverseMembershipDatasetId,
            EvidenceDatasetKind.UniverseMembership,
            cancellationToken);
        RequireMarketBarSemantics(adjustedBarsManifest, "all");
        RequireMarketBarSemantics(asTradedBarsManifest, "raw");

        var adjustedRows = await dataReader.ReadMarketBarsAsync(
            adjustedBarsManifest,
            cancellationToken);
        var asTradedRows = await dataReader.ReadMarketBarsAsync(
            asTradedBarsManifest,
            cancellationToken);
        var universeRows = await dataReader.ReadUniverseMembershipAsync(
            universeManifest,
            cancellationToken);
        var evidence = BuildMomentumEvidence(
            adjustedRows,
            asTradedRows,
            universeRows,
            request.Definition.Options.ExchangeTimezone);
        var eligibleSymbolsByDate = evidence.EligibleSymbolsByDate;
        if (eligibleSymbolsByDate.Count == 0)
        {
            throw new InvalidDataException(
                "The committed universe-membership dataset contains no active members.");
        }

        var evidenceDefinition = request.Definition with
        {
            PointInTimeUniverseEvidence = true,
            AdjustedPricesConfirmed = true,
            AsTradedLiquidityPricesConfirmed = true,
            SecurityAndIssuerIdentityConfirmed = evidence.SecurityIdentityConfirmed,
            CorporateActionsReconciled = HasTrueAttribute(
                adjustedBarsManifest,
                "corporate_actions_reconciled"),
            TerminalOutcomesReconciled = HasTrueAttribute(
                adjustedBarsManifest,
                "terminal_outcomes_reconciled"),
            ListedBarCoverageConfirmed = HasMinimumDecimalAttribute(
                adjustedBarsManifest,
                "listed_bar_coverage_pct",
                95m),
            BenchmarkCoverageConfirmed = HasTrueAttribute(
                adjustedBarsManifest,
                "benchmark_coverage_confirmed"),
            UniverseDecisionCutoffsConfirmed = evidence.DecisionCutoffsConfirmed
        };
        var report = new CrossSectionalMomentumResearchAnalyzer().Analyze(
            evidence.AdjustedBars,
            evidenceDefinition,
            eligibleSymbolsByDate,
            evidence.AsTradedBars,
            executionCosts);
        var audit = new MomentumResearchAuditAnalyzer().Analyze(
            report.RankObservations,
            report.Options.DecisionCadenceBars,
            executionCosts);
        return new CatalogMomentumStudyResult(
            adjustedBarsManifest.DatasetId,
            asTradedBarsManifest.DatasetId,
            universeManifest.DatasetId,
            report,
            audit);
    }

    public async Task<CatalogCatalystStudyResult> RunCatalystDiagnosticAsync(
        CatalogCatalystStudyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EndUtc <= request.StartUtc)
        {
            throw new ArgumentException(
                "Catalyst study end must follow its start.",
                nameof(request));
        }

        var barsManifest = await RequireDatasetAsync(
            request.MarketBarsDatasetId,
            EvidenceDatasetKind.MarketBarsAsTraded,
            cancellationToken);
        RequireSipFeed(barsManifest, "bar");
        var newsManifest = await RequireDatasetAsync(
            request.NewsDatasetId,
            EvidenceDatasetKind.NewsRevisions,
            cancellationToken);
        var sentimentManifest = await RequireDatasetAsync(
            request.SentimentAssessmentsDatasetId,
            EvidenceDatasetKind.SentimentAssessments,
            cancellationToken);
        var universeManifest = await RequireDatasetAsync(
            request.UniverseMembershipDatasetId,
            EvidenceDatasetKind.UniverseMembership,
            cancellationToken);
        EvidenceDatasetManifest? quotesManifest = null;
        if (!String.IsNullOrWhiteSpace(request.SipQuotesDatasetId))
        {
            quotesManifest = await RequireDatasetAsync(
                request.SipQuotesDatasetId,
                EvidenceDatasetKind.SipQuotes,
                cancellationToken);
            RequireSipFeed(quotesManifest, "quote");
        }

        var barRows = await dataReader.ReadMarketBarsAsync(barsManifest, cancellationToken);
        var newsRows = await dataReader.ReadNewsRevisionsAsync(
            newsManifest,
            cancellationToken);
        var sentimentRows = await dataReader.ReadSentimentAssessmentsAsync(
            sentimentManifest,
            cancellationToken);
        var universeRows = await dataReader.ReadUniverseMembershipAsync(
            universeManifest,
            cancellationToken);
        var bars = ToOhlcvBySymbol(barRows);
        var failures = new List<string>();
        var sentimentJoinedNews = JoinSentimentAssessments(newsRows, sentimentRows);
        AddSentimentModelVintageReadinessFailures(
            sentimentJoinedNews,
            request.Options?.StudyPartitions ?? [],
            failures);
        var sessionResolver = exchangeSessionResolver ??
            throw new InvalidOperationException(
                "Catalog catalyst research requires a committed exchange-calendar resolver.");
        var catalysts = FilterCatalystsByPointInTimeUniverse(
            ToCatalystsBySymbol(sentimentJoinedNews),
            universeRows,
            sessionResolver,
            failures);
        var report = new CatalystTechnicalEventStudyRunner(
            sessionResolver)
            .Analyze(
            bars,
            catalysts,
            request.StartUtc,
            request.EndUtc,
            request.CandleTimeframe,
            request.Options);

        IReadOnlyList<CatalogCatalystExecutableObservation> executableObservations = [];
        if (quotesManifest is null)
        {
            failures.Add("historical_sip_nbbo_dataset_missing");
        }
        else if (request.Execution is null)
        {
            failures.Add("executable_quote_assumptions_missing");
        }
        else
        {
            var quotes = await dataReader.ReadSipQuotesAsync(
                quotesManifest,
                cancellationToken);
            executableObservations = BuildExecutableObservations(
                report,
                quotes,
                request.Execution,
                sessionResolver,
                failures);
        }

        if (report.Observations.Any(observation =>
                !observation.AvailabilityEvidence.Equals(
                    CatalystAvailabilityEvidence.ObservedReceiptTime,
                    StringComparison.Ordinal) &&
                !observation.AvailabilityEvidence.Equals(
                    CatalystAvailabilityEvidence.NewsAndAssessmentObservedTime,
                    StringComparison.Ordinal)))
        {
            failures.Add("historical_news_uses_provider_timestamp_proxy");
        }

        var independentEventCount = report.Observations
            .Select(observation => observation.StoryId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (independentEventCount < MinimumIndependentCatalystEvents)
        {
            failures.Add(
                $"independent_catalyst_events_below_minimum:" +
                $"{independentEventCount}/{MinimumIndependentCatalystEvents}");
        }

        foreach (var cohort in report.Observations
                     .GroupBy(observation => observation.EventCategory, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var cohortCount = cohort
                .Select(observation => observation.StoryId)
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (cohortCount < MinimumIndependentEventsPerObservedCohort)
            {
                failures.Add(
                    $"independent_catalyst_cohort_events_below_minimum:" +
                    $"{cohort.Key}:{cohortCount}/" +
                    $"{MinimumIndependentEventsPerObservedCohort}");
            }
        }

        return new CatalogCatalystStudyResult(
            barsManifest.DatasetId,
            newsManifest.DatasetId,
            sentimentManifest.DatasetId,
            universeManifest.DatasetId,
            quotesManifest?.DatasetId,
            failures.Count == 0,
            failures.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            report,
            executableObservations);
    }

    private static void AddSentimentModelVintageReadinessFailures(
        IReadOnlyList<SentimentJoinedNewsRevision> rows,
        IReadOnlyList<CatalystStudyPartition> studyPartitions,
        ICollection<string> readinessFailures)
    {
        if (rows.Count == 0)
        {
            readinessFailures.Add("sentiment_model_vintage_missing");
            return;
        }

        var vintages = rows
            .Select(item => new
            {
                item.Assessment.ModelArtifactId,
                item.Assessment.ModelArtifactSha256,
                item.Assessment.ModelTrainingDataCutoffUtc
            })
            .Distinct()
            .ToArray();
        if (vintages.Length != 1)
        {
            readinessFailures.Add("sentiment_model_vintage_not_homogeneous");
            return;
        }

        var vintage = vintages[0];
        var firstNewsAvailability = rows.Min(item =>
            item.News.AvailabilityTimestampUtc);
        if (vintage.ModelTrainingDataCutoffUtc > firstNewsAvailability)
        {
            readinessFailures.Add(
                "sentiment_model_training_cutoff_after_first_news_availability:" +
                $"{vintage.ModelTrainingDataCutoffUtc:O}>" +
                $"{firstNewsAvailability:O}");
        }

        var holdoutBoundaries = studyPartitions
            .Where(partition => partition.Label.Equals(
                "holdout",
                StringComparison.Ordinal))
            .Select(partition => partition.StartUtc)
            .Distinct()
            .ToArray();
        if (holdoutBoundaries.Length > 1)
        {
            readinessFailures.Add("sentiment_model_holdout_boundary_ambiguous");
            return;
        }

        if (holdoutBoundaries.Length == 1 &&
            vintage.ModelTrainingDataCutoffUtc > holdoutBoundaries[0])
        {
            readinessFailures.Add(
                "sentiment_model_training_cutoff_after_holdout_boundary:" +
                $"{vintage.ModelTrainingDataCutoffUtc:O}>" +
                $"{holdoutBoundaries[0]:O}");
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<CatalystEvent>>
        FilterCatalystsByPointInTimeUniverse(
            IReadOnlyDictionary<string, IReadOnlyList<CatalystEvent>> catalystsBySymbol,
            IReadOnlyList<UniverseMembershipEvidenceRow> memberships,
            IExchangeSessionResolver sessionResolver,
            ICollection<string> readinessFailures)
    {
        var membershipBySymbolAndDate = memberships
            .GroupBy(
                row => MembershipKey(row.Symbol, row.EffectiveSessionDate),
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(row => row.AsOfUtc)
                    .ThenBy(row => row.ReceivedAtUtc)
                    .ToArray(),
                StringComparer.Ordinal);
        var filtered = new Dictionary<string, IReadOnlyList<CatalystEvent>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in catalystsBySymbol.OrderBy(
                     value => value.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            var eligible = new List<CatalystEvent>();
            foreach (var catalyst in pair.Value.OrderBy(value =>
                         CatalystAvailability.Resolve(value).AvailableAtUtc))
            {
                var availableAtUtc = CatalystAvailability.Resolve(catalyst).AvailableAtUtc;
                var tradeDate = sessionResolver.Resolve(availableAtUtc).TradeDate;
                if (!membershipBySymbolAndDate.TryGetValue(
                        MembershipKey(pair.Key, tradeDate),
                        out var candidates))
                {
                    readinessFailures.Add(
                        $"point_in_time_universe_membership_missing:{pair.Key}:{tradeDate:yyyy-MM-dd}");
                    continue;
                }

                var knowable = candidates
                    .Where(row =>
                        row.AsOfUtc <= availableAtUtc &&
                        row.ReceivedAtUtc <= availableAtUtc)
                    .ToArray();
                if (knowable.Length == 0)
                {
                    readinessFailures.Add(
                        $"point_in_time_universe_membership_unavailable:{pair.Key}:{tradeDate:yyyy-MM-dd}");
                    continue;
                }

                var latestAsOf = knowable.Max(row => row.AsOfUtc);
                var latest = knowable
                    .Where(row => row.AsOfUtc == latestAsOf)
                    .ToArray();
                if (latest.Select(row => row.Included).Distinct().Count() != 1)
                {
                    readinessFailures.Add(
                        $"point_in_time_universe_membership_ambiguous:{pair.Key}:{tradeDate:yyyy-MM-dd}");
                    continue;
                }

                if (latest[0].Included)
                {
                    eligible.Add(catalyst);
                }
            }

            if (eligible.Count > 0)
            {
                filtered[pair.Key] = eligible;
            }
        }

        return filtered;
    }

    private static string MembershipKey(string symbol, DateOnly tradeDate) =>
        $"{symbol.Trim().ToUpperInvariant()}\u001f{tradeDate:yyyy-MM-dd}";

    private static IReadOnlyList<CatalogCatalystExecutableObservation> BuildExecutableObservations(
        CatalystEventStudyReport report,
        IReadOnlyList<SipQuoteEvidenceRow> quotes,
        CatalogCatalystExecutionOptions execution,
        IExchangeSessionResolver sessionResolver,
        ICollection<string> readinessFailures)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(execution.QuoteEligibilityPolicy);
        ArgumentNullException.ThrowIfNull(execution.CostModel);
        if (!Enum.IsDefined(execution.Direction))
        {
            throw new ArgumentOutOfRangeException(
                nameof(execution),
                "Executable catalyst direction is invalid.");
        }

        if (execution.RequestedQuantity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(execution),
                "Executable catalyst quantity must be positive.");
        }

        if (execution.MaximumQuoteDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(execution),
                "Executable catalyst quote delay must be positive.");
        }

        var securityIdsBySymbol = quotes
            .GroupBy(quote => quote.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(quote => quote.SecurityId)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var analyzer = new CatalystExecutableReturnAnalyzer();
        var results = new List<CatalogCatalystExecutableObservation>();
        foreach (var observation in report.Observations)
        {
            if (!securityIdsBySymbol.TryGetValue(observation.Ticker, out var securityIds) ||
                securityIds.Length == 0)
            {
                readinessFailures.Add(
                    $"historical_sip_nbbo_security_missing:{observation.Ticker}");
                continue;
            }

            if (securityIds.Length != 1)
            {
                readinessFailures.Add(
                    $"historical_sip_nbbo_identity_ambiguous:{observation.Ticker}");
                continue;
            }

            if (observation.FirstConfirmableTimestampUtc is not { } entryAtUtc)
            {
                continue;
            }

            var entrySession = sessionResolver.Resolve(entryAtUtc);
            if (!TryMapExecutionSession(entrySession, out var executionSession))
            {
                readinessFailures.Add(
                    $"historical_sip_nbbo_session_unsupported:{observation.MarketSession}");
                continue;
            }

            foreach (var forwardReturn in observation.ForwardReturns)
            {
                if (forwardReturn.TargetTimestampUtc is not { } exitAtUtc ||
                    exitAtUtc <= entryAtUtc)
                {
                    continue;
                }

                var exitSession = sessionResolver.Resolve(exitAtUtc);
                if (exitSession.TradeDate != entrySession.TradeDate ||
                    exitSession.Session != entrySession.Session)
                {
                    readinessFailures.Add(
                        $"historical_sip_nbbo_cross_session_target:{observation.Ticker}:{forwardReturn.Horizon}");
                    continue;
                }

                var request = new CatalystExecutableReturnRequest(
                    securityIds[0],
                    observation.Ticker,
                    execution.Direction,
                    entryAtUtc,
                    exitAtUtc,
                    executionSession,
                    entrySession.SessionStartUtc,
                    entrySession.SessionEndUtc,
                    execution.MaximumQuoteDelay,
                    execution.RequestedQuantity,
                    execution.QuoteEligibilityPolicy,
                    execution.CostModel,
                    execution.CalibrationPolicy);
                results.Add(new CatalogCatalystExecutableObservation(
                    observation.StoryId,
                    observation.RevisionId,
                    observation.StudyPartition,
                    forwardReturn.Horizon,
                    analyzer.Analyze(request, quotes)));
            }
        }

        return results
            .OrderBy(item => item.Result.Symbol, StringComparer.Ordinal)
            .ThenBy(item => item.Result.EntryQuoteTimestampUtc)
            .ThenBy(item => item.Horizon, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryMapExecutionSession(
        ExchangeSessionResolution resolution,
        out UsEquityTradingSession session)
    {
        session = resolution.Session switch
        {
            TradingFlow.Engine.Execution.EquityTradingSession.Premarket =>
                UsEquityTradingSession.Premarket,
            TradingFlow.Engine.Execution.EquityTradingSession.Regular =>
                UsEquityTradingSession.Regular,
            TradingFlow.Engine.Execution.EquityTradingSession.AfterHours =>
                UsEquityTradingSession.Postmarket,
            _ => default
        };
        return resolution.Session is
            TradingFlow.Engine.Execution.EquityTradingSession.Premarket or
            TradingFlow.Engine.Execution.EquityTradingSession.Regular or
            TradingFlow.Engine.Execution.EquityTradingSession.AfterHours;
    }

    private async Task<EvidenceDatasetManifest> RequireDatasetAsync(
        string datasetId,
        EvidenceDatasetKind expectedKind,
        CancellationToken cancellationToken)
    {
        var manifest = await catalog.GetDatasetAsync(
            RequireId(datasetId, nameof(datasetId)),
            cancellationToken) ?? throw new KeyNotFoundException(
            $"Committed evidence dataset '{datasetId}' was not found.");
        if (manifest.Kind != expectedKind)
        {
            throw new InvalidDataException(
                $"Dataset '{datasetId}' has kind {manifest.Kind}; expected {expectedKind}.");
        }

        if (!manifest.Quality.Passed ||
            manifest.Partitions.Any(partition => !partition.Quality.Passed))
        {
            throw new InvalidDataException(
                $"Dataset '{datasetId}' failed evidence quality validation.");
        }

        return manifest;
    }

    private static MomentumEvidence BuildMomentumEvidence(
        IReadOnlyList<MarketBarEvidenceRow> adjustedRows,
        IReadOnlyList<MarketBarEvidenceRow> asTradedRows,
        IReadOnlyList<UniverseMembershipEvidenceRow> universeRows,
        string exchangeTimezone)
    {
        var adjustedDaily = RequireDailyRows(adjustedRows, "all", exchangeTimezone);
        var asTradedDaily = RequireDailyRows(asTradedRows, "raw", exchangeTimezone);
        var adjustedByIdentity = adjustedDaily.ToDictionary(
            item => (item.Row.SecurityId, item.SessionDate),
            item => item.Row);
        var asTradedByIdentity = asTradedDaily.ToDictionary(
            item => (item.Row.SecurityId, item.SessionDate),
            item => item.Row);

        var eligible = new Dictionary<DateOnly, HashSet<string>>();
        var identityConfirmed = true;
        var cutoffsConfirmed = true;
        foreach (var row in universeRows.Where(row => row.Included))
        {
            var key = (row.SecurityId, row.EffectiveSessionDate);
            if (!adjustedByIdentity.TryGetValue(key, out var adjusted) ||
                !asTradedByIdentity.TryGetValue(key, out var asTraded) ||
                !adjusted.Symbol.Equals(row.Symbol, StringComparison.OrdinalIgnoreCase) ||
                !asTraded.Symbol.Equals(row.Symbol, StringComparison.OrdinalIgnoreCase) ||
                String.IsNullOrWhiteSpace(row.IssuerId))
            {
                identityConfirmed = false;
                continue;
            }

            if (row.ReceivedAtUtc > adjusted.BarEndUtc ||
                row.AsOfUtc > adjusted.BarEndUtc)
            {
                cutoffsConfirmed = false;
                continue;
            }

            if (!eligible.TryGetValue(row.EffectiveSessionDate, out var symbols))
            {
                symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                eligible.Add(row.EffectiveSessionDate, symbols);
            }

            symbols.Add(row.Symbol);
        }

        return new MomentumEvidence(
            ToOhlcvBySymbol(adjustedDaily.Select(item => item.Row)),
            ToOhlcvBySymbol(asTradedDaily.Select(item => item.Row)),
            eligible.ToDictionary(
                item => item.Key,
                item => (IReadOnlySet<string>)item.Value),
            identityConfirmed,
            cutoffsConfirmed);
    }

    private static IReadOnlyList<(MarketBarEvidenceRow Row, DateOnly SessionDate)>
        RequireDailyRows(
            IReadOnlyList<MarketBarEvidenceRow> rows,
            string expectedAdjustment,
            string exchangeTimezone)
    {
        if (rows.Count == 0)
        {
            throw new InvalidDataException("A momentum market-bar dataset cannot be empty.");
        }

        var daily = rows
            .Where(row => row.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase))
            .Select(row => (
                Row: row,
                SessionDate: TradingFlow.Engine.Execution.ExecutionRunContextFactory.ResolveSessionDate(
                    row.BarStartUtc,
                    exchangeTimezone)))
            .ToArray();
        if (daily.Length != rows.Count ||
            daily.Any(item =>
                !item.Row.Adjustment.Equals(
                    expectedAdjustment,
                    StringComparison.OrdinalIgnoreCase) ||
                !item.Row.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase) ||
                !item.Row.DataFeed.Equals("sip", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"Momentum bars must be 1d SIP USD rows with adjustment={expectedAdjustment}.");
        }

        if (daily
            .GroupBy(item => (item.Row.SecurityId, item.SessionDate))
            .Any(group => group.Count() != 1))
        {
            throw new InvalidDataException(
                "Momentum bars contain duplicate security/session observations.");
        }

        if (daily
            .GroupBy(item => item.Row.Symbol, StringComparer.OrdinalIgnoreCase)
            .Any(group => group
                .Select(item => item.Row.SecurityId)
                .Distinct(StringComparer.Ordinal)
                .Count() != 1))
        {
            throw new InvalidDataException(
                "Momentum bars contain symbol reuse across distinct security identities.");
        }

        if (daily
            .GroupBy(item => item.Row.SecurityId, StringComparer.Ordinal)
            .Any(group => group
                .Select(item => item.Row.Symbol)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != 1))
        {
            throw new InvalidDataException(
                "Momentum bars contain ticker changes that require an explicit identity-aware series mapping.");
        }

        return daily;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> ToOhlcvBySymbol(
        IEnumerable<MarketBarEvidenceRow> rows) =>
        rows
            .GroupBy(row => row.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<OhlcvBar>)group
                    .OrderBy(row => row.BarStartUtc)
                    .Select(row => new OhlcvBar(
                        row.Symbol,
                        row.BarStartUtc,
                        row.Timeframe,
                        EvidenceFixedDecimal.FromPriceUnits(row.OpenPriceUnits),
                        EvidenceFixedDecimal.FromPriceUnits(row.HighPriceUnits),
                        EvidenceFixedDecimal.FromPriceUnits(row.LowPriceUnits),
                        EvidenceFixedDecimal.FromPriceUnits(row.ClosePriceUnits),
                        row.Volume))
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<SentimentJoinedNewsRevision> JoinSentimentAssessments(
        IReadOnlyList<NewsRevisionEvidenceRow> newsRows,
        IReadOnlyList<SentimentAssessmentEvidenceRow> assessmentRows)
    {
        ArgumentNullException.ThrowIfNull(newsRows);
        ArgumentNullException.ThrowIfNull(assessmentRows);
        var assessmentGroups = assessmentRows
            .GroupBy(AssessmentJoinKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(row => row.AssessmentId, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        var joined = new List<SentimentJoinedNewsRevision>(newsRows.Count);
        foreach (var news in newsRows)
        {
            var key = NewsJoinKey(news);
            if (!assessmentGroups.TryGetValue(key, out var matches))
            {
                throw new InvalidDataException(
                    $"Sentiment assessment is missing for news revision " +
                    $"'{news.Provider}/{news.ProviderArticleId}/{news.RevisionId}'.");
            }

            if (matches.Length != 1)
            {
                throw new InvalidDataException(
                    $"Sentiment assessment is ambiguous for news revision " +
                    $"'{news.Provider}/{news.ProviderArticleId}/{news.RevisionId}'.");
            }

            var assessment = matches[0];
            var expectedContent =
                SentimentAssessmentEvidenceRow.CreateNewsRevisionInputContent(news);
            var expectedHash =
                SentimentAssessmentEvidenceRow.ComputeInputContentSha256(expectedContent);
            if (!assessment.InputContent.Equals(expectedContent, StringComparison.Ordinal) ||
                !assessment.InputContentSha256.Equals(expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Sentiment assessment input hash does not match news revision " +
                    $"'{news.Provider}/{news.ProviderArticleId}/{news.RevisionId}'.");
            }

            if (!assessment.Sources.SequenceEqual(news.Sources))
            {
                throw new InvalidDataException(
                    $"Sentiment assessment lineage does not exactly match news revision " +
                    $"'{news.Provider}/{news.ProviderArticleId}/{news.RevisionId}'.");
            }

            joined.Add(new SentimentJoinedNewsRevision(news, assessment));
        }

        return joined;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<CatalystEvent>>
        ToCatalystsBySymbol(IEnumerable<SentimentJoinedNewsRevision> rows) =>
        rows
            .SelectMany(item => item.News.Symbols.Select(symbol => new CatalystEvent(
                symbol,
                item.News.PublishedAtUtc,
                CatalystType.NewsReport,
                item.News.Headline,
                item.Assessment.Score,
                item.News.Provider,
                item.News.ProviderArticleId,
                item.News.Summary,
                item.News.Provider,
                item.News.ArticleUrl,
                item.News.AvailabilityEvidence == NewsAvailabilityEvidence.ObservedReceiptTime
                    ? item.News.ReceivedAtUtc
                    : null,
                item.News.ProviderUpdatedAtUtc,
                item.News.AvailabilityEvidence switch
                {
                    NewsAvailabilityEvidence.ProviderTimestampOnly =>
                        CatalystAvailabilityEvidence.ProviderTimestampOnly,
                    NewsAvailabilityEvidence.ProviderUpdatedTimestampOnly =>
                        CatalystAvailabilityEvidence.ProviderUpdatedTimestampOnly,
                    NewsAvailabilityEvidence.ObservedReceiptTime =>
                        CatalystAvailabilityEvidence.ObservedReceiptTime,
                    _ => throw new InvalidDataException(
                        $"Unsupported news availability evidence " +
                        $"'{item.News.AvailabilityEvidence}'.")
                },
                DecisionAvailableAt: LaterOf(
                    item.News.AvailabilityTimestampUtc,
                    item.Assessment.ObservedAtUtc),
                DecisionAvailabilityEvidence:
                    item.News.AvailabilityEvidence ==
                    NewsAvailabilityEvidence.ObservedReceiptTime
                        ? CatalystAvailabilityEvidence.NewsAndAssessmentObservedTime
                        : CatalystAvailabilityEvidence
                            .ProviderTimestampAndAssessmentObservedTime)))
            .GroupBy(catalyst => catalyst.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CatalystEvent>)group
                    .OrderBy(catalyst => CatalystAvailability.Resolve(catalyst).AvailableAtUtc)
                    .ThenBy(catalyst => catalyst.ExternalId, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

    private static DateTimeOffset LaterOf(
        DateTimeOffset left,
        DateTimeOffset right) =>
        left >= right ? left : right;

    private static string NewsJoinKey(NewsRevisionEvidenceRow row) =>
        $"{row.Provider}\u001f{row.ProviderArticleId}\u001f{row.RevisionId}";

    private static string AssessmentJoinKey(SentimentAssessmentEvidenceRow row) =>
        $"{row.NewsProvider}\u001f{row.ProviderArticleId}\u001f{row.NewsRevisionId}";

    private static void RequireMarketBarSemantics(
        EvidenceDatasetManifest manifest,
        string expectedAdjustment)
    {
        RequireSipFeed(manifest, "bar");
        if (manifest.Partitions.Any(partition =>
                !partition.Provenance.Adjustment.Equals(
                    expectedAdjustment,
                    StringComparison.OrdinalIgnoreCase) ||
                !partition.Provenance.Currency.Equals(
                    "USD",
                    StringComparison.OrdinalIgnoreCase) ||
                !partition.Provenance.Timeframe.Equals(
                    "1d",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"Dataset '{manifest.DatasetId}' must use 1d SIP USD bars with " +
                $"adjustment={expectedAdjustment}.");
        }
    }

    private static bool HasTrueAttribute(
        EvidenceDatasetManifest manifest,
        string name) =>
        manifest.Attributes.TryGetValue(name, out var value) &&
        Boolean.TryParse(value, out var parsed) &&
        parsed;

    private static bool HasMinimumDecimalAttribute(
        EvidenceDatasetManifest manifest,
        string name,
        decimal minimum) =>
        manifest.Attributes.TryGetValue(name, out var value) &&
        Decimal.TryParse(
            value,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) &&
        parsed >= minimum;

    private static void RequireSipFeed(EvidenceDatasetManifest manifest, string label)
    {
        if (!manifest.DataFeed.Equals("sip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Catalyst research requires SIP {label} evidence; dataset " +
                $"'{manifest.DatasetId}' uses '{manifest.DataFeed}'.");
        }
    }

    private static string RequireId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private sealed record MomentumEvidence(
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> AdjustedBars,
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> AsTradedBars,
        IReadOnlyDictionary<DateOnly, IReadOnlySet<string>> EligibleSymbolsByDate,
        bool SecurityIdentityConfirmed,
        bool DecisionCutoffsConfirmed);

    private sealed record SentimentJoinedNewsRevision(
        NewsRevisionEvidenceRow News,
        SentimentAssessmentEvidenceRow Assessment);
}
