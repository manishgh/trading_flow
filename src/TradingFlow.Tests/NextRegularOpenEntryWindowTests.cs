using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Indicators;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Market;

namespace TradingFlow.Tests;

public sealed class NextRegularOpenEntryWindowTests
{
    // Daily midnight labels cannot grant an opening fill outside the exclusive replay deadline,
    // or move a late receipt to a later bar. An exactly-on-open receipt remains executable.
    [Theory]
    [InlineData(0, 300, 0)]
    [InlineData(-60, 300, 0)]
    [InlineData(1, 300, 2)]
    [InlineData(0, 0, 2)]
    [InlineData(-60, -1, 2)]
    public void EconomicOpenControlsReceiptAndExclusiveReplayEnd(int receiptOffset, int expiryOffset, int expected)
    {
        var source = DateTimeOffset.Parse("2026-11-13T05:00:00Z");
        var open = DateTimeOffset.Parse("2026-11-16T14:30:00Z");
        var calendar = Calendar(source, open.AddDays(1));
        var window = NextRegularOpenEntryWindow.Resolve(Strategy(), source, calendar)!;
        OhlcvBar[] bars =
        [
            new("AAPL", DateTimeOffset.Parse("2026-11-16T05:00:00Z"), "1d", 100, 101, 99, 100, 1000),
            new("AAPL", DateTimeOffset.Parse("2026-11-17T05:00:00Z"), "1d", 100, 101, 99, 100, 1000)
        ];
        var opens = BacktestDecisionTiming.IndexEconomicOpens(bars, "America/New_York", calendar);
        Assert.Equal(expected, BacktestDecisionTiming.FindWindowEntryIndex(window, open.AddSeconds(receiptOffset),
            open.AddSeconds(expiryOffset), opens, bars.Length));
    }

    // A source-bound window crosses weekends/DST and never depends on when its host receives the setup.
    [Theory]
    [InlineData("2026-10-30T04:00:00Z", "2026-11-02T14:30:00Z")]
    [InlineData("2026-03-06T05:00:00Z", "2026-03-09T13:30:00Z")]
    public void SourceSessionFreezesOpenAcrossDst(string sourceText, string openText)
    {
        var source = DateTimeOffset.Parse(sourceText);
        var open = DateTimeOffset.Parse(openText);
        var calendar = Calendar(source, open);
        var window = Assert.IsType<NextRegularOpenEntryWindow>(NextRegularOpenEntryWindow.Resolve(Strategy(), source, calendar));
        Assert.Equal(open, window.OpenUtc);
        Assert.Equal(open.AddMinutes(5), window.EndUtc);
        Assert.False(window.Contains(open.AddTicks(-1)));
        Assert.True(window.Contains(open));
        Assert.True(window.Contains(window.EndUtc.AddTicks(-1)));
        Assert.False(window.Contains(window.EndUtc));
        Assert.False(window.Contains(open.AddDays(1)));
    }

    // Holiday schedules and early closes are authoritative even when a larger explicit grace was supplied.
    [Fact]
    public void HolidayAndEarlyCloseClipTheWindow()
    {
        var source = DateTimeOffset.Parse("2026-11-25T05:00:00Z");
        var open = DateTimeOffset.Parse("2026-11-27T14:30:00Z");
        var calendar = Calendar(source, open);
        calendar[new DateOnly(2026, 11, 26)] = MarketSessionSchedule.Closed(new(2026, 11, 26));
        calendar[new DateOnly(2026, 11, 27)] = new(new(2026, 11, 27), true, new(9, 30), new(13, 0));
        var window = NextRegularOpenEntryWindow.Resolve(Strategy(86400), source, calendar)!;
        Assert.Equal(open, window.OpenUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-11-27T18:00:00Z"), window.EndUtc);
        calendar.Remove(new(2026, 11, 26));
        Assert.Throws<InvalidDataException>(() => NextRegularOpenEntryWindow.Resolve(Strategy(), source, calendar));
    }

    // Default-off preserves prior strategies, while explicit policy input must be complete and supported.
    [Theory]
    [InlineData(0, 1, "next_regular_open_window")]
    [InlineData(-1, 1, "next_regular_open_window")]
    [InlineData(300, 2, "next_regular_open_window")]
    [InlineData(300, 1, "unknown")]
    public void UnsupportedPoliciesFailClosed(int grace, int version, string policy)
    {
        var strategy = Strategy(grace);
        strategy = strategy with { Execution = strategy.Execution with { EntryWindow = new(policy, version, grace) } };
        Assert.Throws<InvalidOperationException>(() => NextRegularOpenEntryWindow.Resolve(strategy, DateTimeOffset.UtcNow, null));
        Assert.Null(NextRegularOpenEntryWindow.Resolve(strategy with
            { Execution = strategy.Execution with { EntryWindow = null } }, DateTimeOffset.UtcNow, null));
    }

    // Parse the opt-in into immutable strategy identity and reject omitted economic values.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void YamlRequiresExplicitVersionAndGrace(bool complete)
    {
        var source = File.ReadAllText(FixturePath());
        var addition = "execution:\n  entry_window:\n    policy: next_regular_open_window\n    version: 1\n" +
            (complete ? "    grace_seconds: 300\n" : "");
        var file = Path.Combine(Path.GetTempPath(), $"entry-window-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(file, source.Replace("\r\n", "\n").Replace("execution:\n", addition));
            if (!complete) Assert.Throws<InvalidOperationException>(() => new SimpleYamlReader().ReadStrategy(file));
            else Assert.Equal(new(EntryWindowRules.NextRegularOpen, 1, 300), new SimpleYamlReader().ReadStrategy(file).Execution.EntryWindow);
        }
        finally { File.Delete(file); }
    }

    // A neutral fixture supplies unrelated technical rules without enabling a catalog strategy.
    private static StrategyDefinition Strategy(int grace = 300)
    {
        var strategy = new SimpleYamlReader().ReadStrategy(FixturePath());
        return strategy with { Execution = strategy.Execution with { EntryWindow = new(EntryWindowRules.NextRegularOpen, 1, grace) } };
    }

    // Resolve the checked-in, test-only strategy; temporary YAML files are owned by the individual test.
    private static string FixturePath() => Path.Combine(TestRepository.FindRoot(), "src", "TradingFlow.Tests", "Fixtures", "Strategies", "daily-trailing-swing.fixture.yaml");

    // Include closed calendar dates so a weekend is proof rather than a gap in provider evidence.
    private static Dictionary<DateOnly, MarketSessionSchedule> Calendar(DateTimeOffset source, DateTimeOffset open)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(source, zone).DateTime);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(open, zone).DateTime);
        return Enumerable.Range(first.DayNumber, last.DayNumber - first.DayNumber + 1).Select(DateOnly.FromDayNumber)
            .ToDictionary(day => day, day => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                ? MarketSessionSchedule.Closed(day) : MarketSessionSchedule.RegularDay(day));
    }
}
