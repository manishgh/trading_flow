using TradingFlow.Cli;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Universe;
using TradingFlow.Research.Signals;

namespace TradingFlow.Tests;

/// <summary>
/// The research-signal-study command on a synthetic evidence catalog (as-traded daily bars, XNYS sessions, corporate actions)
/// with Connors 9.1.0 (as-traded, no stop) and a published eligible-list screen for 1 June to 28 September 2018. Weekday
/// sessions from 2 January 2017; each stock rises 0.05 a session and opens 0.20 above its prior close. AAA closes 3.00 below
/// trend on 10 July 2018 (RSI(2) under 5, above its SMA200) and 0.60 above trend from 11 July (above its SMA5). EEE falls the
/// same way on 10 July and lower on 11 and 12 July, and splits 2-for-1 with ex-date 13 July. BBB rises throughout.
/// </summary>
public sealed class SignalStudyCommandTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateOnly SignalDay = new(2018, 7, 10);
    private static readonly DateOnly ExDate = new(2018, 7, 13);
    private static readonly DateOnly From = new(2018, 6, 1);
    private static readonly DateOnly To = new(2018, 9, 28);
    private static readonly DateOnly[] Sessions = Enumerable.Range(new DateOnly(2017, 1, 2).DayNumber, new DateOnly(2018, 12, 31).DayNumber - new DateOnly(2017, 1, 2).DayNumber + 1)
        .Select(DateOnly.FromDayNumber).Where(day => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToArray();
    private static readonly string[] Pool = ["AAA", "BBB", "EEE"];

    // Next-open entry: AAA's 10 July signal enters at the 11 July open and its SMA5 exit on the 11 July close fills at the
    // 12 July open; the net return charges 10 bps on both sides; the benchmark is the eligible stocks' equal-weight return.
    [Fact]
    public async Task KnownSignal_EntersAtTheNextOpenAndExitsAtTheOpenAfterItsExitBar()
    {
        using var market = new SyntheticEvidenceCatalog();
        using var output = new TemporaryFolder();
        var rows = await RunAsync(market, output.Path, SignalStudyEntry.NextOpen);

        var aaa = Assert.Single(rows, row => row["ticker"] == "AAA");
        Assert.Equal(("2018-07-10", "2018-07-11", "2018-07-12", "technical_exit_above_sma5", "1"),
            (aaa["signal_date"], aaa["entry_date"], aaa["exit_date"], aaa["exit_reason"], aaa["holding_days"]));
        var gross = (double)(Open("AAA", Day(2018, 7, 12)) / Open("AAA", Day(2018, 7, 11))) - 1;
        Assert.Equal(gross, Number(aaa["gross_return"]), 7);
        Assert.Equal((1 + gross) * 0.999 * 0.999 - 1, Number(aaa["net_return"]), 7); // cost charged on entry and on exit
        var universe = Pool.Average(ticker => (double)(Open(ticker, Day(2018, 7, 12)) / Open(ticker, Day(2018, 7, 11))) - 1);
        Assert.Equal(universe, Number(aaa["universe_return"]), 7);
        var manifest = File.ReadAllText(Path.Combine(output.Path, "study", SignalStudyCommand.ManifestFileName));
        Assert.Contains("\"lastBarRead\": \"2018-09-28\"", manifest);
        Assert.Contains("swing.connors-rsi2@9.1.0", manifest);
    }

    // Same-close entry (the declared upper bound): AAA enters at the 10 July close; its one holding day runs from that close
    // to the 12 July open, with the same exit.
    [Fact]
    public async Task SameCloseEntry_StartsFromTheSignalClose()
    {
        using var market = new SyntheticEvidenceCatalog();
        using var output = new TemporaryFolder();
        var rows = await RunAsync(market, output.Path, SignalStudyEntry.SameClose);

        var aaa = Assert.Single(rows, row => row["ticker"] == "AAA");
        Assert.Equal(("2018-07-10", "2018-07-12"), (aaa["entry_date"], aaa["exit_date"]));
        Assert.Equal((double)(Open("AAA", Day(2018, 7, 12)) / Close("AAA", Day(2018, 7, 10))) - 1, Number(aaa["gross_return"]), 7);
    }

    // EEE's 10 July trade is closed at the 12 July open, the last open before its 13 July ex-date, and counted; its 11 July
    // signal, whose entry open is that last open, is left out and counted.
    [Fact]
    public async Task SplitDuringAHold_ClosesAtTheLastOpenBeforeTheExDate()
    {
        using var market = new SyntheticEvidenceCatalog();
        using var output = new TemporaryFolder();
        var rows = await RunAsync(market, output.Path, SignalStudyEntry.NextOpen);

        var split = Assert.Single(rows, row => row["ticker"] == "EEE" && row["signal_date"] == "2018-07-10");
        Assert.Equal(("2018-07-12", SignalStudyOutcomes.SplitExitReason, "1"), (split["exit_date"], split["exit_reason"], split["holding_days"]));
        var manifest = File.ReadAllText(Path.Combine(output.Path, "study", SignalStudyCommand.ManifestFileName));
        Assert.Contains($"\"{SignalStudyOutcomes.SplitClosedCount}\": 1", manifest);
        Assert.Contains($"\"{SignalStudyOutcomes.SplitAtEntryCount}\": 1", manifest);
    }

    // A window reaching past the development years (or before them) is refused before any bar is read.
    [Theory]
    [InlineData("2018-06-01", "2024-01-31")]
    [InlineData("2016-06-01", "2018-09-28")]
    public async Task WindowOutsideTheDevelopmentYears_IsRefused(string from, string to)
    {
        using var market = new SyntheticEvidenceCatalog();
        using var output = new TemporaryFolder();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(market, output.Path, SignalStudyEntry.NextOpen,
            DateOnly.Parse(from), DateOnly.Parse(to)));
        Assert.Contains("development years", error.Message);
        Assert.False(File.Exists(Path.Combine(output.Path, "study", SignalStudyCommand.ManifestFileName)));
    }

    // The same inputs give byte-identical reports, tables and manifests.
    [Fact]
    public async Task SameInputs_GiveIdenticalOutputs()
    {
        using var market = new SyntheticEvidenceCatalog();
        using var first = new TemporaryFolder();
        using var second = new TemporaryFolder();
        await RunAsync(market, first.Path, SignalStudyEntry.NextOpen, repeatInto: Path.Combine(second.Path, "study"));

        var files = Directory.GetFiles(Path.Combine(first.Path, "study")).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(4, files.Length);
        foreach (var file in files)
            Assert.Equal(File.ReadAllText(Path.Combine(first.Path, "study", file!)), File.ReadAllText(Path.Combine(second.Path, "study", file!)));
    }

    // Publishes the fixture into the catalog, publishes the screen, runs the study into folder/study (and again with the same
    // inputs into repeatInto when given) and returns the outcome rows.
    private static async Task<IReadOnlyList<Dictionary<string, string>>> RunAsync(SyntheticEvidenceCatalog market, string folder,
        SignalStudyEntry entry, DateOnly? from = null, DateOnly? to = null, string? repeatInto = null)
    {
        var bars = await market.BarsAsync("1d", null, Pool.SelectMany(ticker => Sessions.Select(day =>
            SyntheticEvidenceCatalog.Row(ticker, "1d", Midnight(day), Open(ticker, day), Math.Max(Open(ticker, day), Close(ticker, day)) + 0.5m,
                Math.Min(Open(ticker, day), Close(ticker, day)) - 0.5m, Close(ticker, day), 2_000_000))).ToArray());
        var actions = await market.SplitsAsync([("EEE", ExDate, 2m)]);
        var sessions = await market.SessionsAsync(new DateOnly(2016, 12, 1), new DateOnly(2019, 1, 31));
        var screen = await PublishScreenAsync(Path.Combine(folder, "screen"), bars.DatasetId, actions.DatasetId, sessions.DatasetId);
        var strategy = RetiredStrategyFixtures.Strategy("swing-connors-rsi2-published-as-traded.bt-v9-1.yaml");
        var request = new SignalStudyCommand.Request(market.CatalogPath, market.ObjectRoot, screen.Folder, bars.DatasetId, actions.DatasetId,
            [strategy], entry, 10m, from ?? From, to ?? To, Path.Combine(folder, "study"));
        var provider = market.Provider(new(market.CatalogPath, market.ObjectRoot, screen.RankedListSha256, bars.DatasetId, sessions.DatasetId, [], [],
            actions.DatasetId));
        await SignalStudyCommand.RunAsync(request, screen, provider, TextWriter.Null, default);
        if (repeatInto is not null) await SignalStudyCommand.RunAsync(request with { OutFolder = repeatInto }, screen, provider, TextWriter.Null, default);
        var csv = Assert.Single(Directory.GetFiles(Path.Combine(folder, "study"), "*-outcomes.csv"));
        var lines = File.ReadAllLines(csv);
        var header = lines[0].Split(',');
        return lines.Skip(1).Select(line => header.Zip(line.Split(','), (name, value) => (name, value)).ToDictionary(pair => pair.name, pair => pair.value)).ToArray();
    }

    // An eligible list (prior-close rule) naming every pool stock on each window session, except EEE from its ex-date on.
    private static async Task<PublishedResearchScreen> PublishScreenAsync(string folder, string bars, string actions, string sessions)
    {
        Directory.CreateDirectory(folder);
        var days = Sessions.Where(day => day >= From && day <= To).ToArray();
        await File.WriteAllLinesAsync(Path.Combine(folder, "eligible.csv"),
            new[] { "session_date,ticker,price,average_volume,average_dollar_volume,atr,history_sessions" }.Concat(days.SelectMany(day =>
                Pool.Where(ticker => ticker != "EEE" || day < ExDate).Select(ticker => $"{day:yyyy-MM-dd},{ticker},60,2000000,120000000,1,300"))));
        await File.WriteAllLinesAsync(Path.Combine(folder, DailyScreenCommand.SplitExclusionsFileName), ["session_date,ticker,reason"]);
        await ResearchScreenManifest.PublishAsync(folder, new(bars, actions, null, sessions),
            SyntheticScreen.Rules with { PriceBasis = DailyScreenPriceBasis.PriorClose }, null, days[0], days[^1],
            "alpaca_security_id", null, default, new ResearchScreenSplitRule(200, 0));
        return PublishedResearchScreen.Read(folder);
    }

    // A stock's close on a session (see the class summary).
    private static decimal Close(string ticker, DateOnly day)
    {
        var i = Array.IndexOf(Sessions, day);
        var trend = (ticker == "BBB" ? 30m : 50m) + 0.05m * i;
        return (ticker, day) switch
        {
            ("AAA", var d) when d == SignalDay => trend - 3m,
            ("AAA", var d) when d > SignalDay => trend + 0.6m,
            ("EEE", var d) when d == SignalDay => trend - 3m,
            ("EEE", var d) when d == SignalDay.AddDays(1) => trend - 3.2m,
            ("EEE", var d) when d == SignalDay.AddDays(2) => trend - 3.4m,
            ("EEE", var d) when d >= ExDate => (trend - 3.6m) / 2m,
            _ => trend
        };
    }

    // A stock's open: 0.20 above its prior close (the first session opens at its close); halved on the split ex-date.
    private static decimal Open(string ticker, DateOnly day)
    {
        var i = Array.IndexOf(Sessions, day);
        if (i == 0) return Close(ticker, day);
        var open = Close(ticker, Sessions[i - 1]) + 0.2m;
        return ticker == "EEE" && day == ExDate ? open / 2m : open;
    }

    private static DateOnly Day(int year, int month, int day) => new(year, month, day);

    private static double Number(string text) => Double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    private static DateTimeOffset Midnight(DateOnly day) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), NewYork), TimeSpan.Zero);
}
