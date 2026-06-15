using TradingFlow.Backtesting;
using TradingFlow.Backtesting.Optimization;
using TradingFlow.Domain.Optimization;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Tests;

public class StrategyOptimizerTests
{
    [Fact]
    public void ApplyParameter_MapsIntradaySelectionFields()
    {
        var optimizer = new StrategyOptimizer(new SimpleYamlReader(), new BacktestRunner(new SimpleYamlReader()));
        var method = typeof(StrategyOptimizer).GetMethod(
            "ApplyParameter",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(
            FindRepositoryRoot(),
            "configs",
            "strategies",
            "intraday-ross-vwap-ema-cumulative-volume.v6-lite.yaml"));

        strategy = (TradingFlow.Domain.Strategies.StrategyDefinition)method.Invoke(
            optimizer,
            [strategy, "entry_rules.min_session_relative_volume", 0.25m])!;
        strategy = (TradingFlow.Domain.Strategies.StrategyDefinition)method.Invoke(
            optimizer,
            [strategy, "entry_rules.min_session_gain_pct", 1.5m])!;
        strategy = (TradingFlow.Domain.Strategies.StrategyDefinition)method.Invoke(
            optimizer,
            [strategy, "entry_rules.max_pre_entry_session_range_pct", 8.0m])!;
        strategy = (TradingFlow.Domain.Strategies.StrategyDefinition)method.Invoke(
            optimizer,
            [strategy, "entry_rules.max_entry_pullback_from_session_high_pct", 3.0m])!;
        strategy = (TradingFlow.Domain.Strategies.StrategyDefinition)method.Invoke(
            optimizer,
            [strategy, "entry_rules.volume_sma_period", 4])!;
        strategy = (TradingFlow.Domain.Strategies.StrategyDefinition)method.Invoke(
            optimizer,
            [strategy, "entry_rules.volume_sma_rising_lookback_bars", 2])!;
        strategy = (TradingFlow.Domain.Strategies.StrategyDefinition)method.Invoke(
            optimizer,
            [strategy, "entry_rules.min_volume_sma_rise_pct", 15.0m])!;

        Assert.Equal(0.25m, strategy.EntryRules.MinSessionRelativeVolume);
        Assert.Equal(1.5m, strategy.EntryRules.MinSessionGainPct);
        Assert.Equal(8.0m, strategy.EntryRules.MaxPreEntrySessionRangePct);
        Assert.Equal(3.0m, strategy.EntryRules.MaxEntryPullbackFromSessionHighPct);
        Assert.Equal(4, strategy.EntryRules.VolumeSmaPeriod);
        Assert.Equal(2, strategy.EntryRules.VolumeSmaRisingLookbackBars);
        Assert.Equal(15.0m, strategy.EntryRules.MinVolumeSmaRisePct);
    }

