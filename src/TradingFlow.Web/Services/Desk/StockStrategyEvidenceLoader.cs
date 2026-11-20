using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Web.Services.Desk;

public sealed record StockStrategyEvidence(TickerMarketState? Market, IReadOnlyList<CatalystEvent> News,
    StrategyEligibilityEvidence? Regime, IReadOnlyList<OhlcvBar>? ReferenceBars,
    IReadOnlyList<DateOnly>? ReferenceSessions, DrawdownDecisionContext? DrawdownContext, string? Missing,
    CatalystQueryResult? NewsCoverage = null, IReadOnlyList<OhlcvBar>? RegimeBars = null,
    // Nasdaq home-market rule: the stock's listing exchange (broker asset record) and the home-market fund's bars (QQQ).
    IReadOnlyList<OhlcvBar>? HomeMarketBars = null, string? ListingExchange = null,
    // Live OPRA 25-delta put-call IV skew; populated only for strategies that declare skew vetoes.
    decimal? OptionsSkew25Delta = null);
public sealed record StockStrategyEvidenceBatch(DateTimeOffset AsOfUtc,
    IReadOnlyDictionary<string, StockStrategyEvidence> Stocks,
    IReadOnlyDictionary<DateOnly, MarketSessionSchedule> Calendar);

public interface IStockStrategyEvidenceLoader
{
    Task<StockStrategyEvidenceBatch> LoadAsync(BacktestRunConfig profile, AuthorizedRuntimeStrategy strategy,
        IReadOnlyList<string> symbols, CancellationToken token);
}

