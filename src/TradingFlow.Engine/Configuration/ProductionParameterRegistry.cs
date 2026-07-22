using System.Collections.ObjectModel;

namespace TradingFlow.Engine.Configuration;

/// <summary>
/// Identifies the runtime representation used to parse and validate a production parameter.
/// </summary>
public enum ProductionParameterKind
{
    Integer,
    Decimal,
    Boolean,
    String,
    Enumeration,
    TimeOfDay,
    TimeWindow,
    StringList
}

/// <summary>
/// Gives numeric values an unambiguous operational meaning for validation and telemetry.
/// </summary>
public enum ProductionParameterUnit
{
    None,
    Seconds,
    Milliseconds,
    Minutes,
    Hours,
    UsDollars,
    Percent,
    BasisPoints,
    Sessions,
    Score,
    Ratio,
    Rows,
    Symbols,
    ArticlesPerDay,
    RequestsPerMinute
}

/// <summary>
/// Represents an exchange-local time window whose start and end are both inclusive.
/// </summary>
public sealed record ProductionTimeWindow(TimeOnly Start, TimeOnly End);

/// <summary>
/// Describes one Appendix-A parameter without coupling the registry to a configuration provider.
/// </summary>
public sealed record ProductionParameterDefinition(
    string Name,
    ProductionParameterKind Kind,
    object? DefaultValue,
    decimal? Minimum,
    decimal? Maximum,
    ProductionParameterUnit Unit,
    string SpecificationReference,
    IReadOnlyList<string>? AllowedValues = null,
    bool IsRequired = false,
    bool IsLockedInLiveV1 = false);

/// <summary>
/// Canonical code registry for every production parameter in binding-spec Appendix A.
/// Runtime loaders consume this immutable registry; tests keep it synchronized with the spec.
/// </summary>
public static class ProductionParameterRegistry
{
    private static readonly IReadOnlyList<ProductionParameterDefinition> DefinitionsValue =
        new ReadOnlyCollection<ProductionParameterDefinition>(BuildDefinitions());

    private static readonly IReadOnlyDictionary<string, ProductionParameterDefinition> ByNameValue =
        new ReadOnlyDictionary<string, ProductionParameterDefinition>(
            DefinitionsValue.ToDictionary(definition => definition.Name, StringComparer.Ordinal));

    public static IReadOnlyList<ProductionParameterDefinition> Definitions => DefinitionsValue;

    public static IReadOnlyDictionary<string, ProductionParameterDefinition> ByName => ByNameValue;

