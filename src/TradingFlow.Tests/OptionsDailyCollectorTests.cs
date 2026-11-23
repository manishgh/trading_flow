using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Application.Preparation;
using TradingFlow.Contracts.Preparation;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Market;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

/// <summary>
/// Daily options readings (step 4 of docs/design-review/2026-11-23/vix-and-options-plan.md): one line per stock and session
/// with request and receipt times, failures kept as failures, resumable after a restart, nothing on market holidays or before
/// 16:30 New York, and no code besides the collector, its schedule and its command reads the folder.
/// </summary>
public sealed class OptionsDailyCollectorTests
{
    private static readonly DateOnly Session = new(2026, 11, 24);

    // Each stored line is the source's own reading with its times; a provider error reported as an unavailable reading, and a
    // thrown request, are failed lines with the reason and no values.
    [Fact]
    public async Task Collect_StoresTheSourceReadingPerStockAndKeepsFailuresAsFailures()
    {
        using var folder = new TemporaryFolder();
        var clock = new StepClock(new DateTimeOffset(2026, 11, 24, 21, 30, 0, TimeSpan.Zero));
        var source = new FakeSource(clock);

        var result = await new OptionsDailyCollector(source, clock).CollectAsync(folder.Path, Session, ["aapl", "NONE", "BOOM", "AAPL"], null, default);

        Assert.Equal((3, 1, 2, 0), (result.Symbols, result.Ok, result.Failed, result.AlreadyStored));
        var lines = ReadLines(OptionsDailyCollector.FilePath(folder.Path, Session));
        Assert.Equal(["AAPL", "NONE", "BOOM"], lines.Select(line => line.Symbol));
        var aapl = lines[0];
        Assert.Equal(OptionsDailyCollector.Ok, aapl.Status);
        Assert.Equal(source.Readings["AAPL"], aapl.Reading);
        Assert.True(aapl.ReceivedAtUtc > aapl.RequestedAtUtc);
        var boom = lines[2];
        Assert.Equal((OptionsDailyCollector.Failed, (OptionsSentimentSnapshot?)null), (boom.Status, boom.Reading));
        Assert.Contains("provider closed the connection", boom.Failure, StringComparison.Ordinal);
        var none = lines[1];
        Assert.Equal((OptionsDailyCollector.Failed, (OptionsSentimentSnapshot?)null, "No option chain."), (none.Status, none.Reading, none.Failure));
        Assert.Equal(Path.Combine(folder.Path, "2026", "2026-11-24.jsonl"), result.FilePath);
    }

    // A restart asks only for stocks without a line; a half-written last line is asked again and the file stays readable.
    [Fact]
    public async Task Collect_ResumesWithoutRepeatingStoredStocksAndRecoversATornLastLine()
    {
        using var folder = new TemporaryFolder();
        var clock = new StepClock(new DateTimeOffset(2026, 11, 24, 21, 30, 0, TimeSpan.Zero));
        var source = new FakeSource(clock);
        var collector = new OptionsDailyCollector(source, clock);
        await collector.CollectAsync(folder.Path, Session, ["AAPL"], null, default);
        var path = OptionsDailyCollector.FilePath(folder.Path, Session);
        File.AppendAllText(path, "{\"sessionDate\":\"2026-11-24\",\"symbol\":\"MSFT\",\"sta");

        var resumed = await collector.CollectAsync(folder.Path, Session, ["AAPL", "MSFT", "NVDA"], null, default);

        Assert.Equal((1, 2), (resumed.AlreadyStored, resumed.Ok));
        Assert.Equal(["AAPL", "MSFT", "NVDA"], source.Requests);
        var readable = File.ReadAllLines(path).Where(line => line.EndsWith('}')).Select(Parse).Select(line => line.Symbol);
        Assert.Equal(["AAPL", "MSFT", "NVDA"], readable);
    }

