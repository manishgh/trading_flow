using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using TradingFlow.Data.Context;
using TradingFlow.Data.News;
using TradingFlow.Data.Wishlists;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Preparation;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Desk;

namespace TradingFlow.Tests;

public sealed class GroupNewsQueryTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly DbContextOptions<TradingFlowDbContext> options;
    private readonly SqliteWishlistRepository groups;
    private readonly SqliteNewsFeedRepository news;
    public GroupNewsQueryTests()
    {
        connection.Open(); options = new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(connection).Options;
        using var context = new TradingFlowDbContext(options); context.Database.EnsureCreated();
        groups = new(new Factory(options)); news = new(new Factory(options));
    }

    [Fact]
    public async Task CreateEmptyGroup_RetainsExactOperatorCommandAndUndoDoesNotResurrect()
    {
        var command = new WishlistCreateRequest(Guid.NewGuid(), " Inline group ");
        var created = await groups.CreateGroupAsync("operator", command, default);
        Assert.Equal(command.CommandId, created.Id);
        Assert.Empty((await groups.GetByIdAsync(created.Id, default))!.Items);
        Assert.Equal(created, await groups.CreateGroupAsync("operator", command with { Name="Inline group" }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => groups.CreateGroupAsync("other", command, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => groups.CreateGroupAsync("operator", command with { Name="Changed" }, default));
        await groups.UndoBulkAsync("operator", command.CommandId, default);
        Assert.Null(await groups.GetByIdAsync(created.Id, default));
        Assert.True((await groups.CreateGroupAsync("operator", command, default)).Receipt.Undone);
        Assert.Null(await groups.GetByIdAsync(created.Id, default));
    }

    [Fact]
    public async Task CreationRejectsExistingNamesAndUndoNeverDeletesLaterMemberships()
    {
        var existing = await groups.SaveAsync(new() { Name="Existing", Description="Keep" }, default);
        await groups.AddOrUpdateItemAsync(existing.Id, "AAA", null, "Keep notes", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => groups.CreateGroupAsync("operator", new(Guid.NewGuid(), "existing"), default));
        Assert.Equal("Keep notes", (await groups.GetByIdAsync(existing.Id, default))!.Items.Single().Notes);
        var created = await groups.CreateGroupAsync("operator", new(Guid.NewGuid(), "Created"), default);
        await groups.AddOrUpdateItemAsync(created.Id, "BBB", null, "Later membership", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => groups.UndoBulkAsync("operator", created.Id, default));
        Assert.Equal("Later membership", (await groups.GetByIdAsync(created.Id, default))!.Items.Single().Notes);
    }

    [Fact]
    public async Task CreationUndoRejectsLaterGroupEditAndPreparationRevision()
    {
        var created = await groups.CreateGroupAsync("operator", new(Guid.NewGuid(), "Created"), default);
        var group = (await groups.GetByIdAsync(created.Id, default))!;
        group.Description = "Later edit";
        await groups.SaveAsync(group, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => groups.UndoBulkAsync("operator", created.Id, default));
        Assert.Equal("Later edit", (await groups.GetByIdAsync(created.Id, default))!.Description);
        var revision = await groups.CreateGroupAsync("operator", new(Guid.NewGuid(), "Revision"), default);
        using var context = new TradingFlowDbContext(options);
        var definition = new PreparationDefinitionRecord { Id = Guid.NewGuid(), WorkflowId = Guid.NewGuid(), OwnerId = "operator" };
        var occurrence = new PreparationOccurrenceRecord { Id = Guid.NewGuid(), DefinitionId = definition.Id, WorkflowId = definition.WorkflowId, SlotKey = "slot" };
        context.PreparationDefinitions.Add(definition); context.PreparationOccurrences.Add(occurrence);
        context.PreparationReviews.Add(new() { Id = Guid.NewGuid(), OccurrenceId = occurrence.Id, WishlistId = revision.Id,
            OwnerId = "operator", IdempotencyKey = "creation", RequestSha256 = "hash", Json = "{}" });
        await context.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => groups.UndoBulkAsync("operator", revision.Id, default));
        Assert.NotNull(await groups.GetByIdAsync(revision.Id, default));
    }

    [Fact]
    public async Task TwentyFiveSymbolCopy_PreservesExistingMetadata_RetriesAndUndoesExactly()
    {
        var source = await groups.SaveAsync(new() { Name = "Source" }, default);
        var target = await groups.SaveAsync(new() { Name = "Target" }, default);
        var symbols = Enumerable.Range(0, 25).Select(i => $"T{i}").ToArray();
        foreach (var symbol in symbols) await groups.AddOrUpdateItemAsync(source.Id, symbol, "source name", "source notes", default);
        await groups.AddOrUpdateItemAsync(target.Id, "T0", "target name", "target notes", default);
        await groups.RemoveItemAsync(target.Id, "T0", default);
        var request = new WishlistBulkRequest(Guid.NewGuid(), "copy", source.Id, target.Id, symbols);
        var first = await groups.ApplyBulkAsync("operator", request, default);
        Assert.Equal(25, first.Affected);
        Assert.Equal(first, await groups.ApplyBulkAsync("operator", request with { Tickers = symbols.Reverse().ToArray() }, default));
        var result = (await groups.GetByIdAsync(target.Id, default))!;
        Assert.Equal(25, result.Items.Count(x => x.Active));
        Assert.Equal("target notes", result.Items.Single(x => x.Ticker == "T0").Notes);
        Assert.Equal("source notes", result.Items.Single(x => x.Ticker == "T1").Notes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => groups.ApplyBulkAsync("other", request, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => groups.ApplyBulkAsync("operator", request with { Operation = "move" }, default));
        await groups.UndoBulkAsync("operator", request.CommandId, default);
        result = (await groups.GetByIdAsync(target.Id, default))!;
        Assert.Single(result.Items); Assert.False(result.Items.Single().Active);
        Assert.True((await groups.ApplyBulkAsync("operator", request, default)).Undone);
        Assert.True((await groups.UndoBulkAsync("operator", request.CommandId, default)).Undone);
    }

    [Fact]
    public async Task MoveFailureIsAtomic_AndUndoRefusesInterveningNotesEdit()
    {
        var source = await groups.SaveAsync(new() { Name = "Source" }, default);
        var target = await groups.SaveAsync(new() { Name = "Target" }, default);
        await groups.AddOrUpdateItemAsync(source.Id, "AAA", null, "keep", default);
        var request = new WishlistBulkRequest(Guid.NewGuid(), "move", source.Id, target.Id, ["AAA", "ZZZ"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => groups.ApplyBulkAsync("operator", request, default));
        Assert.Empty((await groups.GetByIdAsync(target.Id, default))!.Items);
        Assert.True((await groups.GetByIdAsync(source.Id, default))!.Items.Single().Active);
        request = request with { Tickers = ["AAA"] };
        await groups.ApplyBulkAsync("operator", request, default);
        Assert.False((await groups.GetByIdAsync(source.Id, default))!.Items.Single().Active);
        await groups.AddOrUpdateItemAsync(target.Id, "AAA", null, "later edit", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => groups.UndoBulkAsync("operator", request.CommandId, default));
        Assert.Equal("later edit", (await groups.GetByIdAsync(target.Id, default))!.Items.Single().Notes);
        Assert.False((await groups.GetByIdAsync(source.Id, default))!.Items.Single().Active);
    }

    [Fact]
    public async Task TagAndUntagPreserveFreeText_AndPreparationRevisionsCannotMutate()
    {
        var group = await groups.SaveAsync(new() { Name = "Group" }, default);
        await groups.AddOrUpdateItemAsync(group.Id, "AAA", null, "Do not reinterpret #momentum", default);
        var command = new WishlistBulkRequest(Guid.NewGuid(), "tag", group.Id, null, ["AAA"], "Earnings");
        await groups.ApplyBulkAsync("operator", command, default);
        var item = (await groups.GetByIdAsync(group.Id, default))!.Items.Single();
        Assert.Equal("[\"earnings\"]", item.TagsJson); Assert.Equal("Do not reinterpret #momentum", item.Notes);
        await groups.ApplyBulkAsync("operator", command with { CommandId = Guid.NewGuid(), Operation = "untag" }, default);
        Assert.Equal("[]", (await groups.GetByIdAsync(group.Id, default))!.Items.Single().TagsJson);
        using var context = new TradingFlowDbContext(options);
        var definition = new PreparationDefinitionRecord { Id = Guid.NewGuid(), WorkflowId = Guid.NewGuid(), OwnerId = "operator" };
        var occurrence = new PreparationOccurrenceRecord { Id = Guid.NewGuid(), DefinitionId = definition.Id, WorkflowId = definition.WorkflowId, SlotKey = "slot" };
        context.PreparationDefinitions.Add(definition); context.PreparationOccurrences.Add(occurrence);
        context.PreparationReviews.Add(new() { Id = Guid.NewGuid(), OccurrenceId = occurrence.Id, WishlistId = group.Id,
            OwnerId = "operator", IdempotencyKey = "one", RequestSha256 = "hash", Json = "{}" });
        await context.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => groups.ApplyBulkAsync("operator", command with { CommandId = Guid.NewGuid() }, default));
    }

    [Fact]
    public async Task NewsFiltersFindOlderMatchesBeyondInitialTwoHundred_AndStablePages()
    {
        var now = DateTimeOffset.UtcNow;
        using var context = new TradingFlowDbContext(options);
        context.NewsItems.AddRange(Enumerable.Range(0, 305).Select(i => new PersistedNewsItem {
            Id = i.ToString("D4"), Ticker = "AAA", Timestamp = now.AddMinutes(-i), IngestedAt = now,
            Headline = i >= 300 ? "Earnings beat expected" : "Other headline", Provider = i >= 300 ? "sec_edgar" : "alpaca",
            Source = i >= 300 ? "filing" : "wire", SentimentScore = i >= 300 ? 0.8m : 0m, Url = "https://example.com" }));
        await context.SaveChangesAsync();
        var filter = new NewsFeedQuery(now.AddHours(-8), Provider: "SEC_EDGAR", Source: "filing", Classification: "sec filing",
            Sentiment: "positive", Search: "beat", LinkedOnly: true, Take: 2);
        var page = await news.QueryAsync(filter, default);
        Assert.Equal(5, page.TotalMatching); Assert.Equal(new[] { "0300", "0301" }, page.Records.Select(x => x.Id));
        page = await news.QueryAsync(filter with { Skip = 2 }, default);
        Assert.Equal(new[] { "0302", "0303" }, page.Records.Select(x => x.Id));
        Assert.Empty((await news.QueryAsync(filter with { ScopeTickers = [] }, default)).Records);
        Assert.Empty((await news.QueryAsync(filter with { Provider = "' OR 1=1 --" }, default)).Records);
    }

    [Theory]
    [InlineData("sec_edgar", "AAA", "headline", "SEC filing")]
    [InlineData("federal_reserve", "MARKET", "headline", "Fed")]
    [InlineData("alpaca", "MARKET", "Inflation falls", "Macro")]
    [InlineData("alpaca", "MARKET", "Stocks move", "Market")]
    [InlineData("alpaca", "AAA", "Inflation falls", "Company")]
    public async Task NewsSqlClassificationMatchesDisplay(string provider, string ticker, string headline, string classification)
    {
        using var context = new TradingFlowDbContext(options);
        var item = new PersistedNewsItem { Id = "one", Provider = provider, Ticker = ticker, Headline = headline, Timestamp = DateTimeOffset.UtcNow };
        context.NewsItems.Add(item); await context.SaveChangesAsync();
        Assert.Equal(classification, NewsEvidenceClassification.Classify(item));
        Assert.Single((await news.QueryAsync(new(DateTimeOffset.UtcNow.AddHours(-1), Classification: classification), default)).Records);
    }

    [Fact]
    public async Task AnonymousPrivateScopesFailBeforeReadingAccount_AndDuplicateSymbolsCombine()
    {
        var account = new Mock<IDeskAccountReader>(MockBehavior.Strict);
        var service = new NewsQueryService(news, groups, account.Object, TimeProvider.System);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.QueryAsync(new() { Scope = "positions" }, anonymous, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.QueryAsync(new() { GroupId = Guid.NewGuid() }, anonymous, default));
        using var context = new TradingFlowDbContext(options);
        context.NewsItems.AddRange(new[] { "AAA", "BBB" }.Select(ticker => new PersistedNewsItem {
            Id = ticker, Ticker = ticker, Provider = "alpaca", Headline = "Same story!", Timestamp = DateTimeOffset.UtcNow }));
        await context.SaveChangesAsync();
        var response = await service.QueryAsync(new(), anonymous, default);
        Assert.Equal(2, response.TotalMatching); Assert.Equal("AAA, BBB", Assert.Single(response.Items).Ticker);
    }

    private sealed class Factory(DbContextOptions<TradingFlowDbContext> options) : IDbContextFactory<TradingFlowDbContext>
    { public TradingFlowDbContext CreateDbContext() => new(options); }
    public void Dispose() => connection.Dispose();
}
