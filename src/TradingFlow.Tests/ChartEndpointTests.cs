using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Context;
using TradingFlow.Web;
using TradingFlow.Web.Services.Charts;

namespace TradingFlow.Tests;

public sealed class ChartEndpointTests
{
    // Exercise the actual HTTP routes and WebSocket hub against synthetic evidence, with no operator host or provider.
    [Fact]
    public async Task HostedChartRequiresAuthOriginAndMembershipAndStreamsKnownStock()
    {
        await using var fixture = await Fixture.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = fixture.Address };
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/charts/history?symbol=AAPL&interval=1d&sessionView=regular&chartContext=year")).StatusCode);

        using var signedIn = new HttpClient { BaseAddress = fixture.Address };
        signedIn.DefaultRequestHeaders.Add("X-Test-User", "reader");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await signedIn.GetAsync("/api/v1/charts/history?symbol=NVDA&interval=1d&sessionView=regular&chartContext=year")).StatusCode);
        Assert.Equal(0, fixture.Reader.ReadCount);
        var history = await signedIn.GetAsync("/api/v1/charts/history?symbol=AAPL&interval=1d&sessionView=regular&chartContext=year");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.True(history.Headers.CacheControl!.Private);
        Assert.True(history.Headers.CacheControl.NoStore);
        Assert.Equal(1, fixture.Reader.ReadCount);

        using var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/v1/charts/hub");
        foreign.Headers.Add("Origin", "https://foreign.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await signedIn.SendAsync(foreign)).StatusCode);
        using var credentialsInUrl = new HttpRequestMessage(HttpMethod.Get, "/api/v1/charts/hub?access_token=forbidden");
        credentialsInUrl.Headers.Add("Origin", fixture.Address.GetLeftPart(UriPartial.Authority));
        Assert.Equal(HttpStatusCode.Forbidden, (await signedIn.SendAsync(credentialsInUrl)).StatusCode);

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Test-User", "reader");
        socket.Options.SetRequestHeader("Origin", fixture.Address.GetLeftPart(UriPartial.Authority));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(new UriBuilder(fixture.Address) { Scheme = "ws", Path = "/api/v1/charts/hub" }.Uri, deadline.Token);
        await SendAsync(socket, "{\"protocol\":\"json\",\"version\":1}", deadline.Token);
        Assert.Contains("{}", await ReceiveAsync(socket, deadline.Token));
        await SendAsync(socket, "{\"type\":4,\"invocationId\":\"one\",\"target\":\"Watch\",\"arguments\":[{\"symbol\":\"AAPL\",\"interval\":\"1d\",\"sessionView\":\"regular\",\"context\":\"year\"}]}", deadline.Token);
        var message = await ReceiveAsync(socket, deadline.Token);
        var item = message.Split('\u001e', StringSplitOptions.RemoveEmptyEntries).Select(value => JsonDocument.Parse(value))
            .First(x => x.RootElement.TryGetProperty("type", out var type) && type.GetInt32() == 2);
        using (item)
        {
            var frame = item.RootElement.GetProperty("item");
            Assert.Equal("snapshot", frame.GetProperty("kind").GetString());
            Assert.Equal("AAPL", frame.GetProperty("key").GetProperty("symbol").GetString());
            Assert.Equal("year", frame.GetProperty("key").GetProperty("context").GetString());
            Assert.Equal(1, frame.GetProperty("parts").GetInt32());
        }
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "fixture finished", deadline.Token);
        Assert.Equal(2, fixture.Reader.ReadCount);
    }

    // A view grant that expires during acquisition cannot release history bytes afterwards.
    [Fact]
    public async Task HistoryRechecksMembershipAfterSlowRead()
    {
        var clock = new EndpointClock();
        await using var fixture = await Fixture.StartAsync(clock);
        fixture.Reader.OnRead = () => clock.Now = clock.Now.AddMinutes(6);
        using var client = new HttpClient { BaseAddress = fixture.Address };
        client.DefaultRequestHeaders.Add("X-Test-User", "reader");
        var response = await client.GetAsync("/api/v1/charts/history?symbol=AAPL&interval=1d&sessionView=regular&chartContext=year");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, fixture.Reader.ReadCount);
    }

    private sealed class EndpointClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-15T12:00:00Z");
        // Move source-acquisition time without blocking the isolated test host.
        public override DateTimeOffset GetUtcNow() => Now;
    }
    // The wire fixtures use the published SignalR JSON protocol record separator.
    private static Task SendAsync(ClientWebSocket socket, string json, CancellationToken token) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json + "\u001e")), WebSocketMessageType.Text, true, token);

    // Bound test reads so a protocol regression fails instead of buffering an unlimited response.
    private static async Task<string> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            output.Write(buffer, 0, result.Count);
            Assert.True(output.Length <= 64 * 1024);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private sealed class Fixture(WebApplication app, SqliteConnection database, Reader reader) : IAsyncDisposable
    {
        public Uri Address => new(app.Urls.Single());
        public Reader Reader => reader;
        // Own one ephemeral in-memory SQLite instance and one random loopback listener.
        public static async Task<Fixture> StartAsync(TimeProvider? clock = null)
        {
            var database = new SqliteConnection("Data Source=:memory:");
            await database.OpenAsync();
            var options = new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(database).Options;
            await using (var db = new TradingFlowDbContext(options)) await db.Database.EnsureCreatedAsync();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuth>("fixture", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 4096);
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddSingleton<TimeProvider>(clock ?? TimeProvider.System);
            builder.Services.AddSingleton<IDbContextFactory<TradingFlowDbContext>>(new Factory(options));
            builder.Services.AddSingleton<ChartAccessPolicy>();
            builder.Services.AddSingleton<ChartHistoryCursor>();
            builder.Services.AddSingleton<ChartTransportLimits>();
            var reader = new Reader();
            builder.Services.AddSingleton<IChartSeriesReader>(reader);
            var app = builder.Build();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseChartOriginGuard();
            app.MapChartEndpoints();
            app.Services.GetRequiredService<ChartAccessPolicy>().RecordDisplayed(
                new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "reader")], "fixture")), ["AAPL"]);
            await app.StartAsync();
            return new Fixture(app, database, reader);
        }
        // Always stop the owned listener and dispose only its isolated database.
        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            await database.DisposeAsync();
        }
    }

    private sealed class Reader : IChartSeriesReader
    {
        public int ReadCount;
        public Action? OnRead;
        // Supply deterministic chart evidence through the production transport boundary.
        public Task<ChartSeriesPage> ReadAsync(ChartSeriesKey key, DateTimeOffset? beforeUtc, CancellationToken token)
        {
            Interlocked.Increment(ref ReadCount);
            OnRead?.Invoke();
            return Task.FromResult(new ChartSeriesPage(new ChartSeriesSnapshot(key, DateTimeOffset.UtcNow,
                "history_only", "Fixture", new("fixture", "sip", "all", "complete", null, null, null, null, true, [], []),
                [], [], [], []), null));
        }
    }

    private sealed class Factory(DbContextOptions<TradingFlowDbContext> options) : IDbContextFactory<TradingFlowDbContext>
    {
        // All access checks share the same isolated in-memory database.
        public TradingFlowDbContext CreateDbContext() => new(options);
    }

    public sealed class FixtureAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        // This test-only scheme supplies a signed-in principal without using operator credentials.
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var user))
                return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, user.ToString())], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
