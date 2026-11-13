using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class BacktestExecutionProfileResolverTests
{
    /// <summary>Day research inherits costs and resource limits but requests only native five-minute baseline context.</summary>
    [Fact]
    public void DayProfile_UsesDistinctPolicyAndDropsInheritedDailyHistoryAndUnusedSources()
    {
        var profile = Profile();
        var result = BacktestExecutionProfileResolver.Resolve(profile, [DayStrategy()]);
        Assert.Equal(ExecutionReplayPolicy.IntradayRthFiveMinuteNextBarMarketV1, result.Engine.ExecutionReplayPolicyId);
        Assert.Equal(new[] { "5m" }, result.Intervals);
        Assert.Equal("5m", result.DerivedTimeframes.Source);
        Assert.Equal(0, result.Engine.IndicatorWarmupBars);
        Assert.Equal(0, result.TimeWindow.WarmupLookbackDays);
        Assert.False(result.Engine.AllowLateWarmup);
        Assert.False(result.Validation.Benchmark.Enabled);
        Assert.False(result.News.Enabled);
        Assert.Equal(profile.Portfolio, result.Portfolio);
        Assert.Equal(profile.Engine.MaxRetainedReplayBars, result.Engine.MaxRetainedReplayBars);
        Assert.Equal(profile.Engine.MaxStrategyWorkItems, result.Engine.MaxStrategyWorkItems);
    }

    /// <summary>Automatic selection leaves all existing swing assumptions unchanged.</summary>
    [Fact]
    public void SwingProfile_PreservesExistingSettings()
    {
        var profile = Profile();
        var strategy = new SimpleYamlReader().ReadStrategy(RetiredStrategyFixtures.Strategy("swing-connors-rsi2-sma5-risk-managed.bt-v7.yaml"));
        Assert.Equal(profile, BacktestExecutionProfileResolver.Resolve(profile, [strategy]));
        Assert.Throws<InvalidOperationException>(() => BacktestExecutionProfileResolver.Resolve(profile, [strategy, DayStrategy()]));
        Assert.Throws<InvalidOperationException>(() => BacktestExecutionProfileResolver.Resolve(profile, [strategy], ExecutionReplayPolicy.IntradayRthFiveMinuteV1));
    }

    /// <summary>Explicit daily assumptions and unknown identifiers cannot silently substitute for intraday limit execution.</summary>
    [Theory]
    [InlineData(ExecutionReplayPolicy.DailyRthFiveMinuteV1)]
    [InlineData(ExecutionReplayPolicy.DailyRthSipPortfolioV1)]
    [InlineData("misspelled-policy")]
    public void DayProfile_RejectsIncompatibleExecutionChoice(string policy) =>
        Assert.Throws<InvalidOperationException>(() => BacktestExecutionProfileResolver.Resolve(Profile(), [DayStrategy()], policy));

    /// <summary>The existing integer warmup includes the actual preceding open across holiday weekends, DST and early closes.</summary>
    [Theory]
    [InlineData("2026-11-30T14:30:00Z", "2026-11-27", "13:00", 3)]
    [InlineData("2026-03-09T13:30:00Z", "2026-03-06", "16:00", 3)]
    [InlineData("2026-11-02T14:30:00Z", "2026-10-30", "16:00", 4)]
    public void PreviousSessionContext_IncludesPrecedingOpen(string start, string priorDate, string close, int expectedDays)
    {
        var date = DateOnly.Parse(priorDate);
        var schedules = new Dictionary<DateOnly, MarketSessionSchedule> {
            [date] = new(date, true, new(9, 30), TimeOnly.Parse(close))
        };
        var result = BacktestSessionContextResolver.IncludePreviousSession(Profile(), DateTimeOffset.Parse(start), schedules,
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        Assert.Equal(expectedDays, result.TimeWindow.WarmupLookbackDays);
    }

    /// <summary>An incomplete preceding-session calendar cannot be replaced with a guessed calendar-day offset.</summary>
    [Fact]
    public void PreviousSessionContext_RejectsAbsentCompletedSession() =>
        Assert.Throws<InvalidOperationException>(() => BacktestSessionContextResolver.IncludePreviousSession(Profile(),
            DateTimeOffset.Parse("2026-10-26T13:30:00Z"), new Dictionary<DateOnly, MarketSessionSchedule>(),
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York")));

    /// <summary>Reads only the immutable source-controlled profile; no provider requests or generated files are used.</summary>
    private static BacktestRunConfig Profile() => new SimpleYamlReader().ReadBacktestRun(
        Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "swing-backtest-profile.yaml"));

    /// <summary>Uses the same registered day recipe the normal catalog presents to both clients.</summary>
    private static StrategyDefinition DayStrategy() => new SimpleYamlReader().ReadStrategy(
        Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "strategies", "opening-range-breakout.bt-v3.yaml"));
}
