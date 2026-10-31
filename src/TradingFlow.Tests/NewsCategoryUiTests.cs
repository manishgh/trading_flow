using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Moq;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Models;
using TradingFlow.Web.Pages;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Desk;

namespace TradingFlow.Tests;

public sealed class NewsCategoryUiTests
{
    [Fact]
    public void PrimaryFiltersLeaveAdvancedDisclosureClosedByDefault()
    {
        var model = Page(Mock.Of<INewsFeedRepository>(MockBehavior.Strict));
        model.Category = "global"; model.Sector = "energy"; model.StockSource = "wishlists";
        model.Hours = 12; model.Ticker = "AAA"; model.Search = "oil";
        Assert.Equal(0, model.AdvancedFilterCount); Assert.False(model.HasAdvancedFilters);
        model.Sentiment = " ALL "; model.Provider = " "; model.Source = " "; model.Classification = " ";
        Assert.Equal(0, model.AdvancedFilterCount); Assert.False(model.HasAdvancedFilters);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("tone")]
    [InlineData("publisher")]
    [InlineData("classification")]
    [InlineData("view")]
    [InlineData("article-link")]
    [InlineData("stock-link")]
    public void EachAdvancedChoiceOpensDisclosureAndContributesOneToCount(string choice)
    {
        var model = Page(Mock.Of<INewsFeedRepository>(MockBehavior.Strict));
        switch (choice)
        {
            case "provider": model.Provider = "wire"; break;
            case "tone": model.Sentiment = "negative"; break;
            case "publisher": model.Source = "publisher"; break;
            case "classification": model.Classification = "Macro"; break;
            case "view": model.View = "ticker"; break;
            case "article-link": model.LinkedOnly = true; break;
            case "stock-link": model.StockLinkedOnly = true; break;
        }
        Assert.Equal(1, model.AdvancedFilterCount); Assert.True(model.HasAdvancedFilters);
    }

    [Fact]
    public void AllSevenAdvancedChoicesAreCountedAndRemainInsideTheSameForm()
    {
        var model = Page(Mock.Of<INewsFeedRepository>(MockBehavior.Strict));
        model.Provider = "wire"; model.Sentiment = "positive"; model.Source = "publisher";
        model.Classification = "Company"; model.View = "ticker"; model.LinkedOnly = true; model.StockLinkedOnly = true;
        Assert.Equal(7, model.AdvancedFilterCount); Assert.True(model.HasAdvancedFilters);
        var page = Source("Pages/News.cshtml");
        var formStart = page.IndexOf("<form method=\"get\"", StringComparison.Ordinal);
        var detailsStart = page.IndexOf("<details class=\"news-more-filters\" open=\"@Model.HasAdvancedFilters\">", StringComparison.Ordinal);
        var detailsEnd = page.IndexOf("</details>", detailsStart, StringComparison.Ordinal);
        var formEnd = page.IndexOf("</form>", StringComparison.Ordinal);
        Assert.True(formStart < detailsStart && detailsEnd < formEnd);
        var primary = page[formStart..detailsStart];
        foreach (var field in new[] { "stockSource", "sector", "hours", "ticker", "search" })
            Assert.Contains($"name=\"{field}\"", primary);
        Assert.Contains("type=\"submit\">Apply", primary); Assert.Contains(">Clear</a>", primary);
        var advanced = page[detailsStart..detailsEnd];
        foreach (var field in new[] { "provider", "sentiment", "source", "classification", "view", "linkedOnly", "stockLinkedOnly" })
        {
            Assert.Contains($"name=\"{field}\"", advanced);
            Assert.DoesNotContain($"name=\"{field}\"", primary);
        }
        Assert.Contains("More filters", advanced); Assert.Contains("@Model.AdvancedFilterCount active", advanced);
        Assert.Contains(".news-feed-column .section-heading p { font-weight: 400; text-transform: none;", Source("wwwroot/css/site.css"));
    }

