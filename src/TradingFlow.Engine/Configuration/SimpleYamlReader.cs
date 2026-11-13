using System.Globalization;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Domain.Optimization;

namespace TradingFlow.Engine.Configuration;

public sealed class SimpleYamlReader
{
    // Resolve typed rules, including fixed Research diagnostic treatments, and reject unknown YAML keys.
    public StrategyDefinition ReadStrategy(string path)
    {
        var map = new TrackingConfigurationMap(ReadKeyValueMap(path));

        var strategy = new StrategyDefinition(
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
                OptionalInt(map, "entry_rules.recent_high_lookback_bars", 20),
                OptionalInt(map, "entry_rules.volatility_contraction_lookback_bars", 10),
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
                OptionalDecimal(map, "entry_rules.min_close_location_value"),
                OptionalBool(map, "entry_rules.reject_weak_close_on_high_relative_volume", false),
                OptionalDecimal(map, "entry_rules.weak_close_max_location_value") ?? 0.40m,
                OptionalDecimal(map, "entry_rules.weak_close_min_relative_volume") ?? 1.0m,
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
                OptionalBool(map, "entry_rules.enable_entry_bar_confirmation", false),
                OptionalDecimal(map, "entry_rules.min_entry_bar_close_location_value") ?? 0.55m,
                OptionalBool(map, "entry_rules.reject_entry_bar_close_location_below_minimum", false),
                OptionalBool(map, "entry_rules.reject_entry_bar_breaks_signal_midpoint", false),
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
                RequireVolumeSmaRising: OptionalBool(map, "entry_rules.require_volume_sma_rising", false),
                VolumeSmaPeriod: OptionalInt(map, "entry_rules.volume_sma_period", 5),
                VolumeSmaRisingLookbackBars: OptionalInt(map, "entry_rules.volume_sma_rising_lookback_bars", 3),
                MinVolumeSmaRisePct: OptionalDecimal(map, "entry_rules.min_volume_sma_rise_pct"),
                MinVolumeSpikeSource: RelativeVolumeMeasureParser.Parse(
                    OptionalString(map, "entry_rules.min_volume_spike_source", "cumulative_same_time")),
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
                ReversionStretchLookbackBars: OptionalInt(map, "entry_rules.reversion_stretch_lookback_bars", 5),
                MinConsecutiveDownClosesForStretch: OptionalInt(map, "entry_rules.min_consecutive_down_closes_for_stretch", 0),
                MaxReversionRsi2: OptionalDecimal(map, "entry_rules.max_reversion_rsi2"),
                EnableLowerBollingerStretch: OptionalBool(map, "entry_rules.enable_lower_bollinger_stretch", false),
                VetoFreshNewsHours: OptionalDecimal(map, "entry_rules.veto_fresh_news_hours"),
                VcpPivotStrengthBars: OptionalInt(map, "entry_rules.vcp_pivot_strength_bars", 2),
                VcpMaximumDepthRatioToPrevious: OptionalDecimal(map, "entry_rules.vcp_maximum_depth_ratio_to_previous") ?? 0.90m,
                VcpMinimumLowRisePct: OptionalDecimal(map, "entry_rules.vcp_minimum_low_rise_pct") ?? 0m,
                VcpMaximumContractionToAdvanceVolumeRatio: OptionalDecimal(map, "entry_rules.vcp_maximum_contraction_to_advance_volume_ratio") ?? 0.70m,
                VcpRequireProgressiveContractionVolume: OptionalBool(map, "entry_rules.vcp_require_progressive_contraction_volume", true),
                VcpMaximumVolumeRatioToPreviousContraction: OptionalDecimal(map, "entry_rules.vcp_maximum_volume_ratio_to_previous_contraction") ?? 1m,
                VcpMinimumVolumeReferenceBars: OptionalInt(map, "entry_rules.vcp_minimum_volume_reference_bars", 1),
                VcpBreakoutBufferPct: OptionalDecimal(map, "entry_rules.vcp_breakout_buffer_pct") ?? 0m,
                PortfolioRankMode: OptionalString(map, "entry_rules.portfolio_rank_mode", "none"),
                Sma200RisingLookbackBars: OptionalInt(map, "entry_rules.sma200_rising_lookback_bars", 0),
                MinBreakoutVolumeVs50DayAverage: OptionalDecimal(map, "entry_rules.min_breakout_volume_vs_50_day_average"),
                TechnicalFactor: ReadTechnicalFactor(map),
                RequireOrderedReclaimPullback: OptionalBool(map, "entry_rules.require_ordered_reclaim_pullback", false),
                DrawdownQualification: ReadDrawdownQualification(map),
                HistoricalNewsDiagnostic: ReadHistoricalNewsDiagnostic(map),
                OpeningRangeBreakout: ReadOpeningRangeBreakout(map),
                MinCloseBelowSmaPct: OptionalDecimal(map, "entry_rules.min_close_below_sma_pct"),
                CloseBelowSmaPeriod: OptionalNullableInt(map, "entry_rules.close_below_sma_period"),
                MinShortReversionRsi2: OptionalDecimal(map, "entry_rules.min_short_reversion_rsi2"),
                RequirePriceBelowSma200ForShort: OptionalBool(map, "entry_rules.require_price_below_sma200_for_short", false),
                MinCloseAboveSmaPct: OptionalDecimal(map, "entry_rules.min_close_above_sma_pct"),
                CloseAboveSmaPeriod: OptionalNullableInt(map, "entry_rules.close_above_sma_period"),
                DecisionSchedule: OptionalNullableString(map, "entry_rules.decision_schedule")),
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
                ReadInitialStopMode(map),
                OptionalString(map, "exit_rules.profit_target_mode", "r_multiple"),
                OptionalDecimal(map, "exit_rules.stop_tick_buffer") ?? 0.01m,
                OptionalBool(map, "exit_rules.require_confirmed_ema20_exit", false),
                OptionalNullableInt(map, "exit_rules.max_hold_bars"),
                OptionalDecimal(map, "exit_rules.exit_on_rsi2_above"),
                OptionalBool(map, "exit_rules.exit_on_close_above_sma5", false),
                OptionalBool(map, "exit_rules.exit_on_close_below_sma5", false),
                OptionalBool(map, "exit_rules.check_exits_from_fill_day", false),
                ReadContinuationProtection(map),
                OptionalDecimal(map, "exit_rules.exit_on_entry_factor_at_or_below"),
                OptionalNullableString(map, "exit_rules.entry_factor_exit_schedule")),
            new ExecutionRules(
                OptionalString(map, "execution.timeframe", RequireString(map, "timeframe")),
                RequireDecimal(map, "execution.slippage_bps"),
                new ExecutionConfirmationRules(
                    OptionalBool(map, "execution.confirmation.enabled", false),
                    OptionalInt(map, "execution.confirmation.max_bars_after_setup", 6),
                    OptionalString(map, "execution.confirmation.price_filter", "none"),
                    OptionalString(map, "execution.confirmation.trend_filter", "none"),
                    OptionalString(map, "execution.confirmation.momentum_filter", "none"),
                    OptionalDecimal(map, "execution.confirmation.min_close_location_value"),
                    OptionalDecimal(map, "execution.confirmation.max_close_location_value")),
                OptionalNullableString(map, "execution.opening_gap_policy")),
            new SessionRules(
                RequireString(map, "session.exchange_timezone"),
                RequireInt(map, "session.quiet_minutes_after_open"),
                RequireInt(map, "session.close_buffer_minutes"),
                RequireInt(map, "session.friday_close_buffer_minutes"),
                OptionalBool(map, "session.is_continuous_market", false),
                OptionalBool(map, "session.use_extended_hours", false)),
            ReadRegimeRules(map));
        if (strategy.EntryRules.Sma200RisingLookbackBars < 0 || strategy.EntryRules.MinBreakoutVolumeVs50DayAverage is <= 0)
            throw new InvalidOperationException("SMA200 rise lookback must be nonnegative; breakout volume threshold must be positive.");
        if (strategy.EntryRules.MinBreakoutVolumeVs50DayAverage is not null && !strategy.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The prior-50-day breakout-volume rule requires completed daily bars.");
        if (strategy.Execution.OpeningGapPolicy is { } gapPolicy && (gapPolicy != ExecutionRules.KeepAndExitNextOpen ||
            !strategy.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase) ||
            !strategy.Execution.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Opening gap policy '{gapPolicy}' requires '{ExecutionRules.KeepAndExitNextOpen}' on a daily strategy.");
        strategy.ExitRules.ContinuationProtection?.Validate(strategy.ExitRules);
        strategy.EntryRules.OpeningRangeBreakout?.Validate(strategy);
        RejectRemovedSetupRules(strategy);
        ValidateNoStopRules(strategy);
        ValidateStretchAndShortThresholds(strategy.EntryRules);
        ValidateShortCoverExit(strategy);
        ValidateScheduledRules(strategy);
        map.ThrowIfUnknownKeys(path);
        return strategy;
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

    private static string ReadInitialStopMode(IReadOnlyDictionary<string, List<string>> map)
    {
        var explicitMode = OptionalString(map, "exit_rules.initial_stop_mode", "atr");
        if (!OptionalBool(map, "exit_rules.stop_at_flush_low", false))
        {
            return explicitMode;
        }

        if (map.ContainsKey("exit_rules.initial_stop_mode") &&
            !explicitMode.Equals("flush_low", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "exit_rules.stop_at_flush_low conflicts with exit_rules.initial_stop_mode.");
        }

        return "flush_low";
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
                RequireBool(runMap, "engine.fail_fast"),
                OptionalInt(runMap, "engine.max_retained_replay_bars", 1_000_000),
                OptionalInt(runMap, "engine.max_strategy_work_items", 10_000),
                OptionalInt(runMap, "engine.max_retained_candidates", 1_000_000),
                OptionalBool(runMap, "engine.allow_late_warmup", false),
                OptionalDateTimeOffset(runMap, "engine.precommitted_liquidation_source_utc"),
                OptionalNullableString(runMap, "engine.execution_replay_policy_id")) {
                    EvidenceManifestPath = OptionalNullableString(runMap, "engine.evidence_manifest_path"),
                    EvidenceResearchRoot = OptionalNullableString(runMap, "engine.evidence_research_root"),
                    EvidenceManifestSha256 = OptionalNullableString(runMap, "engine.evidence_manifest_sha256"),
                    EvidenceDependencyGraphSha256 = OptionalNullableString(runMap, "engine.evidence_dependency_graph_sha256"),
                    ExecutionSourceManifestPath = OptionalNullableString(runMap, "engine.execution_source_manifest_path"),
                    ExecutionSourceManifestSha256 = OptionalNullableString(runMap, "engine.execution_source_manifest_sha256") },
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
                    OptionalString(runMap, "providers.alpaca.data_feed", "sip")),
                ReadEvidenceCatalogProvider(runMap)),
            new PortfolioConfig(
                RequireDecimal(runMap, "portfolio.starting_capital"),
                RequireDecimal(runMap, "portfolio.account_risk_budget_pct"),
                RequireDecimal(runMap, "portfolio.max_position_notional_pct"),
                OptionalInt(runMap, "portfolio.max_concurrent_positions", 5),
                RequireDecimal(runMap, "portfolio.fixed_buy_fee"),
                RequireDecimal(runMap, "portfolio.fixed_sell_fee"),
                OptionalInt(runMap, "portfolio.max_open_trades_per_ticker", 1),
                RequireBool(runMap, "portfolio.prevent_overlapping_ticker_positions"),
                OptionalDecimal(runMap, "portfolio.max_bar_participation_pct") ?? 0m,
                OptionalDecimal(runMap, "portfolio.sec_fee_rate") ?? 0m,
                OptionalDecimal(runMap, "portfolio.finra_taf_per_share") ?? 0m,
                OptionalDecimal(runMap, "portfolio.finra_taf_cap") ?? 0m,
                OptionalDecimal(runMap, "portfolio.impact_bps_at_max_participation") ?? 0m,
                OptionalInt(runMap, "portfolio.max_execution_order_history", 1_000_000),
                OptionalDecimal(runMap, "portfolio.commission_per_share") ?? 0m,
                OptionalDecimal(runMap, "portfolio.max_gross_exposure_pct")),
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
            ReadUniverseConfig(runMap),
            ReadUniverseRankConfig(runMap),
            ReadDiscoveryRuntimeConfig(runMap));
    }

    /// <summary>
    /// Reads <c>universe.rank</c>. Every field falls back to the shipped default,
    /// so a profile may state only the weights it wants to move and the rest stay
    /// at the documented values rather than silently becoming zero.
    /// </summary>
    private static UniverseRankConfig ReadUniverseRankConfig(
        IReadOnlyDictionary<string, List<string>> runMap)
    {
        var fallback = UniverseRankConfig.Default;
        var catalysts = new Dictionary<string, decimal>(
            fallback.CatalystWeights,
            StringComparer.OrdinalIgnoreCase);
        foreach (var kind in fallback.CatalystWeights.Keys.ToArray())
        {
            var configured = OptionalDecimal(runMap, $"universe.rank.catalyst_weights.{kind.ToLowerInvariant()}");
            if (configured is { } value)
            {
                catalysts[kind] = value;
            }
        }

        return new UniverseRankConfig(
            OptionalString(runMap, "universe.rank.weights_version", fallback.WeightsVersion),
            ReadRankWeights(runMap, "universe.rank.swing", fallback.Swing),
            catalysts,
            OptionalDecimal(runMap, "universe.rank.veto_penalty") ?? fallback.VetoPenalty,
            OptionalDecimal(runMap, "universe.rank.liquidity_spread_ceiling_bps") ?? fallback.LiquiditySpreadCeilingBps);
    }

    private static UniverseRankWeights ReadRankWeights(
        IReadOnlyDictionary<string, List<string>> runMap,
        string prefix,
        UniverseRankWeights fallback) => new(
            OptionalDecimal(runMap, $"{prefix}.model_edge") ?? fallback.ModelEdge,
            OptionalDecimal(runMap, $"{prefix}.market_structure") ?? fallback.MarketStructure,
            OptionalDecimal(runMap, $"{prefix}.catalyst") ?? fallback.Catalyst,
            OptionalDecimal(runMap, $"{prefix}.technical_state") ?? fallback.TechnicalState,
            OptionalDecimal(runMap, $"{prefix}.liquidity") ?? fallback.Liquidity);

    private static UniverseConfig? ReadUniverseConfig(IReadOnlyDictionary<string, List<string>> runMap)
    {
        var mode = OptionalString(runMap, "universe.mode", UniverseConfig.StaticMode);
        if (String.IsNullOrWhiteSpace(mode) ||
            mode.Equals(UniverseConfig.StaticMode, StringComparison.OrdinalIgnoreCase))
        {
            // Rules on a static list would be dropped silently; ReadDailyEligibility refuses every mode but resolved_snapshot.
            _ = ReadDailyEligibility(runMap, UniverseConfig.StaticMode);
            _ = ReadDailyRanking(runMap, null);
            _ = ReadRankedMembership(runMap, UniverseConfig.StaticMode, null);
            return null;
        }

        var dailyEligibility = ReadDailyEligibility(runMap, mode);
        var rankedMembership = ReadRankedMembership(runMap, mode, dailyEligibility);
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
            OptionalString(runMap, "universe.rescreen_frequency", UniverseConfig.RescreenPerRun),
            OptionalString(runMap, "universe.source", String.Empty),
            OptionalList(runMap, "universe.selected_source_tickers"),
            OptionalList(runMap, "universe.screener_tickers"),
            OptionalString(runMap, "universe.resolved_screener_query", String.Empty) is { Length: > 0 } resolvedQuery
                ? resolvedQuery
                : null,
            OptionalDateTimeOffset(runMap, "universe.resolved_at_utc"),
            dailyEligibility,
            ReadDailyRanking(runMap, dailyEligibility),
            rankedMembership);
    }

    /// <summary>
    /// Reads universe.ranked_membership: the absolute path and SHA-256 of a published research screen's manifest. Only a
    /// resolved_snapshot list may carry it, and never with the run's own daily rules, because the screen's rules apply.
    /// </summary>
    private static RankedMembershipReference? ReadRankedMembership(IReadOnlyDictionary<string, List<string>> runMap, string mode,
        DailyScreenRules? eligibility)
    {
        const string prefix = "universe.ranked_membership.";
        if (!runMap.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal))) return null;
        if (!mode.Equals(UniverseConfig.ResolvedSnapshotMode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("universe.ranked_membership applies only to a resolved_snapshot stock list.");
        if (eligibility is not null)
            throw new InvalidDataException("universe.ranked_membership takes its rules from the research screen; remove universe.daily_eligibility.");
        var path = RequireString(runMap, prefix + "manifest_path");
        var sha = RequireString(runMap, prefix + "manifest_sha256");
        if (!Path.IsPathFullyQualified(path) || sha.Length != 64 || !sha.All(Uri.IsHexDigit))
            throw new InvalidDataException("universe.ranked_membership needs an absolute manifest_path and a 64-character manifest_sha256.");
        return new RankedMembershipReference(Path.GetFullPath(path), sha.ToLowerInvariant());
    }

    /// <summary>
    /// Reads providers.evidence_catalog: catalog and artifact-root paths, the ranked-list SHA-256 and every frozen dataset ID.
    /// A partial block fails instead of defaulting; absent means the run does not read the evidence catalog.
    /// </summary>
    private static EvidenceCatalogProviderConfig? ReadEvidenceCatalogProvider(IReadOnlyDictionary<string, List<string>> runMap)
    {
        const string prefix = "providers.evidence_catalog.";
        if (!runMap.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal))) return null;
        var five = OptionalList(runMap, prefix + "five_minute_datasets");
        // Daily research screens read daily bars only, so the intraday dataset lists may be empty.
        return new EvidenceCatalogProviderConfig(Path.GetFullPath(RequireString(runMap, prefix + "catalog_path")),
            Path.GetFullPath(RequireString(runMap, prefix + "artifact_root")),
            RequireString(runMap, prefix + "ranked_list_sha256"), RequireString(runMap, prefix + "daily_dataset"),
            RequireString(runMap, prefix + "exchange_sessions_dataset"), five, OptionalList(runMap, prefix + "one_minute_datasets"),
            OptionalNullableString(runMap, prefix + "corporate_actions_dataset"), OptionalList(runMap, prefix + "hourly_datasets") is { Count: > 0 } hourly ? hourly : null);
    }

    /// <summary>
    /// Reads the optional per-session ranking of the daily-eligible stocks. Every field is required so a frozen plan never
    /// depends on a code default, and the block is refused without daily eligibility rules, whose stocks it ranks.
    /// </summary>
    private static DailyRankingRules? ReadDailyRanking(IReadOnlyDictionary<string, List<string>> runMap, DailyScreenRules? eligibility)
    {
        const string prefix = "universe.daily_ranking.";
        if (!runMap.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal))) return null;
        if (eligibility is null)
            throw new InvalidDataException("universe.daily_ranking ranks the stocks that pass universe.daily_eligibility and needs that block.");
        try
        {
            var rules = new DailyRankingRules(
                RequireString(runMap, prefix + "measure"),
                RequireInt(runMap, prefix + "baseline_sessions"),
                DailyRankingRules.ParseAverage(RequireString(runMap, prefix + "baseline_average")),
                RequireDecimal(runMap, prefix + "min_relative_volume"),
                RequireInt(runMap, prefix + "max_per_session"));
            rules.Validate();
            return rules;
        }
        catch (ArgumentException exception) { throw new InvalidDataException(exception.Message, exception); }
    }

    /// <summary>
    /// Reads the optional per-day eligibility block of a resolved stock snapshot. When present, each stock may trade only on
    /// days it passes these rules from completed earlier sessions. A partial or contradictory block fails instead of defaulting.
    /// </summary>
    private static DailyScreenRules? ReadDailyEligibility(IReadOnlyDictionary<string, List<string>> runMap, string mode)
    {
        const string prefix = "universe.daily_eligibility.";
        if (!runMap.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal))) return null;
        if (!mode.Equals(UniverseConfig.ResolvedSnapshotMode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("universe.daily_eligibility applies only to a resolved_snapshot stock list.");
        var rules = new DailyScreenRules(
            RequireDecimal(runMap, prefix + "min_price"),
            DailyScreenRules.ParsePriceBasis(RequireString(runMap, prefix + "price_basis")),
            RequireInt(runMap, prefix + "average_sessions"),
            OptionalDecimal(runMap, prefix + "min_average_volume"),
            OptionalDecimal(runMap, prefix + "min_average_dollar_volume"),
            OptionalNullableInt(runMap, prefix + "atr_period"),
            OptionalDecimal(runMap, prefix + "min_atr"),
            RequireInt(runMap, prefix + "min_history_sessions"));
        try { rules.Validate(); }
        catch (ArgumentException exception) { throw new InvalidDataException(exception.Message, exception); }
        return rules;
    }

    private static DiscoveryRuntimeConfig? ReadDiscoveryRuntimeConfig(
        IReadOnlyDictionary<string, List<string>> runMap)
    {
        if (!OptionalBool(runMap, "discovery.enabled", false))
        {
            return null;
        }

        var wishlistIdText = OptionalString(runMap, "discovery.wishlist_id", String.Empty);
        if (wishlistIdText.Length > 0 && !Guid.TryParse(wishlistIdText, out _))
        {
            throw new InvalidDataException("Discovery wishlist_id must be a valid GUID when provided.");
        }
        Guid? wishlistId = Guid.TryParse(wishlistIdText, out var parsedWishlistId)
            ? parsedWishlistId
            : null;
        var refreshSeconds = OptionalInt(
            runMap,
            "discovery.refresh_seconds",
            DiscoveryRuntimeConfig.DefaultRefreshSeconds);
        var expirySeconds = OptionalInt(
            runMap,
            "discovery.source_expiry_seconds",
            DiscoveryRuntimeConfig.DefaultSourceExpirySeconds);
        if (refreshSeconds <= 0 || expirySeconds <= refreshSeconds)
        {
            throw new InvalidDataException(
                "Discovery refresh_seconds must be positive and source_expiry_seconds must exceed it.");
        }

        return new DiscoveryRuntimeConfig(
            true,
            refreshSeconds,
            expirySeconds,
            wishlistId,
            OptionalList(runMap, "discovery.wishlist_tickers"),
            OptionalString(runMap, "discovery.finviz_query", String.Empty) is { Length: > 0 } finvizQuery
                ? finvizQuery
                : null,
            OptionalList(runMap, "discovery.finviz_tickers"),
            OptionalList(runMap, "discovery.operator_tickers"),
            OptionalList(runMap, "discovery.alert_tickers"),
            OptionalList(runMap, "discovery.news_tickers"),
            OptionalList(runMap, "discovery.earnings_tickers"));
    }

    /// <summary>Reads optimizer choices, rejecting an explicitly empty parameter axis rather than silently deleting it.</summary>
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

                if (parsedValues.Count == 0)
                    throw new InvalidDataException($"Optimization parameter '{paramKey}' requires at least one value.");
                parameters[paramKey] = parsedValues;
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

    private static ParsedYamlMap ReadKeyValueMap(string path)
    {
        var result = new ParsedYamlMap();
        var sectionStack = new Stack<(int Indent, string Key, string FullKey)>();
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

            foreach (var section in sectionStack)
            {
                result.SetKind(section.FullKey, YamlValueKind.Mapping);
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (activeListKey is null)
                {
                    throw new InvalidOperationException($"List item without list key in {path}: {line}");
                }

                result[activeListKey].Add(Unquote(line[2..].Trim()));
                result.SetKind(activeListKey, YamlValueKind.Sequence);
                continue;
            }

            var colonIndex = line.IndexOf(':', StringComparison.Ordinal);
            if (colonIndex < 0)
            {
                throw new InvalidOperationException($"Malformed YAML line without a key/value separator in {path}: {line}");
            }

            var key = line[..colonIndex].Trim();
            var value = line[(colonIndex + 1)..].Trim();
            var fullKey = sectionStack.Count == 0
                ? key
                : String.Join(".", sectionStack.Reverse().Select(x => x.Key).Append(key));

            if (String.IsNullOrEmpty(value))
            {
                RejectDuplicateKey(result, fullKey, path);
                sectionStack.Push((indent, key, fullKey));
                activeListKey = fullKey;
                result.Add(fullKey, new List<string>(), YamlValueKind.Empty);
            }
            else
            {
                RejectDuplicateKey(result, fullKey, path);
                activeListKey = null;
                if (value.StartsWith("[", StringComparison.Ordinal) &&
                    value.EndsWith("]", StringComparison.Ordinal))
                {
                    var values = value[1..^1]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(Unquote)
                        .ToList();
                    result.Add(fullKey, values, YamlValueKind.Sequence);
                }
                else
                {
                    result.Add(fullKey, new List<string> { Unquote(value) }, YamlValueKind.Scalar);
                }
            }
        }

        return result;
    }

    private static void RejectDuplicateKey(
        IReadOnlyDictionary<string, List<string>> map,
        string key,
        string path)
    {
        if (map.ContainsKey(key))
        {
            throw new InvalidOperationException($"Duplicate YAML key '{key}' in '{path}'.");
        }
    }

    // Only reviewed presence/assessment treatments are supported; diagnostic timing has no editable YAML knobs.
    private static string? ReadHistoricalNewsDiagnostic(IReadOnlyDictionary<string, List<string>> map)
    {
        var treatment = OptionalNullableString(map, "entry_rules.historical_news_diagnostic");
        return treatment is null or HistoricalNewsDiagnosticPolicy.EvidenceChecked or HistoricalNewsDiagnosticPolicy.AvoidRecentCompanyNews ||
            HistoricalCompanyNewsDiagnosticPolicy.IsTreatment(treatment)
            ? treatment : throw new InvalidOperationException("Unsupported historical_news_diagnostic treatment.");
    }

    // Thresholds of the removed VCP, anchored-VWAP bounce and swing-reclaim setups (and daily short setups) have no
    // evaluator rule any more. Reject them when the file is loaded; silently ignoring them would let a copied
    // config trade without the filter its author set. The parameter fields stay because kept configs list them at
    // their defaults and they are part of each strategy's identity hash.
    private static void RejectRemovedSetupRules(StrategyDefinition strategy)
    {
        var rules = strategy.EntryRules;
        if (rules.MinContractions is not null || rules.MaxContractions is not null ||
            rules.RequireVolatilityHalvingLeftToRight || rules.RequireVolumeDryUpPreBreakout ||
            rules.MinBreakoutVolumeRatio is not null || rules.AvwapProximityPct is not null ||
            rules.RequirePullbackVolumeDryup || rules.MinBounceVolumeRatio is not null ||
            rules.RequireOrderedReclaimPullback)
            throw new InvalidOperationException(
                "This strategy sets a threshold of a removed setup (VCP, anchored-VWAP bounce or swing reclaim); no rule enforces it.");
        if (rules.EnableShort && rules.OpeningRangeBreakout is null && rules.ShortSetupType.ToLowerInvariant() is not ("rsi2_overbought" or "momentum"))
            throw new InvalidOperationException("enable_short on a daily strategy supports short_setup_type rsi2_overbought or momentum; other short setups were removed.");
    }

    // Each stretch gate (below or above an SMA) needs both its threshold and an SMA period the indicator snapshot computes;
    // an RSI(2) short setup needs its threshold, otherwise it would silently never fire.
    private static void ValidateStretchAndShortThresholds(EntryRules rules)
    {
        ValidateStretchGate(rules.MinCloseBelowSmaPct, rules.CloseBelowSmaPeriod, "min_close_below_sma_pct", "close_below_sma_period");
        ValidateStretchGate(rules.MinCloseAboveSmaPct, rules.CloseAboveSmaPeriod, "min_close_above_sma_pct", "close_above_sma_period");
        var rsi2ShortSetup = rules.EnableShort && rules.ShortSetupType.Equals("rsi2_overbought", StringComparison.OrdinalIgnoreCase);
        if ((rsi2ShortSetup && rules.MinShortReversionRsi2 is null) ||
            rules.MinShortReversionRsi2 is not (null or (> 0m and < 100m)))
            throw new InvalidOperationException("min_short_reversion_rsi2 is required for short_setup_type rsi2_overbought and must be above 0 and below 100.");
    }

    // Decision schedules must be known names on a daily rule. The entry-factor exit recomputes the long entry factor from the
    // stock's own completed bars, so it needs a factor that does not read a benchmark; its schedule needs the exit itself.
    private static void ValidateScheduledRules(StrategyDefinition strategy)
    {
        DecisionSchedules.Validate(strategy.EntryRules.DecisionSchedule, "entry_rules.decision_schedule");
        DecisionSchedules.Validate(strategy.ExitRules.EntryFactorExitSchedule, "exit_rules.entry_factor_exit_schedule");
        var scheduled = strategy.EntryRules.DecisionSchedule is not null || strategy.ExitRules.EntryFactorExitSchedule is not null;
        if (scheduled && !(strategy.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase) &&
                strategy.Execution.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Decision schedules require a daily strategy and daily execution.");
        if (strategy.ExitRules.EntryFactorExitSchedule is not null && strategy.ExitRules.ExitOnEntryFactorAtOrBelow is null)
            throw new InvalidOperationException("exit_rules.entry_factor_exit_schedule requires exit_rules.exit_on_entry_factor_at_or_below.");
        if (strategy.ExitRules.ExitOnEntryFactorAtOrBelow is not null &&
            strategy.EntryRules.TechnicalFactor is not { Metric: not TechnicalFactorRules.RelativeRoc })
            throw new InvalidOperationException("exit_rules.exit_on_entry_factor_at_or_below requires an entry technical_factor that does not use a benchmark.");
    }

    // The close-below-SMA5 cover exit is checked only on short positions; on any other direction it would silently never fire.
    private static void ValidateShortCoverExit(StrategyDefinition strategy)
    {
        if (strategy.ExitRules.ExitOnCloseBelowSma5 && strategy.Direction.ToLowerInvariant() is not "short")
            throw new InvalidOperationException("exit_on_close_below_sma5 covers a short position and requires direction: short.");
    }

    // Reject a threshold without its period (or the reverse) and periods the snapshot does not compute.
    private static void ValidateStretchGate(decimal? pct, int? period, string pctKey, string periodKey)
    {
        if (pct is null && period is null) return;
        if (pct is not (> 0m and < 100m) || period is not (5 or 10 or 20 or 50 or 150 or 200))
            throw new InvalidOperationException(
                $"{pctKey} (above 0 and below 100) and {periodKey} (5, 10, 20, 50, 150 or 200) must be set together.");
    }

    // A strategy without a protective stop is a daily research rule (long or short) sized by position notional: there is no
    // stop distance for an R-multiple target, a trailing stop or continuation protection to work from.
    private static void ValidateNoStopRules(StrategyDefinition strategy)
    {
        var exits = strategy.ExitRules;
        if (exits.HasProtectiveStop)
        {
            // A stop placed at the fill bar's close cannot also be judged on that bar; the fill-day check is for no-stop rules only.
            if (exits.CheckExitsFromFillDay)
                throw new InvalidOperationException("check_exits_from_fill_day requires initial_stop_mode: none.");
            // Without a target-aware broker path a target-less daily position would still get a broker target in paper
            // trading; the published opening-range recipe holds to its stop or the close and is backtest-only.
            if (!exits.HasProfitTarget && strategy.EntryRules.OpeningRangeBreakout is not { IsPublishedRecipe: true })
                throw new InvalidOperationException("profit_target_mode: none requires initial_stop_mode: none, except for the published opening-range recipe.");
            return;
        }
        if (strategy.Direction.ToLowerInvariant() is not ("long" or "short") ||
            !strategy.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase) ||
            !strategy.Execution.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase) ||
            exits.EnableAtrTrailingStop || exits.ContinuationProtection is not null || exits.HasProfitTarget)
            throw new InvalidOperationException(
                "initial_stop_mode: none requires a single-direction (long or short) strategy with daily setup and execution bars, profit_target_mode: none, and no trailing stop or continuation protection.");
    }

    // An explicit mapping selects the reviewed ORB policy; omission preserves historical strategy identities.
    private static OpeningRangeBreakoutRules? ReadOpeningRangeBreakout(IReadOnlyDictionary<string, List<string>> map)
    {
        const string section = "entry_rules.opening_range_breakout";
        if (!map.ContainsKey(section) && !map.Keys.Any(key => key.StartsWith(section + ".", StringComparison.OrdinalIgnoreCase)))
            return null;
        return new OpeningRangeBreakoutRules(OptionalString(map, section + ".policy_id", "opening-range-breakout-v1"),
            OptionalInt(map, section + ".max_filled_parent_trades_per_session", 2),
            OptionalNullableInt(map, section + ".range_minutes"),
            OptionalNullableInt(map, section + ".volume_lookback_sessions"),
            OptionalDecimal(map, section + ".min_relative_volume"),
            OptionalNullableInt(map, section + ".atr_sessions"),
            OptionalBool(map, section + ".stop_from_fill", false),
            OptionalBool(map, section + ".exit_at_session_close", false));
    }

    // A present mapping opts in; absent mappings leave all existing strategy behavior unchanged.
    private static ContinuationProtectionRules? ReadContinuationProtection(IReadOnlyDictionary<string, List<string>> map)
    {
        const string section = "exit_rules.continuation_protection";
        const string prefix = section + ".";
        if (!map.ContainsKey(section) && !map.Keys.Any(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return null;
        return new ContinuationProtectionRules(
            map.ContainsKey(prefix + "activation_r") ? RequireDecimal(map, prefix + "activation_r") : 2.4m,
            map.ContainsKey(prefix + "cost_buffer_ticks") ? RequireInt(map, prefix + "cost_buffer_ticks") : 1,
            map.ContainsKey(prefix + "confirmation_bars") ? RequireInt(map, prefix + "confirmation_bars") : 2,
            map.ContainsKey(prefix + "target_step_r") ? RequireDecimal(map, prefix + "target_step_r") : 1m);
    }

    private static DrawdownQualificationRules? ReadDrawdownQualification(IReadOnlyDictionary<string, List<string>> map)
    {
        const string prefix = "entry_rules.drawdown_qualification.";
        if (!map.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal))) return null;
        var rules = new DrawdownQualificationRules(
            OptionalString(map, prefix + "mode", "observe"),
            OptionalNullableString(map, prefix + "metric"),
            OptionalDecimal(map, prefix + "min_value"), OptionalDecimal(map, prefix + "max_value"),
            OptionalBool(map, prefix + "require_sector_context", false),
            OptionalBool(map, prefix + "require_event_context", false),
            OptionalInt(map, prefix + "minimum_matured_episodes", 0),
            OptionalBool(map, prefix + "reject_adverse_events", false),
            OptionalNullableInt(map, prefix + "event_lookback_days"),
            OptionalNullableInt(map, prefix + "event_max_coverage_age_hours"));
        rules.Validate();
        return rules;
    }

    private static TechnicalFactorRules? ReadTechnicalFactor(IReadOnlyDictionary<string, List<string>> map)
    {
        if (!map.Keys.Any(key => key.StartsWith("entry_rules.technical_factor.", StringComparison.Ordinal)) &&
            !map.ContainsKey("entry_rules.technical_factor")) return null;
        if (!RequireString(map, "direction").Equals("long", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Technical factors currently require a long strategy.");
        var rules = new TechnicalFactorRules(RequireString(map, "entry_rules.technical_factor.metric"),
            OptionalInt(map, "entry_rules.technical_factor.lookback_bars", 20),
            OptionalDecimal(map, "entry_rules.technical_factor.min_value"),
            OptionalDecimal(map, "entry_rules.technical_factor.max_value"),
            OptionalDecimal(map, "entry_rules.technical_factor.max_context_value"),
            OptionalString(map, "entry_rules.technical_factor.benchmark_symbol", "SPY"));
        rules.Validate();
        return rules;
    }

    private static string RequireString(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        if (!TryGetScalar(map, key, out var value) || String.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing YAML key: {key}");
        }

        return value;
    }

    private static IReadOnlyList<string> RequireList(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        if (!map.TryGetValue(key, out var values))
        {
            throw new InvalidOperationException($"Missing YAML list: {key}");
        }

        if (GetNodeKind(map, key) == YamlValueKind.Empty)
        {
            return Array.Empty<string>();
        }

        EnsureNodeKind(map, key, YamlValueKind.Sequence);

        return values;
    }

    private static IReadOnlyList<string> OptionalList(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        if (!map.TryGetValue(key, out var values))
        {
            return Array.Empty<string>();
        }

        if (GetNodeKind(map, key) == YamlValueKind.Empty)
        {
            return Array.Empty<string>();
        }

        EnsureNodeKind(map, key, YamlValueKind.Sequence);
        return values.Where(value => !String.IsNullOrWhiteSpace(value)).ToArray();
    }

    private static int RequireInt(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        return Int32.Parse(RequireString(map, key), CultureInfo.InvariantCulture);
    }

    private static int OptionalInt(IReadOnlyDictionary<string, List<string>> map, string key, int fallback)
    {
        return TryGetScalar(map, key, out var value) && !String.IsNullOrWhiteSpace(value)
            ? Int32.Parse(value, CultureInfo.InvariantCulture)
            : fallback;
    }

    private static int? OptionalNullableInt(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        return TryGetScalar(map, key, out var value) && !String.IsNullOrWhiteSpace(value)
            ? Int32.Parse(value, CultureInfo.InvariantCulture)
            : null;
    }

    private static decimal RequireDecimal(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        return Decimal.Parse(RequireString(map, key), CultureInfo.InvariantCulture);
    }

    private static decimal? OptionalDecimal(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        return TryGetScalar(map, key, out var value) && !String.IsNullOrWhiteSpace(value)
            ? Decimal.Parse(value, CultureInfo.InvariantCulture)
            : null;
    }

    private static bool RequireBool(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        return Boolean.Parse(RequireString(map, key));
    }

    private static bool OptionalBool(IReadOnlyDictionary<string, List<string>> map, string key, bool fallback)
    {
        return TryGetScalar(map, key, out var value) && !String.IsNullOrWhiteSpace(value)
            ? Boolean.Parse(value)
            : fallback;
    }

    private static string OptionalString(IReadOnlyDictionary<string, List<string>> map, string key, string fallback)
    {
        return TryGetScalar(map, key, out var value) && !String.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    private static string? OptionalNullableString(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        return TryGetScalar(map, key, out var value) && !String.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static DateOnly? OptionalDateOnly(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        if (!TryGetScalar(map, key, out var value) || String.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.Parse(value, CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? OptionalDateTimeOffset(IReadOnlyDictionary<string, List<string>> map, string key)
    {
        if (!TryGetScalar(map, key, out var value) || String.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }

    private static bool TryGetScalar(
        IReadOnlyDictionary<string, List<string>> map,
        string key,
        out string value)
    {
        if (!map.TryGetValue(key, out var values))
        {
            value = String.Empty;
            return false;
        }

        var kind = GetNodeKind(map, key);
        if (kind == YamlValueKind.Empty)
        {
            value = String.Empty;
            return false;
        }

        if (kind != YamlValueKind.Scalar || values.Count != 1)
        {
            throw new InvalidOperationException(
                $"YAML key '{key}' must contain exactly one scalar value, not a list or mapping.");
        }

        value = values[0];
        return true;
    }

    private static void EnsureNodeKind(
        IReadOnlyDictionary<string, List<string>> map,
        string key,
        YamlValueKind expected)
    {
        var actual = GetNodeKind(map, key);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"YAML key '{key}' must be a {expected.ToString().ToLowerInvariant()}, not {actual.ToString().ToLowerInvariant()}.");
        }
    }

    private static YamlValueKind GetNodeKind(
        IReadOnlyDictionary<string, List<string>> map,
        string key)
    {
        return map is IYamlShapeMap shaped
            ? shaped.GetKind(key)
            : YamlValueKind.Scalar;
    }

    private enum YamlValueKind
    {
        Empty,
        Scalar,
        Sequence,
        Mapping
    }

    private interface IYamlShapeMap
    {
        YamlValueKind GetKind(string key);
    }

    private sealed class ParsedYamlMap : IReadOnlyDictionary<string, List<string>>, IYamlShapeMap
    {
        private readonly Dictionary<string, List<string>> values = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, YamlValueKind> kinds = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<string> Keys => values.Keys;

        public IEnumerable<List<string>> Values => values.Values;

        public int Count => values.Count;

        public List<string> this[string key] => values[key];

        public void Add(string key, List<string> nodeValues, YamlValueKind kind)
        {
            values.Add(key, nodeValues);
            kinds.Add(key, kind);
        }

        public void SetKind(string key, YamlValueKind kind) => kinds[key] = kind;

        public YamlValueKind GetKind(string key) => kinds[key];

        public bool ContainsKey(string key) => values.ContainsKey(key);

        public bool TryGetValue(string key, out List<string> value) => values.TryGetValue(key, out value!);

        public IEnumerator<KeyValuePair<string, List<string>>> GetEnumerator() => values.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Records every key the typed strategy parser asks for. A key present in
    /// YAML but never requested is therefore a typo or unsupported rule and is
    /// rejected instead of being silently ignored.
    /// </summary>
    private sealed class TrackingConfigurationMap(
        IReadOnlyDictionary<string, List<string>> inner)
        : IReadOnlyDictionary<string, List<string>>, IYamlShapeMap
    {
        private static readonly HashSet<string> KnownSectionKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "entry_rules",
            "entry_rules.technical_factor",
            "entry_rules.opening_range_breakout",
            "entry_rules.drawdown_qualification",
            "risk_guards",
            "risk_guards.per_ticker_daily",
            "confluence",
            "exit_rules",
            "exit_rules.continuation_protection",
            "execution",
            "execution.confirmation",
            "session",
            "regime"
        };
        private readonly HashSet<string> accessed = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<string> Keys => inner.Keys;

        public IEnumerable<List<string>> Values => inner.Values;

        public int Count => inner.Count;

        public List<string> this[string key]
        {
            get
            {
                accessed.Add(key);
                return inner[key];
            }
        }

        public bool ContainsKey(string key)
        {
            accessed.Add(key);
            return inner.ContainsKey(key);
        }

        public bool TryGetValue(string key, out List<string> value)
        {
            accessed.Add(key);
            return inner.TryGetValue(key, out value!);
        }

        public IEnumerator<KeyValuePair<string, List<string>>> GetEnumerator() => inner.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public YamlValueKind GetKind(string key) => GetNodeKind(inner, key);

        public void ThrowIfUnknownKeys(string path)
        {
            var invalidSections = KnownSectionKeys
                .Where(inner.ContainsKey)
                .Where(key => GetNodeKind(inner, key) != YamlValueKind.Mapping)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (invalidSections.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Strategy YAML mapping section(s) contain scalar/list data in '{path}': {String.Join(", ", invalidSections)}");
            }

            var unknown = inner.Keys
                .Where(key => !accessed.Contains(key) && !KnownSectionKeys.Contains(key))
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (unknown.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Unsupported strategy YAML key(s) in '{path}': {String.Join(", ", unknown)}");
            }
        }
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
            if (File.Exists(Path.Combine(directory.FullName, "TradingFlow.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