    private static List<ProductionParameterDefinition> BuildDefinitions()
    {
        return
        [
            Choice("account_mode", "margin", ["margin", "cash"], "ACC-05"),
            Decimal("pdt_equity_floor", 25_500m, 25_000m, null, ProductionParameterUnit.UsDollars, "ACC-03"),
            Integer("pdt_recheck_interval_s", 300, 60, 900, ProductionParameterUnit.Seconds, "ACC-02"),
            Decimal("max_gross_exposure_intraday_pct", 100m, 10m, 200m, ProductionParameterUnit.Percent, "ACC-07"),
            Decimal("max_gross_exposure_overnight_pct", 75m, 10m, 100m, ProductionParameterUnit.Percent, "ACC-07"),
            Boolean("allow_swing_shorts", false, "ACC-10"),
            Integer("asset_check_max_age_s", 3_600, 60, 86_400, ProductionParameterUnit.Seconds, "ACC-11"),
            Text("expected_account_id", null, "ACC-12", isRequired: true),

            Boolean("allow_iex_fallback", false, "DATA-04"),
            Integer("rvol_lookback_sessions", 20, 10, 60, ProductionParameterUnit.Sessions, "DATA-03"),
            Integer("ws_backoff_initial_ms", 500, 100, 5_000, ProductionParameterUnit.Milliseconds, "DATA-06"),
            Integer("ws_backoff_max_ms", 30_000, 5_000, 120_000, ProductionParameterUnit.Milliseconds, "DATA-06"),
            Integer("ws_stale_after_s", 10, 3, 60, ProductionParameterUnit.Seconds, "DATA-07"),
            Integer("ws_stale_after_ext_s", 60, 10, 300, ProductionParameterUnit.Seconds, "DATA-07"),
            Integer("quote_max_age_ms", 2_000, 250, 10_000, ProductionParameterUnit.Milliseconds, "DATA-08"),
            Integer("ws_symbol_budget", 300, 30, 2_000, ProductionParameterUnit.Symbols, "DATA-09"),
            Integer("snapshot_poll_interval_s", 30, 5, 300, ProductionParameterUnit.Seconds, "DATA-09"),
            Integer("rest_budget_market_data_per_min", 9_000, null, null, ProductionParameterUnit.RequestsPerMinute, "DATA-12"),
            Integer("rest_budget_trading_per_min", 180, null, null, ProductionParameterUnit.RequestsPerMinute, "DATA-12"),
            Integer("data_latency_alert_ms", 1_500, 250, 10_000, ProductionParameterUnit.Milliseconds, "DATA-14"),
            Integer("clock_drift_max_ms", 250, 50, 1_000, ProductionParameterUnit.Milliseconds, "DATA-15"),

            Time("calendar_fetch_time", new TimeOnly(3, 30), new TimeOnly(0, 0), new TimeOnly(6, 0), "CAL-01"),
            Decimal("min_session_hours_for_close_model", 6.0m, 3m, 6.5m, ProductionParameterUnit.Hours, "CAL-02"),
            Integer("eod_flatten_offset_min", 10, 2, 30, ProductionParameterUnit.Minutes, "CAL-02"),
            Choice("earnings_calendar_source", "finviz", ["finviz", "other"], "CAL-07"),

            Integer("finviz_min_interval_s", 60, 30, 600, ProductionParameterUnit.Seconds, "FVZ-02"),
            Integer("finviz_timeout_s", 20, 5, 60, ProductionParameterUnit.Seconds, "FVZ-02"),
            Integer("finviz_min_rows", 1, null, null, ProductionParameterUnit.Rows, "FVZ-01"),
            Integer("finviz_max_rows", 5_000, null, null, ProductionParameterUnit.Rows, "FVZ-01"),

            Choice("dedup_method", "embedding_cosine", ["embedding_cosine", "jaccard"], "NWS-04"),
            Decimal("dedup_similarity_threshold", 0.92m, 0.80m, 0.99m, ProductionParameterUnit.Ratio, "NWS-04"),
            Integer("dedup_window_hours", 72, 24, 168, ProductionParameterUnit.Hours, "NWS-04"),
            Integer("news_latency_alert_s", 90, 10, 600, ProductionParameterUnit.Seconds, "NWS-03"),
            Text("catalyst_llm_model", null, "NWS-05", isRequired: true),
            Integer("catalyst_llm_timeout_s", 8, 2, 30, ProductionParameterUnit.Seconds, "NWS-06"),
            Integer("catalyst_llm_max_articles_per_day", 500, 50, 5_000, ProductionParameterUnit.ArticlesPerDay, "NWS-07"),
            Decimal("catalyst_llm_daily_cost_cap_usd", 10m, 1m, 100m, ProductionParameterUnit.UsDollars, "NWS-07"),
            Integer("catalyst_pipeline_p95_s", 15, 5, 60, ProductionParameterUnit.Seconds, "NWS-08"),
            Choice("estimates_source", "none", ["none", "licensed"], "NWS-09"),
            Decimal("catalyst_min_category_acc", 0.85m, 0.7m, 1.0m, ProductionParameterUnit.Ratio, "NWS-12"),
            Decimal("catalyst_min_direction_acc", 0.90m, 0.8m, 1.0m, ProductionParameterUnit.Ratio, "NWS-12"),
            Integer("contradiction_window_min", 120, 30, 480, ProductionParameterUnit.Minutes, "NWS-13"),
            Integer("ambiguity_cooloff_min", 240, 60, 1_440, ProductionParameterUnit.Minutes, "NWS-13"),

            Integer("setup_max_age_s", 20, 5, 120, ProductionParameterUnit.Seconds, "EXE-04"),
            Decimal("max_spread_bps", 20m, 2m, 100m, ProductionParameterUnit.BasisPoints, "EXE-04"),
            Decimal("max_expected_slippage_bps", 15m, 2m, 100m, ProductionParameterUnit.BasisPoints, "EXE-04"),
            Decimal("min_stop_spread_mult", 4m, 2m, 10m, ProductionParameterUnit.Ratio, "EXE-05"),
            Decimal("min_stop_atr_frac", 0.25m, 0.1m, 1.0m, ProductionParameterUnit.Ratio, "EXE-05"),
            Decimal("max_notional_per_trade_pct", 15m, 1m, 50m, ProductionParameterUnit.Percent, "EXE-06"),
            Decimal("max_pct_adv", 1m, 0.1m, 5m, ProductionParameterUnit.Percent, "EXE-06"),
            Decimal("max_pct_recent_dollar_vol", 5m, 1m, 20m, ProductionParameterUnit.Percent, "EXE-06"),
            Integer("reconcile_interval_s", 60, 15, 300, ProductionParameterUnit.Seconds, "EXE-08"),
            Integer("order_orphan_timeout_s", 30, 10, 120, ProductionParameterUnit.Seconds, "EXE-02"),
            Integer("order_poll_interval_s", 15, 5, 60, ProductionParameterUnit.Seconds, "EXE-03"),
            Decimal("backstop_atr_mult", 1.5m, 1.0m, 3.0m, ProductionParameterUnit.Ratio, "EXE-09"),
            Boolean("allow_multi_strategy_same_symbol", false, "EXE-10", isLockedInLiveV1: true),
            Boolean("allow_extended_hours_trading", false, "EXE-11"),
            Boolean("shutdown_flatten_day", true, "EXE-13"),
            Boolean("shutdown_flatten_swing", false, "EXE-13"),

            Decimal("per_trade_risk_pct_day", 0.5m, 0.1m, 2.0m, ProductionParameterUnit.Percent, "RSK-01"),
            Decimal("per_trade_risk_pct_swing", 0.5m, 0.1m, 2.0m, ProductionParameterUnit.Percent, "RSK-01"),
            Decimal("max_daily_loss_pct", 2m, 0.5m, 5m, ProductionParameterUnit.Percent, "RSK-01"),
            Decimal("max_weekly_loss_pct", 4m, 1m, 10m, ProductionParameterUnit.Percent, "RSK-01"),
            Integer("max_consecutive_losses", 3, 2, 10, ProductionParameterUnit.None, "RSK-01"),
            Integer("max_positions_day", 1, 1, 20, ProductionParameterUnit.None, "RSK-01"),
            Integer("max_positions_swing", 3, 1, 20, ProductionParameterUnit.None, "RSK-01"),
            Integer("news_stream_down_max_s", 120, 30, 600, ProductionParameterUnit.Seconds, "RSK-03"),
            Integer("max_order_rejects", 3, 1, 10, ProductionParameterUnit.None, "RSK-03"),
            Boolean("manual_kill_flatten", true, "RSK-04"),
            Decimal("equity_staleness_haircut_pct", 5m, 1m, 20m, ProductionParameterUnit.Percent, "RSK-06"),

            StringList("swing_enabled_strategies", ["SWG-A"], "SWG-05"),
            Decimal("swga_min_catalyst", 70m, 50m, 95m, ProductionParameterUnit.Score, "SWG-06"),
            Decimal("swga_close_range_pct", 25m, 10m, 50m, ProductionParameterUnit.Percent, "SWG-06"),
            Decimal("swga_min_rvol", 2.0m, 1.2m, 5m, ProductionParameterUnit.Ratio, "SWG-06"),
            Decimal("swga_min_confirm", 60m, 40m, 90m, ProductionParameterUnit.Score, "SWG-06"),
            TimeWindow(
                "swga_entry_window",
                new ProductionTimeWindow(new TimeOnly(9, 45), new TimeOnly(10, 30)),
                new TimeOnly(9, 30),
                new TimeOnly(16, 0),
                "SWG-06"),
            Decimal("swga_max_entry_gap_atr", 1.0m, 0.25m, 2.0m, ProductionParameterUnit.Ratio, "SWG-06"),
            Decimal("swga_stop_atr_mult", 1.5m, 0.5m, 3.0m, ProductionParameterUnit.Ratio, "SWG-06"),
            Decimal("swga_target_r", 2.0m, 1.0m, 5.0m, ProductionParameterUnit.Ratio, "SWG-06"),
            Integer("swga_max_hold_sessions", 10, 2, 20, ProductionParameterUnit.Sessions, "SWG-06"),
            Integer("swga_trail_ma", 10, 5, 20, ProductionParameterUnit.Sessions, "SWG-06"),
            Integer("swgb_ma_slope_lookback", 5, 3, 10, ProductionParameterUnit.Sessions, "SWG-07"),
            Decimal("swgb_ma_zone_atr", 0.5m, 0.1m, 1.0m, ProductionParameterUnit.Ratio, "SWG-07"),
            Decimal("swgb_pullback_vol_ratio", 0.8m, 0.5m, 1.0m, ProductionParameterUnit.Ratio, "SWG-07"),
            Decimal("swgb_stop_atr_frac", 0.25m, 0.1m, 1.0m, ProductionParameterUnit.Ratio, "SWG-07"),
            Decimal("swgb_target_r", 2.5m, 1.0m, 5.0m, ProductionParameterUnit.Ratio, "SWG-07"),
            Integer("swgb_max_hold_sessions", 15, 3, 30, ProductionParameterUnit.Sessions, "SWG-07"),
            Decimal("gap_stress_atr_mult", 2.0m, 1.0m, 4.0m, ProductionParameterUnit.Ratio, "SWG-08"),
            Decimal("swing_gap_risk_budget_pct", 1.0m, 0.25m, 3.0m, ProductionParameterUnit.Percent, "SWG-08"),
            Decimal("swing_portfolio_gap_budget_pct", 3.0m, 1m, 10m, ProductionParameterUnit.Percent, "SWG-09"),
            Integer("swing_max_per_sector", 2, 1, 5, ProductionParameterUnit.None, "SWG-09"),
            Choice("swg_entry_order_type", "marketable_limit", ["marketable_limit", "limit"], "SWG-11"),
            Decimal("entry_limit_offset_bps", 10m, 0m, 50m, ProductionParameterUnit.BasisPoints, "SWG-11"),
            Decimal("min_fill_ratio", 50m, 10m, 100m, ProductionParameterUnit.Percent, "SWG-11"),
            Choice("swg_stop_order_type", "stop", ["stop", "stop_limit"], "SWG-12"),
            Decimal("swg_news_exit_materiality", 0.7m, 0.5m, 1.0m, ProductionParameterUnit.Ratio, "SWG-13"),
            Integer("swg_earnings_buffer_sessions", 1, 1, 3, ProductionParameterUnit.Sessions, "SWG-14"),
            Boolean("allow_day_to_swing_conversion", false, "DAY-03", isLockedInLiveV1: true),
            Choice("raw_md_sampling", "full_subscribed", ["full_subscribed"], "PER-01")
        ];
    }

