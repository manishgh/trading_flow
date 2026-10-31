using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Web.Services.Desk;

public sealed record StockStrategyEvidence(TickerMarketState? Market, IReadOnlyList<CatalystEvent> News,
    StrategyEligibilityEvidence? Regime, IReadOnlyList<OhlcvBar>? ReferenceBars,
    IReadOnlyList<DateOnly>? ReferenceSessions, DrawdownDecisionContext? DrawdownContext, string? Missing,
    CatalystQueryResult? NewsCoverage = null, IReadOnlyList<OhlcvBar>? RegimeBars = null);
public sealed record StockStrategyEvidenceBatch(DateTimeOffset AsOfUtc,
    IReadOnlyDictionary<string, StockStrategyEvidence> Stocks,
    IReadOnlyDictionary<DateOnly, MarketSessionSchedule> Calendar);

public interface IStockStrategyEvidenceLoader
{
    Task<StockStrategyEvidenceBatch> LoadAsync(BacktestRunConfig profile, AuthorizedRuntimeStrategy strategy,
        IReadOnlyList<string> symbols, CancellationToken token);
}

/// <summary>Collects existing provider/pipeline evidence; strategy conditions remain entirely in the shared kernel.</summary>
public sealed class StockStrategyEvidenceLoader(PaperRuntimeFactory factory, ICandleStore candles, TimeProvider clock)
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
            var news = await LoadNewsAsync(strategy, symbols, newsProvider, clock.GetUtcNow(), deadline.Token);
            var asOf = clock.GetUtcNow();
            var result = new Dictionary<string, StockStrategyEvidence>(StringComparer.Ordinal);
            foreach (var symbol in symbols)
            {
                market.TickerStates.TryGetValue(symbol, out var state);
                var missing = state is null ? market.Failures.GetValueOrDefault(symbol) ?? "No candles returned for this stock."
                    : MissingCurrentMarket(state, required, calendar, asOf, strategy.Session.ExchangeTimezone);
                var regime = ResolveRegime(strategy, benchmarks, calendar, asOf, out var regimeMissing);
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
                result[symbol] = new(state, query?.Events ?? [], regime, reference, sessions, context, missing,
                    query, strategy.Regime is { IsActive: true } rule ? benchmarks.GetValueOrDefault(rule.BenchmarkSymbol) : null);
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

    /// <summary>Loads each actual regime/relative-strength benchmark once, preserving absent history as absent.</summary>
    private static async Task<Dictionary<string, IReadOnlyList<OhlcvBar>>> LoadBenchmarksAsync(StrategyDefinition strategy,
        IMarketDataProvider provider, IReadOnlyList<string> intervals, DateTimeOffset start, DateTimeOffset end, CancellationToken token)
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (strategy.Regime is { IsActive: true } regime) symbols.Add(regime.BenchmarkSymbol);
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

    /// <summary>Calls the same regime calculator only after sufficient observed benchmark bars are proved.</summary>
    private static StrategyEligibilityEvidence? ResolveRegime(StrategyDefinition strategy,
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> benchmarks,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> calendar, DateTimeOffset asOf, out string? missing)
    {
        missing = null;
        if (strategy.Regime is not { IsActive: true } rule)
            return new(true, "strategy_regime_not_required", "not-required", asOf, "{\"required\":false}");
        var bars = benchmarks.GetValueOrDefault(rule.BenchmarkSymbol) ?? [];
        missing = MissingBenchmark(rule.BenchmarkSymbol, bars, rule.SmaPeriod, calendar, asOf, strategy.Session.ExchangeTimezone);
        if (missing is not null) return null;
        var available = bars.Where(bar => bar.Timestamp.AddDays(1) <= asOf && (bar.KnownAtUtc is null || bar.KnownAtUtc <= asOf))
            .Select(bar => bar.Timestamp).Distinct().Count();
        var eligible = RegimeCalendarBuilder.IsRegimeOnAt(bars, rule.SmaPeriod, asOf);
        return new(eligible, "shared_regime_gate", $"{rule.BenchmarkSymbol}:{rule.SmaPeriod}:{asOf:O}", asOf,
            System.Text.Json.JsonSerializer.Serialize(new { eligible, rule, available, latest = bars.Max(bar => bar.Timestamp) }));
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
            missing = MissingBenchmark(regime.BenchmarkSymbol, stock.RegimeBars, regime.SmaPeriod, calendar, cutoff, strategy.Session.ExchangeTimezone);
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
