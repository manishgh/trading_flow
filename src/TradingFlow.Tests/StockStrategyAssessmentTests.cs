using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingFlow.Contracts.Transport;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Strategies;
using TradingFlow.Web;
using TradingFlow.Web.Services.Desk;

namespace TradingFlow.Tests;

public sealed class StockStrategyAssessmentTests
{
    /// <summary>Exercises real persistence, production handlers and the transport used by Android against original kernel evidence.</summary>
    [Fact]
    public async Task SharedApi_PreservesKernelChecksAndDoesNotBorrowMissingStockEvidence()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var seeded = await SeedDecisionAsync(database);
        var clock = new CandidateWorkflowTestClock(seeded.Request.AsOfUtc.AddDays(1));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = Service(database, clock, cache);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(service);
        await using var app = builder.Build();
        // Cookie/permission acceptance is separate; this fixture checks the same read handlers and wire contract.
        app.MapGroup("/api/v1/desk").MapStockStrategyEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            var client = new TradingWorkflowClient(http, () => address);
            var strategy = Identity(seeded.Request);
            var choices = await client.StockStrategiesAsync();
            Assert.Contains(choices, choice => choice.CandidateRunId == seeded.RunId && choice.Strategy == strategy);
            var response = await client.StockStrategyAssessmentsAsync(seeded.RunId, strategy, ["RGTI", "MSFT"]);
            var observed = Assert.Single(response.Stocks, item => item.Symbol == "RGTI");
            Assert.Equal("recorded", observed.State);
            Assert.True(observed.Expired);
            Assert.Equal(seeded.Request.AsOfUtc, observed.EvaluatedAtUtc);
            Assert.Equal(seeded.Decision.Rules.Select(item => ("strategy:" + item.Code, item.Passed)),
                observed.Checks.Select(item => (item.GateName, item.Passed)));
            Assert.Contains(observed.Checks, check => check.Inputs.ContainsKey("strategyEntryRules"));
            Assert.DoesNotContain(observed.Checks, check => check.GateName.Contains("Swing watch", StringComparison.OrdinalIgnoreCase));
            var absent = Assert.Single(response.Stocks, item => item.Symbol == "MSFT");
            Assert.Equal("missing", absent.State);
            Assert.StartsWith("No strategy evaluation for this stock", absent.Detail);
            Assert.Empty(absent.Checks);
            Assert.Null(absent.EvaluatedAtUtc);
            Assert.Equal(strategy, response.Strategy);

