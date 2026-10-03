using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingFlow.Contracts.Preparation;
using TradingFlow.Contracts.Transport;
using TradingFlow.Domain.Persistence;
using TradingFlow.Web;
namespace TradingFlow.Tests;
public sealed class PreparationEndpointTests
{
    [Fact]
    public async Task HostedApi_UsesSharedClient_RequiresCsrf_AndEnforcesOwner()
    {
        await using var h=await PreparationWorkflowTests.Harness.Create();
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{EnvironmentName="Testing"});builder.Logging.ClearProviders();builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("fixture-cookie").AddCookie("fixture-cookie");builder.Services.AddAuthorization();builder.Services.AddApiMutationSecurity();
        builder.Services.AddSingleton(h.Service);builder.Services.AddSingleton<IApplicationEventRepository>(h.Database.Workflows);
        await using var app=builder.Build();app.UseAuthentication();
        app.Use(async(context,next)=>{if(context.Request.Headers.TryGetValue("Fixture-Owner",out var owner))context.User=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,owner.ToString())],"fixture"));await next(context);});
        app.UseAuthorization();app.UseApiMutationSecurity();
        app.MapGet("/fixture/sign-in/{owner}",async (string owner,HttpContext context)=>{await context.SignInAsync("fixture-cookie",new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,owner)],"fixture-cookie")));return Results.Ok();});
        app.MapGet("/api/auth/csrf",(HttpContext context,IAntiforgery antiforgery)=>Results.Ok(new{requestToken=antiforgery.GetAndStoreTokens(context).RequestToken}));
        app.MapGroup("/api/v1").RequireAuthorization().MapPreparationEndpoints();await app.StartAsync();
        try
        {
            var url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var raw=new HttpClient();raw.DefaultRequestHeaders.Add("Fixture-Owner",h.Owner);
            using var rejected=await raw.PostAsJsonAsync(url+"/api/v1/preparation",new SavePreparationRequest(Guid.Empty,0,new()));Assert.Equal(HttpStatusCode.BadRequest,rejected.StatusCode);
            using var http=new HttpClient(new ApiAntiforgeryHandler(new HttpClientHandler {CookieContainer=new CookieContainer()}));await http.GetAsync(url+"/fixture/sign-in/"+h.Owner);
            var client=new TradingWorkflowClient(http,()=>url);
            var saved=await client.SavePreparationAsync(new(Guid.Empty,0,new(){Sources=[new("finviz","fixture")] }));
            Assert.False(saved.Settings.Enabled);Assert.Equal(saved.WorkflowId,Assert.Single(await client.PreparationAsync()).WorkflowId);
            var detail=await client.PreparationAsync(saved.WorkflowId);Assert.NotNull(detail.PlanningError);Assert.Empty(detail.History);
            await http.GetAsync(url+"/fixture/sign-in/"+Guid.NewGuid());
            Assert.Empty(await client.PreparationAsync());var exception=await Assert.ThrowsAsync<HttpRequestException>(()=>client.PreparationAsync(saved.WorkflowId));Assert.Equal(HttpStatusCode.NotFound,exception.StatusCode);
        }
        finally{await app.StopAsync();}
    }
}