/// <summary>Collects existing provider/pipeline evidence; strategy conditions remain entirely in the shared kernel.</summary>
public sealed class StockStrategyEvidenceLoader(PaperRuntimeFactory factory, ICandleStore candles, TimeProvider clock,
    IVixCloseSource? vixCloses = null, IOptionsSentimentSource? optionsSentiment = null,
    ILogger<StockStrategyEvidenceLoader>? logger = null)
    : IStockStrategyEvidenceLoader
{
    /// <summary>Uses the profile's bounded pipeline and collection timeout; classification receipts precede the decision cutoff.</summary>
    public async Task<StockStrategyEvidenceBatch> LoadAsync(BacktestRunConfig profile, AuthorizedRuntimeStrategy runtime,
        IReadOnlyList<string> symbols, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(profile.Engine.TickerTimeoutSeconds));
        var strategy = runtime.Definition;
        var end = clock.GetUtcNow();
        var lookback = profile.TimeWindow.WarmupLookbackDays > 0
            ? profile.TimeWindow.WarmupLookbackDays : Math.Max(profile.TimeWindow.LookbackDays, 260);
        var start = end.AddDays(-lookback);
        var required = StrategyDecisionRequestAssembler.ResolveRequiredTimeframes(strategy);
        var download = RunConfigWriter.ResolveDownloadTimeframes(profile, [strategy]);
        var provider = factory.CreateProvider(profile);
        var newsProvider = StrategyCapabilityInspector.UsesNews(strategy) ? factory.CreateNewsProvider(profile) : null;
        try
        {
            if (provider is not IMarketSessionScheduleProvider schedules)
                throw new InvalidOperationException("Current strategy assessment requires the provider's complete exchange calendar.");
            var calendar = await schedules.LoadMarketSessionSchedulesAsync(DateOnly.FromDateTime(start.UtcDateTime),
                DateOnly.FromDateTime(end.UtcDateTime).AddDays(1), deadline.Token);
            var market = await new CandlePipelineEngine(candles).RunAsync(new(symbols, download, required,
                profile.DerivedTimeframes.Source, start, end, profile.Engine.BoundedCapacity, profile.Engine.WorkerCount,
                profile.Execution.AllowExtendedHoursTrading, strategy.Session.ExchangeTimezone,
                new("strategy_assessment", profile.RunName, profile.Provider), profile.Engine.MaxRetainedReplayBars), provider, deadline.Token);
            var benchmarks = await LoadBenchmarksAsync(strategy, provider, download, start, end, deadline.Token);
            var needsBrokerEligibility = strategy.Regime is { IsActive: true, UsesNasdaqHomeMarket: true } ||
                strategy.Direction.Equals("short", StringComparison.OrdinalIgnoreCase);
            var eligibilities = needsBrokerEligibility
                ? await LoadEligibilitiesAsync(profile, symbols.Where(market.TickerStates.ContainsKey).ToArray(), deadline.Token)
                : null;
            var news = await LoadNewsAsync(strategy, symbols, newsProvider, clock.GetUtcNow(), deadline.Token);
            var vix = strategy.Regime is { IsActive: true, UsesVix: true }
                ? await VixRegime.TryLoadLatestAsync(vixCloses, deadline.Token)
                : null;
            var needsOptionsSkew = strategy.EntryRules.MaxLongPutSkew is not null ||
                strategy.EntryRules.MinShortPutCallSkew is not null;
            var optionsSkewBySymbol = needsOptionsSkew
                ? await LoadOptionsSkewAsync(symbols, market.TickerStates, strategy.Timeframe, deadline.Token)
                : null;
            var asOf = clock.GetUtcNow();
            var result = new Dictionary<string, StockStrategyEvidence>(StringComparer.Ordinal);
            foreach (var symbol in symbols)
            {
                market.TickerStates.TryGetValue(symbol, out var state);
                var missing = state is null ? market.Failures.GetValueOrDefault(symbol) ?? "No candles returned for this stock."
                    : MissingCurrentMarket(state, required, calendar, asOf, strategy.Session.ExchangeTimezone);
                var lookup = eligibilities is not null && eligibilities.TryGetValue(symbol, out var found)
                    ? found
                    : default;
                var listing = lookup.LookupFailed ? ListingLookupFailed : lookup.Eligibility?.Exchange;
                var regime = ResolveRegime(strategy, benchmarks, calendar, asOf, listing, vix, out var regimeMissing,
                    lookup.Eligibility, lookup.LookupFailed);
                missing ??= regimeMissing;
                var query = news.GetValueOrDefault(symbol);
                var factor = strategy.EntryRules.TechnicalFactor;
                var reference = factor is { Metric: TechnicalFactorRules.RelativeRoc }
                    ? benchmarks.GetValueOrDefault(factor.BenchmarkSymbol) : null;
                var sessions = factor is { Metric: TechnicalFactorRules.RelativeRoc }
                    ? TechnicalFactorCalculator.ExpectedCompletedSessions(calendar, asOf, factor.LookbackBars + 1) : null;
                var context = state is null ? null : await StrategyDecisionRequestAssembler.ResolveDrawdownContextAsync(
                    FrozenManifestDecisionContextProvider.FromConfig(profile.Engine), symbol, asOf,
                    state.BarsByTimeframe, "diagnostic_replay", deadline.Token);
                decimal? optionsSkew = optionsSkewBySymbol is not null && optionsSkewBySymbol.TryGetValue(symbol, out var skew)
                    ? skew
                    : null;
                result[symbol] = new(state, query?.Events ?? [], regime, reference, sessions, context, missing,
                    query, strategy.Regime is { IsActive: true } rule ? benchmarks.GetValueOrDefault(rule.BenchmarkSymbol) : null,
                    strategy.Regime is { UsesNasdaqHomeMarket: true } home ? benchmarks.GetValueOrDefault(home.NasdaqListingBenchmark!) : null, listing,
                    optionsSkew);
            }
            return new(asOf, result, calendar);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"Strategy data collection exceeded the profile's {profile.Engine.TickerTimeoutSeconds}-second timeout.");
        }
        finally
        {
            (newsProvider as IDisposable)?.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }

    // Loads live OPRA 25-delta put-call IV skew with bounded concurrency only when skew vetoes are configured; failures fail closed.
    private async Task<IReadOnlyDictionary<string, decimal?>> LoadOptionsSkewAsync(
        IReadOnlyList<string> symbols,
        IReadOnlyDictionary<string, TickerMarketState> tickerStates,
        string strategyTimeframe,
        CancellationToken token)
    {
        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, decimal?>(StringComparer.Ordinal);
        if (optionsSentiment is null || symbols.Count == 0)
        {
            return results;
        }

        await Parallel.ForEachAsync(
            symbols,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
            async (symbol, ct) =>
            {
                decimal? spotHint = null;
                if (tickerStates.TryGetValue(symbol, out var state) &&
                    state.BarsByTimeframe.TryGetValue(strategyTimeframe, out var bars) &&
                    bars.Count > 0)
                {
                    spotHint = bars[^1].Close;
                }

                try
                {
                    var snapshot = await optionsSentiment.GetSnapshotAsync(symbol, spotHint, cancellationToken: ct);
                    results[symbol] = snapshot.Skew25Delta;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogWarning(ex, "OPRA options skew lookup failed for {Symbol}; strategy skew gate will fail closed.", symbol);
                    results[symbol] = null;
                }
            });

        return results;
    }

    /// <summary>Loads each actual regime/relative-strength benchmark once, preserving absent history as absent.</summary>
    private static async Task<Dictionary<string, IReadOnlyList<OhlcvBar>>> LoadBenchmarksAsync(StrategyDefinition strategy,
        IMarketDataProvider provider, IReadOnlyList<string> intervals, DateTimeOffset start, DateTimeOffset end, CancellationToken token)
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (strategy.Regime is { IsActive: true } regime)
        {
            symbols.Add(regime.BenchmarkSymbol);
            if (regime.UsesNasdaqHomeMarket) symbols.Add(regime.NasdaqListingBenchmark!);
        }
        if (strategy.EntryRules.TechnicalFactor is { Metric: TechnicalFactorRules.RelativeRoc } factor) symbols.Add(factor.BenchmarkSymbol);
        var result = new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase);
        var reader = new RegimeGateService();
        foreach (var symbol in symbols)
            result[symbol] = await reader.LoadBenchmarkDailyBarsAsync(provider, symbol, intervals, start, end, token);
        return result;
    }

    /// <summary>Queries only the strategy's news window and retains complete-empty versus failed/truncated collection.</summary>
    internal static async Task<Dictionary<string, CatalystQueryResult>> LoadNewsAsync(StrategyDefinition strategy,
        IReadOnlyList<string> symbols, ICatalystProvider? provider, DateTimeOffset end, CancellationToken token)
    {
        var result = new Dictionary<string, CatalystQueryResult>(StringComparer.Ordinal);
        if (!StrategyCapabilityInspector.UsesNews(strategy)) return result;
        var start = end.AddHours(-(double)StrategyCapabilityInspector.RequiredNewsLookbackHours(strategy));
        if (provider is not ICatalystEvidenceProvider evidence)
        {
            var detail = provider is null ? "The profile has no enabled news provider; no HTTP query was made." :
                $"The configured {provider.ProviderName} news provider does not return strict coverage evidence; no strict HTTP query was made.";
            foreach (var symbol in symbols) result[symbol] = new([], false, detail, 0, start, end);
            return result;
        }
        foreach (var symbol in symbols)
            result[symbol] = await evidence.GetCatalystEvidenceAsync(symbol, start, end, token);
        return result;
    }

    /// <summary>
    /// Each symbol's broker asset eligibility (listing exchange for the Nasdaq home-market rule, plus Shortable &amp;
    /// EasyToBorrow for short strategies). A symbol the broker cannot describe is left null (failing closed); no broker
    /// means no eligibility records.
    /// </summary>
    private async Task<Dictionary<string, (AssetTradingEligibility? Eligibility, bool LookupFailed)>> LoadEligibilitiesAsync(
        BacktestRunConfig profile, IReadOnlyList<string> symbols, CancellationToken token)
    {
        var results = new Dictionary<string, (AssetTradingEligibility? Eligibility, bool LookupFailed)>(StringComparer.Ordinal);
        var now = clock.GetUtcNow();
        var uncached = new List<string>();
        foreach (var symbol in symbols)
        {
            if (eligibilityCache.TryGetValue(symbol, out var cached) && now - cached.ReadAtUtc < ListingCacheLifetime)
                results[symbol] = (cached.Eligibility, false);
            else
                uncached.Add(symbol);
        }
        if (uncached.Count == 0) return results;
        var broker = factory.CreateBrokerClient(profile);
        if (broker is null) return results;
        try
        {
            foreach (var symbol in uncached)
            {
                try
                {
                    var eligibility = await broker.GetEligibilityAsync(symbol, token);
                    if (eligibility is null) continue;
                    results[symbol] = (eligibility, false);
                    eligibilityCache[symbol] = (eligibility, now);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One failed lookup (rate limit, server error) marks only that stock; it fails closed while the
                    // remaining stocks are still assessed.
                    results[symbol] = (null, true);
                }
            }
        }
        finally
        {
            (broker as IDisposable)?.Dispose();
        }
        return results;
    }

    // Broker asset records kept for an hour across Desk refreshes so each refresh does not repeat one request per stock.
    // Only successful lookups are cached.
    private static readonly TimeSpan ListingCacheLifetime = TimeSpan.FromHours(1);
    // Marks a stock whose broker lookup failed in this refresh (never cached, never a real exchange code).
    private const string ListingLookupFailed = "lookup_failed";
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (AssetTradingEligibility Eligibility, DateTimeOffset ReadAtUtc)> eligibilityCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the Desk/Paper short-borrow rejection reason for <paramref name="strategy"/>, or null when the strategy
    /// is not short or the stock is confirmed active, tradable, shortable, and easy-to-borrow.
    /// </summary>
    internal static string? GetShortBorrowRejection(
        StrategyDefinition strategy,
        AssetTradingEligibility? eligibility,
        bool lookupFailed)
    {
        if (!strategy.Direction.Equals("short", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (lookupFailed || !ShortBorrowEligibility.IsShortableAndEasyToBorrow(eligibility))
        {
            return ShortBorrowEligibility.NotEasyToBorrowReason;
        }

        return null;
    }

    /// <summary>
    /// Calls the shared <see cref="RegimeGateService.Evaluate"/> helper only after sufficient observed benchmark bars are
    /// proved. Under the Nasdaq home-market rule a Nasdaq listing with missing home-market bars reports data missing (so
    /// the Desk surfaces missing data rather than a market-off refusal); otherwise SPY, Nasdaq/QQQ, VIX sizing, and
    /// live short easy-to-borrow checks use the exact same fail-closed decision rules as backtests and paper trading.
    /// </summary>
    internal static StrategyEligibilityEvidence? ResolveRegime(StrategyDefinition strategy,
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> benchmarks,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> calendar, DateTimeOffset asOf, string? listing, VixCloseSeries? vix,
        out string? missing, AssetTradingEligibility? brokerEligibility = null, bool eligibilityLookupFailed = false)
    {
        missing = null;
        var shortBorrowRejection = GetShortBorrowRejection(strategy, brokerEligibility, eligibilityLookupFailed);
        if (strategy.Regime is not { IsActive: true } rule)
        {
            return shortBorrowRejection is null
                ? new(true, "strategy_regime_not_required", "not-required", asOf, "{\"required\":false}")
                : new(false, "short_borrow_eligibility", $"{ShortBorrowEligibility.NotEasyToBorrowReason}:{asOf:O}", asOf,
                    System.Text.Json.JsonSerializer.Serialize(new { eligible = false, rejection = shortBorrowRejection }));
        }
        var bars = benchmarks.GetValueOrDefault(rule.BenchmarkSymbol) ?? [];
        missing = MissingBenchmark(rule.BenchmarkSymbol, bars, rule.RequiredCloses, calendar, asOf, strategy.Session.ExchangeTimezone);
        if (missing is not null) return null;
        var available = bars.Where(bar => bar.Timestamp.AddDays(1) <= asOf && (bar.KnownAtUtc is null || bar.KnownAtUtc <= asOf))
            .Select(bar => bar.Timestamp).Distinct().Count();

        var lookupFailed = listing == ListingLookupFailed || eligibilityLookupFailed;
        var exchange = lookupFailed ? null : listing;
        IReadOnlyList<OhlcvBar>? homeBars = null;
        if (rule.UsesNasdaqHomeMarket &&
            !lookupFailed &&
            !String.IsNullOrWhiteSpace(exchange) &&
            exchange.Equals(RegimeRules.NasdaqExchange, StringComparison.OrdinalIgnoreCase) &&
            RegimeCalendarBuilder.IsRegimeOnAt(VixRegime.FilterTradingDayBars(bars, calendar), rule.SmaPeriod, asOf, rule.SlopeLookbackBars, rule.IsBearishRule))
        {
            homeBars = benchmarks.GetValueOrDefault(rule.NasdaqListingBenchmark!) ?? [];
            missing = MissingBenchmark(rule.NasdaqListingBenchmark!, homeBars, rule.RequiredCloses, calendar, asOf, strategy.Session.ExchangeTimezone);
            if (missing is not null) return null;
        }

        var decision = RegimeGateService.Evaluate(rule, bars, asOf, exchange, lookupFailed, homeBars, vix, calendar);
        var rejection = decision.Rejection ?? shortBorrowRejection;
        var eligible = rejection is null;
        var evidenceJson = decision.Vix is null
            ? System.Text.Json.JsonSerializer.Serialize(new { eligible, rule, available, latest = bars.Max(bar => bar.Timestamp), listing, rejection })
            : System.Text.Json.JsonSerializer.Serialize(new { eligible, rule, available, latest = bars.Max(bar => bar.Timestamp), listing, rejection,
                vix = decision.Vix, decision.RiskScale });
        return new(eligible, "shared_regime_gate", $"{rule.EvidenceRule}:{asOf:O}", asOf, evidenceJson);
    }

    /// <summary>Checks the complete prepared evidence at the actual decision time after source refresh, without inventing newer coverage.</summary>
    public static string? MissingAtDecision(StrategyDefinition strategy, StockStrategyEvidence stock,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> calendar, DateTimeOffset cutoff)
    {
        if (stock.Market is null) return "Prepared stock candles are missing.";
        var missing = MissingCurrentMarket(stock.Market, StrategyDecisionRequestAssembler.ResolveRequiredTimeframes(strategy),
            calendar, cutoff, strategy.Session.ExchangeTimezone);
        if (missing is not null) return missing;
        if (strategy.Regime is { IsActive: true } regime)
            missing = MissingBenchmark(regime.BenchmarkSymbol, stock.RegimeBars, regime.RequiredCloses, calendar, cutoff, strategy.Session.ExchangeTimezone);
        // A Nasdaq-listed stock under the home-market rule also needs current home-market fund bars at the decision time.
        if (missing is null && strategy.Regime is { IsActive: true, UsesNasdaqHomeMarket: true } homeRule &&
            RegimeRules.NasdaqExchange.Equals(stock.ListingExchange, StringComparison.OrdinalIgnoreCase))
            missing = MissingBenchmark(homeRule.NasdaqListingBenchmark!, stock.HomeMarketBars, homeRule.RequiredCloses, calendar, cutoff,
                strategy.Session.ExchangeTimezone);
        if (missing is not null) return missing;
        if (strategy.EntryRules.TechnicalFactor is { Metric: TechnicalFactorRules.RelativeRoc } factor)
            missing = MissingBenchmark(factor.BenchmarkSymbol, stock.ReferenceBars, factor.LookbackBars + 1, calendar, cutoff, strategy.Session.ExchangeTimezone);
        return missing ?? MissingNewsCoverage(strategy, stock.NewsCoverage, stock.News, cutoff);
    }

    /// <summary>Preserves exact query bounds: an earlier complete window cannot prove that no fresh news arrived before a later decision.</summary>
    internal static string? MissingNewsCoverage(StrategyDefinition strategy, CatalystQueryResult? query,
        IReadOnlyList<CatalystEvent> news, DateTimeOffset cutoff)
    {
        if (!StrategyCapabilityInspector.UsesNews(strategy)) return null;
        var requiredStart = cutoff.AddHours(-(double)StrategyCapabilityInspector.RequiredNewsLookbackHours(strategy));
        var required = $"Required publication window {requiredStart:O} through {cutoff:O} UTC.";
        var latest = news.Where(item => (item.DecisionAvailableAt ?? item.ReceivedAt) <= cutoff)
            .OrderByDescending(item => item.Timestamp).FirstOrDefault();
        var latestText = latest is null ? "No returned article is known before this decision." :
            $"Latest known returned article: publication {latest.Timestamp:O}; available {latest.DecisionAvailableAt ?? latest.ReceivedAt:O} UTC.";
        if (query is null) return $"No completed news query is recorded for this strategy check. {required} {latestText}";
        var observed = $"Collection window {query.WindowStartUtc:O} through {query.WindowEndUtc:O} UTC; {query.PagesRead} page(s) read.";
        if (!query.Complete) return $"News query coverage is incomplete: {query.Detail} {required} {observed} {latestText}";
        if (query.WindowStartUtc >= query.WindowEndUtc || query.WindowEndUtc > cutoff)
            return $"News query bounds are invalid or extend beyond this decision. {required} {observed} {latestText}";
        if (query.WindowStartUtc > requiredStart)
            return $"News coverage does not prove {requiredStart:O} through {query.WindowStartUtc:O} UTC. {required} {observed} {latestText}";
        if (query.WindowEndUtc < cutoff)
            return $"News coverage does not prove {query.WindowEndUtc:O} through {cutoff:O} UTC after collection/source refresh. {required} {observed} {latestText}";
        return null;
    }

    /// <summary>Requires actual benchmark candles for the latest authoritative completed sessions, not merely enough older rows.</summary>
    internal static string? MissingBenchmark(string symbol, IReadOnlyList<OhlcvBar>? bars, int requiredBars,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> calendar, DateTimeOffset cutoff, string timezone)
    {
        if (bars is null || bars.Count == 0) return $"No completed daily benchmark candles were collected for {symbol}.";
        var state = new TickerMarketState(symbol, new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["1d"] = bars },
            new Dictionary<string, IReadOnlyList<IndicatorSnapshot>>());
        var freshness = MissingCurrentMarket(state, ["1d"], calendar, cutoff, timezone);
        if (freshness is not null) return $"Benchmark evidence is missing: {freshness}";
        IReadOnlyList<DateOnly> expected;
        try { expected = TechnicalFactorCalculator.ExpectedCompletedSessions(calendar, cutoff, requiredBars); }
        catch (InvalidDataException exception) { return $"{symbol} benchmark calendar: {exception.Message}"; }
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        var completed = bars.Where(bar => bar.Timestamp.AddDays(1) <= cutoff && (bar.KnownAtUtc is null || bar.KnownAtUtc <= cutoff))
            .GroupBy(bar => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(bar.Timestamp, zone).DateTime))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var missing = expected.Where(date => !completed.ContainsKey(date)).ToArray();
        if (missing.Length > 0) return $"{symbol} requires {requiredBars} latest completed daily benchmark sessions; missing {String.Join(", ", missing)}.";
        if (expected.Any(date => completed[date].Length != 1 || completed[date][0].Ticker != symbol ||
            completed[date][0].DataFeed != "sip" || completed[date][0].AdjustmentPolicy != "all"))
            return $"{symbol} benchmark candles have ambiguous revisions or do not match the required Alpaca SIP/all evidence.";
        return null;
    }

    /// <summary>Rejects stale series using actual exchange sessions and the shared resampler's bucket boundaries.</summary>
    public static string? MissingCurrentMarket(TickerMarketState market, IReadOnlyList<string> required,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> calendar, DateTimeOffset collectedThrough, string timezone)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        var profile = new MarketEvidenceProfile(MarketEvidenceProfile.ProductionVersion,
            MarketEvidenceProfile.DefaultLookbackSessions, MarketEvidenceProfile.DefaultMinimumValidSamples,
            timezone, calendar, allowStandardWeekdayFallback: false);
        var resampler = new BarResampler(profile);
        foreach (var timeframe in required)
        {
            var duration = TimeframeParser.Parse(timeframe);
            var bars = market.BarsByTimeframe.GetValueOrDefault(timeframe) ?? [];
            var latest = bars.Where(bar => bar.Timestamp.Add(duration) <= collectedThrough &&
                (bar.KnownAtUtc is null || bar.KnownAtUtc <= collectedThrough)).MaxBy(bar => bar.Timestamp);
            if (latest is null) return $"No completed {timeframe} candle for {market.Ticker}.";
            // Scan only authoritative sessions. A provider's empty response never proves that a stock had no trades.
            DateTimeOffset? expected = null;
            foreach (var session in calendar.Values.Where(item => item.IsTradingDay).OrderByDescending(item => item.TradeDate))
            {
                var open = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(session.TradeDate.ToDateTime(session.RegularOpen), zone));
                var close = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(session.TradeDate.ToDateTime(session.RegularClose), zone));
                if (open >= collectedThrough) continue;
                if (TimeframeParser.IsDailyOrHigher(timeframe))
                {
                    var bucket = resampler.GetBucketWindow(open, timeframe);
                    if (bucket.StartUtc.Add(duration) <= collectedThrough) { expected = bucket.StartUtc; break; }
                }
                else
                {
                    var through = collectedThrough < close ? collectedThrough : close;
                    var bucket = resampler.GetBucketWindow(through.AddTicks(-1), timeframe);
                    if (bucket.StartUtc.Add(duration) > collectedThrough)
                        bucket = resampler.GetBucketWindow(bucket.StartUtc.AddTicks(-1), timeframe);
                    if (bucket.StartUtc >= open) { expected = bucket.StartUtc; break; }
                }
            }
            if (expected is null) return $"The exchange calendar does not prove the latest completed {timeframe} session for {market.Ticker}.";
            if (latest.Timestamp < expected)
                return $"{market.Ticker} {timeframe} candles are stale: latest {latest.Timestamp:O}; expected a completed interval from {expected:O}.";
        }
        return null;
    }
}