    private static ProductionParameterDefinition Integer(
        string name,
        int defaultValue,
        int? minimum,
        int? maximum,
        ProductionParameterUnit unit,
        string specificationReference) =>
        new(name, ProductionParameterKind.Integer, defaultValue, minimum, maximum, unit, specificationReference);

    private static ProductionParameterDefinition Decimal(
        string name,
        decimal defaultValue,
        decimal? minimum,
        decimal? maximum,
        ProductionParameterUnit unit,
        string specificationReference) =>
        new(name, ProductionParameterKind.Decimal, defaultValue, minimum, maximum, unit, specificationReference);

    private static ProductionParameterDefinition Boolean(
        string name,
        bool defaultValue,
        string specificationReference,
        bool isLockedInLiveV1 = false) =>
        new(
            name,
            ProductionParameterKind.Boolean,
            defaultValue,
            null,
            null,
            ProductionParameterUnit.None,
            specificationReference,
            IsLockedInLiveV1: isLockedInLiveV1);

    private static ProductionParameterDefinition Text(
        string name,
        string? defaultValue,
        string specificationReference,
        bool isRequired = false) =>
        new(
            name,
            ProductionParameterKind.String,
            defaultValue,
            null,
            null,
            ProductionParameterUnit.None,
            specificationReference,
            IsRequired: isRequired);

