using System.Runtime.CompilerServices;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Tests;

public sealed class HistoricalScreenerUniverseProviderTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ResolveAsync_SelectsOnlyLiquidTickersUsingPriorData()
    {
        var resolution = await Resolve(BuildBars());

        Assert.Equal(new[] { "LIQ" }, resolution.Tickers);
        Assert.Equal(UniverseConfig.HistoricalScreenerMode, resolution.Source);
        Assert.Equal(new DateOnly(2026, 6, 1), resolution.AsOfDate);
    }

    [Fact]
    public async Task ResolveAsync_IgnoresBarsDatedAtOrAfterAsOf_NoLookahead()
    {
        var resolution = await Resolve(BuildBars());

        // FUTURE only has bars on/after the as-of date, so it must be invisible to selection.
        Assert.DoesNotContain("FUTURE", resolution.Tickers);
        Assert.Contains(resolution.Rejections, reason => reason.StartsWith("FUTURE") && reason.Contains("no_prior_daily_data"));

        // SMALL is illiquid before as-of but has a huge-volume bar ON the as-of date; if the
        // screener leaked that future bar into the average it would wrongly qualify.
        Assert.DoesNotContain("SMALL", resolution.Tickers);
        Assert.Contains(resolution.Rejections, reason => reason.StartsWith("SMALL") && reason.Contains("adv"));
    }

    [Fact]
    public async Task ResolveAsync_RejectsBelowMinPrice()
    {
        var resolution = await Resolve(BuildBars());

        Assert.DoesNotContain("CHEAP", resolution.Tickers);
        Assert.Contains(resolution.Rejections, reason => reason.StartsWith("CHEAP") && reason.Contains("price"));
    }

    [Fact]
    public async Task ResolveMembershipAsync_TickerBecomesMemberOnlyAfterItQualifies_NoLookahead()
    {
        // RAMP is thin on 06-01..06-03 then liquid from 06-04. With lookback=1, eligibility on
        // day D uses only D-1. LIQ is liquid throughout.
        var bars = new List<OhlcvBar>();
        var days = new[]
        {
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 3, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 4, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero),
        };
        foreach (var (day, i) in days.Select((d, i) => (d, i)))
        {
            bars.Add(new OhlcvBar("LIQ", day, "1d", 100m, 100m, 100m, 100m, 20_000m));         // adv 2M always
            bars.Add(new OhlcvBar("RAMP", day, "1d", 100m, 100m, 100m, 100m, i >= 3 ? 20_000m : 1_000m));
        }

        var universe = new UniverseConfig(
            UniverseConfig.HistoricalScreenerMode,
            new[] { "LIQ", "RAMP" },
            MinPrice: 5m,
            MinAvgDollarVolume: 1_000_000m,
            LookbackDays: 1,
            MinPriorReturnPct: null,
            MaxSymbols: null);

        var provider = new HistoricalScreenerUniverseProvider(new FakeDailyProvider(bars));
        var membership = await provider.ResolveMembershipAsync(
            new UniverseRequest(
                Array.Empty<string>(),
                universe,
                new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero),
                "1d",
                new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero)),
            CancellationToken.None);

        // LIQ is a member every window day.
        Assert.True(membership.IsMember("LIQ", new DateOnly(2026, 6, 2)));
        // RAMP's volume jumps on 06-04, but that day itself is NOT a member (eligibility uses 06-03).
        Assert.False(membership.IsMember("RAMP", new DateOnly(2026, 6, 4)));
        Assert.False(membership.IsMember("RAMP", new DateOnly(2026, 6, 2)));
        // It becomes a member on 06-05 (prior day 06-04 was liquid) and stays.
        Assert.True(membership.IsMember("RAMP", new DateOnly(2026, 6, 5)));
        Assert.True(membership.IsMember("RAMP", new DateOnly(2026, 6, 6)));
    }

    // A saved plan's daily eligibility rules replace the legacy screen fields. With the session-open basis a stock that
    // closed below the minimum qualifies once that day opens above it; share volume uses only earlier sessions, so a
    // volume spike on the decision day itself cannot qualify it.
    [Fact]
    public async Task ResolveMembershipAsync_AppliesDailyEligibilityRulesFromEarlierSessions()
    {
        var day = new DateTimeOffset(2026, 6, 1, 4, 0, 0, TimeSpan.Zero);
        var bars = new List<OhlcvBar>();
        for (var i = 0; i < 6; i++)
        {
            var at = day.AddDays(i);
            // GAPUP closes at $4 every day, but opens at $6 from 06-05 (day index 4).
            bars.Add(new OhlcvBar("GAPUP", at, "1d", i >= 4 ? 6m : 4m, 7m, 3m, 4m, 5_000m));
            // THIN trades 100 shares a day before 06-05 and 1,000,000 shares on 06-05 itself.
            bars.Add(new OhlcvBar("THIN", at, "1d", 50m, 51m, 49m, 50m, i == 4 ? 1_000_000m : 100m));
        }

        var rules = new DailyScreenRules(5m, DailyScreenPriceBasis.SessionOpen, 2, MinAverageShareVolume: 1_000m, MinHistorySessions: 2);
        var universe = UniverseConfig.StaticUniverse with { Mode = UniverseConfig.ResolvedSnapshotMode, DailyEligibility = rules };
        var membership = await new HistoricalScreenerUniverseProvider(new FakeDailyProvider(bars)).ResolveMembershipAsync(
            new UniverseRequest(["GAPUP", "THIN"], universe, day.AddDays(2), "1d", day.AddDays(5)), CancellationToken.None);

        Assert.False(membership.IsMember("GAPUP", new DateOnly(2026, 6, 4)));
        Assert.True(membership.IsMember("GAPUP", new DateOnly(2026, 6, 5)));
        Assert.True(membership.IsMember("GAPUP", new DateOnly(2026, 6, 6)));
        Assert.False(membership.IsMember("THIN", new DateOnly(2026, 6, 5)));
        Assert.True(membership.IsMember("THIN", new DateOnly(2026, 6, 6)));
        Assert.Equal([new DateOnly(2026, 6, 5), new DateOnly(2026, 6, 6)], membership.EligibleDays("GAPUP"));
        Assert.Equal(DailyScreenRules.AtrStabilizationSessions * 3 + 15,
            (universe with { DailyEligibility = rules with { AtrPeriod = 14, MinAtr = 0.5m } }).ScreenFetchCalendarDays);
        // A longer ATR period reads seven periods of earlier sessions so its starting estimate no longer matters.
        Assert.Equal(350, (rules with { AtrPeriod = 50, MinAtr = 0.5m }).PriorSessionsNeeded);
        Assert.Throws<ArgumentException>(() => rules.RequireKnownBeforeEntry(entriesFillAtSessionOpen: true));
        (rules with { PriceBasis = DailyScreenPriceBasis.PriorClose }).RequireKnownBeforeEntry(entriesFillAtSessionOpen: true);
    }

    private static Task<UniverseResolution> Resolve(IReadOnlyList<OhlcvBar> bars)
    {
        var universe = new UniverseConfig(
            UniverseConfig.HistoricalScreenerMode,
            new[] { "LIQ", "CHEAP", "SMALL", "FUTURE" },
            MinPrice: 5m,
            MinAvgDollarVolume: 1_000_000m,
            LookbackDays: 20,
            MinPriorReturnPct: null,
            MaxSymbols: null);

        var provider = new HistoricalScreenerUniverseProvider(new FakeDailyProvider(bars));
        return provider.ResolveAsync(
            new UniverseRequest(Array.Empty<string>(), universe, AsOf, "1d"),
            CancellationToken.None);
    }

    private static IReadOnlyList<OhlcvBar> BuildBars()
    {
        var bars = new List<OhlcvBar>();
        for (var i = 1; i <= 20; i++)
        {
            var ts = AsOf.AddDays(-i);
            bars.Add(new OhlcvBar("LIQ", ts, "1d", 100m, 101m, 99m, 100m, 1_000_000m));   // adv 100M
            bars.Add(new OhlcvBar("CHEAP", ts, "1d", 2m, 2m, 2m, 2m, 5_000_000m));         // below min price
            bars.Add(new OhlcvBar("SMALL", ts, "1d", 100m, 100m, 100m, 100m, 1_000m));     // adv 100k, too thin
        }

        // Bars dated on/after the as-of date. These MUST be ignored by the screener.
        bars.Add(new OhlcvBar("FUTURE", AsOf, "1d", 100m, 100m, 100m, 100m, 100_000_000m));
        bars.Add(new OhlcvBar("FUTURE", AsOf.AddDays(1), "1d", 100m, 100m, 100m, 100m, 100_000_000m));
        bars.Add(new OhlcvBar("SMALL", AsOf, "1d", 100m, 100m, 100m, 100m, 100_000_000m)); // would inflate adv if leaked

        return bars;
    }

    private sealed class FakeDailyProvider(IReadOnlyList<OhlcvBar> bars) : IMarketDataProvider
    {
        // Deliberately returns every bar it holds (including future-dated ones) so the test
        // proves the provider's own guard drops them, not an upstream end-bound filter.
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var bar in bars)
            {
                if (tickers.Contains(bar.Ticker, StringComparer.OrdinalIgnoreCase))
                {
                    yield return bar;
                }
            }

            await Task.CompletedTask;
        }
    }
}
