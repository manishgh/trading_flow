using System.Globalization;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Domain.Optimization;

namespace TradingFlow.Engine.Configuration;

public sealed class SimpleYamlReader
{
    public StrategyDefinition ReadStrategy(string path)
    {
        var map = ReadKeyValueMap(path);

        return new StrategyDefinition(
            RequireString(map, "strategy_id"),
            RequireString(map, "strategy_name"),
            RequireString(map, "source"),
            RequireInt(map, "version"),
            RequireString(map, "timeframe"),
            RequireString(map, "direction"),
            new EntryRules(
                OptionalString(map, "entry_rules.setup_type", "momentum"),
                RequireDecimal(map, "entry_rules.min_volume_spike"),
                RequireDecimal(map, "entry_rules.min_entry_rsi"),
                RequireDecimal(map, "entry_rules.max_entry_rsi"),
                RequireString(map, "entry_rules.trend_filter"),
                RequireString(map, "entry_rules.macd_filter"),
                RequireBool(map, "entry_rules.require_price_above_bb_middle"),
                RequireBool(map, "entry_rules.require_macd_histogram_positive"),
                OptionalBool(map, "entry_rules.require_price_above_vwap", false),
                OptionalBool(map, "entry_rules.require_price_above_ema20", false),
                OptionalBool(map, "entry_rules.require_price_above_ema50", false),
                OptionalBool(map, "entry_rules.require_ema20_above_ema50", false),
                OptionalDecimal(map, "entry_rules.max_vwap_extension_atr"),
                OptionalInt(map, "entry_rules.opening_range_minutes", 15),
                OptionalInt(map, "entry_rules.recent_high_lookback_bars", 20),
                OptionalInt(map, "entry_rules.volatility_contraction_lookback_bars", 10)),
            new ConfluenceRules(
                OptionalBool(map, "confluence.enabled", false),
                OptionalString(map, "confluence.timeframe", RequireString(map, "timeframe")),
                OptionalInt(map, "confluence.ema_period", 50),
                OptionalString(map, "confluence.macd_filter", "none")),
            new ExitRules(
                RequireDecimal(map, "exit_rules.stop_atr_multiple"),
                RequireDecimal(map, "exit_rules.target_r_multiple"),
                RequireDecimal(map, "exit_rules.max_hold_hours"),
                OptionalBool(map, "exit_rules.enable_atr_trailing_stop", false),
                OptionalDecimal(map, "exit_rules.trailing_stop_atr_multiple") ?? 2.0m,
                OptionalDecimal(map, "exit_rules.trailing_activation_r") ?? 1.0m,
                OptionalBool(map, "exit_rules.exit_on_close_below_ema20", false),
                OptionalBool(map, "exit_rules.exit_on_close_below_vwap", false),
                OptionalBool(map, "exit_rules.exit_on_macd_histogram_negative", false),
                OptionalInt(map, "exit_rules.min_hold_bars_before_technical_exit", 1)),
            new ExecutionRules(
                OptionalString(map, "execution.timeframe", RequireString(map, "timeframe")),
                RequireDecimal(map, "execution.slippage_bps")),
            new SessionRules(
                RequireString(map, "session.exchange_timezone"),
                RequireInt(map, "session.quiet_minutes_after_open"),
                RequireInt(map, "session.close_buffer_minutes"),
                RequireInt(map, "session.friday_close_buffer_minutes"),
                OptionalBool(map, "session.is_continuous_market", false)));
    }

