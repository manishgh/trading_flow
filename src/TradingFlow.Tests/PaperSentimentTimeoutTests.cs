using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingFlow.Alpaca;
using TradingFlow.Domain.Market;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class PaperSentimentTimeoutTests
{
    /// <summary>The paper factory's selected timeout must cancel a slow sidecar before its unrelated default deadline.</summary>
    [Fact]
    public async Task ConfiguredTimeout_CancelsSidecarAndReturnsTheActualFallbackIdentity()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.MapPost("/analyze", async (HttpContext context) => {
            requested.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
            return Results.Ok();
        });
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var analyzer = PaperRuntimeFactory.CreateSentimentAnalyzer(1, address);
            Assert.IsType<FinbertHttpSentimentAnalyzer>(analyzer);
            var inference = analyzer.AnalyzeAsync(new NewsArticle("fixture", "fixture-timeout", "Company update",
                DateTimeOffset.UtcNow, ["AAPL"]), default);
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await inference.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.StartsWith("vader", result.AnalyzerName, StringComparison.OrdinalIgnoreCase);
        }
        finally { await app.StopAsync(); }
    }
}
