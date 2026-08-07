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
    public async Task NewsResult_FillsAfterCloseActualAndSurvivesTheNextProviderRefresh()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(connection).Options;
        await using (var db = new TradingFlowDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var repository = new SqliteEarningsRepository(new TestDbContextFactory(options));
        var observedAt = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        // The provider knows the estimate but publishes no actual on the evening of the release.
        var scheduled = CreateEvent(observedAt, null);
        scheduled.EpsEstimate = 1.25m;
        await repository.ReplaceProviderWindowAsync(
            "finviz",
            scheduled.ReportDateExchange,
            scheduled.ReportDateExchange,
            [scheduled],
            CancellationToken.None);

        var headlineTime = DateTimeOffset.Parse("2026-07-31T12:33:00Z");
        var applied = await repository.TryApplyNewsResultAsync(
            "TEST",
            headlineTime,
            new EarningsNewsResult(1.20m, 1.37m, 14.17m, null, null, null),
            CancellationToken.None);

        Assert.True(applied);
        var stored = Assert.Single(await repository.GetCalendarAsync(
            scheduled.ReportDateExchange,
            scheduled.ReportDateExchange,
            ["TEST"],
            CancellationToken.None));
        Assert.Equal(1.37m, stored.EpsActual);
        // The headline's own estimate and surprise come with it. Keeping the provider estimate of
        // 1.25 here would report a 9.6% surprise that neither source states.
        Assert.Equal(1.20m, stored.EpsEstimate);
        Assert.Equal(14.17m, stored.EpsSurprisePercent);
        Assert.Equal(headlineTime, stored.ResultFirstSeenAtUtc);

        // The provider refresh still reports no actual; it must not erase what the headline gave us.
        var refreshed = CreateEvent(observedAt.AddMinutes(30), null);
        refreshed.EpsEstimate = 1.25m;
        await repository.ReplaceProviderWindowAsync(
            "finviz",
            refreshed.ReportDateExchange,
            refreshed.ReportDateExchange,
            [refreshed],
            CancellationToken.None);

        var afterRefresh = Assert.Single(await repository.GetCalendarAsync(
            refreshed.ReportDateExchange,
            refreshed.ReportDateExchange,
            ["TEST"],
            CancellationToken.None));
        Assert.Equal(1.37m, afterRefresh.EpsActual);
        Assert.Equal(1.20m, afterRefresh.EpsEstimate);
        Assert.Equal(14.17m, afterRefresh.EpsSurprisePercent);

        // Once the provider publishes its own result it is authoritative and takes the whole set.
        var reported = CreateEvent(observedAt.AddHours(2), null);
        reported.EpsEstimate = 1.25m;
        reported.EpsActual = 1.36m;
        reported.EpsSurprisePercent = 8.8m;
        await repository.ReplaceProviderWindowAsync(
            "finviz",
            reported.ReportDateExchange,
            reported.ReportDateExchange,
            [reported],
            CancellationToken.None);

        var afterProviderResult = Assert.Single(await repository.GetCalendarAsync(
            reported.ReportDateExchange,
            reported.ReportDateExchange,
            ["TEST"],
            CancellationToken.None));
        Assert.Equal(1.36m, afterProviderResult.EpsActual);
        Assert.Equal(1.25m, afterProviderResult.EpsEstimate);
        Assert.Equal(8.8m, afterProviderResult.EpsSurprisePercent);
    }

    [Fact]
    public async Task NewsResult_DoesNotOverwriteAnActualTheProviderAlreadyPublished()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(connection).Options;
        await using (var db = new TradingFlowDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var repository = new SqliteEarningsRepository(new TestDbContextFactory(options));
        var observedAt = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        var reported = CreateEvent(observedAt, 7.5m);
        reported.EpsEstimate = 1.25m;
        reported.EpsActual = 1.34m;
        await repository.ReplaceProviderWindowAsync(
            "finviz",
            reported.ReportDateExchange,
            reported.ReportDateExchange,
            [reported],
            CancellationToken.None);

        var applied = await repository.TryApplyNewsResultAsync(
            "TEST",
            DateTimeOffset.Parse("2026-07-31T12:33:00Z"),
            new EarningsNewsResult(1.20m, 1.37m, 14.17m, null, null, null),
            CancellationToken.None);

        Assert.False(applied);
        var stored = Assert.Single(await repository.GetCalendarAsync(
            reported.ReportDateExchange,
            reported.ReportDateExchange,
            ["TEST"],
            CancellationToken.None));
        Assert.Equal(1.34m, stored.EpsActual);
        Assert.Equal(7.5m, stored.EpsSurprisePercent);
    }

    [Fact]
    public async Task NewsResult_IgnoresHeadlinesWithNoNearbyScheduledEvent()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(connection).Options;
        await using (var db = new TradingFlowDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var repository = new SqliteEarningsRepository(new TestDbContextFactory(options));
        var calendarEvent = CreateEvent(DateTimeOffset.Parse("2026-07-31T12:00:00Z"), null);
        await repository.ReplaceProviderWindowAsync(
            "finviz",
            calendarEvent.ReportDateExchange,
            calendarEvent.ReportDateExchange,
            [calendarEvent],
            CancellationToken.None);

        // A quarter away from the scheduled event, so it belongs to a report we do not track.
        Assert.False(await repository.TryApplyNewsResultAsync(
            "TEST",
            DateTimeOffset.Parse("2026-10-30T12:33:00Z"),
            new EarningsNewsResult(1.20m, 1.37m, 14.17m, null, null, null),
            CancellationToken.None));

        Assert.False(await repository.TryApplyNewsResultAsync(
            "OTHER",
            DateTimeOffset.Parse("2026-07-31T12:33:00Z"),
            new EarningsNewsResult(1.20m, 1.37m, 14.17m, null, null, null),
            CancellationToken.None));
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
