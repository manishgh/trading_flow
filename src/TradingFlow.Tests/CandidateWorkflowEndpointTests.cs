using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingFlow.Application.Candidates;
using TradingFlow.Contracts.V1;
using TradingFlow.Web;

namespace TradingFlow.Tests;

public sealed class CandidateWorkflowEndpointTests
{
    [Fact]
    public async Task ProductionEndpoints_ProjectDurableEvidenceAndMatchAuditFixture()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(CandidateAuditIntegrityTests.Now);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IUniverseDiscoveryService>(UniverseWorkflowIntegrityTests.Discovery(database, clock));
        builder.Services.AddSingleton<ICandidateWorkflow>(UniverseWorkflowIntegrityTests.Workflow(database, clock));
        builder.Services.AddSingleton<ICandidateQueryService>(database.Queries(clock));
        builder.Services.AddSingleton<IDecisionAuditQuery>(database.Queries(clock));
        await using var app = builder.Build();
        // This fixture exercises production handlers and persistence. Full cookie,
        // permission and CSRF middleware acceptance belongs to the access checkpoint.
        app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture-operator")], "fixture"));
            await next(context);
        });
        app.UseAuthorization();
        app.MapGroup("/api/v1").RequireAuthorization().MapCandidateWorkflowEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
            var previewRequest = new UniversePreviewRequest(ContractVersions.VersionOne, "swing", null,
                [new UniverseSourceSelectionRequest("wishlist", "watch")]);
            using var badPreview = await client.PostAsJsonAsync("/api/v1/universes/preview", previewRequest with { AsOfUtc = clock.Now.AddSeconds(-1) });
            Assert.Equal(HttpStatusCode.BadRequest, badPreview.StatusCode);
            using var previewResponse = await client.PostAsJsonAsync("/api/v1/universes/preview", previewRequest);
            previewResponse.EnsureSuccessStatusCode();
            var preview = (await previewResponse.Content.ReadFromJsonAsync<UniversePreviewResponse>())!;
            var request = UniverseWorkflowIntegrityTests.Request(preview);
            using var startResponse = await client.PostAsJsonAsync("/api/v1/candidate-runs", request);
            startResponse.EnsureSuccessStatusCode();
            var started = (await startResponse.Content.ReadFromJsonAsync<CandidateRunResponse>())!;
            var id = Assert.Single(started.Candidates).CandidateId;
            var run = (await client.GetFromJsonAsync<CandidateRunResponse>($"/api/v1/candidate-runs/{started.CandidateRunId}"))!;
            Assert.Equal(preview.UniverseSnapshotId, run.Provenance.UniverseSnapshotId);
            Assert.Equal(request.Strategies[0], run.Candidates[0].Strategy);
            Assert.Equal(preview.Members[0].Sources[0], Assert.Single(run.Candidates[0].Sources!));
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
            var emptyJson = JsonNode.Parse(await client.GetStringAsync($"/api/v1/candidates/{id}/audit"))!;
            var empty = emptyJson.Deserialize<CandidateAuditResponse>(options)!;
            Assert.Null(empty.MarketEvidence);
            Assert.Null(empty.CatalystEvidence);
            Assert.Null(empty.ExecutionEvidence);
            await CandidateAuditIntegrityTests.PopulateAsync(database, id);
            var auditJson = JsonNode.Parse(await client.GetStringAsync($"/api/v1/candidates/{id}/audit"))!;
            var audit = auditJson.Deserialize<CandidateAuditResponse>(options)!;
            var stored = (await database.Candidates.GetAsync(id))!;
            Assert.Equal(stored.SemanticDecisionSha256, audit.SemanticDecisionSha256);
            Assert.Equal(stored.LastPrice, audit.MarketEvidence!.LastPrice);
            Assert.Equal(stored.CatalystResultId, audit.CatalystEvidence!.CatalystResultId);
            Assert.Equal("in_trade", audit.Candidate.Readiness);
            Assert.Equal(preview.Members[0].Sources[0], Assert.Single(audit.Candidate.Sources!));
            var actual = new JsonObject { ["discovered"] = emptyJson, ["consumed"] = auditJson };
            // Snapshot-derived identifiers and hashes vary by capture. Their lineage
            // is asserted above; all other fields are checked verbatim by the fixture.
            foreach (var name in new[] { "discovered", "consumed" })
            {
                actual[name]!["candidate"]!["candidateId"] = "<candidate-id>";
                var source = actual[name]!["candidate"]!["sources"]![0]!;
                Assert.Matches("^[0-9a-f]{64}$", source["contentSha256"]!.GetValue<string>());
                source["contentSha256"] = "<source-sha256>";
            }
            var expected = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Contracts", "candidate_workflow_audit.json")));
            Assert.True(JsonNode.DeepEquals(expected, actual), actual.ToJsonString(options));
            clock.Now = clock.Now.AddMinutes(10);
            using var retry = await client.PostAsJsonAsync("/api/v1/candidate-runs", request);
            retry.EnsureSuccessStatusCode();
            var retried = (await retry.Content.ReadFromJsonAsync<CandidateRunResponse>())!;
            Assert.Equal(started.CandidateRunId, retried.CandidateRunId);
            Assert.Equal("Consumed", retried.Candidates[0].State);
            using var conflict = await client.PostAsJsonAsync("/api/v1/candidate-runs", request with { Mode = "paper_shadow" });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("idempotency_key_conflict", (await conflict.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
            using var missing = await client.GetAsync($"/api/v1/candidates/{Guid.NewGuid()}/audit");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally { await app.StopAsync(); }
    }
}
