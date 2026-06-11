using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Backtesting.StrategyEvaluation;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public class StrategyEvaluationServiceTests
{
    [Fact]
    public async Task EvaluateAsync_ReturnsExplainableDecision_ForMomentumStrategy()
    {
        var root = CreateTempDirectory();
        try
        {
            var configPath = Path.Combine(root, "paper.yaml");
            var strategyPath = Path.Combine(root, "momentum.yaml");
            await File.WriteAllTextAsync(configPath, CreateRunConfigYaml(strategyPath));
            await File.WriteAllTextAsync(strategyPath, CreateStrategyYaml());

            var yamlReader = new SimpleYamlReader();
            var service = new StrategyEvaluationService(
                yamlReader,
                new ProjectPaths(root),
                new AlpacaCredentialProvider(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
            var runConfig = yamlReader.ReadBacktestRun(configPath);

            var response = await service.EvaluateAsync(
                new StrategyEvaluationRequest(configPath, strategyPath, ["NVTS"], 10, null, null),
                new FakeMarketDataProvider(),
                runConfig,
                CancellationToken.None);

            var result = Assert.Single(response.Results);
            Assert.Equal("NVTS", result.Ticker);
            Assert.Equal("Rejected", result.Decision);
            Assert.StartsWith("confluence_price_below_ema50", result.Reason);
            Assert.NotNull(result.RelativeVolume);
            Assert.NotNull(result.Signal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static string CreateRunConfigYaml(string strategyPath)
    {
        return $$"""
run_name: api-test
mode: paper

engine:
  pipeline: tpl
  worker_count: 1
  bounded_capacity: 100
  indicator_warmup_bars: 50
  fail_fast: true

time_window:
  type: rolling
  lookback_days: 10
  start:
  end:

tickers:
  - NVTS

market_data:
  provider: alpaca
  download_timeframes:
    - 15m
  derive_from: 15m
  raw_root: data/paper/raw
  normalized_root: data/paper/normalized
  results_root: data/paper/results
  cache_policy: refresh

providers:
  alpaca:
    data_feed: sip

portfolio:
  starting_capital: 100000
  risk_per_trade_pct: 1.0
  max_position_value_pct: 20.0
  max_concurrent_positions: 5
  fixed_buy_fee: 0
  fixed_sell_fee: 0
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
    percent: 30.0
  walk_forward:
    enabled: false
    window_days: 10
    step_days: 5
  benchmark:
    enabled: false
    ticker: SPY
  data_quality:
    enabled: false
    max_duplicate_bars: 0
    max_invalid_ohlc_bars: 0
    max_zero_volume_pct: 10.0
  bias_risk:
    universe_source: static_config
    universe_as_of_date:
    price_adjustment_policy: alpaca_sip

execution:
  router: paper
  broker: none
  dry_run: true
  allow_live_orders: false
  order_type: bracket

news:
  enabled: false

strategies:
  - {{strategyPath.Replace('\\', '/')}}
""";
    }

    private static string CreateStrategyYaml()
    {
        return """
strategy_id: test.momentum
strategy_name: Test Momentum
source: test
version: 1
timeframe: 15m
direction: long
entry_rules:
  setup_type: momentum
  min_volume_spike: 0.0
  min_entry_rsi: 0.0
  max_entry_rsi: 100.0
  trend_filter: none
  macd_filter: none
  require_price_above_bb_middle: false
  require_macd_histogram_positive: false
confluence:
  enabled: true
  timeframe: 15m
  ema_period: 50
  macd_filter: none
exit_rules:
  stop_atr_multiple: 2.0
  target_r_multiple: 3.0
  max_hold_hours: 48.0
execution:
  timeframe: 15m
  slippage_bps: 0.0
session:
  exchange_timezone: America/New_York
  quiet_minutes_after_open: 0
  close_buffer_minutes: 0
  friday_close_buffer_minutes: 0
""";
    }

    private sealed class FakeMarketDataProvider : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var ticker in tickers)
            {
                foreach (var timeframe in timeframes)
                {
                    var timestamp = new DateTimeOffset(2026, 6, 1, 13, 30, 0, TimeSpan.Zero);
                    for (var day = 0; day < 8; day++)
                    {
                        for (var bar = 0; bar < 32; bar++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var sequence = day * 32 + bar;
                            var close = 100m - sequence * 0.05m;
                            yield return new OhlcvBar(
                                ticker,
                                timestamp.AddDays(day).AddMinutes(bar * 15),
                                timeframe,
                                close + 0.02m,
                                close + 0.2m,
                                close - 0.2m,
                                close,
                                100000m + bar);
                            await Task.Yield();
                        }
                    }
                }
            }
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "trading-flow-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
