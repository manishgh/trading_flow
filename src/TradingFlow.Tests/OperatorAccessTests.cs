using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using TradingFlow.Application.Candidates;
using TradingFlow.Contracts.Transport;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Context;
using TradingFlow.Data.Identity;
using TradingFlow.Data.Orders;
using TradingFlow.Engine.Execution;
using TradingFlow.Web;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class OperatorAccessTests
{
    [Fact]
    public async Task ProductionCookiePipeline_RequiresCsrfChecksRolesAndSupportsAndroidSessionLifecycle()
    {
        await using var fixture = await OrderWorkflowIntegrityTests.Fixture.CreateAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddScoped<TradingFlowDbContext>(_ => fixture.Database.CreateDbContext());
        builder.Services.AddOperatorAuthentication();
        builder.Services.AddApiMutationSecurity();
        builder.Services.AddHttpContextAccessor();
        var cookieClock = new CandidateWorkflowTestClock(DateTimeOffset.UtcNow);
        builder.Services.ConfigureApplicationCookie(options => options.TimeProvider = cookieClock);
        builder.Services.AddSingleton<IOrderWorkflowService>(services => new OrderWorkflowService(
            fixture.Tickets, fixture.Broker.Object, fixture.Database.Candidates, fixture.Database.Workflows,
            Mock.Of<IStrategyIdentityResolver>(), fixture.Database.Workflows, new SqliteOrderIntentRepository(fixture.Database),
            new SqliteOrderEventRepository(fixture.Database), new BrokerAccountBindingOptions("account"),
            services.GetRequiredService<IHttpContextAccessor>(), fixture.Clock));
        builder.Services.AddSingleton(services => new OrderReplacementReviewService(
            services.GetRequiredService<IOrderWorkflowService>(), new SqliteOrderIntentRepository(fixture.Database),
            new SqliteOrderEventRepository(fixture.Database), fixture.Broker.Object, new BrokerAccountBindingOptions("account")));
        await using var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseApiMutationSecurity();
        app.MapTradingFlowAuthApi();
        app.MapGroup("/api/v1").RequireAuthorization().MapOrderWorkflowEndpoints();
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<TradingFlowUser>>();
            var created = await users.CreateAsync(new TradingFlowUser { Id = Guid.NewGuid(), UserName = "test-operator" }, "Test-Operator-Password-1!");
            Assert.True(created.Succeeded);
        }
        await app.StartAsync();
        try
        {
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            var jar = new CookieContainer();
            using var raw = new HttpClient(new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = false }) { BaseAddress = address };
            using var mobile = new HttpClient(new ApiAntiforgeryHandler(new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = false })) { BaseAddress = address };
            using (var anonymous = await raw.GetAsync("/api/auth/me")) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            foreach (var route in new[] { "orders/plan", "orders/preview", "orders/confirm", "orders/cancel", "orders/replacement/cancel", "positions/close" })
            {
                using var response = await raw.PostAsJsonAsync("/api/v1/" + route, new { });
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
            foreach (var route in new[] { "portfolio", "orders/journal", "orders/replacement/review?clientOrderId=missing" })
            {
                using var response = await raw.GetAsync("/api/v1/" + route);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
            var login = new { userName = "test-operator", password = "Test-Operator-Password-1!", rememberMe = true };
            using (var missingToken = await raw.PostAsJsonAsync("/api/auth/login", login)) Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
            raw.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "invalid");
            using (var invalidToken = await raw.PostAsJsonAsync("/api/auth/login", login)) Assert.Equal(HttpStatusCode.BadRequest, invalidToken.StatusCode);
            raw.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            using (var signedIn = await mobile.PostAsJsonAsync("/api/auth/login", login)) signedIn.EnsureSuccessStatusCode();
            using (var me = await mobile.GetAsync("/api/auth/me")) { me.EnsureSuccessStatusCode(); Assert.Contains("test-operator", await me.Content.ReadAsStringAsync()); }
            foreach (var route in new[] { "orders/plan", "orders/preview", "orders/confirm", "orders/cancel", "orders/replacement/cancel", "positions/close" })
            {
                using var response = await raw.PostAsJsonAsync("/api/v1/" + route, new { });
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Contains("csrf_validation_failed", await response.Content.ReadAsStringAsync());
            }
            using (var forbidden = await mobile.PostAsJsonAsync("/api/v1/orders/preview", fixture.Request with
                { ExecutionPolicy = "operator_override", CandidateId = null, CandidateRunId = null, CandidateVersion = null }))
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using (var malformed = await mobile.PostAsJsonAsync("/api/v1/orders/preview", new { contractVersion = "1.0", strategy = (object?)null }))
                Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            using (var valid = await mobile.PostAsJsonAsync("/api/v1/orders/preview", fixture.Request)) valid.EnsureSuccessStatusCode();
            Assert.Equal(0, fixture.Submissions);
            using (var logout = await mobile.PostAsync("/api/auth/logout", null)) Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            using (var me = await raw.GetAsync("/api/auth/me")) Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
            using (var signedIn = await mobile.PostAsJsonAsync("/api/auth/login", login)) signedIn.EnsureSuccessStatusCode();
            cookieClock.Now = cookieClock.Now.AddHours(13);
            using (var expired = await mobile.GetAsync("/api/auth/me")) Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        }
        finally { await app.StopAsync(); }
    }
}
