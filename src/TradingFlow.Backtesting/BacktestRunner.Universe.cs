using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Backtesting;

// Point-in-time universe helpers for BacktestRunner. Split into a partial file so the runner's
// orchestration stays readable; behavior is identical to when these lived inline. The as-of and
// per-day screening logic itself lives in the Engine (HistoricalScreenerUniverseProvider).
public sealed partial class BacktestRunner
{
    /// <summary>
    /// The failed stock work items that withhold a daily run's economics. A research-screen run (whole market) does not stop
    /// when a stock's data is unavailable for a named reason (<see cref="TradingFlow.Engine.Pipeline.MarketDataUnavailable"/>:
    /// missing or unusable bars, insufficient history): those items are skipped, published and counted. An engine error or a
    /// timeout still stops it. Every other run keeps the existing rule that any failed item withholds the results.
    /// </summary>
    internal static IReadOnlyList<StrategyTickerBacktestBatch> StoppingBatches(BacktestRunConfig run, IReadOnlyList<StrategyTickerBacktestBatch> batches) =>
        batches.Where(batch => !batch.Succeeded && !(run.Universe?.RankedMembership is not null &&
            TradingFlow.Engine.Pipeline.MarketDataUnavailable.IsNamedReason(batch.ErrorMessage))).ToArray();

