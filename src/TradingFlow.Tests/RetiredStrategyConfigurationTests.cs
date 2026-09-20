using TradingFlow.Engine.Configuration;

namespace TradingFlow.Tests;

public sealed class RetiredStrategyConfigurationTests
{
    public static TheoryData<string> RetiredKeys => new()
    {
        "entry_rules.opening_range_minutes",
        "entry_rules.opening_range_break_buffer",
        "entry_rules.vwap_hold_bars",
        "entry_rules.max_entries_per_ticker_per_day",
        "entry_rules.min_day_gain_pct",
        "entry_rules.min_session_gain_pct",
        "entry_rules.max_pre_entry_session_range_pct",
        "entry_rules.max_entry_pullback_from_session_high_pct",
        "entry_rules.min_bull_flag_pole_move_pct",
        "entry_rules.bull_flag_pole_max_bars",
        "entry_rules.bull_flag_pullback_min_bars",
        "entry_rules.bull_flag_pullback_max_bars",
        "entry_rules.bull_flag_max_depth_pct_of_pole",
        "entry_rules.bull_flag_pullback_volume_ratio_max",
        "entry_rules.bull_flag_breakout_volume_ratio_min",
        "entry_rules.enable_premarket_filter",
        "entry_rules.require_premarket_high_break",
        "entry_rules.premarket_high_break_buffer_pct",
        "entry_rules.max_premarket_vwap_extension_pct",
        "entry_rules.max_premarket_run_pct",
        "entry_rules.max_opening_range_pct",
        "entry_rules.reject_opening_exhaustion",
        "entry_rules.opening_exhaustion_minutes",
        "entry_rules.opening_exhaustion_max_day_gain_pct",
        "entry_rules.opening_exhaustion_max_session_range_pct",
        "entry_rules.opening_exhaustion_min_pullback_from_high_pct",
        "entry_rules.min_consecutive_closes_above_vwap",
        "entry_rules.max_vwap_extension_pct_for_direct_entry",
        "entry_rules.extended_vwap_min_entry_bar_close_location_value",
        "entry_rules.max_bollinger_position_for_direct_entry",
        "entry_rules.extended_bollinger_min_entry_bar_close_location_value",
        "entry_rules.require_prior_flush_below_vwap_bars",
        "entry_rules.vwap_reclaim_max_bars_since_flush",
        "entry_rules.min_reclaim_volume_ratio",
        "risk_guards.per_ticker_daily.enabled",
        "risk_guards.per_ticker_daily.max_failed_trades",
        "risk_guards.per_ticker_daily.max_loss_r",
        "risk_guards.per_ticker_daily.max_loss_pct_of_account",
        "entry_rules.require_prior_inside_day",
        "entry_rules.require_prior_nr7",
        "entry_rules.prior_compression_mode",
        "entry_rules.prior_nr7_lookback_days",
        "entry_rules.min_vwap_distance_atr_for_divergence",
        "entry_rules.divergence_lookback_bars",
        "entry_rules.divergence_start_hour",
        "exit_rules.enable_failed_breakout_circuit_breaker",
        "exit_rules.failed_breakout_bars",
        "exit_rules.failed_breakout_min_r",
    };

    [Theory]
    [MemberData(nameof(RetiredKeys))]
    public void ReadStrategy_RejectsRetiredRuleRatherThanIgnoringIt(string key)
    {
        var parts = key.Split('.');
        var content = CanonicalContent();
        if (parts.Length == 2)
        {
            var section = parts[0] + ":\n";
            Assert.Contains(section, content, StringComparison.Ordinal);
            content = content.Replace(section,
                section + "  " + parts[1] + ": 1\n", StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal("risk_guards", parts[0]);
            content += "\nrisk_guards:\n  per_ticker_daily:\n    " + parts[2] + ": 1\n";
        }

        WithTemporaryStrategy(content, path =>
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                new SimpleYamlReader().ReadStrategy(path));
            Assert.Contains("Unsupported strategy YAML key(s)", error.Message, StringComparison.Ordinal);
            Assert.Contains(key, error.Message, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("1h")]
    [InlineData("15m")]
    [InlineData("5m")]
    public void ReadStrategy_PreservesDailySwingWithSubdailyExecution(string timeframe)
    {
        var content = CanonicalContent();
        const string dailyExecution = "execution:\n  timeframe: 1d";
        Assert.Contains(dailyExecution, content, StringComparison.Ordinal);
        content = content.Replace(dailyExecution,
            "execution:\n  timeframe: " + timeframe, StringComparison.Ordinal);

        WithTemporaryStrategy(content, path =>
        {
            var strategy = new SimpleYamlReader().ReadStrategy(path);
            Assert.Equal("1d", strategy.Timeframe);
            Assert.Equal(timeframe, strategy.Execution.Timeframe);
            Assert.Equal(12, strategy.EntryRules.LogPriceLookbackBars);
            Assert.Equal(12, strategy.EntryRules.LogVolumeLookbackBars);
        });
    }

    private static string CanonicalContent() => File.ReadAllText(Path.Combine(
            TestRepository.FindRoot(), "configs", "strategies",
            "minervini-trend-template-vcp.v4-trend-rider.yaml"))
        .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static void WithTemporaryStrategy(string content, Action<string> verify)
    {
        var path = Path.Combine(Path.GetTempPath(), $"swing-rule-validation-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(path, content);
            verify(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
