using TradingFlow.Domain.Market;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Regime;

namespace TradingFlow.Tests;

public sealed class RegimeCalendarTests
{
    [Fact]
    public void Resample_IntradayToDaily_ThenBuild_MatchesNativeDailyRegime()
    {
        // A benchmark cache may hold only intraday bars (e.g. SPY 5m). Resampling them to daily must
        // reproduce the same regime calendar as native daily bars, so the regime gate works offline
        // from intraday — this is the fallback path in BacktestRunner.Regime.LoadBenchmarkDailyBarsAsync.
        var closes = new (string Date, decimal Close)[]
        {
            ("2026-06-01", 10m), ("2026-06-02", 10m), ("2026-06-03", 10m), ("2026-06-04", 20m),
            ("2026-06-05", 20m), ("2026-06-06", 5m), ("2026-06-07", 5m), ("2026-06-08", 5m),
        };

        var intraday = new List<OhlcvBar>();
        foreach (var (date, close) in closes)
        {
            // Three intraday bars within the same UTC date; the last bar's close is the session close.
            intraday.Add(Intraday(date, 14, close - 1m));
            intraday.Add(Intraday(date, 15, close + 2m));
            intraday.Add(Intraday(date, 16, close));
        }

        var daily = new BarResampler().Resample(intraday, "1d");
        var calendar = RegimeCalendarBuilder.Build(daily, smaPeriod: 3);

        // Same on/off days as Build_RegimeOn_UsesOnlyPriorCloses_NoLookahead, which uses native daily bars.
        Assert.True(calendar.IsActive);
        Assert.False(calendar.IsOn(new DateOnly(2026, 6, 4)));
        Assert.True(calendar.IsOn(new DateOnly(2026, 6, 5)));
        Assert.True(calendar.IsOn(new DateOnly(2026, 6, 6)));
        Assert.False(calendar.IsOn(new DateOnly(2026, 6, 7)));
    }

    [Fact]
    public void Build_RegimeOn_UsesOnlyPriorCloses_NoLookahead()
    {
        // SMA period 3. Closes engineered so day D4 is itself a big up day, but the regime on D4
        // is decided from D1..D3 (all flat at 10) -> OFF. That D4's own close is not used proves
        // the no-lookahead property.
        var bars = new[]
        {
            Daily("2026-06-01", 10m),
            Daily("2026-06-02", 10m),
            Daily("2026-06-03", 10m),
            Daily("2026-06-04", 20m),
            Daily("2026-06-05", 20m),
            Daily("2026-06-06", 5m),
            Daily("2026-06-07", 5m),
            Daily("2026-06-08", 5m),
        };

        var calendar = RegimeCalendarBuilder.Build(bars, smaPeriod: 3);

        Assert.True(calendar.IsActive);
        // Insufficient prior history -> off.
        Assert.False(calendar.IsOn(new DateOnly(2026, 6, 1)));
        // D4: prior window D1..D3 = 10, SMA 10, prior close 10 -> not above -> OFF (its own 20 ignored).
        Assert.False(calendar.IsOn(new DateOnly(2026, 6, 4)));
        // D5: prior window D2..D4 includes the 20, prior close 20 > SMA ~13.3 -> ON.
        Assert.True(calendar.IsOn(new DateOnly(2026, 6, 5)));
        // D6: prior close 20 > SMA ~16.7 -> ON.
        Assert.True(calendar.IsOn(new DateOnly(2026, 6, 6)));
        // D7: prior close 5 < SMA 15 -> OFF.
        Assert.False(calendar.IsOn(new DateOnly(2026, 6, 7)));
        Assert.False(calendar.IsOn(new DateOnly(2026, 6, 8)));
    }

    [Fact]
    public void IsRegimeOnAsOf_MatchesCalendar_AndIsRobustToMissingTodayBar()
    {
        var bars = new[]
        {
            Daily("2026-06-01", 10m),
            Daily("2026-06-02", 10m),
            Daily("2026-06-03", 10m),
            Daily("2026-06-04", 20m),
            Daily("2026-06-05", 20m),
            Daily("2026-06-06", 5m),
            Daily("2026-06-07", 5m),
            Daily("2026-06-08", 5m),
        };
        var calendar = RegimeCalendarBuilder.Build(bars, smaPeriod: 3);

        // The point query equals the calendar for days that have a bar.
        foreach (var day in new[] { new DateOnly(2026, 6, 5), new DateOnly(2026, 6, 6), new DateOnly(2026, 6, 7) })
        {
            Assert.Equal(calendar.IsOn(day), RegimeCalendarBuilder.IsRegimeOnAsOf(bars, 3, day));
        }

        // D5: prior window D2..D4 last close 20 > SMA ~13.3 -> on.
        Assert.True(RegimeCalendarBuilder.IsRegimeOnAsOf(bars, 3, new DateOnly(2026, 6, 5)));
        // D7: prior close 5 < SMA 15 -> off.
        Assert.False(RegimeCalendarBuilder.IsRegimeOnAsOf(bars, 3, new DateOnly(2026, 6, 7)));

        // Robust to a missing "today" bar: querying 2026-06-09 (no bar) uses closes through 06-08
        // (5,5,5 -> SMA 5, latest 5 not above) -> off, without needing a bar dated 06-09. This is the
        // live case where today's daily bar is still forming.
        Assert.False(RegimeCalendarBuilder.IsRegimeOnAsOf(bars, 3, new DateOnly(2026, 6, 9)));

        // Insufficient prior history -> off.
        Assert.False(RegimeCalendarBuilder.IsRegimeOnAsOf(bars, 3, new DateOnly(2026, 6, 2)));
    }

    [Fact]
    public void Build_ZeroPeriodOrNoBars_IsInactive_AlwaysOn()
    {
        Assert.False(RegimeCalendarBuilder.Build(Array.Empty<OhlcvBar>(), 50).IsActive);
        Assert.True(RegimeCalendarBuilder.Build(Array.Empty<OhlcvBar>(), 50).IsOn(new DateOnly(2026, 6, 1)));
        Assert.False(RegimeCalendarBuilder.Build(new[] { Daily("2026-06-01", 10m) }, 0).IsActive);
    }

    [Fact]
    public void Inactive_IsAlwaysOn()
    {
        Assert.True(RegimeCalendar.Inactive.IsOn(new DateOnly(2000, 1, 1)));
        Assert.False(RegimeCalendar.Inactive.IsActive);
    }

    private static OhlcvBar Daily(string date, decimal close)
    {
        var ts = new DateTimeOffset(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture), TimeOnly.MinValue, TimeSpan.Zero);
        return new OhlcvBar("SPY", ts, "1d", close, close, close, close, 1_000_000m);
    }

    private static OhlcvBar Intraday(string date, int hourUtc, decimal close)
    {
        var day = DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture);
        var ts = new DateTimeOffset(day, new TimeOnly(hourUtc, 0), TimeSpan.Zero);
        return new OhlcvBar("SPY", ts, "5m", close, close + 0.5m, close - 0.5m, close, 10_000m);
    }
}