    [Fact]
    public async Task PageAdapterPassesTopicsAndExistingFiltersToSharedQueryAndKeepsFourHourDefault()
    {
        var records = new Mock<INewsFeedRepository>(MockBehavior.Strict);
        NewsFeedQuery? observed = null;
        records.Setup(x => x.QueryAsync(It.IsAny<NewsFeedQuery>(), It.IsAny<CancellationToken>()))
            .Callback<NewsFeedQuery, CancellationToken>((query, _) => observed = query)
            .ReturnsAsync(new NewsFeedPage([], 41));
        var model = Page(records.Object);
        model.Category = " FINANCIAL "; model.Sector = " ENERGY "; model.Ticker = "AAA";
        model.Provider = "wire"; model.Source = "publisher"; model.Classification = "Macro";
        model.Search = "inflation"; model.Sentiment = "negative"; model.LinkedOnly = true;
        model.PageNumber = 2; model.PageSize = 20; model.StocksPage = 3;
        Assert.IsType<PageResult>(await model.OnGetAsync(default));
        Assert.NotNull(observed);
        Assert.Equal("financial", observed.Category); Assert.Equal("energy", observed.Sector);
        Assert.Equal("AAA", observed.Ticker); Assert.Equal("wire", observed.Provider);
        Assert.Equal("publisher", observed.Source); Assert.Equal("macro", observed.Classification);
        Assert.Equal("inflation", observed.Search); Assert.Equal("negative", observed.Sentiment);
        Assert.True(observed.LinkedOnly); Assert.Equal(20, observed.Skip); Assert.Equal(20, observed.Take);
        Assert.False(observed.StockLinkedOnly);
        Assert.InRange((DateTimeOffset.UtcNow - observed.SinceUtc).TotalHours, 3.99, 4.01);
        Assert.Equal(4, model.Hours); Assert.Equal("financial", model.Category); Assert.Equal("energy", model.Sector);
        Assert.Equal("Financial / macro", model.CategoryLabel); Assert.Equal("Energy", model.SectorLabel);
        Assert.Equal(2, model.PageNumber); Assert.Equal(3, model.StocksPage);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ArticleLinksAndDirectStockLinksRemainIndependentFilters(bool articleLink, bool stockLink)
    {
        var records = new Mock<INewsFeedRepository>(MockBehavior.Strict);
        NewsFeedQuery? observed = null;
        records.Setup(x => x.QueryAsync(It.IsAny<NewsFeedQuery>(), It.IsAny<CancellationToken>()))
            .Callback<NewsFeedQuery, CancellationToken>((query, _) => observed = query)
            .ReturnsAsync(new NewsFeedPage([], 0));
        var model = Page(records.Object); model.LinkedOnly = articleLink; model.StockLinkedOnly = stockLink;
        Assert.IsType<PageResult>(await model.OnGetAsync(default));
        Assert.NotNull(observed); Assert.Equal(articleLink, observed.LinkedOnly); Assert.Equal(stockLink, observed.StockLinkedOnly);
    }

    [Theory]
    [InlineData("invented", null)]
    [InlineData("all", "invented")]
    public async Task InvalidTopicQueriesAreBadRequestsBeforeReadingAnyNews(string category, string? sector)
    {
        var records = new Mock<INewsFeedRepository>(MockBehavior.Strict);
        var model = Page(records.Object); model.Category = category; model.Sector = sector;
        Assert.IsType<BadRequestObjectResult>(await model.OnGetAsync(default));
        records.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PrivateScopeSignInReturnPreservesCategorySectorAndOtherQueryDimensions()
    {
        var records = new Mock<INewsFeedRepository>(MockBehavior.Strict);
        var model = Page(records.Object); model.StockSource = "wishlists"; model.Category = "global"; model.Sector = "energy";
        model.HttpContext.Request.PathBase = "/desk"; model.HttpContext.Request.Path = "/News";
        model.HttpContext.Request.QueryString = new("?category=global&sector=energy&stockSource=wishlists&hours=12&pageNumber=2&search=oil");
        var redirect = Assert.IsType<RedirectToPageResult>(await model.OnGetAsync(default));
        Assert.Equal("/Login", redirect.PageName);
        Assert.Equal("/desk/News?category=global&sector=energy&stockSource=wishlists&hours=12&pageNumber=2&search=oil", redirect.RouteValues!["ReturnUrl"]);
        records.VerifyNoOtherCalls();
    }

    [Fact]
    public void PresentationKeepsInferredTopicsSeparateFromProviderSymbolsAndLegacyClassification()
    {
        var item = new MobileNewsItem("MARKET, AAA", DateTimeOffset.UtcNow, "Headline", 0, "wire", null, null, null,
            "Macro", ["financial", "policy"], ["energy"], "Inferred topic rules", "display-topics-v1");
        var display = NewsPresentation.From(item);
        Assert.Equal(new[] { "Financial / macro", "Government / policy" }, display.CategoryLabels);
        Assert.Equal(new[] { "Energy" }, display.SectorLabels); Assert.True(display.HasTopics);
        Assert.Equal("Macro", display.Classification); Assert.Equal("AAA", display.GroupableTickers);
        Assert.Equal("AAA, Broad market (MARKET)", display.SymbolsLabel);
        Assert.Equal("Inferred topic rules", display.CategoryRationale); Assert.Equal("display-topics-v1", display.CategoryVersion);
        Assert.False(NewsPresentation.From(item with { Categories = null, Sectors = null }).HasTopics);
    }

    [Fact]
    public void NavigationAndBothPagersPreserveFiltersAndTopicChangesResetArticlePage()
    {
        var page = Source("Pages/News.cshtml");
        foreach (var helper in new[] { "PageUrl", "StocksUrl", "CategoryUrl" })
        {
            var start = page.IndexOf("string " + helper, StringComparison.Ordinal);
            var end = page.IndexOf("})!;", start, StringComparison.Ordinal);
            var body = page[start..end];
            foreach (var key in new[] { "hours", "ticker", "provider", "source", "classification", "category", "sector", "sentiment", "search", "linkedOnly", "stockLinkedOnly", "stockSource", "view", "pageNumber", "pageSize", "stocksPage" })
                Assert.Contains(key, body);
            if (helper == "CategoryUrl") Assert.Contains("pageNumber = 1", body);
        }
        Assert.Contains("<nav class=\"tab-strip news-category-nav\" aria-label=\"News categories\">", page);
        Assert.Contains("Model.Category == category.Key ? \"page\" : null", page);
        Assert.Contains("name=\"category\" value=\"@Model.Category\"", page);
        Assert.Contains("name=\"sector\"", page); Assert.Contains("name=\"pageNumber\" value=\"1\"", page);
        Assert.Contains("name=\"stockLinkedOnly\"", page); Assert.Contains("name=\"linkedOnly\"", page);
        Assert.Contains("name=\"pageSize\" value=\"@Model.PageSize\"", page);
        Assert.DoesNotContain("role=\"tab\"", page);
        Assert.Contains("No stories match @Model.CategoryLabel / @Model.SectorLabel", page);
        Assert.Contains("30000", Source("wwwroot/js/news-live.js"));
        Assert.Contains(".tab-strip.news-category-nav { flex-wrap: wrap", Source("wwwroot/css/site.css"));
    }

    [Fact]
    public void LiveRendererMirrorsTaxonomyLabelsAndKeepsOptionalMetadataCompatibleWithDesk()
    {
        var script = Source("wwwroot/js/news-evidence.js");
        foreach (var option in NewsDisplayTaxonomy.Categories.Where(x => x.Key != "all").Concat(NewsDisplayTaxonomy.Sectors))
            Assert.Contains($"'{option.Key}':'{option.Label}'", script);
        Assert.Contains("raw.categories", script); Assert.Contains("raw.sectors", script);
        Assert.Contains("raw.categoryRationale", script); Assert.Contains("raw.categoryVersion", script);
        Assert.Contains("Inferred display labels", script); Assert.Contains("x!=='MARKET'", script);
        Assert.Contains("if(topics)", script); Assert.Contains("Provider symbols:", script);
        var partial = Source("Pages/Shared/_NewsEvidence.cshtml");
        Assert.Contains("Inferred display labels", partial); Assert.Contains("@Model.Classification", partial);
        Assert.Contains("@Model.GroupableTickers", partial);
    }

    private static NewsModel Page(INewsFeedRepository records) => new(
        new NewsQueryService(records, Mock.Of<IWishlistRepository>(MockBehavior.Strict),
            Mock.Of<IDeskAccountReader>(MockBehavior.Strict), TimeProvider.System),
        new NewsStockSourceService(null!))
        { PageContext = new PageContext { HttpContext = new DefaultHttpContext() } };

    private static string Source(string path) => File.ReadAllText(Path.Combine(TestRepository.FindRoot(), "src", "TradingFlow.Web", path));
}
