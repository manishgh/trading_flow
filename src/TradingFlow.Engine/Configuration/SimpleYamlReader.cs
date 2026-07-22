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
                OptionalInt(map, "entry_rules.volatility_contraction_lookback_bars", 10),
                OptionalDecimal(map, "entry_rules.opening_range_break_buffer") ?? 0m,
                OptionalBool(map, "entry_rules.require_log_price_rising", false),
                OptionalBool(map, "entry_rules.require_log_volume_rising", false),
                OptionalInt(map, "entry_rules.log_price_lookback_bars", 12),
                OptionalInt(map, "entry_rules.log_volume_lookback_bars", 12),
                OptionalDecimal(map, "entry_rules.min_log_price_slope") ?? 0m,
                OptionalDecimal(map, "entry_rules.min_log_volume_slope") ?? 0m,
                OptionalDecimal(map, "entry_rules.min_log_price_r2"),
                OptionalDecimal(map, "entry_rules.min_log_volume_r2"),
                OptionalBool(map, "entry_rules.reject_falling_price_rising_volume", false),
                OptionalDecimal(map, "entry_rules.red_volume_max_price_slope") ?? -0.0001m,
                OptionalDecimal(map, "entry_rules.red_volume_min_volume_slope") ?? 0.003m,
                OptionalInt(map, "entry_rules.vwap_hold_bars", 2),
                OptionalInt(map, "entry_rules.max_entries_per_ticker_per_day", 0),
                OptionalDecimal(map, "entry_rules.min_close_location_value"),
                OptionalBool(map, "entry_rules.reject_weak_close_on_high_relative_volume", false),
                OptionalDecimal(map, "entry_rules.weak_close_max_location_value") ?? 0.40m,
                OptionalDecimal(map, "entry_rules.weak_close_min_relative_volume") ?? 1.0m,
                OptionalDecimal(map, "entry_rules.min_day_gain_pct"),
                OptionalDecimal(map, "entry_rules.min_session_gain_pct"),
                OptionalDecimal(map, "entry_rules.max_pre_entry_session_range_pct"),
                OptionalDecimal(map, "entry_rules.max_entry_pullback_from_session_high_pct"),
                OptionalBool(map, "entry_rules.require_positive_news", false),
                OptionalDecimal(map, "entry_rules.min_news_sentiment"),
                OptionalDecimal(map, "entry_rules.veto_news_sentiment_below"),
                OptionalDecimal(map, "entry_rules.max_news_age_hours") ?? 72m,
                OptionalDecimal(map, "entry_rules.min_catalyst_price_move_pct"),
                OptionalDecimal(map, "entry_rules.max_catalyst_price_move_pct"),
                OptionalBool(map, "entry_rules.enable_short", false),
                OptionalString(map, "entry_rules.short_setup_type", "catalyst_vwap_breakdown"),
                OptionalDecimal(map, "entry_rules.max_short_news_sentiment"),
                OptionalDecimal(map, "entry_rules.min_short_catalyst_drop_pct"),
                OptionalDecimal(map, "entry_rules.max_short_entry_rsi"),
                OptionalDecimal(map, "entry_rules.min_short_entry_rsi"),
                OptionalDecimal(map, "entry_rules.max_short_close_location_value"),
                OptionalBool(map, "entry_rules.require_price_below_vwap_for_short", true),
                OptionalBool(map, "entry_rules.require_macd_bearish_for_short", true),
                OptionalDecimal(map, "entry_rules.min_bull_flag_pole_move_pct"),
                OptionalInt(map, "entry_rules.bull_flag_pole_max_bars", 10),
                OptionalInt(map, "entry_rules.bull_flag_pullback_min_bars", 2),
                OptionalInt(map, "entry_rules.bull_flag_pullback_max_bars", 5),
                OptionalDecimal(map, "entry_rules.bull_flag_max_depth_pct_of_pole") ?? 50m,
                OptionalDecimal(map, "entry_rules.bull_flag_pullback_volume_ratio_max") ?? 0.70m,
                OptionalDecimal(map, "entry_rules.bull_flag_breakout_volume_ratio_min") ?? 1.50m,
                OptionalBool(map, "entry_rules.enable_premarket_filter", false),
                OptionalBool(map, "entry_rules.require_premarket_high_break", false),
                OptionalDecimal(map, "entry_rules.premarket_high_break_buffer_pct") ?? 0.25m,
                OptionalDecimal(map, "entry_rules.max_premarket_vwap_extension_pct"),
                OptionalDecimal(map, "entry_rules.max_premarket_run_pct"),
                OptionalDecimal(map, "entry_rules.max_opening_range_pct"),
                OptionalBool(map, "entry_rules.reject_opening_exhaustion", false),
                OptionalInt(map, "entry_rules.opening_exhaustion_minutes", 20),
                OptionalDecimal(map, "entry_rules.opening_exhaustion_max_day_gain_pct"),
                OptionalDecimal(map, "entry_rules.opening_exhaustion_max_session_range_pct"),
                OptionalDecimal(map, "entry_rules.opening_exhaustion_min_pullback_from_high_pct") ?? 0.50m,
                OptionalInt(map, "entry_rules.min_consecutive_closes_above_vwap", 0),
                OptionalBool(map, "entry_rules.enable_entry_bar_confirmation", false),
                OptionalDecimal(map, "entry_rules.min_entry_bar_close_location_value") ?? 0.55m,
                OptionalBool(map, "entry_rules.reject_entry_bar_close_location_below_minimum", false),
                OptionalBool(map, "entry_rules.reject_entry_bar_breaks_signal_midpoint", false),
                OptionalDecimal(map, "entry_rules.max_vwap_extension_pct_for_direct_entry"),
                OptionalDecimal(map, "entry_rules.extended_vwap_min_entry_bar_close_location_value"),
                OptionalDecimal(map, "entry_rules.max_bollinger_position_for_direct_entry"),
                OptionalDecimal(map, "entry_rules.extended_bollinger_min_entry_bar_close_location_value"),
                OptionalBool(map, "entry_rules.require_price_above_sma10", false),
                OptionalBool(map, "entry_rules.require_price_above_sma20", false),
                OptionalBool(map, "entry_rules.require_price_above_sma50", false),
                OptionalBool(map, "entry_rules.require_sma10_above_sma20", false),
                OptionalBool(map, "entry_rules.require_sma20_above_sma50", false),
                OptionalString(map, "entry_rules.anchored_vwap_mode", "none"),
                OptionalInt(map, "entry_rules.anchored_vwap_lookback_bars", 20),
                OptionalBool(map, "entry_rules.require_price_above_anchored_vwap", false),
                OptionalBool(map, "entry_rules.require_price_below_anchored_vwap_for_short", false),
                OptionalDecimal(map, "entry_rules.max_anchored_vwap_extension_atr"),
                OptionalString(map, "entry_rules.short_anchored_vwap_mode", "none"),
                OptionalInt(map, "entry_rules.short_anchored_vwap_lookback_bars", 20),
                OptionalBool(map, "entry_rules.require_price_below_short_anchored_vwap", false),
                OptionalDecimal(map, "entry_rules.max_short_anchored_vwap_extension_atr"),
                OptionalInt(map, "entry_rules.step_prior_move_lookback_bars", 60),
                OptionalInt(map, "entry_rules.step_consolidation_min_bars", 10),
                OptionalInt(map, "entry_rules.step_consolidation_max_bars", 40),
                OptionalDecimal(map, "entry_rules.min_step_prior_move_pct") ?? 30m,
                OptionalDecimal(map, "entry_rules.min_step_prior_decline_pct") ?? 20m,
                OptionalDecimal(map, "entry_rules.max_step_base_depth_pct") ?? 35m,
                OptionalDecimal(map, "entry_rules.max_step_base_volume_ratio"),
                OptionalDecimal(map, "entry_rules.min_step_breakout_volume_ratio"),
                OptionalBool(map, "entry_rules.require_step_higher_lows", false),
                OptionalBool(map, "entry_rules.require_step_lower_highs_for_short", false),
                OptionalBool(map, "entry_rules.require_step_bollinger_contraction", false),
                OptionalDecimal(map, "entry_rules.step_bollinger_width_ratio_max") ?? 0.85m,
                OptionalInt(map, "entry_rules.reclaim_lookback_bars", 20),
                OptionalDecimal(map, "entry_rules.min_reclaim_pullback_depth_pct") ?? 8m,
                OptionalDecimal(map, "entry_rules.max_reclaim_pullback_depth_pct") ?? 35m,
                OptionalBool(map, "entry_rules.require_reclaim_low_above_sma50", true),
                OptionalBool(map, "entry_rules.require_reclaim_close_above_sma10", true),
                OptionalBool(map, "entry_rules.require_reclaim_close_above_sma20", true),
                OptionalInt(map, "entry_rules.rollover_lookback_bars", 20),
                OptionalDecimal(map, "entry_rules.min_rollover_advance_pct") ?? 12m,
                OptionalDecimal(map, "entry_rules.min_rollover_drop_from_high_pct") ?? 3m,
                OptionalDecimal(map, "entry_rules.max_rollover_drop_from_high_pct") ?? 25m,
                OptionalBool(map, "entry_rules.require_rollover_close_below_sma10", true),
                OptionalBool(map, "entry_rules.require_rollover_close_below_sma20", true),
                OptionalBool(map, "entry_rules.require_rollover_close_below_sma50", false),
                OptionalInt(map, "entry_rules.rollover_consecutive_lower_close_bars", 1),
                OptionalBool(map, "entry_rules.require_rollover_close_below_prior_low", false),
                RequirePriceAboveEma10: OptionalBool(map, "entry_rules.require_price_above_ema10", false),
                RequireEma10AboveEma20: OptionalBool(map, "entry_rules.require_ema10_above_ema20", false),
                MinSessionRelativeVolume: OptionalDecimal(map, "entry_rules.min_session_relative_volume"),
                MinGapUpPct: OptionalDecimal(map, "entry_rules.min_gap_up_pct"),
                AnchorType: OptionalNullableString(map, "entry_rules.anchor_type"),
                AvwapProximityPct: OptionalDecimal(map, "entry_rules.avwap_proximity_pct"),
                RequirePullbackVolumeDryup: OptionalBool(map, "entry_rules.require_pullback_volume_dryup", false),
                RequirePriceAboveSma50Daily: OptionalBool(map, "entry_rules.require_price_above_sma50_daily", false),
                RequirePriceAboveSma200Daily: OptionalBool(map, "entry_rules.require_price_above_sma200_daily", false),
                RequirePriceAboveSma150: OptionalBool(map, "entry_rules.require_price_above_sma150", false),
                RequirePriceAboveSma200: OptionalBool(map, "entry_rules.require_price_above_sma200", false),
                RequireSma50AboveSma150: OptionalBool(map, "entry_rules.require_sma50_above_sma150", false),
                RequireSma150AboveSma200: OptionalBool(map, "entry_rules.require_sma150_above_sma200", false),
                MinPriceVs52WeekLowPct: OptionalDecimal(map, "entry_rules.min_price_vs_52_week_low_pct"),
                MaxPriceVs52WeekHighPct: OptionalDecimal(map, "entry_rules.max_price_vs_52_week_high_pct"),
                MinContractions: OptionalNullableInt(map, "entry_rules.min_contractions"),
                MaxContractions: OptionalNullableInt(map, "entry_rules.max_contractions"),
                RequireVolatilityHalvingLeftToRight: OptionalBool(map, "entry_rules.require_volatility_halving_left_to_right", false),
                RequireVolumeDryUpPreBreakout: OptionalBool(map, "entry_rules.require_volume_dry_up_pre_breakout", false),
                MinBreakoutVolumeRatio: OptionalDecimal(map, "entry_rules.min_breakout_volume_ratio"),
                RequirePriceAboveEma5_65m: OptionalBool(map, "entry_rules.require_price_above_ema5_65m", false),
                MinBounceVolumeRatio: OptionalDecimal(map, "entry_rules.min_bounce_volume_ratio"),
                RequirePriorFlushBelowVwapBars: OptionalNullableInt(map, "entry_rules.require_prior_flush_below_vwap_bars"),
                VwapReclaimMaxBarsSinceFlush: OptionalNullableInt(map, "entry_rules.vwap_reclaim_max_bars_since_flush"),
                MinReclaimVolumeRatio: OptionalDecimal(map, "entry_rules.min_reclaim_volume_ratio"),
                RequireVolumeSmaRising: OptionalBool(map, "entry_rules.require_volume_sma_rising", false),
                VolumeSmaPeriod: OptionalInt(map, "entry_rules.volume_sma_period", 5),
                VolumeSmaRisingLookbackBars: OptionalInt(map, "entry_rules.volume_sma_rising_lookback_bars", 3),
                MinVolumeSmaRisePct: OptionalDecimal(map, "entry_rules.min_volume_sma_rise_pct"),
                EnablePerTickerDailyLossGuard: OptionalBool(map, "risk_guards.per_ticker_daily.enabled", false),
                MaxPerTickerDailyFailedTrades: OptionalInt(map, "risk_guards.per_ticker_daily.max_failed_trades", 0),
                MaxPerTickerDailyLossR: OptionalDecimal(map, "risk_guards.per_ticker_daily.max_loss_r"),
                MaxPerTickerDailyLossPctOfAccount: OptionalDecimal(map, "risk_guards.per_ticker_daily.max_loss_pct_of_account"),
                MinVolumeSpikeSource: OptionalString(map, "entry_rules.min_volume_spike_source", "cumulative_same_time"),
                VolumeConfirmationMode: OptionalString(map, "entry_rules.volume_confirmation_mode", "hard_gate"),
                MinVolumeLiquidityFloor: OptionalDecimal(map, "entry_rules.min_volume_liquidity_floor"),
                MaxCatalystConfirmationBars: OptionalNullableInt(map, "entry_rules.max_catalyst_confirmation_bars"),
                GapVariantMinPct: OptionalDecimal(map, "entry_rules.gap_variant_min_pct"),
                MinAdx: OptionalDecimal(map, "entry_rules.min_adx"),
                RequireAdxRising: OptionalBool(map, "entry_rules.require_adx_rising", false),
                AdxRisingLookbackBars: OptionalInt(map, "entry_rules.adx_rising_lookback_bars", 3),
                RequireObvRising: OptionalBool(map, "entry_rules.require_obv_rising", false),
                ObvRisingLookbackBars: OptionalInt(map, "entry_rules.obv_rising_lookback_bars", 3),
                MinObvChange: OptionalDecimal(map, "entry_rules.min_obv_change"),
                MaxMacdHistogram: OptionalDecimal(map, "entry_rules.max_macd_histogram"),
                PriorEntryGainLookbackBars: OptionalInt(map, "entry_rules.prior_entry_gain_lookback_bars", 5),
                MaxPriorEntryGainPct: OptionalDecimal(map, "entry_rules.max_prior_entry_gain_pct"),
                RequirePriorInsideDay: OptionalBool(map, "entry_rules.require_prior_inside_day", false),
                RequirePriorNr7: OptionalBool(map, "entry_rules.require_prior_nr7", false),
                PriorCompressionMode: OptionalString(map, "entry_rules.prior_compression_mode", "none"),
                PriorNr7LookbackDays: OptionalInt(map, "entry_rules.prior_nr7_lookback_days", 7),
                MinVwapDistanceAtrForDivergence: OptionalDecimal(map, "entry_rules.min_vwap_distance_atr_for_divergence"),
                DivergenceLookbackBars: OptionalInt(map, "entry_rules.divergence_lookback_bars", 20),
                DivergenceStartHour: OptionalInt(map, "entry_rules.divergence_start_hour", 12),
                ReversionStretchLookbackBars: OptionalInt(map, "entry_rules.reversion_stretch_lookback_bars", 5),
                MinConsecutiveDownClosesForStretch: OptionalInt(map, "entry_rules.min_consecutive_down_closes_for_stretch", 0),
                MaxReversionRsi2: OptionalDecimal(map, "entry_rules.max_reversion_rsi2"),
                EnableLowerBollingerStretch: OptionalBool(map, "entry_rules.enable_lower_bollinger_stretch", false),
                VetoFreshNewsHours: OptionalDecimal(map, "entry_rules.veto_fresh_news_hours")),
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
                OptionalInt(map, "exit_rules.min_hold_bars_before_technical_exit", 1),
                OptionalBool(map, "exit_rules.exit_on_log_price_fade", false),
                OptionalInt(map, "exit_rules.exit_log_price_lookback_bars", 6),
                OptionalInt(map, "exit_rules.exit_log_volume_lookback_bars", 6),
                OptionalDecimal(map, "exit_rules.max_exit_log_price_slope") ?? -0.0001m,
                OptionalDecimal(map, "exit_rules.min_exit_log_volume_slope") ?? 0.003m,
                OptionalBool(map, "exit_rules.require_rising_volume_for_log_fade_exit", false),
                OptionalBool(map, "exit_rules.require_below_vwap_for_log_fade_exit", false),
                OptionalBool(map, "exit_rules.enable_confirmed_vwap_exit", false),
                OptionalInt(map, "exit_rules.confirmed_vwap_exit_bars", 2),
                OptionalDecimal(map, "exit_rules.confirmed_vwap_exit_atr_buffer") ?? 0m,
                OptionalDecimal(map, "exit_rules.disable_confirmed_vwap_exit_after_r"),
                OptionalBool(map, "exit_rules.exit_on_sma10_near_sma20", false),
                OptionalDecimal(map, "exit_rules.sma10_near_sma20_pct") ?? 0.25m,
                OptionalBool(map, "exit_rules.exit_on_sma10_cross_below_sma20", false),
                OptionalBool(map, "exit_rules.exit_short_on_sma10_cross_above_sma20", false),
                OptionalBool(map, "exit_rules.exit_on_ema10_cross_below_ema20", false),
                OptionalBool(map, "exit_rules.allow_same_bar_stop_target", true),
                OptionalString(map, "exit_rules.initial_stop_mode", "atr"),
                OptionalString(map, "exit_rules.profit_target_mode", "r_multiple"),
                OptionalBool(map, "exit_rules.enable_failed_breakout_circuit_breaker", false),
                OptionalInt(map, "exit_rules.failed_breakout_bars", 3),
                OptionalDecimal(map, "exit_rules.failed_breakout_min_r") ?? 0m,
                OptionalDecimal(map, "exit_rules.stop_tick_buffer") ?? 0.01m,
                OptionalBool(map, "exit_rules.require_confirmed_ema20_exit", false)),
            new ExecutionRules(
                OptionalString(map, "execution.timeframe", RequireString(map, "timeframe")),
                RequireDecimal(map, "execution.slippage_bps")),
            new SessionRules(
                RequireString(map, "session.exchange_timezone"),
                RequireInt(map, "session.quiet_minutes_after_open"),
                RequireInt(map, "session.close_buffer_minutes"),
                RequireInt(map, "session.friday_close_buffer_minutes"),
                OptionalBool(map, "session.is_continuous_market", false),
                OptionalBool(map, "session.use_extended_hours", false)),
            ReadRegimeRules(map));
    }

    private static RegimeRules? ReadRegimeRules(IReadOnlyDictionary<string, List<string>> map)
    {
        var benchmark = OptionalString(map, "regime.benchmark", string.Empty);
        if (string.IsNullOrWhiteSpace(benchmark))
        {
            return null;
        }

        return new RegimeRules(
            benchmark.Trim().ToUpperInvariant(),
            OptionalString(map, "regime.rule", RegimeRules.PriceAboveSma),
            OptionalInt(map, "regime.sma_period", 50));
    }

    public BacktestRunConfig ReadBacktestRun(string path)
    {
        var runMap = ReadKeyValueMap(path);
        var runDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ??
            throw new DirectoryNotFoundException($"Could not resolve directory for {path}.");
        var repositoryRoot = FindRepositoryRoot(runDirectory) ?? Environment.CurrentDirectory;

        return new BacktestRunConfig(
            RequireString(runMap, "run_name"),
            RequireString(runMap, "mode"),
            new EngineConfig(
                RequireString(runMap, "engine.pipeline"),
                RequireInt(runMap, "engine.worker_count"),
                RequireInt(runMap, "engine.bounded_capacity"),
                RequireInt(runMap, "engine.indicator_warmup_bars"),
                OptionalInt(runMap, "engine.ticker_timeout_seconds", 120),
                RequireBool(runMap, "engine.fail_fast")),
            new TimeWindowConfig(
                RequireString(runMap, "time_window.type"),
                RequireInt(runMap, "time_window.lookback_days"),
                OptionalDateTimeOffset(runMap, "time_window.start"),
                OptionalDateTimeOffset(runMap, "time_window.end"),
                OptionalInt(runMap, "time_window.warmup_lookback_days", 0)),
            OptionalList(runMap, "tickers"),
            RequireString(runMap, "market_data.provider"),
            RequireList(runMap, "market_data.download_timeframes"),
            ResolveRepositoryPath(repositoryRoot, RequireString(runMap, "market_data.raw_root")),
            ResolveRepositoryPath(repositoryRoot, RequireString(runMap, "market_data.normalized_root")),
            ResolveRepositoryPath(repositoryRoot, RequireString(runMap, "market_data.results_root")),
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
                new AlpacaProviderConfig(
                    OptionalString(runMap, "providers.alpaca.data_feed", "sip"))),
            new PortfolioConfig(
                RequireDecimal(runMap, "portfolio.starting_capital"),
                RequireDecimal(runMap, "portfolio.risk_per_trade_pct"),
                RequireDecimal(runMap, "portfolio.max_position_value_pct"),
                OptionalInt(runMap, "portfolio.max_concurrent_positions", 5),
                RequireDecimal(runMap, "portfolio.fixed_buy_fee"),
                RequireDecimal(runMap, "portfolio.fixed_sell_fee"),
                OptionalInt(runMap, "portfolio.max_open_trades_per_ticker", 1),
                RequireBool(runMap, "portfolio.prevent_overlapping_ticker_positions"),
                OptionalDecimal(runMap, "portfolio.max_bar_participation_pct") ?? 0m,
                OptionalDecimal(runMap, "portfolio.sec_fee_rate") ?? 0m,
                OptionalDecimal(runMap, "portfolio.finra_taf_per_share") ?? 0m,
                OptionalDecimal(runMap, "portfolio.finra_taf_cap") ?? 0m),
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
                OptionalBool(runMap, "execution.allow_extended_hours_trading", false)),
            new NewsConfig(
                OptionalBool(runMap, "news.enabled", false),
                OptionalString(runMap, "news.provider.name", "none"),
                OptionalInt(runMap, "news.veto.ttl_minutes", 60),
                OptionalDecimal(runMap, "news.veto.negative_threshold") ?? -0.5m,
                OptionalInt(runMap, "news.max_articles_per_ticker", 120),
                OptionalInt(runMap, "news.sentiment_timeout_seconds", 3)),
            new ScreenerConfig(
                OptionalBool(runMap, "screener.enabled", false),
                OptionalString(runMap, "screener.provider", "finviz"),
                OptionalList(runMap, "screener.filters")),
            new ArtifactRetentionConfig(
                OptionalString(runMap, "artifacts.retention_mode", "full")),
            RequireList(runMap, "strategies")
                .Select(strategyPath => ResolveConfigPath(runDirectory, strategyPath))
                .ToArray(),
            ReadUniverseConfig(runMap));
    }

    private static UniverseConfig? ReadUniverseConfig(IReadOnlyDictionary<string, List<string>> runMap)
    {
        var mode = OptionalString(runMap, "universe.mode", UniverseConfig.StaticMode);
        if (String.IsNullOrWhiteSpace(mode) ||
            mode.Equals(UniverseConfig.StaticMode, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new UniverseConfig(
            mode,
            OptionalList(runMap, "universe.candidates"),
            OptionalDecimal(runMap, "universe.min_price") ?? 0m,
            OptionalDecimal(runMap, "universe.min_avg_dollar_volume") ?? 0m,
            OptionalInt(runMap, "universe.lookback_days", 20),
            OptionalDecimal(runMap, "universe.min_prior_return_pct"),
            OptionalNullableInt(runMap, "universe.max_symbols"),
            OptionalString(runMap, "universe.candidate_source", UniverseConfig.StaticCandidateSource),
            OptionalString(runMap, "universe.candidate_screener_query", string.Empty) is { Length: > 0 } query
                ? query
                : null,
            OptionalString(runMap, "universe.rescreen_frequency", UniverseConfig.RescreenPerRun));
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

    private static int? OptionalNullableInt(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        return map.TryGetValue(key, out var values) && values.Count > 0 && !String.IsNullOrWhiteSpace(values[0])
            ? Int32.Parse(values[0], CultureInfo.InvariantCulture)
            : null;
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

    private static string? OptionalNullableString(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        return map.TryGetValue(key, out var values) && values.Count > 0 && !String.IsNullOrWhiteSpace(values[0])
            ? values[0]
            : null;
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

    private static string ResolveRepositoryPath(string repositoryRoot, string path)
    {
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(repositoryRoot, path));
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TradingFlow.sln")) ||
                Directory.Exists(Path.Combine(directory.FullName, "configs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