            var noRun = await client.StockStrategyAssessmentsAsync(null, strategy, ["RGTI"]);
            Assert.Equal("missing", Assert.Single(noRun.Stocks).State);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.StockStrategyAssessmentsAsync(
                seeded.RunId, strategy with { ContentSha256 = new string('a', 64) }, ["RGTI"]));
            using var invalid = await http.GetAsync("/api/v1/desk/strategy-assessments?strategyId=x&semanticVersion=1&contentSha256=bad&symbols=RGTI");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

            await using var after = database.CreateDbContext();
            Assert.Single(await after.Candidates.ToArrayAsync());
            Assert.Empty(await after.Wishlists.ToArrayAsync());
            Assert.Empty(await after.OrderIntents.ToArrayAsync());
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>Rejects an incorrect audit identity rather than showing another stock's successful checks.</summary>
    [Fact]
    public async Task Projection_RejectsMismatchedStockAndDoesNotTurnAccountGatesIntoStrategyChecks()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var seeded = await SeedDecisionAsync(database);
        var clock = new CandidateWorkflowTestClock(seeded.Request.AsOfUtc);
        var audit = (await database.Queries(clock).GetAsync(seeded.Request.Candidate.CandidateId))!;
        var strategy = Identity(seeded.Request);
        Assert.Throws<InvalidDataException>(() => DeskStrategyAssessmentService.Project("MSFT", strategy, seeded.RunId, audit, clock.Now));
        var unrelated = audit with {
            GateEvaluations = [new(1, "spread", true, null, clock.Now, new Dictionary<string, string?> { ["spreadBps"] = "2" })]
        };
        var missing = DeskStrategyAssessmentService.Project("RGTI", strategy, seeded.RunId, unrelated, clock.Now);
        Assert.Equal("missing", missing.State);
        Assert.Empty(missing.Checks);
        Assert.Null(missing.CandidateId);
    }

    /// <summary>Hides retired strategies without catalog YAML and disabled catalog strategies from the Desk picker without deleting SQLite audit rows.</summary>
    [Fact]
    public async Task ChoicesAsync_ExcludesRetiredAndDisabledRecordedStrategiesWithoutDeletingRows()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var enabled = await SeedDecisionAsync(database, "technical.bollinger-squeeze-breakout", Guid.NewGuid());
        var retired = await SeedDecisionAsync(database, "swing.connors-rsi2", Guid.NewGuid());
        var disabled = await SeedDecisionAsync(database, "technical.opening-range-breakout", Guid.NewGuid());
        var clock = new CandidateWorkflowTestClock(enabled.Request.AsOfUtc.AddMinutes(1));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = Service(database, clock, cache);

        var choices = await service.ChoicesAsync(default);
        Assert.Contains(choices, choice => choice.CandidateRunId == enabled.RunId && choice.Strategy.StrategyId == "technical.bollinger-squeeze-breakout");
        Assert.DoesNotContain(choices, choice => choice.Strategy.StrategyId == "swing.connors-rsi2");
        Assert.DoesNotContain(choices, choice => choice.Strategy.StrategyId == "technical.opening-range-breakout");

        // Persisted SQLite rows remain intact and directly readable by exact run/strategy identity.
        await using var after = database.CreateDbContext();
        Assert.Equal(3, await after.Candidates.CountAsync());
        var retiredRead = await service.ReadAsync(retired.RunId, Identity(retired.Request), ["RGTI"], default);
        Assert.Equal("recorded", Assert.Single(retiredRead.Stocks).State);
        var disabledRead = await service.ReadAsync(disabled.RunId, Identity(disabled.Request), ["RGTI"], default);
        Assert.Equal("recorded", Assert.Single(disabledRead.Stocks).State);
    }

    /// <summary>Creates only disposable test rows containing the existing deterministic kernel fixture's original result.</summary>
    private static async Task<(Guid RunId, StrategyDecisionRequest Request, StrategyDecisionResult Decision)> SeedDecisionAsync(
        CandidateWorkflowTestDatabase database,
        string strategyId = "technical.bollinger-squeeze-breakout",
        Guid? candidateId = null)
    {
        var baseRequest = StrategyDecisionKernelTests.CreateRequest();
        var strategy = baseRequest.Strategy with { StrategyId = strategyId };
        var identity = new StrategyArtifactIdentity(
            strategy.StrategyId,
            baseRequest.StrategyIdentity.SemanticVersion,
            EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(
                strategy.StrategyId,
                baseRequest.StrategyIdentity.SemanticVersion,
                strategy,
                baseRequest.AdmissionProfile)));
        var effectiveCandidateId = candidateId ?? baseRequest.Candidate.CandidateId;
        var request = baseRequest with
        {
            Strategy = strategy,
            StrategyIdentity = identity,
            Candidate = baseRequest.Candidate with
            {
                CandidateId = effectiveCandidateId,
                Identity = baseRequest.Candidate.Identity with { StrategyContentSha256 = identity.ContentSha256 }
            }
        };
        var decision = new StrategyDecisionKernel().Evaluate(request);
        var runId = Guid.NewGuid();
        await using var db = database.CreateDbContext();
        db.ProductionRuns.Add(new ProductionRun {
            RunId = runId, Profile = "backtest", Status = "completed", StartedAtUtc = request.AsOfUtc,
            ConfigHash = new string('c', 64), CodeVersion = "fixture"
        });
        db.Candidates.Add(new CandidateRecord {
            CandidateId = request.Candidate.CandidateId, RunId = runId, ConfigHash = new string('c', 64), CodeVersion = "fixture",
            Symbol = "RGTI", Horizon = "swing", DiscoverySource = "fixture", SelectedStrategy = request.StrategyIdentity.StrategyId,
            StrategySemanticVersion = request.StrategyIdentity.SemanticVersion, StrategyContentSha256 = request.StrategyIdentity.ContentSha256,
            AdmissionProfileId = "fixture", AdmissionProfileVersion = "1", SetupKey = "fixture",
            DiscoveryWindowStartUtc = request.Candidate.Identity.DiscoveryWindowStartUtc,
            DiscoveryWindowEndUtc = request.Candidate.Identity.DiscoveryWindowEndUtc,
            DiscoveredAtUtc = request.AsOfUtc.AddMinutes(-1), RevalidatedAtUtc = request.AsOfUtc,
            State = decision.State, Version = 1, ExpiresAtUtc = request.AsOfUtc.AddMinutes(5),
            SemanticDecisionSha256 = decision.SemanticDecisionSha256
        });
        db.CandidateTransitions.Add(new CandidateTransitionRecord {
            RunId = runId, CandidateId = request.Candidate.CandidateId, ConfigHash = new string('c', 64), CodeVersion = "fixture",
            Sequence = 1, PreviousState = request.Candidate.State, NewState = decision.State, OccurredAtUtc = request.AsOfUtc,
            Source = "strategy_decision_kernel", SemanticDecisionSha256 = decision.SemanticDecisionSha256,
            EvidenceJson = decision.CanonicalDecisionJson, ReasonCode = "execution_trigger_satisfied"
        });
        await db.SaveChangesAsync();
        return (runId, request, decision);
    }

    /// <summary>Constructs the production read service using an isolated test database and the read-only strategy catalog.</summary>
    private static DeskStrategyAssessmentService Service(CandidateWorkflowTestDatabase database, TimeProvider clock, IMemoryCache cache)
    {
        var root = TestRepository.FindRoot();
        return new(database, database.Queries(clock), database.Queries(clock),
            new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), new SimpleYamlReader()), cache, clock);
    }

    /// <summary>Preserves the fixture's exact version/hash instead of identifying a strategy by its display name.</summary>
    private static StrategyReference Identity(StrategyDecisionRequest request) =>
        new(request.StrategyIdentity.StrategyId, request.StrategyIdentity.SemanticVersion, request.StrategyIdentity.ContentSha256);
}