    /// <summary>
    /// Publishes the run's data gaps beside the results: each skipped stock work item (stock, strategy, reason) with the
    /// skipped stocks counted against the pool and flagged above 5%, and each holding closed at its last real close because
    /// its bars never resumed or a split took effect, with the counts. Writes nothing when none of these occurred.
    /// </summary>
    private async Task PublishDataGapsAsync(BacktestRunConfig run, IReadOnlyList<StrategyTickerBacktestBatch> batches,
        IReadOnlyList<StrategyTickerBacktestBatch> stopping, IReadOnlyList<BacktestTrade> portfolioTrades,
        CancellationToken cancellationToken)
    {
        var skipped = batches.Where(batch => !batch.Succeeded && !stopping.Contains(batch)).ToArray();
        var closed = portfolioTrades.Where(trade => trade.ExitReason is ChronologicalBacktestExecutionCoordinator.DataEndedExitReason
            or ChronologicalBacktestExecutionCoordinator.SplitExitReason).ToArray();
        if (skipped.Length == 0 && closed.Length == 0) return;
        var pool = run.Tickers.Count;
        var skippedStocks = skipped.Select(batch => batch.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var skippedPct = pool == 0 ? 0m : Decimal.Round(skippedStocks * 100m / pool, 4);
        var payload = new
        {
            runName = run.RunName, poolStocks = pool, skippedStocks, skippedStocksPct = skippedPct,
            skippedAboveFivePercent = skippedPct > 5m, skippedStockWorkItems = skipped.Length,
            dataEndedLastCloseExits = closed.Count(trade => trade.ExitReason == ChronologicalBacktestExecutionCoordinator.DataEndedExitReason),
            splitExitsBeforeExDate = closed.Count(trade => trade.ExitReason == ChronologicalBacktestExecutionCoordinator.SplitExitReason),
            items = skipped.OrderBy(batch => batch.Ticker, StringComparer.Ordinal)
                .Select(batch => new { ticker = batch.Ticker, strategyId = batch.StrategyId, reason = batch.ErrorMessage }).ToArray(),
            forcedExits = closed.Select(trade => new { ticker = trade.Ticker, strategyId = trade.StrategyId, reason = trade.ExitReason,
                candidateId = trade.CandidateId, lastCloseUtc = trade.ExitTimestamp, exitPrice = trade.ExitPrice }).ToArray()
        };
        await _artifactWriter.WriteTextAsync(Path.Combine(run.ResultsRoot, "evidence", $"{run.RunName}-data-gaps.json"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

    private static string ResolveDailyTimeframe(BacktestRunConfig run)
    {
        return run.Intervals.FirstOrDefault(interval =>
            interval.Equals("1d", StringComparison.OrdinalIgnoreCase) ||
            interval.Equals("1day", StringComparison.OrdinalIgnoreCase) ||
            interval.Equals("1D", StringComparison.Ordinal)) ?? "1d";
    }

    // Broad candidate pool from a Finviz Elite screener export. Intended for structural/liquidity
    // screens (price, volume, market cap, sector) so the pool is not hand-picked. Note: Finviz
    // returns a current screen with no as-of, so for historical backtests this carries delisting
    // survivorship at the candidate level; the no-lookahead screen still governs selection.
    private async Task<IReadOnlyList<string>> ResolveFinvizCandidatePoolAsync(
        UniverseConfig universe,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(universe.CandidateScreenerQuery))
        {
            throw new InvalidOperationException(
                "universe.candidate_source=finviz requires universe.candidate_screener_query (a Finviz screener URL or filter string).");
        }

        using var httpClient = new HttpClient();
        using var client = new TradingFlow.Finviz.FinvizClient(
            httpClient,
            TradingFlow.Finviz.FinvizOptions.CreateDefault() with
            {
                AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? String.Empty
            },
            RequireRawArchiveWriter());

        var tickers = await client.GetScreenerTickersAsync(universe.CandidateScreenerQuery, cancellationToken);
        if (tickers.Count == 0)
        {
            throw new InvalidOperationException(
                "Finviz screener returned zero candidates. Check universe.candidate_screener_query and FINVIZ_API_KEY.");
        }

        return tickers;
    }

    /// <summary>
    /// For a saved plan's fixed stock list with daily eligibility rules: decides, for every session in the window, which
    /// stocks qualified using only completed earlier sessions (and that session's open when the rules use it). The stock
    /// list itself is unchanged; trades on a day a stock did not qualify are refused as universe_membership_missing.
    /// Publishes each stock's eligible days with the run results so every refusal can be checked. With a daily ranking the
    /// eligible days are each session's ranked members, and the ranked list with its values is published as well.
    /// </summary>
    private async Task<UniverseMembership> ResolveDailyEligibilityAsync(BacktestRunConfig run, UniverseConfig universe,
        CancellationToken cancellationToken, IProgress<BacktestProgress>? progress)
    {
        var (_, evaluationStart, windowEnd) = ResolveWindow(run.TimeWindow);
        var intraday = ExecutionReplayPolicy.IsIntradayCandle(run.Engine.ExecutionReplayPolicyId);
        universe.DailyEligibility!.RequireKnownBeforeEntry(!intraday);
        // The ranking is known at 09:35 New York; only the intraday replay checks each entry's time against that.
        if (universe.DailyRanking is not null && !intraday)
            throw new InvalidOperationException("universe.daily_ranking is known only at 09:35 New York and needs an intraday candle replay policy.");
        progress?.Report(BacktestProgress.StageOnly("resolving_universe",
            $"Checking each stock's daily eligibility from earlier sessions, {evaluationStart:yyyy-MM-dd} to {windowEnd:yyyy-MM-dd}."));
        var screener = new TradingFlow.Engine.Universe.HistoricalScreenerUniverseProvider(CreateProvider(run));
        var membership = await screener.ResolveMembershipAsync(new UniverseRequest(run.Tickers,
            universe with { Candidates = [] }, evaluationStart, ResolveDailyTimeframe(run), windowEnd), cancellationToken);
        var eligibleDays = run.Tickers.ToDictionary(ticker => ticker, ticker => membership.EligibleDays(ticker)
            .Select(day => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).ToArray(), StringComparer.OrdinalIgnoreCase);
        object payload = universe.DailyRanking is { } ranking
            ? new { runName = run.RunName, rule = universe.DailyEligibility, ranking, issuerIdentityBasis = "symbol",
                eligibleDays, rankedSessions = membership.SessionRankings.Select(PublishedSessionRanking).ToArray() }
            : new { runName = run.RunName, rule = universe.DailyEligibility, eligibleDays };
        await _artifactWriter.WriteTextAsync(Path.Combine(run.ResultsRoot, "universe", $"{run.RunName}-daily-eligibility.json"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                // Price basis is published as prior_close / session_open, the same spelling as the run settings.
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
            }), cancellationToken);
        progress?.Report(BacktestProgress.StageOnly("resolved_universe",
            $"Daily eligibility: {membership.Tickers.Count} of {run.Tickers.Count} stock(s) qualified on at least one day."));
        return membership;
    }

    /// <summary>
    /// For a research-screen plan: reads each session's members from the screen's published ranked list instead of
    /// recomputing them, after re-checking the manifest SHA-256 frozen in the run settings and every output fingerprint. Builds
    /// the same membership as the in-run ranking (members per session, ranked lists, known from 09:35 New York) and publishes
    /// each stock's member days with the manifest and ranked-list fingerprints.
    /// </summary>
    private async Task<UniverseMembership> ResolveRankedMembershipAsync(BacktestRunConfig run, RankedMembershipReference ranked,
        IReadOnlyList<StrategyDefinition> strategies, CancellationToken cancellationToken, IProgress<BacktestProgress>? progress)
    {
        var screen = TradingFlow.Engine.Universe.PublishedResearchScreen.Read(Path.GetDirectoryName(ranked.ManifestPath)!, ranked.ManifestSha256);
        RequireMembershipKnownBeforeEntry(run, screen);
        RequireSplitLookbackCoversStrategies(screen, strategies.Where(strategy =>
            ResolveRuntimeStrategy(strategy).AdmissionProfile.ExecutionTimingPolicy == ExecutionRules.KeptGapAsTradedTimingPolicy));
        var (_, evaluationStart, windowEnd) = ResolveWindow(run.TimeWindow);
        var membership = screen.ToMembership(NewYorkDate(evaluationStart), NewYorkDate(windowEnd.AddTicks(-1)));
        var eligibleDays = run.Tickers.ToDictionary(ticker => ticker, ticker => membership.EligibleDays(ticker)
            .Select(day => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).ToArray(), StringComparer.OrdinalIgnoreCase);
        var payload = new
        {
            runName = run.RunName, researchScreen = screen.Name, manifestSha256 = screen.ManifestSha256, rankedListSha256 = screen.RankedListSha256,
            rules = new { screen.Manifest.ScreenRules, screen.Manifest.RankingRules }, issuerIdentityBasis = screen.Manifest.IssuerIdentityBasis,
            exclusionsFile = "ranked-daily-exclusions.csv (in the research screen, identified by the manifest SHA-256)",
            eligibleDays, rankedSessions = membership.SessionRankings.Select(PublishedSessionRanking).ToArray()
        };
        await _artifactWriter.WriteTextAsync(Path.Combine(run.ResultsRoot, "universe", $"{run.RunName}-daily-eligibility.json"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        progress?.Report(BacktestProgress.StageOnly("resolved_universe",
            $"Ranked list {screen.Name}: {eligibleDays.Count(pair => pair.Value.Length > 0)} of {run.Tickers.Count} stock(s) selected on at least one session."));
        return membership;
    }

    /// <summary>
    /// A ranked list is known only at 09:35 New York, so it needs the intraday replay. An eligible list is known before the
    /// open, unless its price rule reads the session's own open, which a daily strategy filling at that open cannot wait for.
    /// </summary>
    private static void RequireMembershipKnownBeforeEntry(BacktestRunConfig run, TradingFlow.Engine.Universe.PublishedResearchScreen screen)
    {
        var intraday = ExecutionReplayPolicy.IsIntradayCandle(run.Engine.ExecutionReplayPolicyId);
        if (screen.IsRanked && !intraday)
            throw new InvalidOperationException("universe.ranked_membership is known only at 09:35 New York and needs an intraday candle replay policy.");
        if (!screen.IsRanked && !intraday && screen.Manifest.ScreenRules.PriceBasis == "session_open")
            throw new InvalidOperationException($"Research screen '{screen.Name}' compares each session's own open, which a daily strategy filling at " +
                "that open cannot know; publish the screen with --price-basis prior_close.");
    }

    /// <summary>
    /// Split ex-dates a research-screen daily run closes holdings before: read from the frozen corporate-actions dataset
    /// (providers.evidence_catalog.corporate_actions_dataset) when any strategy reads as-traded prices. A run that is not a
    /// research screen, or whose strategies read split-adjusted prices, needs none. Refuses an as-traded research-screen run
    /// whose provider cannot list splits, because a split during a hold would otherwise be booked as a price move.
    /// </summary>
    private async Task<IReadOnlySet<(string Symbol, DateOnly ExDate)>> ResearchScreenSplitExDatesAsync(BacktestRunConfig run,
        IReadOnlyList<StrategyDefinition> strategies, CancellationToken cancellationToken)
    {
        if (run.Universe?.RankedMembership is null || !strategies.Any(strategy =>
                ResolveRuntimeStrategy(strategy).AdmissionProfile.ExecutionTimingPolicy == ExecutionRules.KeptGapAsTradedTimingPolicy))
            return new HashSet<(string, DateOnly)>();
        var splits = marketDataProviderFactory?.Invoke(run) as IShareSplitProvider ??
            (run.Providers.EvidenceCatalog is { } catalog ? TradingFlow.Data.Evidence.EvidenceCatalogMarketDataProvider.Open(catalog,
                _rawArchiveWriter ?? new TradingFlow.Engine.Storage.FileSystemRawArchiveWriter(new TradingFlow.Engine.Storage.RawArchiveOptions(run.RawRoot))) : null);
        if (splits is null)
            throw new InvalidOperationException("An as-traded research-screen run needs a provider that lists splits " +
                "(providers.evidence_catalog with corporate_actions_dataset).");
        return (await splits.LoadShareSplitsAsync(cancellationToken))
            .SelectMany(pair => pair.Value.Select(split => (pair.Key.ToUpperInvariant(), split.ExDate))).ToHashSet();
    }

    /// <summary>
    /// An as-traded strategy reads unadjusted prices, so an eligible-list screen must have left out every stock-day whose
    /// look-back holds a split for at least as many sessions as the strategy's longest indicator reads; otherwise its SMA200
    /// would mix two share bases. Ranked screens serve intraday versions and are not checked here.
    /// </summary>
    internal static void RequireSplitLookbackCoversStrategies(TradingFlow.Engine.Universe.PublishedResearchScreen screen,
        IEnumerable<StrategyDefinition> asTradedStrategies)
    {
        if (screen.IsRanked) return;
        foreach (var strategy in asTradedStrategies)
        {
            var required = LongestIndicatorLookback(strategy);
            if ((screen.Manifest.SplitRule?.LookbackSessions ?? 0) < required)
                throw new InvalidOperationException($"Research screen '{screen.Name}' left out split stock-days for a look-back of " +
                    $"{screen.Manifest.SplitRule?.LookbackSessions?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no"} sessions; " +
                    $"{strategy.StrategyName} reads {required} sessions of as-traded prices. Publish the screen with --exclude-split-lookback {required}.");
        }
    }

    /// <summary>The longest number of daily sessions a strategy's entry indicators read: SMA200, the confluence EMA, or 20.</summary>
    internal static int LongestIndicatorLookback(StrategyDefinition strategy) => new[]
    {
        strategy.EntryRules.RequirePriceAboveSma200 || strategy.EntryRules.RequirePriceAboveSma200Daily ? 200 : 0,
        strategy.Confluence.Enabled ? strategy.Confluence.EmaPeriod : 0,
        20
    }.Max();

    /// <summary>The New York calendar date of an instant.</summary>
    private static DateOnly NewYorkDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime);

    // One session of the published ranked list: members with rank, measured values and how many baseline sessions were
    // proven zero-volume, the count that met the minimum, and each excluded stock's reason.
    private static object PublishedSessionRanking(TradingFlow.Engine.Universe.SessionRanking session) => new
    {
        session = session.Session.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        qualified = session.QualifiedCount,
        members = session.Members.Select(member => new
        {
            member.Rank, symbol = member.Security.Symbol, issuerId = member.Security.IssuerId,
            member.OpeningVolume, member.BaselineVolume, member.RelativeVolume, member.ProvenZeroBaselineSessions
        }).ToArray(),
        rejections = session.Rejections.Select(rejection => new { symbol = rejection.Symbol, reason = rejection.Reason }).ToArray()
    };

    private async Task PersistUniverseSnapshotAsync(
        BacktestRunConfig run,
        UniverseResolution resolution,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(run.ResultsRoot, "universe");
        var path = Path.Combine(directory, $"{resolution.AsOfDate:yyyyMMdd}-{run.RunName}.json");
        var payload = new
        {
            runName = run.RunName,
            asOfDate = resolution.AsOfDate.ToString("yyyy-MM-dd"),
            source = resolution.Source,
            rule = run.Universe,
            selected = resolution.Selected,
            rejections = resolution.Rejections
        };
        await _artifactWriter.WriteTextAsync(
            path,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
    }
}
