using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Data.Earnings;
using TradingFlow.Domain.Earnings;

namespace TradingFlow.Tests;

public sealed class EarningsRepositoryTests
{
    [Fact]
    public async Task ReplaceWindow_UpdatesProviderResultAndPreservesFirstSeen()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(connection).Options;
        await using (var db = new TradingFlowDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var repository = new SqliteEarningsRepository(new TestDbContextFactory(options));
        var firstSeen = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        var initial = CreateEvent(firstSeen, null);
        await repository.ReplaceProviderWindowAsync(
            "finviz",
            initial.ReportDateExchange,
            initial.ReportDateExchange,
            [initial],
            CancellationToken.None);

        var updated = CreateEvent(firstSeen.AddMinutes(30), 7.5m);
        await repository.ReplaceProviderWindowAsync(
            "finviz",
            updated.ReportDateExchange,
            updated.ReportDateExchange,
            [updated],
            CancellationToken.None);

        var stored = Assert.Single(await repository.GetCalendarAsync(
            updated.ReportDateExchange,
            updated.ReportDateExchange,
            ["TEST"],
            CancellationToken.None));
        Assert.Equal(firstSeen, stored.FirstSeenAtUtc);
        Assert.Equal(firstSeen.AddMinutes(30), stored.LastSeenAtUtc);
        Assert.Equal(firstSeen.AddMinutes(30), stored.ResultFirstSeenAtUtc);
        Assert.Equal(7.5m, stored.EpsSurprisePercent);
    }

    [Fact]
    public async Task LatestAnalysis_ReturnsMostRecentSnapshotPerEvent()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(connection).Options;
        await using (var db = new TradingFlowDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var repository = new SqliteEarningsRepository(new TestDbContextFactory(options));
        var calendarEvent = CreateEvent(DateTimeOffset.Parse("2026-07-31T12:00:00Z"), 3m);
        await repository.ReplaceProviderWindowAsync(
            "finviz",
            calendarEvent.ReportDateExchange,
            calendarEvent.ReportDateExchange,
            [calendarEvent],
            CancellationToken.None);
        await repository.UpsertAnalysisAsync(CreateAnalysis(calendarEvent.Id, new DateTimeOffset(2026, 7, 31, 12, 35, 0, TimeSpan.Zero), EarningsBreakoutAssessment.NotConfirmed), CancellationToken.None);
        await repository.UpsertAnalysisAsync(CreateAnalysis(calendarEvent.Id, new DateTimeOffset(2026, 7, 31, 12, 40, 0, TimeSpan.Zero), EarningsBreakoutAssessment.Possible), CancellationToken.None);

        var latest = await repository.GetLatestAnalysesAsync([calendarEvent.Id], CancellationToken.None);

        Assert.Equal(EarningsBreakoutAssessment.Possible, latest[calendarEvent.Id].BreakoutAssessment);
    }

    [Fact]
    public async Task CalendarTickerFilter_SupportsWishlistLargerThanSqliteParameterLimit()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(connection).Options;
        await using (var db = new TradingFlowDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var repository = new SqliteEarningsRepository(new TestDbContextFactory(options));
        var calendarEvent = CreateEvent(DateTimeOffset.Parse("2026-07-31T12:00:00Z"), 3m);
        await repository.ReplaceProviderWindowAsync(
            "finviz",
            calendarEvent.ReportDateExchange,
            calendarEvent.ReportDateExchange,
            [calendarEvent],
            CancellationToken.None);
        var tickers = Enumerable.Range(0, 1200)
            .Select(index => $"T{index:D4}")
            .Append("TEST")
            .ToArray();

        var result = await repository.GetCalendarAsync(
            calendarEvent.ReportDateExchange,
            calendarEvent.ReportDateExchange,
            tickers,
            CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("TEST", result[0].Ticker);
    }

    private static EarningsCalendarEvent CreateEvent(DateTimeOffset observedAt, decimal? surprise) => new()
    {
        Id = "finviz:test",
        Ticker = "TEST",
        CompanyName = "Test Corp",
        ReportDateExchange = new DateOnly(2026, 7, 31),
        ScheduledAtUtc = DateTimeOffset.Parse("2026-07-31T12:30:00Z"),
        ReleaseWindow = EarningsReleaseWindow.BeforeMarketOpen,
        EpsSurprisePercent = surprise,
        ResultFirstSeenAtUtc = surprise.HasValue ? observedAt : null,
        Provider = "finviz",
        SourceUrl = "https://example.com",
        SourceArtifactSha256 = new string('a', 64),
        ProviderReceivedAtUtc = observedAt,
        FirstSeenAtUtc = observedAt,
        LastSeenAtUtc = observedAt
    };

    private static EarningsAnalysisSnapshot CreateAnalysis(
        string eventId,
        DateTimeOffset analyzedAt,
        EarningsBreakoutAssessment assessment) => new()
    {
        EarningsEventId = eventId,
        Ticker = "TEST",
        AnalyzedAtUtc = analyzedAt,
        ResultAssessment = EarningsResultAssessment.Positive,
        BreakoutAssessment = assessment,
        Reason = "test"
    };

    private sealed class TestDbContextFactory(DbContextOptions<TradingFlowDbContext> options)
        : IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);
    }
}
