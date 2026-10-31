using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingFlow.Application.Jobs;
using TradingFlow.Contracts.Transport;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Orders;
using TradingFlow.Web;

namespace TradingFlow.Tests;

public sealed class TradingLimitApiTests
{
    /// <summary>Uses Android's shared client against the actual endpoints; saves no jobs and rejects malformed/stale editors explicitly.</summary>
    [Fact]
    public async Task SharedClient_SavesAccountLimitsWithoutStartingJobs_AndRejectsNullMode()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new TradingLimitsService(new SqliteOperationalTradingLimitStore(database), TimeProvider.System));
        await using var app = builder.Build();
        app.MapGroup("/api/v1").MapTradingLimitEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient();
            var client = new TradingWorkflowClient(http, () => address);
            var profile = await client.TradingLimitsAsync("paper");
            Assert.Equal(5, profile.MaxNewStocksPerDay);
            Assert.Equal(5, profile.MaxOpenPositions);
            var saved = await client.SaveTradingLimitsAsync(new("1.0", "paper", 2, 3, profile.Revision));
            Assert.Equal(2, (await client.TradingLimitsAsync("paper")).MaxNewStocksPerDay);
            Assert.Equal(5, (await client.TradingLimitsAsync("live")).MaxOpenPositions);
            using var malformed = await http.PostAsJsonAsync(address + "/api/v1/trading-limits", new {
                contractVersion = "1.0", mode = (string?)null, maxNewStocksPerDay = 5, maxOpenPositions = 5, expectedRevision = 0
            });
            Assert.True(malformed.StatusCode == HttpStatusCode.BadRequest, await malformed.Content.ReadAsStringAsync());
            using var stale = await http.PostAsJsonAsync(address + "/api/v1/trading-limits", new SaveTradingLimitProfileRequest("1.0", "paper", 4, 4, profile.Revision));
            Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
            Assert.Equal(saved, await client.TradingLimitsAsync("paper"));
            Assert.Empty(await new TradingFlow.Data.Jobs.SqliteJobRepository(database).GetAllJobsAsync(default));
        }
        finally { await app.StopAsync(); }
    }
}
