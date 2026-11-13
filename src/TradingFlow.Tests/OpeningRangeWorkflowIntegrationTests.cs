using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class OpeningRangeWorkflowIntegrationTests
{
    /// <summary>Saved-plan review text reflects the selected recipe's actual declared filled-parent limit.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ReviewAssumptionNote_PreservesDeclaredParentTradeLimit(int limit)
    {
        var strategy = ReadStrategy("opening-range-breakout-stop-market.v1.fixture.yaml");
        strategy = strategy with { EntryRules = strategy.EntryRules with {
            OpeningRangeBreakout = strategy.EntryRules.OpeningRangeBreakout! with { MaxFilledParentTradesPerSession = limit }
        }};
        strategy.EntryRules.OpeningRangeBreakout!.Validate(strategy);
        var formatter = typeof(WorkflowRunConfiguration).GetMethod("ExecutionAssumptionNote",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var note = Assert.IsType<string>(formatter.Invoke(null, [ReadProfile(), strategy]));
        Assert.Contains($"Filled parent trade limit per stock per session: {limit},", note);
        Assert.DoesNotContain("At most two", note);
    }

    /// <summary>Normal saved plans select the distinct stop-market contract and preserve ordinary portfolio costs and limits.</summary>
    [Theory]
    [InlineData("opening-range-breakout-stop-market.v1.fixture.yaml")]
    [InlineData("opening-range-breakout-stop-market.v2.fixture.yaml")]
    public void OrdinaryProfile_SelectsStopMarketPolicyAndRejectsHorizonMixing(string recipe)
    {
        var profile = ReadProfile();
        var openingRange = ReadStrategy(recipe);
        var daily = new SimpleYamlReader().ReadStrategy(RetiredStrategyFixtures.Strategy("swing-connors-rsi2-sma5-risk-managed.bt-v7.yaml"));
        var effective = BacktestExecutionProfileResolver.Resolve(profile, [openingRange]);
        Assert.Equal(ExecutionReplayPolicy.IntradayRthFiveMinuteStopMarketV1, effective.Engine.ExecutionReplayPolicyId);
        Assert.Equal(profile.Portfolio, effective.Portfolio);
        Assert.Equal(new[] { "5m" }, effective.Intervals);
        Assert.Equal(0, effective.Engine.IndicatorWarmupBars);
        Assert.Throws<InvalidOperationException>(() => BacktestExecutionProfileResolver.Resolve(profile, [openingRange], ExecutionReplayPolicy.IntradayRthFiveMinuteV1));
        Assert.Throws<InvalidOperationException>(() => BacktestExecutionProfileResolver.Resolve(profile, [daily], ExecutionReplayPolicy.IntradayRthFiveMinuteStopMarketV1));
        Assert.Throws<InvalidOperationException>(() => BacktestExecutionProfileResolver.Resolve(profile, [openingRange, daily]));
    }

    /// <summary>Every normal generated strategy snapshot retains the reviewed ORB rule and the amended two-parent filled-trade limit.</summary>
    [Theory]
    [InlineData("opening-range-breakout-stop-market.v1.fixture.yaml")]
    [InlineData("opening-range-breakout-stop-market.v2.fixture.yaml")]
    [InlineData("opening-range-breakout.bt-v3.yaml")]
    public void OrdinaryStrategySnapshot_RoundTripsOpeningRangeRules(string recipe)
    {
        var original = ReadStrategy(recipe);
        var path = Path.Combine(Path.GetTempPath(), "orb-strategy-snapshot-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, RunConfigWriter.SerializeStrategyYaml(original));
            var actual = new SimpleYamlReader().ReadStrategy(path);
            Assert.Equal(original, actual);
            Assert.Equal(2, actual.EntryRules.OpeningRangeBreakout!.MaxFilledParentTradesPerSession);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Catalog registration never enables ORB in operational modes, even if a grant names its exact immutable identity.</summary>
    [Fact]
    public void OpeningRangeCatalog_RemainsBacktestOnlyUnderExplicitOperationalGrants()
    {
        var root = TestRepository.FindRoot();
        var catalog = new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), new SimpleYamlReader());
        var artifacts = catalog.GetSelectable(StrategySelectionMode.Backtest)
            .Where(artifact => artifact.Identity.StrategyId == "technical.opening-range-breakout").ToArray();
        Assert.Equal(new[] { "1.2.0", "2.0.0", "2.1.0" }, artifacts.Select(artifact => artifact.Identity.SemanticVersion));
        foreach (var identity in artifacts.Select(artifact => artifact.Identity))
        foreach (var mode in new[] { StrategySelectionMode.CreatePaperExperiment, StrategySelectionMode.DiagnosticReplay,
            StrategySelectionMode.RunPaperExperiment, StrategySelectionMode.RunPaperShadow, StrategySelectionMode.RunLive })
            Assert.DoesNotContain(catalog.GetSelectable(mode, authorizedPaperShadow: [identity], authorizedValidated: [identity]),
                artifact => artifact.Identity == identity);
    }

    /// <summary>The preserved stop-market fixtures (retired catalog versions 1.0.0 and 1.1.0) differ only by their submitted-price policy.</summary>
    [Fact]
    public void StopMarketFixtures_DifferOnlyBySubmittedPricePolicy()
    {
        var control = ReadStrategy("opening-range-breakout-stop-market.v1.fixture.yaml");
        var rounded = ReadStrategy("opening-range-breakout-stop-market.v2.fixture.yaml");
        var expected = control with { Version = 2, StrategyName = rounded.StrategyName,
            EntryRules = control.EntryRules with { OpeningRangeBreakout = new(
                OpeningRangeBreakoutRules.OutwardOrderRoundingPolicyId, 2) } };
        Assert.Equal(expected, rounded);
    }

    /// <summary>The new recipe automatically selects its explicit market clock and cannot share a stop/limit plan.</summary>
    [Fact]
    public void CompletedBreakoutProfile_UsesNextOpenMarketAndRetainsControls()
    {
        var profile = ReadProfile();
        var strategy = ReadStrategy("opening-range-breakout.bt-v3.yaml");
        var actual = BacktestExecutionProfileResolver.Resolve(profile, [strategy]);
        Assert.Equal(ExecutionReplayPolicy.IntradayRthFiveMinuteNextBarMarketV1, actual.Engine.ExecutionReplayPolicyId);
        Assert.Equal(profile.Portfolio, actual.Portfolio);
        Assert.Throws<InvalidOperationException>(() => BacktestExecutionProfileResolver.Resolve(profile, [strategy], ExecutionReplayPolicy.IntradayRthFiveMinuteStopMarketV1));
        Assert.Throws<InvalidOperationException>(() => BacktestExecutionProfileResolver.Resolve(profile, [strategy, ReadStrategy("opening-range-breakout-stop-market.v2.fixture.yaml")]));
    }

    /// <summary>Calendar-day warmup covers twenty actual preceding sessions across the DST weekend and a declared holiday.</summary>
    [Fact]
    public void OpeningVolumeContext_UsesTwentyImmediatelyPrecedingAuthoritativeSessions()
    {
        var first = new DateOnly(2026, 11, 2);
        var schedules = Calendar(first.AddDays(-45), first);
        var preceding = schedules.Values.Where(session => session.IsTradingDay && session.TradeDate < first)
            .OrderByDescending(session => session.TradeDate).Take(20).ToArray();
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var start = DateTimeOffset.Parse("2026-11-02T14:30:00Z");
        var open = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(preceding[^1].TradeDate.ToDateTime(new(9, 30)), zone), TimeSpan.Zero);
        var effective = BacktestSessionContextResolver.IncludeCompletedSessions(ReadProfile(), start, schedules, zone, 20);
        Assert.Equal((int)Math.Ceiling((start - open).TotalDays), effective.TimeWindow.WarmupLookbackDays);
        Assert.True(start.AddDays(-effective.TimeWindow.WarmupLookbackDays) <= open);
    }

    /// <summary>Nineteen sessions or a missing explicit calendar date cannot silently borrow an older baseline observation.</summary>
    [Fact]
    public void OpeningVolumeContext_RejectsTooFewSessionsOrMissingCalendarDates()
    {
        var first = new DateOnly(2026, 11, 2);
        var schedules = Calendar(first.AddDays(-45), first);
        var start = DateTimeOffset.Parse("2026-11-02T14:30:00Z");
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var required = schedules.Values.Where(session => session.IsTradingDay && session.TradeDate < first)
            .OrderByDescending(session => session.TradeDate).Take(20).ToArray();
        var shortCalendar = schedules.Where(item => item.Key >= required[^2].TradeDate).ToDictionary();
        Assert.Throws<InvalidOperationException>(() => BacktestSessionContextResolver.IncludeCompletedSessions(ReadProfile(), start, shortCalendar, zone, 20));
        schedules.Remove(new(2026, 10, 25));
        var error = Assert.Throws<InvalidOperationException>(() => BacktestSessionContextResolver.IncludeCompletedSessions(ReadProfile(), start, schedules, zone, 20));
        Assert.Contains("2026-10-25", error.Message);
    }

    /// <summary>Creates an explicit synthetic calendar with closed weekends and a holiday, independent of the provider or operator data.</summary>
    private static Dictionary<DateOnly, MarketSessionSchedule> Calendar(DateOnly first, DateOnly last)
    {
        var result = new Dictionary<DateOnly, MarketSessionSchedule>();
        for (var date = first; date <= last; date = date.AddDays(1))
            result[date] = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || date == new DateOnly(2026, 10, 12)
                ? MarketSessionSchedule.Closed(date) : new(date, true, new(9, 30), new(16, 0));
        return result;
    }

    /// <summary>Uses immutable ordinary recipes, so the tests exercise the same supported selections as Web and Android.</summary>
    private static StrategyDefinition ReadStrategy(string name) => new SimpleYamlReader().ReadStrategy(name.EndsWith(".fixture.yaml", StringComparison.Ordinal)
        ? Path.Combine(TestRepository.FindRoot(), "src", "TradingFlow.Tests", "Fixtures", "Strategies", name)
        : Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "strategies", name));

    /// <summary>Loads the shared profile without generating a run or requesting market records.</summary>
    private static BacktestRunConfig ReadProfile() => new SimpleYamlReader().ReadBacktestRun(
        Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "swing-backtest-profile.yaml"));
}
