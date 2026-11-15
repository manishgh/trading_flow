using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingFlow.Data.Identity;
using TradingFlow.Domain.Execution;
using TradingFlow.Engine.Execution;
using TradingFlow.Web;

namespace TradingFlow.Tests;

public sealed class AccountLossReviewEndpointTests
{
    // Exercise production routes and CSRF middleware with no broker, runtime or operator credentials.
    [Fact]
    public async Task ReviewRequiresAdministratorCsrfAndUsesAuthenticatedActor()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, Auth>("fixture", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddApiMutationSecurity();
        var service = new ReviewService();
        builder.Services.AddSingleton<IAccountLossReviewService>(service);
        await using var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseApiMutationSecurity();
        app.MapAccountLossReviewEndpoints();
        app.MapGet("/csrf", (HttpContext context, IAntiforgery csrf) =>
            Results.Ok(new { token = csrf.GetAndStoreTokens(context).RequestToken }));
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/operations/account-loss/")).StatusCode);
            client.DefaultRequestHeaders.Add("X-Test-User", "reader");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/operations/account-loss/")).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-User");
            client.DefaultRequestHeaders.Add("X-Test-User", "administrator");
            var request = new { expectedRevision = 12, action = "reset_peak", reason = "Reviewed risk evidence.", actor = "forged" };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/operations/account-loss/review", request)).StatusCode);
            var tokenResponse = await client.GetFromJsonAsync<JsonElement>("/csrf");
            client.DefaultRequestHeaders.Add(ApiMutationSecurity.HeaderName, tokenResponse.GetProperty("token").GetString());
            var accepted = await client.PostAsJsonAsync("/api/operations/account-loss/review", request);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.Equal("administrator", service.Actor);
            Assert.Equal(12, service.Request?.ExpectedRevision);
            Assert.True(accepted.Headers.CacheControl!.NoStore);
            Assert.Equal(1, service.Calls);
            service.Refuse = true;
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/operations/account-loss/review", request)).StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    private sealed class ReviewService : IAccountLossReviewService
    {
        public string? Actor;
        public AccountLossReviewRequest? Request;
        public int Calls;
        public bool Refuse;
        // Return a bounded synthetic journal view; never acquire external account data.
        public Task<AccountLossReviewSnapshot> GetAsync(CancellationToken token) => Task.FromResult(new AccountLossReviewSnapshot(12, null));
        // Capture the actual identity passed by the endpoint and model a revision conflict.
        public Task<AccountLossReviewSnapshot> ReviewAsync(AccountLossReviewRequest request, string actor, CancellationToken token)
        {
            Calls++; Actor = actor; Request = request;
            if (Refuse) throw new InvalidOperationException("State changed. Reload.");
            return Task.FromResult(new AccountLossReviewSnapshot(13, null));
        }
    }

    public sealed class Auth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        // Supply isolated identities solely to exercise the production administrator policy.
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var user))
                return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.ToString()) };
            if (user == "administrator") claims.Add(new(ClaimTypes.Role, TradingFlowRoles.Administrator));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