    private static ProductionParameterDefinition Choice(
        string name,
        string defaultValue,
        IReadOnlyList<string> allowedValues,
        string specificationReference) =>
        new(
            name,
            ProductionParameterKind.Enumeration,
            defaultValue,
            null,
            null,
            ProductionParameterUnit.None,
            specificationReference,
            Array.AsReadOnly(allowedValues.ToArray()));

    private static ProductionParameterDefinition Time(
        string name,
        TimeOnly defaultValue,
        TimeOnly minimum,
        TimeOnly maximum,
        string specificationReference) =>
        new(
            name,
            ProductionParameterKind.TimeOfDay,
            defaultValue,
            minimum.Hour * 60m + minimum.Minute,
            maximum.Hour * 60m + maximum.Minute,
            ProductionParameterUnit.Minutes,
            specificationReference);

    private static ProductionParameterDefinition TimeWindow(
        string name,
        ProductionTimeWindow defaultValue,
        TimeOnly minimum,
        TimeOnly maximum,
        string specificationReference) =>
        new(
            name,
            ProductionParameterKind.TimeWindow,
            defaultValue,
            minimum.Hour * 60m + minimum.Minute,
            maximum.Hour * 60m + maximum.Minute,
            ProductionParameterUnit.Minutes,
            specificationReference);

    private static ProductionParameterDefinition StringList(
        string name,
        IReadOnlyList<string> defaultValue,
        string specificationReference) =>
        new(
            name,
            ProductionParameterKind.StringList,
            Array.AsReadOnly(defaultValue.ToArray()),
            null,
            null,
            ProductionParameterUnit.None,
            specificationReference);
}