    // A failed stock is asked again on later passes, at most three times per session, each attempt keeping its own line.
    [Fact]
    public async Task Collect_RetriesAFailedStockUpToThreeAttempts()
    {
        using var folder = new TemporaryFolder();
        var clock = new StepClock(new DateTimeOffset(2026, 11, 24, 21, 30, 0, TimeSpan.Zero));
        var source = new FakeSource(clock);
        var collector = new OptionsDailyCollector(source, clock);

        for (var pass = 0; pass < 3; pass++)
            Assert.Equal(1, (await collector.CollectAsync(folder.Path, Session, ["AAPL", "BOOM"], null, default)).Failed);
        var last = await collector.CollectAsync(folder.Path, Session, ["AAPL", "BOOM"], null, default);

        Assert.Equal((0, 0, 1, 1), (last.Ok, last.Failed, last.AttemptsUsedUp, last.AlreadyStored));
        Assert.Equal(["AAPL", "BOOM", "BOOM", "BOOM"], source.Requests);
        Assert.Equal(3, ReadLines(OptionsDailyCollector.FilePath(folder.Path, Session)).Count(line => line.Status == OptionsDailyCollector.Failed));
    }

    // Failed stocks wait at least 30 minutes between passes and stop at their attempt limit; an unreadable screen leaves
    // the wishlist stocks collected and is named in the day's request record.
    [Fact]
    public async Task Scheduler_SpacesRetriesAndKeepsWishlistsWhenTheScreenCannotBeRead()
    {
        using var folder = new TemporaryFolder();
        var clock = new StepClock(new DateTimeOffset(2026, 11, 24, 21, 31, 0, TimeSpan.Zero)); // 16:31 New York
        var source = new FakeSource(clock);
        var scheduler = await SchedulerAsync(folder.Path, clock, source, "missing-screen", "AAPL", "BOOM");

        Assert.Equal((1, 1), Counts(await scheduler.RunOnceAsync(default)));
        clock.Set(new DateTimeOffset(2026, 11, 24, 21, 40, 0, TimeSpan.Zero));
        Assert.Null(await scheduler.RunOnceAsync(default));
        foreach (var minute in new[] { 22 * 60 + 2, 22 * 60 + 33 })
        {
            clock.Set(new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero).AddMinutes(minute));
            Assert.Equal((0, 1), Counts(await scheduler.RunOnceAsync(default)));
        }
        clock.Set(new DateTimeOffset(2026, 11, 24, 23, 4, 0, TimeSpan.Zero));
        Assert.Equal(1, (await scheduler.RunOnceAsync(default))!.AttemptsUsedUp);
        clock.Set(new DateTimeOffset(2026, 11, 24, 23, 40, 0, TimeSpan.Zero));
        Assert.Null(await scheduler.RunOnceAsync(default));
        Assert.Equal(["AAPL", "BOOM", "BOOM", "BOOM"], source.Requests);
        var record = File.ReadLines(Path.Combine(folder.Path, "research", "options-daily", "2026", "2026-11-24.requests.jsonl")).First();
        Assert.Contains("missing-screen", record, StringComparison.Ordinal);
        Assert.Contains("\"screenProblem\":\"", record, StringComparison.Ordinal);
    }

    // A site that was down at 16:30 still takes the day's first readings after 20:00; only re-asks stop there.
    [Fact]
    public async Task Scheduler_RunsAFirstPassAfterTheRetryCutOff()
    {
        using var folder = new TemporaryFolder();
        var clock = new StepClock(new DateTimeOffset(2026, 11, 25, 1, 15, 0, TimeSpan.Zero)); // 20:15 New York, 24 November
        var source = new FakeSource(clock);
        var scheduler = await SchedulerAsync(folder.Path, clock, source, "fixture-screen", "BOOM");

        var result = await scheduler.RunOnceAsync(default);

        Assert.Equal((new DateOnly(2026, 11, 24), 2, 1), (result!.SessionDate, result.Ok, result.Failed));
        clock.Set(new DateTimeOffset(2026, 11, 25, 1, 50, 0, TimeSpan.Zero));
        Assert.Null(await scheduler.RunOnceAsync(default));
    }

    private static (int Ok, int Failed) Counts(OptionsDailyCollectionResult? result) => (result!.Ok, result.Failed);

    // Weekends, holidays and the hours before 16:30 New York request nothing; a due trading day collects the active wishlist
    // stocks and the named research screen's stocks once.
    [Fact]
    public async Task Scheduler_CollectsOnlyOnTradingDaysAfterTheCloseAndOncePerDay()
    {
        using var folder = new TemporaryFolder();
        var clock = new StepClock(new DateTimeOffset(2026, 11, 26, 22, 0, 0, TimeSpan.Zero)); // Thanksgiving, 17:00 New York
        var source = new FakeSource(clock);
        var scheduler = await SchedulerAsync(folder.Path, clock, source, "fixture-screen", "msft", "AAPL");

        Assert.Null(await scheduler.RunOnceAsync(default));
        clock.Set(new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero)); // 13:00 New York, before the run time
        Assert.Null(await scheduler.RunOnceAsync(default));
        Assert.Empty(source.Requests);

        clock.Set(new DateTimeOffset(2026, 11, 27, 21, 31, 0, TimeSpan.Zero)); // 16:31 New York
        var result = await scheduler.RunOnceAsync(default);
        Assert.Equal(new DateOnly(2026, 11, 27), result!.SessionDate);
        Assert.Equal(["AAPL", "MSFT", "NVDA"], source.Requests);
        var lines = ReadLines(result.FilePath);
        var aapl = lines.Single(line => line.Symbol == "AAPL");
        Assert.Equal((201.5m, OptionsDailyCollector.RegularCloseBasis, 201.5m), (aapl.RegularClose!.Value, aapl.SpotBasis, aapl.Reading!.SpotPrice!.Value));
        var nvda = lines.Single(line => line.Symbol == "NVDA");
        Assert.Equal(((decimal?)null, OptionsDailyCollector.ProviderLatestBasis), (nvda.RegularClose, nvda.SpotBasis));
        Assert.Null(await scheduler.RunOnceAsync(default));
        Assert.Equal(3, source.Requests.Count);
    }

    // Sealed folder: apart from the collector, its schedule, its command, the path and the host wiring, no source names it.
    [Fact]
    public void OptionsDailyReadings_AreReadByNoStrategyGateOrDeskCode()
    {
        var root = TestRepository.FindRoot();
        string[] allowed =
        [
            Path.Combine("TradingFlow.Engine", "Market", "OptionsDailyCollector.cs"),
            Path.Combine("TradingFlow.Web", "Services", "OptionsDailyCollection.cs"),
            Path.Combine("TradingFlow.Web", "Services", "RuntimeHostedServiceRegistration.cs"),
            Path.Combine("TradingFlow.Web", "Services", "ProjectPaths.cs"),
            Path.Combine("TradingFlow.Web", "Program.cs"),
            Path.Combine("TradingFlow.Cli", "OptionsDailyCommand.cs"),
            Path.Combine("TradingFlow.Cli", "Program.cs"),
        ];
        var readers = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}TradingFlow.Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path) is var text &&
                (text.Contains("OptionsDaily", StringComparison.Ordinal) || text.Contains("options-daily", StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(Path.Combine(root, "src"), path))
            .Where(path => !allowed.Contains(path, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(readers);
    }

    private static async Task<OptionsDailyCollectionScheduler> SchedulerAsync(
        string dataRoot, StepClock clock, FakeSource source, string screenName, params string[] wishlistTickers)
    {
        await SyntheticScreen.PublishAsync(Path.Combine(dataRoot, "research", "screens", "fixture-screen"),
            [("2026-11-02", "NVDA", "sec-nvda"), ("2026-11-02", "AAPL", "sec-aapl")]);
        var lists = new Mock<IWishlistRepository>();
        var wishlist = new Wishlist { Id = Guid.NewGuid(), Name = "long" };
        foreach (var ticker in wishlistTickers)
            wishlist.Items.Add(new WishlistItem { Ticker = ticker, Active = true });
        wishlist.Items.Add(new WishlistItem { Ticker = "OLD", Active = false });
        lists.Setup(repository => repository.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([wishlist]);
        var paths = new ProjectPaths(dataRoot, dataRoot, Path.Combine(dataRoot, "cache"));
        return new OptionsDailyCollectionScheduler(
            new OptionsDailyCollectionOptions(true, true, true, false, screenName, new TimeOnly(16, 30), new TimeOnly(20, 0)),
            new Calendar(clock), lists.Object, new ResearchScreenSources(paths), new Lazy<IMarketDataProvider>(() => new CloseBars()),
            new OptionsDailyCollector(source, clock), paths, clock,
            NullLogger<OptionsDailyCollectionScheduler>.Instance);
    }

    private static List<OptionsDailyReading> ReadLines(string path) => File.ReadAllLines(path).Select(Parse).ToList();

    private static OptionsDailyReading Parse(string line) =>
        JsonSerializer.Deserialize<OptionsDailyReading>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    // Answers AAPL/MSFT/NVDA with a fixed reading, NONE as unavailable and throws for BOOM; each answer moves the clock on.
    private sealed class FakeSource(StepClock clock) : IOptionsSentimentSource
    {
        public Dictionary<string, OptionsSentimentSnapshot> Readings { get; } = [];
        public List<string> Requests { get; } = [];

        public Task<OptionsSentimentSnapshot> GetSnapshotAsync(string symbol, decimal? spotPriceHint = null, decimal? adr20Pct = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(symbol);
            clock.Advance(TimeSpan.FromMilliseconds(250));
            if (symbol == "BOOM")
                throw new HttpRequestException("The provider closed the connection.".ToLowerInvariant());
            if (symbol == "NONE")
                return Task.FromResult(OptionsSentimentSnapshot.Unavailable(symbol, clock.GetUtcNow(), "No option chain."));
            var reading = new OptionsSentimentSnapshot(symbol, clock.GetUtcNow(), spotPriceHint ?? 190m, new DateOnly(2026, 11, 28), 3, 2.1m, 31.5m,
                0.04m, -0.012m, 1200m, 800m, 1.5m, null, 14, "neutral", "fixture reading");
            Readings[symbol] = reading;
            return Task.FromResult(reading);
        }
    }

    // Regular-session closing bars: AAPL 15:55-16:00 New York closes at 201.50 and also has an after-hours 16:00 bar, MSFT
    // closes at 410, NVDA has no closing bar (the reading falls back to the provider's own price).
    private sealed class CloseBars : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes,
            DateTimeOffset start, DateTimeOffset end, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new OhlcvBar("AAPL", start, "5m", 201m, 202m, 200m, 201.5m, 1_000m);
            yield return new OhlcvBar("AAPL", end, "5m", 208m, 209m, 207m, 208.5m, 300m);
            yield return new OhlcvBar("MSFT", start, "5m", 409m, 411m, 408m, 410m, 900m);
        }
    }

    private sealed class StepClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan step) => current += step;
        public void Set(DateTimeOffset value) => current = value;
    }

    // Weekdays trade except Thanksgiving 2026; 27 November closes early.
    private sealed class Calendar(TimeProvider clock) : IPreparationCalendar
    {
        public Task<PreparationCalendar> LoadAsync(DateOnly start, DateOnly end, CancellationToken token) =>
            Task.FromResult(new PreparationCalendar(clock.GetUtcNow(), "fixture",
                Enumerable.Range(0, end.DayNumber - start.DayNumber + 1).Select(start.AddDays)
                    .Select(date => new PreparationSession(date,
                        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && date != new DateOnly(2026, 11, 26),
                        PreparationCalendarAdapter.Utc(date, new TimeOnly(9, 30)),
                        PreparationCalendarAdapter.Utc(date, new TimeOnly(date == new DateOnly(2026, 11, 27) ? 13 : 16, 0))))
                    .ToArray()));
    }
}
