using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Contracts.Transport;
using TradingFlow.WarmupService.Services;
using TradingFlow.Web.Services;
using TradingFlow.Web;

namespace TradingFlow.Tests;

public sealed class ServiceTransportTests
{
    /// <summary>Production proxy configuration cannot disable the boundary with an all-address network.</summary>
    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("not-a-network")]
    public void ProxyTrust_RejectsAllAddressOrMalformedNetworks(string network)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Transport:TrustedProxyNetworks:0"] = network }).Build();
        using var services = new ServiceCollection().AddLogging().AddTrustedProxyTransport(configuration).BuildServiceProvider();
        Assert.Throws<ArgumentException>(() => services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>>().Value);
    }

    /// <summary>Only the configured reverse proxy may assert HTTPS; direct production HTTP remains rejected except for probes.</summary>
    [Theory]
    [InlineData("127.0.0.1", false, true)]
    [InlineData("203.0.113.7", false, false)]
    [InlineData("203.0.113.7", true, true)]
    public async Task ForwardedHttps_RequiresKnownProxy(string peer, bool explicitlyTrusted, bool permitted)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        if (explicitlyTrusted) builder.Configuration["Transport:TrustedProxyNetworks:0"] = "203.0.113.0/24";
        builder.Services.AddTrustedProxyTransport(builder.Configuration);
        await using var app = builder.Build();
        // Replaces only the socket peer in this isolated host to exercise the production proxy trust boundary.
        app.Use(async (context, next) => { context.Connection.RemoteIpAddress = IPAddress.Parse(peer); await next(context); });
        app.UseTrustedProxyTransport(app.Environment);
        app.MapGet("/api/auth/me", () => "secure request");
        app.MapGet("/health", () => "ok");
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(Address(app)) };
            client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
            Assert.Equal(permitted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, (await client.GetAsync("/api/auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>Neither an invalid saved origin nor an explicit HTTP URL can reach the client transport with credentials.</summary>
    [Theory]
    [InlineData("http://example.test", false, false)]
    [InlineData("http://192.168.1.2", true, false)]
    [InlineData("http://127.0.0.1:5000", false, false)]
    [InlineData("http://127.0.0.1:5000", true, true)]
    [InlineData("http://10.0.2.2:5000", true, true)]
    [InlineData("https://configured.ngrok-free.app", false, true)]
    [InlineData("https://name:password@example.test", false, false)]
    public async Task BackendOrigin_IsCheckedBeforeTransport(string origin, bool development, bool permitted)
    {
        var transport = new RecordingTransport();
        using var client = new HttpClient(new BackendOriginHandler(transport, development));
        if (permitted)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(origin + "/api/auth/me")).StatusCode);
        else
            await Assert.ThrowsAsync<ArgumentException>(() => client.PostAsync(origin + "/api/auth/login", new StringContent("fake credentials")));
        Assert.Equal(permitted ? 1 : 0, transport.Requests);
    }

    /// <summary>Unconfigured, absent and incorrect service keys cannot read or mutate work; health stays public.</summary>
    [Theory]
    [InlineData(null, null, 503)]
    [InlineData("test-service-key", null, 401)]
    [InlineData("test-service-key", "wrong-key", 401)]
    [InlineData("test-service-key", "test-service-key", 200)]
    public async Task WarmupApi_RequiresConfiguredPrivateServiceKey(string? configured, string? supplied, int expected)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            [WarmupServiceAuthentication.ConfigurationKey] = configured }).Build();
        await using var app = builder.Build();
        app.UseWarmupServiceAccess(configuration);
        var commands = 0;
        app.MapPost("/api/warmup/run-now", () => { commands++; return "accepted"; });
        app.MapGet("/api/warmup/watchlist", () => "private list");
        app.MapGet("/health", () => "ok");
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(Address(app)) };
            if (supplied is not null) client.DefaultRequestHeaders.Add(WarmupServiceAuthentication.HeaderName, supplied);
            Assert.Equal(expected, (int)(await client.PostAsync("/api/warmup/run-now", null)).StatusCode);
            Assert.Equal(expected, (int)(await client.GetAsync("/api/warmup/watchlist")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            Assert.Equal(expected == 200 ? 1 : 0, commands);
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>The Web proxy supplies its host key; Android never needs the service credential.</summary>
    [Fact]
    public async Task WebWarmupClient_SendsHostServiceKey()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        var configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            [WarmupServiceAuthentication.ConfigurationKey] = "test-service-key" }).Build();
        await using var app = builder.Build();
        app.UseWarmupServiceAccess(configured);
        app.MapPost("/api/warmup/run-now", () => new { runId = "fixture", queued = true });
        await app.StartAsync();
        try
        {
            configured["WarmupService:BaseUrl"] = Address(app);
            var accepted = await new WarmupServiceClient(configured).RunNowAsync(new(null, "fixture", "fixture-key"), default);
            Assert.Equal("fixture", accepted!.RunId);
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>A deliberately blank environment source disables credentials rather than exposing another host's local file.</summary>
    [Fact]
    public void Credentials_UseOnlyExplicitHostSources()
    {
        var name = "TRADINGFLOW_TEST_CREDENTIAL_" + Guid.NewGuid().ToString("N");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Provider:Key"] = "fake-config-key" }).Build();
        var resolver = new WarmupCredentialResolver(config, NullLogger<WarmupCredentialResolver>.Instance);
        Assert.Equal("fake-config-key", resolver.Resolve("Provider", "Key", name));
        try
        {
            Environment.SetEnvironmentVariable(name, " ");
            Assert.Equal(" ", resolver.Resolve("Provider", "Key", name));
            Environment.SetEnvironmentVariable(name, "fake-env-key");
            Assert.Equal("fake-env-key", resolver.Resolve("Provider", "Key", name));
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    /// <summary>Reads the actual ephemeral listener rather than hard-coding a test port.</summary>
    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private sealed class RecordingTransport : HttpMessageHandler
    {
        public int Requests { get; private set; }
        /// <summary>Counts allowed requests without opening a network connection.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }
}