    [Fact]
    public async Task OptimizeAsync_ForwardsInnerBacktestProgress()
    {
        var root = Path.Combine(Path.GetTempPath(), "trading-flow-tests", Guid.NewGuid().ToString("N"));
        var configRoot = Path.Combine(root, "configs");
        var dataRoot = Path.Combine(root, "data");
        var resultRoot = Path.Combine(root, "results");
        Directory.CreateDirectory(configRoot);
        Directory.CreateDirectory(Path.Combine(dataRoot, "TST"));

        var strategyPath = Path.Combine(configRoot, "strategy.yaml");
        await File.WriteAllTextAsync(strategyPath, """
strategy_id: test.progress
strategy_name: Progress Strategy
source: test
version: 1
timeframe: 15m
direction: long
entry_rules:
  min_volume_spike: 0.0
  min_entry_rsi: 0.0
  max_entry_rsi: 100.0
  trend_filter: none
  macd_filter: none
  require_price_above_bb_middle: false
  require_macd_histogram_positive: false
confluence:
  enabled: false
  timeframe: 15m
  ema_period: 10
  macd_filter: none
exit_rules:
  stop_atr_multiple: 2.0
  target_r_multiple: 2.0
  max_hold_hours: 6.0
  enable_atr_trailing_stop: false
  trailing_stop_atr_multiple: 2.0
  trailing_activation_r: 1.0
  exit_on_close_below_ema20: false
  exit_on_close_below_vwap: false
  exit_on_macd_histogram_negative: false
  min_hold_bars_before_technical_exit: 1
execution:
  timeframe: 15m
  slippage_bps: 0.0
session:
  exchange_timezone: America/New_York
  quiet_minutes_after_open: 0
  close_buffer_minutes: 0
  friday_close_buffer_minutes: 0
  is_continuous_market: true
""");

        var backtestPath = Path.Combine(configRoot, "backtest.yaml");
        await File.WriteAllTextAsync(backtestPath, $"""
run_name: progress-test
mode: backtest

engine:
  pipeline: tpl
  worker_count: 2
  bounded_capacity: 16
  indicator_warmup_bars: 1
  fail_fast: false

time_window:
  type: fixed
  lookback_days: 1
  start: 2026-01-05T14:30:00Z
  end: 2026-01-05T20:00:00Z

tickers:
  - TST

market_data:
  provider: csv
  download_timeframes:
    - 15m
  derive_from: 15m
  raw_root: {NormalizePath(dataRoot)}
  normalized_root: {NormalizePath(dataRoot)}
  results_root: {NormalizePath(resultRoot)}
  cache_policy: use_cache

providers:
  alpaca:
    data_feed: sip

portfolio:
  starting_capital: 100000
  risk_per_trade_pct: 1.0
  max_position_value_pct: 20.0
  max_concurrent_positions: 2
  max_open_trades_per_ticker: 1
  fixed_buy_fee: 0.0
  fixed_sell_fee: 0.0
  prevent_overlapping_ticker_positions: true

signal_source:
  type: internal_candles
  require_signature: false
  secret_env:
  dedupe_window_seconds: 300
  reject_stale_after_seconds: 0

validation:
  out_of_sample:
    enabled: false
    percent: 0.0
  walk_forward:
    enabled: false
    window_days: 1
    step_days: 1
  benchmark:
    enabled: true
    ticker: SPY
  data_quality:
    enabled: false
    max_duplicate_bars: 0
    max_invalid_ohlc_bars: 0
    max_zero_volume_pct: 100.0
  bias_risk:
    universe_source: test
    universe_as_of_date:
    price_adjustment_policy: csv

execution:
  router: simulated
  broker: none
  dry_run: true
  allow_live_orders: false
  order_type: bracket
  order_expiration: gtc
  entry_order_type: limit
  extended_hours: true

news:
  enabled: false

strategies:
  - strategy.yaml
""");

        var optimizationPath = Path.Combine(configRoot, "optimization.yaml");
        await File.WriteAllTextAsync(optimizationPath, """
run_name: progress-optimization
base_strategy: strategy.yaml
backtest_config: backtest.yaml
metric: TotalReturnPct
top_n_results: 1
parameters:
  exit_rules.target_r_multiple:
    - 2.0
    - 3.0
""");

        await File.WriteAllTextAsync(
            Path.Combine(dataRoot, "TST", "bars_15m.csv"),
            BuildCsvBars());

        var messages = new List<BacktestProgress>();
        var optimizationUpdates = new List<OptimizationProgress>();
        var optimizer = new StrategyOptimizer(new SimpleYamlReader(), new BacktestRunner(new SimpleYamlReader()));

        await optimizer.OptimizeAsync(
            optimizationPath,
            CancellationToken.None,
            new CapturingBacktestProgress(messages),
            new CapturingOptimizationProgress(optimizationUpdates));

        Assert.Contains(messages, update => update.Stage == "optimization_run");
        Assert.Contains(messages, update => update.Stage == "optimization_market_pipeline");
        Assert.Contains(messages, update => update.Stage == "optimization_running_strategy_matrix");
        Assert.Contains(messages, update =>
            update.Stage == "optimization_market_pipeline" &&
            update.Message.Contains("Prepared reusable market state", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(messages, update => update.Message.StartsWith("Permutation 1 of 2:", StringComparison.Ordinal));
        Assert.Contains(messages, update => update.Message.StartsWith("Permutation 2 of 2:", StringComparison.Ordinal));
        Assert.Contains(optimizationUpdates, update =>
            update.Kind == "started" &&
            update.CurrentPermutation == 1 &&
            update.ParameterValues["exit_rules.target_r_multiple"].Equals(2.0m));
        Assert.Equal(2, optimizationUpdates.Count(update => update.Kind == "started"));
        Assert.Equal(2, optimizationUpdates.Count(update => update.Kind == "completed" && update.CompletedRun is not null));
        Assert.All(
            optimizationUpdates.Where(update => update.Kind == "completed" && update.CompletedRun is not null),
            update =>
            {
                Assert.Null(update.CompletedRun!.BacktestResult.Validation.Benchmark.BenchmarkReturnPct);
                Assert.True(update.CompletedRun.BacktestResult.TradingDayCount > 0);
            });
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static string FindRepositoryRoot()
    {
        return TestRepository.FindRoot();
    }

    private static string BuildCsvBars()
    {
        var lines = new List<string> { "ticker,timestamp,open,high,low,close,volume" };
        var timestamp = DateTimeOffset.Parse("2026-01-05T14:30:00Z");
        for (var i = 0; i < 24; i++)
        {
            var open = 100m + i * 0.1m;
            var close = open + 0.05m;
            lines.Add($"TST,{timestamp.AddMinutes(i * 15):O},{open:F2},{open + 0.25m:F2},{open - 0.25m:F2},{close:F2},10000");
        }

        return String.Join(Environment.NewLine, lines);
    }

    private sealed class CapturingBacktestProgress(List<BacktestProgress> messages) : IProgress<BacktestProgress>
    {
        public void Report(BacktestProgress value)
        {
            messages.Add(value);
        }
    }

    private sealed class CapturingOptimizationProgress(List<OptimizationProgress> messages) : IProgress<OptimizationProgress>
    {
        public void Report(OptimizationProgress value)
        {
            messages.Add(value);
        }
    }
}
