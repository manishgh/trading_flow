using Microsoft.Extensions.Caching.Memory;
using Moq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingFlow.Application.Jobs;
using TradingFlow.Contracts.Transport;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web;
using TradingFlow.Web.Services.Desk;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Tests;

public sealed class BacktestStockSourcesTests
{
    /// <summary>Research source choices contain only operator lists and saved Finviz queries, with no provider collection or list mutation.</summary>
    [Fact]
    public async Task Choices_ContainRealWishlistsAndSavedFinviz_WithoutBuiltInOrSignalScreens()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var longList = new Wishlist { Id = Guid.NewGuid(), Name = "Long" };
        var volatileList = new Wishlist { Id = Guid.NewGuid(), Name = "Volatile" };
        var wishlists = new Mock<IWishlistRepository>(MockBehavior.Strict);
        wishlists.Setup(item => item.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([longList, volatileList]);
        var presets = new ScreenerPresetService(database, TimeProvider.System);
        var screen = await presets.SaveAsync("Saved pullback", ScreenerScope.Swing, "cap_midover,ta_rsi_os30", default);
        var screener = new Mock<IScreenerSnapshotSource>(MockBehavior.Strict);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sources = new DeskSources(database, wishlists.Object, presets, screener.Object, cache);
        var choices = await sources.BacktestChoicesAsync(default);
        Assert.Equal(3, choices.Count);
        Assert.Equal(longList.Id, Assert.Single(choices, choice => choice.Name == "Long").WishlistId);
        var finviz = Assert.Single(choices, choice => choice.Kind == "finviz");
        Assert.Equal("saved:" + screen.Id, finviz.Key);
        Assert.Equal(screen.FilterQuery, finviz.Query);
        Assert.Null(finviz.WishlistId);
        Assert.DoesNotContain(choices, choice => choice.Key.StartsWith("signal:", StringComparison.Ordinal) || choice.Key.StartsWith("swing:", StringComparison.Ordinal));
        wishlists.Verify(item => item.ListAsync(It.IsAny<CancellationToken>()), Times.Once);
        wishlists.VerifyNoOtherCalls();
        screener.VerifyNoOtherCalls();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(sources);
        builder.Services.AddSingleton<BacktestPlanService>(_ => throw new InvalidOperationException("Listing choices must not resolve or start a run."));
        await using var app = builder.Build();
        app.MapGroup("/api/v1").MapBacktestPlanEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient();
            var client = new TradingWorkflowClient(http, () => address);
            Assert.Equal(choices, await client.BacktestSourcesAsync());
            screener.VerifyNoOtherCalls();
        }
        finally { await app.StopAsync(); }
    }
}