    public BacktestRunConfig ReadBacktestRun(string path)
    {
        var runMap = ReadKeyValueMap(path);
        var runDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ??
            throw new DirectoryNotFoundException($"Could not resolve directory for {path}.");

        return new BacktestRunConfig(
            RequireString(runMap, "run_name"),
            RequireString(runMap, "mode"),
            new EngineConfig(
                RequireString(runMap, "engine.pipeline"),
                RequireInt(runMap, "engine.worker_count"),
                RequireInt(runMap, "engine.bounded_capacity"),
                RequireInt(runMap, "engine.indicator_warmup_bars"),
                RequireBool(runMap, "engine.fail_fast")),
            new TimeWindowConfig(
                RequireString(runMap, "time_window.type"),
                RequireInt(runMap, "time_window.lookback_days"),
                OptionalDateTimeOffset(runMap, "time_window.start"),
                OptionalDateTimeOffset(runMap, "time_window.end")),
            RequireList(runMap, "tickers"),
            RequireString(runMap, "market_data.provider"),
            RequireList(runMap, "market_data.download_timeframes"),
            RequireString(runMap, "market_data.raw_root"),
            RequireString(runMap, "market_data.normalized_root"),
            RequireString(runMap, "market_data.results_root"),
            RequireString(runMap, "market_data.cache_policy"),
            new DerivedTimeframeConfig(RequireString(runMap, "market_data.derive_from")),
            new ValidationConfig(
                new OutOfSampleConfig(
                    RequireBool(runMap, "validation.out_of_sample.enabled"),
                    RequireDecimal(runMap, "validation.out_of_sample.percent")),
                new WalkForwardConfig(
                    RequireBool(runMap, "validation.walk_forward.enabled"),
                    RequireInt(runMap, "validation.walk_forward.window_days"),
                    RequireInt(runMap, "validation.walk_forward.step_days")),
                new BenchmarkConfig(
                    RequireBool(runMap, "validation.benchmark.enabled"),
                    RequireString(runMap, "validation.benchmark.ticker")),
                new DataQualityConfig(
                    RequireBool(runMap, "validation.data_quality.enabled"),
                    RequireInt(runMap, "validation.data_quality.max_duplicate_bars"),
                    RequireInt(runMap, "validation.data_quality.max_invalid_ohlc_bars"),
                    RequireDecimal(runMap, "validation.data_quality.max_zero_volume_pct")),
                new BiasRiskConfig(
                    RequireString(runMap, "validation.bias_risk.universe_source"),
                    OptionalDateOnly(runMap, "validation.bias_risk.universe_as_of_date"),
                    RequireString(runMap, "validation.bias_risk.price_adjustment_policy"))),
            new ProviderConfig(
                new YahooProviderConfig(
                    RequireString(runMap, "providers.yahoo.api.base_url"),
                    new YahooHeaderConfig(
                        RequireString(runMap, "providers.yahoo.headers.user_agent"),
                        RequireString(runMap, "providers.yahoo.headers.accept"),
                        RequireString(runMap, "providers.yahoo.headers.accept_language")),
                    RequireInt(runMap, "providers.yahoo.request_timeout_seconds"),
                    RequireInt(runMap, "providers.yahoo.max_retries"),
                    RequireInt(runMap, "providers.yahoo.throttle_ms")),
                new TradingViewProviderConfig(false)),
            new PortfolioConfig(
                RequireDecimal(runMap, "portfolio.starting_capital"),
                RequireDecimal(runMap, "portfolio.risk_per_trade_pct"),
                RequireDecimal(runMap, "portfolio.max_position_value_pct"),
                OptionalInt(runMap, "portfolio.max_concurrent_positions", 5),
                RequireDecimal(runMap, "portfolio.fixed_buy_fee"),
                RequireDecimal(runMap, "portfolio.fixed_sell_fee"),
                OptionalInt(runMap, "portfolio.max_open_trades_per_ticker", 1),
                RequireBool(runMap, "portfolio.prevent_overlapping_ticker_positions")),
            new SignalSourceConfig(
                OptionalString(runMap, "signal_source.type", "internal_candles"),
                OptionalBool(runMap, "signal_source.require_signature", false),
                OptionalString(runMap, "signal_source.secret_env", String.Empty),
                OptionalInt(runMap, "signal_source.dedupe_window_seconds", 300),
                OptionalInt(runMap, "signal_source.reject_stale_after_seconds", 60)),
            new ExecutionConfig(
                RequireString(runMap, "execution.router"),
                RequireString(runMap, "execution.broker"),
                RequireBool(runMap, "execution.dry_run"),
                RequireBool(runMap, "execution.allow_live_orders"),
                RequireString(runMap, "execution.order_type"),
                OptionalString(runMap, "execution.order_expiration", "gtc"),
                OptionalString(runMap, "execution.entry_order_type", "limit"),
                OptionalBool(runMap, "execution.extended_hours", false)),
            new NewsConfig(
                OptionalBool(runMap, "news.enabled", false),
                OptionalString(runMap, "news.provider.name", "none"),
                OptionalInt(runMap, "news.veto.ttl_minutes", 60),
                OptionalDecimal(runMap, "news.veto.negative_threshold") ?? -0.5m),
            new ScreenerConfig(
                OptionalBool(runMap, "screener.enabled", false),
                OptionalString(runMap, "screener.provider", "finviz"),
                OptionalList(runMap, "screener.filters")),
            RequireList(runMap, "strategies")
                .Select(strategyPath => ResolveConfigPath(runDirectory, strategyPath))
                .ToArray());
    }

