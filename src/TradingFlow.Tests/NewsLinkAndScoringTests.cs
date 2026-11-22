using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TradingFlow.Data.Context;
using TradingFlow.Data.News;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

/// <summary>Link honesty, article identity, write rules and scorer provenance for stored news.</summary>
public sealed class NewsLinkAndScoringTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private DbContextOptions<TradingFlowDbContext> options = null!;
    private SqliteNewsFeedRepository repository = null!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        options = new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(connection).Options;
        await using var db = new TradingFlowDbContext(options);
        await db.Database.EnsureCreatedAsync();
        repository = new SqliteNewsFeedRepository(new Factory(options));
    }

    public async Task DisposeAsync() => await connection.DisposeAsync();

    [Theory]
    [InlineData("AMC Networks Inc. (NASDAQ:AMCX) Receives Consensus 'Strong Sell' Rating", "https://stocktwits.com/symbol/AMC/news", "related")]
    [InlineData("DoorDash Integrates Linq AI Infrastructure for Ordering", "https://fourweekmba.com/ai-linq-raises-90m-from-a16z-so-ai-agents-can-text-and-call/", "related")]
    [InlineData("Merck Announces TroFuse-015 Trial Evaluating Sacituzumab", "https://www.businesswire.com/newsroom/industry/health/pharmaceutical", "related")]
    [InlineData("Stifel Adjusts Price Target on Marvell Technology", "https://example.com/quote/stock/MARVELL-TECHNOLOGY-INC-123/", "related")]
    [InlineData("Erste Group Increases Tender Offer Price for Erste Bank Polska Shares", "https://example.com/analyse/erste-group-bank-overweight", "related")]
    [InlineData("Nvidia", "https://example.com/nvidia-unveils-chip", "related")]
    [InlineData("Marvell Technology Reports Third Quarter Results", "https://example.com/company/marvell-technology/", "related")]
    [InlineData("Stifel Adjusts Price Target on Marvell Technology", "https://www.marketscreener.com/news/stifel-adjusts-price-target-on-marvell-technology-to-280", "article")]
    [InlineData("LVMH Hosts Les Journées Particulières in Shanghai", "https://example.com/lvmh-hosts-les-journees-particulieres-2026", "article")]
    [InlineData("Any headline", "javascript:alert(1)", "none")]
    [InlineData("Any headline", null, "none")]
    public void AlpacaLink_IsTheArticleOnlyWhenItSharesTheHeadlinesWords(string headline, string? url, string expected) =>
        Assert.Equal(expected, NewsLinkMatch.Classify("alpaca", headline, url));

    /// <summary>Publishers' own links often carry only an ID, so providers other than Alpaca are trusted as given.</summary>
    [Fact]
    public void OtherProvidersLinks_AreTrustedAsGiven() =>
        Assert.Equal(NewsLinkMatch.Article, NewsLinkMatch.Classify("finviz", "Anthropic shares fall", "https://www.bbc.co.uk/news/articles/c6808ym10vzgo"));

    [Fact]
    public void RelatedLink_SaysItMayNotBeTheStoryAndNamesTheWebsite()
    {
        var item = new MobileNewsItem("DASH", DateTimeOffset.UnixEpoch, "DoorDash Integrates Linq AI Infrastructure", 0m, "alpaca", "fiscal.ai",
            "https://www.fourweekmba.com/ai-linq-raises-90m/", null);
        var shown = NewsPresentation.From(item);
        Assert.False(shown.LinkIsArticle);
        Assert.Equal("Related page, may not be this story: fourweekmba.com", shown.LinkLabel);
        Assert.NotEqual(shown.Key, NewsPresentation.From(item with { Headline = "Another story" }).Key);
    }

    [Fact]
    public void TwoAlpacaHeadlinesSharingOneLink_AreTwoArticlesInTheReadAndInTheStore()
    {
        var first = Story("alpaca", "1", "First headline", "https://example.com/list");
        var second = first with { ExternalId = "2", Headline = "Second headline" };
        Assert.NotEqual(NewsFeedService.NewsDedupeKey(first), NewsFeedService.NewsDedupeKey(second));
        Assert.NotEqual(NewsFeedService.StorageDedupeKey(first), NewsFeedService.StorageDedupeKey(second));
        // The stored row and the incoming event of the same article must agree, or every pass would store it again.
        Assert.Equal(NewsFeedService.StorageDedupeKey(first), NewsFeedService.StorageDedupeKey(
            new PersistedNewsItem { Id = SqliteNewsFeedRepository.StorageId(first), Provider = "alpaca", Ticker = "MU" }));
        Assert.Equal(NewsFeedService.StorageDedupeKey(first with { Provider = "finviz" }),
            NewsFeedService.StorageDedupeKey(second with { Provider = "finviz" }));
    }

    /// <summary>The refresh compares against every stored article of its window, not the newest few hundred.</summary>
    [Fact]
    public async Task StoredIdentities_AreReadWithoutARowLimit()
    {
        await repository.InsertNewAsync(Enumerable.Range(0, 1_200).Select(index => Story("alpaca", $"a{index}", $"Headline {index}", null)).ToArray(), default);
        Assert.Equal(1_200, (await repository.ListIdentitiesSinceAsync(DateTimeOffset.UtcNow.AddHours(-1), default)).Count);
    }

    [Fact]
    public async Task RefreshInsert_NeverChangesAnExistingRow()
    {
        var received = DateTimeOffset.UtcNow.AddMinutes(-30);
        await repository.UpsertAsync([Story("alpaca", "live", "Live article", null) with
            { ClassificationVersion = "finbert", SentimentScore = -0.7m, Summary = "live", ReceivedAt = received }], default);

        var copy = Story("alpaca", "live", "Live article", null) with
            { ClassificationVersion = "vader", SentimentScore = 0.9m, Summary = "copy", ReceivedAt = DateTimeOffset.UtcNow };
        var added = await repository.InsertNewAsync([copy, Story("finviz", "new", "New article", null)], default);
        Assert.Equal("finviz:new", Assert.Single(added));
        await repository.ApplyEnrichmentAsync([copy with { SentimentScore = 0.8m }], added, default);

        var row = await RowAsync("alpaca:live");
        Assert.Equal(-0.7m, row.SentimentScore);
        Assert.Equal("finbert", row.SentimentAnalyzer);
        Assert.Equal("live", row.Summary);
        Assert.Equal(received.ToUniversalTime().Ticks, row.IngestedAt.Ticks);
        // An article whose first scoring did not finish says so, and is picked up by re-scoring.
        Assert.Equal(SqliteNewsFeedRepository.PendingScorer, (await RowAsync("finviz:new")).SentimentAnalyzer);
    }

    [Fact]
    public async Task Enrichment_UpdatesOnlyTheRowThisRefreshAdded()
    {
        var story = Story("finviz", "1", "Fresh article", null) with { ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var added = await repository.InsertNewAsync([story], default);
        await repository.ApplyEnrichmentAsync([story with { SentimentScore = 0.4m, ClassificationVersion = "finbert", Summary = "text" }], added, default);
        var row = await RowAsync("finviz:1");
        Assert.Equal(0.4m, row.SentimentScore);
        Assert.Equal("finbert", row.SentimentAnalyzer);
        Assert.Equal(story.ReceivedAt!.Value.ToUniversalTime().Ticks, row.IngestedAt.Ticks);
    }

    [Fact]
    public async Task Rescoring_ReplacesOnlyRecordedFallbackOrPendingScores()
    {
        await repository.UpsertAsync([
            Story("alpaca", "fallback", "Alpha downgraded", null) with { ClassificationVersion = "vader", SentimentScore = 0.5m },
            Story("alpaca", "unrecorded", "Gamma downgraded", null) with { SentimentScore = 0.5m },
            Story("alpaca", "old", "Delta downgraded", null) with { ClassificationVersion = "vader", SentimentScore = 0.5m, Timestamp = DateTimeOffset.UtcNow.AddDays(-10) }
        ], default);
        await repository.InsertNewAsync([Story("finviz", "pending", "Epsilon downgraded", null)], default);

        Assert.Equal(2, await Service(new ScriptedScorer(_ => "finbert", -0.8m)).RescoreAsync(default));
        Assert.Equal(-0.8m, (await RowAsync("alpaca:fallback")).SentimentScore);
        Assert.Equal("finbert", (await RowAsync("finviz:pending")).SentimentAnalyzer);
        Assert.Equal(0.5m, (await RowAsync("alpaca:unrecorded")).SentimentScore);
        Assert.Null((await RowAsync("alpaca:unrecorded")).SentimentAnalyzer);
        Assert.Equal(0.5m, (await RowAsync("alpaca:old")).SentimentScore);
    }

    /// <summary>One article FinBERT cannot score must not block the others, and a stopped FinBERT must cost only a few requests.</summary>
    [Fact]
    public async Task Rescoring_SkipsOneRefusedArticleAndStopsAfterSeveralInARow()
    {
        await repository.UpsertAsync(Enumerable.Range(0, 10).Select(index => Story("alpaca", $"v{index}", $"Headline {index}", null) with
            { ClassificationVersion = "vader", Timestamp = DateTimeOffset.UtcNow.AddMinutes(-index) }).ToArray(), default);

        var stopped = new ScriptedScorer(_ => "vader", 0.9m);
        Assert.Equal(0, await Service(stopped).RescoreAsync(default));
        Assert.Equal(3, stopped.Calls);
        Assert.Equal(9, await Service(new ScriptedScorer(article => article.Headline == "Headline 0" ? "vader" : "finbert", -0.8m)).RescoreAsync(default));
    }

    [Fact]
    public async Task Rescoring_NeverOverwritesANewerLiveUpdate()
    {
        var story = Story("alpaca", "1", "Headline", null) with { ClassificationVersion = "vader", ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        await repository.UpsertAsync([story], default);
        var read = Assert.Single(await repository.GetScoredByAsync(["vader"], DateTimeOffset.UtcNow.AddHours(-1), 10, default));
        await repository.UpsertAsync([story with { SentimentScore = 0.3m, ReceivedAt = DateTimeOffset.UtcNow }], default);
        Assert.False(await repository.UpdateSentimentAsync(read, -0.8m, "finbert", default));
        Assert.Equal(0.3m, (await RowAsync("alpaca:1")).SentimentScore);
    }

    [Fact]
    public void LongArticles_AreShortenedToWhatTheSidecarAccepts()
    {
        var article = new NewsArticle("alpaca", "1", new string('h', 900), DateTimeOffset.UnixEpoch, ["MU"], new string('s', 5_000), new string('c', 9_000));
        var sent = NewsSentimentAnalyzer.Shorten(article);
        Assert.Equal(article.Headline, sent.Headline);
        Assert.Equal(article.Summary, sent.Summary);
        Assert.Equal(NewsSentimentAnalyzer.MaximumTextLength, String.Join(". ", sent.Headline, sent.Summary, sent.Content).Length);
    }

    [Fact]
    public async Task UnconfiguredFinbert_IsNamedAsNotSetUpRatherThanNotAnswering()
    {
        using var scorer = new NewsSentimentAnalyzer(new ScriptedScorer(_ => "vader", 0m), configured: false);
        await scorer.ReadHealthAsync(default);
        Assert.Contains("not set up", scorer.Problem);
    }

    /// <summary>A fallback score from a late enrichment must not replace a FinBERT score written meanwhile.</summary>
    [Fact]
    public async Task Enrichment_NeverReplacesAFinbertScoreWithTheFallback()
    {
        var story = Story("finviz", "1", "Fresh article", null) with { ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var added = await repository.InsertNewAsync([story], default);
        await repository.ApplyEnrichmentAsync([story with { SentimentScore = -0.8m, ClassificationVersion = "finbert" }], added, default);
        await repository.ApplyEnrichmentAsync([story with { SentimentScore = 0.9m, ClassificationVersion = "vader" }], added, default);
        Assert.Equal(-0.8m, (await RowAsync("finviz:1")).SentimentScore);
    }

    [Fact]
    public async Task NewestLookup_ReturnsTheNewestMatchOrNull()
    {
        var newest = new DateTimeOffset(2026, 11, 20, 22, 46, 0, TimeSpan.Zero);
        await repository.UpsertAsync([Story("alpaca", "1", "Older", null) with { Timestamp = newest.AddHours(-1) },
            Story("alpaca", "2", "Newer", null) with { Timestamp = newest }], default);
        Assert.Equal(newest, await repository.FindNewestAsync(new NewsFeedQuery(newest.AddDays(-5), "MU"), default));
        Assert.Null(await repository.FindNewestAsync(new NewsFeedQuery(newest.AddDays(-5), "DASH"), default));
    }

    [Fact]
    public async Task EarningsTimeline_LabelsAnUnrelatedLinkAsRelated()
    {
        await repository.UpsertAsync([Story("alpaca", "1", "DoorDash Integrates Linq AI Infrastructure for Ordering",
            "https://fourweekmba.com/ai-linq-raises-90m-from-a16z-so-ai-agents-can-text-and-call/") with { Ticker = "DASH", Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5) }], default);
        var shown = Assert.Single(await Service(new ScriptedScorer(_ => "vader", 0m)).GetEarningsTimelineAsync(["DASH"], DateTimeOffset.UtcNow, default));
        Assert.Equal(NewsLinkMatch.Related, shown.LinkKind);
    }

    /// <summary>A live article is saved with the shared scorer's result and names that scorer.</summary>
    [Fact]
    public async Task LiveArticle_IsSavedWithTheSharedScorersResult()
    {
        var scorer = new ScriptedScorer(_ => "finbert", -0.8m);
        await LiveService(new NewsSentimentAnalyzer(scorer)).ProcessMessageAsync(System.Text.Json.JsonDocument.Parse(
            """{"T":"n","id":77,"headline":"Maxim Downgrades Kairos Pharma to Hold","symbols":["KAPA"],"created_at":"2026-11-20T15:00:00Z","source":"fiscal.ai","url":"https://example.com/a"}""").RootElement, default);
        var row = await RowAsync("alpaca:77");
        Assert.Equal(-0.8m, row.SentimentScore);
        Assert.Equal("finbert", row.SentimentAnalyzer);
        Assert.Equal(1, scorer.Calls);
    }

    /// <summary>A hung FinBERT may delay only a few articles per minute; afterwards it is asked again.</summary>
    [Fact]
    public async Task RepeatedFailures_PauseFinbertForAMinuteThenAskAgain()
    {
        var clock = new StepClock();
        var answer = "vader";
        var scorer = new ScriptedScorer(_ => answer, -0.8m);
        using var analyzer = new NewsSentimentAnalyzer(scorer, clock);
        var article = new NewsArticle("alpaca", "1", "Headline", DateTimeOffset.UnixEpoch, ["MU"]);
        for (var index = 0; index < 8; index++) Assert.Equal("vader", (await analyzer.AnalyzeAsync(article, default)).AnalyzerName);
        Assert.Equal(3, scorer.Calls);
        Assert.Contains("not asked for one minute", analyzer.Problem);
        answer = "finbert";
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("finbert", (await analyzer.AnalyzeAsync(article, default)).AnalyzerName);
        Assert.Equal(4, scorer.Calls);
        Assert.Null(analyzer.Problem);
    }

    [Fact]
    public async Task BackgroundFailures_NeverPauseLiveScoring()
    {
        var scorer = new ScriptedScorer(_ => "vader", 0m);
        using var analyzer = new NewsSentimentAnalyzer(scorer, new StepClock());
        var article = new NewsArticle("alpaca", "1", "Headline", DateTimeOffset.UnixEpoch, ["MU"]);
        for (var index = 0; index < 6; index++) await analyzer.AnalyzeInBackgroundAsync(article, default);
        await analyzer.AnalyzeAsync(article, default);
        Assert.Equal(7, scorer.Calls);
    }

    /// <summary>The real migration keeps existing rows and leaves their scorer empty.</summary>
    [Fact]
    public async Task Migration_KeepsExistingNewsAndRecordsNoScorerForIt()
    {
        await using var file = new SqliteConnection("Data Source=:memory:");
        await file.OpenAsync();
        var migrated = new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(file).Options;
        await using var db = new TradingFlowDbContext(migrated);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261115220000_AddDecisionAuditDeduplication");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO NewsItems (Id,Ticker,Timestamp,Headline,SentimentScore,Provider,IngestedAt) VALUES ('alpaca:1','DASH','2026-11-20 22:46:00','Kept','0.5','alpaca','2026-11-20 22:47:00')");
        await migrator.MigrateAsync();
        var row = await db.NewsItems.AsNoTracking().SingleAsync();
        Assert.Equal(0.5m, row.SentimentScore);
        Assert.Null(row.SentimentAnalyzer);
    }

    // Reads one stored row.
    private async Task<PersistedNewsItem> RowAsync(string id)
    {
        await using var db = new TradingFlowDbContext(options);
        return await db.NewsItems.AsNoTracking().SingleAsync(row => row.Id == id);
    }

    // One story for MU, published now.
    private static CatalystEvent Story(string provider, string id, string headline, string? url) =>
        new("MU", DateTimeOffset.UtcNow, CatalystType.NewsReport, headline, 0m, provider, id, Url: url);

    // The live feed handler with a real store and no earnings calendar.
    private AlpacaNewsStreamService LiveService(NewsSentimentAnalyzer scorer) => new(
        new AlpacaCredentialProvider(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), _ => String.Empty), repository, Moq.Mock.Of<TradingFlow.Domain.Earnings.IEarningsRepository>(), scorer,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AlpacaNewsStreamService>.Instance);

    // A news service with only the parts re-scoring uses.
    private NewsFeedService Service(ISentimentAnalyzer scorer) =>
        new(null!, null!, null!, repository, null!, null!, null!, null!, null!, null!, TimeProvider.System,
            new NewsSentimentAnalyzer(scorer), Microsoft.Extensions.Logging.Abstractions.NullLogger<NewsFeedService>.Instance);

    // A clock whose elapsed time moves only when a test advances it.
    private sealed class StepClock : TimeProvider
    {
        private long ticks = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan by) => ticks += by.Ticks;
    }

    private sealed class ScriptedScorer(Func<NewsArticle, string> name, decimal score) : ISentimentAnalyzer
    {
        public int Calls { get; private set; }
        public string AnalyzerName => "scripted";

        public Task<SentimentResult> AnalyzeAsync(NewsArticle article, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new SentimentResult(score, "scripted", name(article)));
        }
    }

    private sealed class Factory(DbContextOptions<TradingFlowDbContext> options) : IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);
    }
}