    public OptimizationConfig ReadOptimizationConfig(string path)
    {
        var map = ReadKeyValueMap(path);
        var runDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ??
            throw new DirectoryNotFoundException($"Could not resolve directory for {path}.");

        var parameters = new Dictionary<string, IReadOnlyList<object>>();
        var prefix = "parameters.";
        
        foreach (var kvp in map)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var paramKey = kvp.Key[prefix.Length..];
                var parsedValues = new List<object>();
                
                foreach (var val in kvp.Value)
                {
                    var cleanVal = Unquote(val);
                    if (String.IsNullOrWhiteSpace(cleanVal)) continue;

                    if (Boolean.TryParse(cleanVal, out var bVal))
                        parsedValues.Add(bVal);
                    else if (Int32.TryParse(cleanVal, CultureInfo.InvariantCulture, out var iVal))
                        parsedValues.Add(iVal);
                    else if (Decimal.TryParse(cleanVal, CultureInfo.InvariantCulture, out var dVal))
                        parsedValues.Add(dVal);
                    else
                        parsedValues.Add(cleanVal);
                }
                
                if (parsedValues.Count > 0)
                {
                    parameters[paramKey] = parsedValues;
                }
            }
        }

        return new OptimizationConfig(
            RequireString(map, "run_name"),
            ResolveConfigPath(runDirectory, RequireString(map, "base_strategy")),
            ResolveConfigPath(runDirectory, RequireString(map, "backtest_config")),
            OptionalString(map, "metric", "TotalReturnPct"),
            OptionalInt(map, "top_n_results", 10),
            parameters);
    }

    private static Dictionary<string, List<string>> ReadKeyValueMap(string path)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var sectionStack = new Stack<(int Indent, string Key)>();
        string? activeListKey = null;

        foreach (var rawLine in File.ReadLines(path))
        {
            var noComment = rawLine.Split('#', 2)[0];
            if (String.IsNullOrWhiteSpace(noComment))
            {
                continue;
            }

            var indent = noComment.Length - noComment.TrimStart().Length;
            var line = noComment.Trim();

            while (sectionStack.Count > 0 && indent <= sectionStack.Peek().Indent)
            {
                sectionStack.Pop();
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (activeListKey is null)
                {
                    throw new InvalidOperationException($"List item without list key in {path}: {line}");
                }

                result[activeListKey].Add(Unquote(line[2..].Trim()));
                continue;
            }

            var colonIndex = line.IndexOf(':', StringComparison.Ordinal);
            if (colonIndex < 0)
            {
                continue;
            }

            var key = line[..colonIndex].Trim();
            var value = line[(colonIndex + 1)..].Trim();
            var fullKey = sectionStack.Count == 0
                ? key
                : String.Join(".", sectionStack.Reverse().Select(x => x.Key).Append(key));

            if (String.IsNullOrEmpty(value))
            {
                sectionStack.Push((indent, key));
                activeListKey = fullKey;
                result[fullKey] = new List<string>();
            }
            else
            {
                activeListKey = null;
                result[fullKey] = new List<string> { Unquote(value) };
            }
        }

        return result;
    }

    private static string RequireString(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        if (!map.TryGetValue(key, out var values) || values.Count == 0)
        {
            throw new InvalidOperationException($"Missing YAML key: {key}");
        }

        return values[0];
    }

    private static IReadOnlyList<string> RequireList(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        if (!map.TryGetValue(key, out var values) || values.Count == 0)
        {
            throw new InvalidOperationException($"Missing YAML list: {key}");
        }

        return values;
    }

    private static IReadOnlyList<string> OptionalList(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        return map.TryGetValue(key, out var values)
            ? values.Where(value => !String.IsNullOrWhiteSpace(value)).ToArray()
            : Array.Empty<string>();
    }

    private static int RequireInt(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        return Int32.Parse(RequireString(map, key), CultureInfo.InvariantCulture);
    }

    private static int OptionalInt(IReadOnlyDictionary<string, List<string>> map, string key, int fallback)
    {
        return map.TryGetValue(key, out var values) && values.Count > 0 && !String.IsNullOrWhiteSpace(values[0])
            ? Int32.Parse(values[0], CultureInfo.InvariantCulture)
            : fallback;
    }

    private static decimal RequireDecimal(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        return Decimal.Parse(RequireString(map, key), CultureInfo.InvariantCulture);
    }

    private static decimal? OptionalDecimal(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        return map.TryGetValue(key, out var values) && values.Count > 0 && !String.IsNullOrWhiteSpace(values[0])
            ? Decimal.Parse(values[0], CultureInfo.InvariantCulture)
            : null;
    }

    private static bool RequireBool(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        return Boolean.Parse(RequireString(map, key));
    }

    private static bool OptionalBool(IReadOnlyDictionary<string, List<string>> map, string key, bool fallback)
    {
        return map.TryGetValue(key, out var values) && values.Count > 0 && !String.IsNullOrWhiteSpace(values[0])
            ? Boolean.Parse(values[0])
            : fallback;
    }

    private static string OptionalString(IReadOnlyDictionary<string, List<string>> map, string key, string fallback)
    {
        return map.TryGetValue(key, out var values) && values.Count > 0 && !String.IsNullOrWhiteSpace(values[0])
            ? values[0]
            : fallback;
    }

    private static DateOnly? OptionalDateOnly(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        if (!map.TryGetValue(key, out var values) || values.Count == 0 || String.IsNullOrWhiteSpace(values[0]))
        {
            return null;
        }

        return DateOnly.Parse(values[0], CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? OptionalDateTimeOffset(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        if (!map.TryGetValue(key, out var values) || values.Count == 0 || String.IsNullOrWhiteSpace(values[0]))
        {
            return null;
        }

        return DateTimeOffset.Parse(values[0], CultureInfo.InvariantCulture);
    }

    private static string Unquote(string value)
    {
        return value.Trim().Trim('"', '\'');
    }

    private static string ResolveConfigPath(string baseDirectory, string path)
    {
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(baseDirectory, path));
    }
}
