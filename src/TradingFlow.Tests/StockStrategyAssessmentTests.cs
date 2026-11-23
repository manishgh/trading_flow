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

    /// <summary>Hides retired strategies without catalog YAML and disabled catalog strategies from the Desk picker without deleting SQLite audit rows, and tags active choices with direction.</summary>
    [Fact]
    public async Task ChoicesAsync_ExcludesRetiredAndDisabledRecordedStrategiesWithoutDeletingRows()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var enabled = await SeedDecisionAsync(database, "technical.bollinger-squeeze-breakout", Guid.NewGuid());
        var retired = await SeedDecisionAsync(database, "swing.connors-rsi2", Guid.NewGuid());
        var disabled = await SeedDecisionAsync(database, "technical.parabolic-exhaustion-short", Guid.NewGuid());
        var clock = new CandidateWorkflowTestClock(enabled.Request.AsOfUtc.AddMinutes(1));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var temp = new TemporaryFolder();
        var root = TestRepository.FindRoot();
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(root, "configs", "strategy-catalog.json")))!;
        var parked = manifest["strategies"]!.AsArray()
            .Single(item => item!["strategyId"]!.GetValue<string>() == "technical.parabolic-exhaustion-short" && item["semanticVersion"]!.GetValue<string>() == "1.0.0")!;
        parked["enabled"] = false;
        parked["disabledReason"] = "Disabled in test fixture.";
        var manifestPath = Path.Combine(temp.Path, "strategy-catalog.json");
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var service = new DeskStrategyAssessmentService(
            database,
            database.Queries(clock),
            database.Queries(clock),
            new StrategyArtifactCatalog(root, manifestPath, new SimpleYamlReader()),
            cache,
            clock);

        var choices = await service.ChoicesAsync(default);
        var enabledRecorded = Assert.Single(choices, choice => choice.CandidateRunId == enabled.RunId && choice.Strategy.StrategyId == "technical.bollinger-squeeze-breakout");
        Assert.Equal("long", enabledRecorded.Direction);
        Assert.Contains(choices, choice => choice.CandidateRunId is null && choice.Strategy.StrategyId == "technical.bollinger-squeeze-breakout" && choice.Direction == "long");
        Assert.Contains(choices, choice => choice.CandidateRunId is null && choice.Strategy.StrategyId == "technical.bollinger-squeeze-breakdown" && choice.Direction == "short");
        Assert.DoesNotContain(choices, choice => choice.Strategy.StrategyId == "swing.connors-rsi2");
        Assert.DoesNotContain(choices, choice => choice.Strategy.StrategyId == "technical.parabolic-exhaustion-short" && choice.Strategy.SemanticVersion == "1.0.0");

        // Persisted SQLite rows remain intact and directly readable by exact run/strategy identity.
        await using var after = database.CreateDbContext();
        Assert.Equal(3, await after.Candidates.CountAsync());
        var retiredRead = await service.ReadAsync(retired.RunId, Identity(retired.Request), ["RGTI"], default);
        Assert.Equal("recorded", Assert.Single(retiredRead.Stocks).State);
        var disabledRead = await service.ReadAsync(disabled.RunId, Identity(disabled.Request), ["RGTI"], default);
        Assert.Equal("recorded", Assert.Single(disabledRead.Stocks).State);
    }

    /// <summary>
    /// Verifies K3: a recorded run of an older superseded strategy version produced by the real
    /// <see cref="StrategyDecisionKernel"/> is labeled "older version" in <c>Name</c> (without duplicating it in <c>Mode</c>),
    /// preserves its own persisted trade direction even when the current enabled catalog version has a different direction,
    /// and degrades cleanly on malformed <c>EvidenceJson</c> (including non-string <c>"source": 123</c> and truncated JSON).
    /// </summary>
    [Fact]
    public async Task ChoicesAsync_LabelsOlderRecordedVersionAndPreservesRecordedDirectionOverNewerCatalogVersion()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();

        // Seed an older v0.9.0 recorded run for "technical.bollinger-squeeze-breakout" (whose current catalog version is "long")
        // evaluated through the REAL StrategyDecisionKernel with Direction = "short" that fails short entry rules, producing
        // semantic.direction == null and a real nested "evidenceJson" string rule carrying "longRejection":"direction_not_long".
        var olderKernelRejectedShort = await SeedRealKernelOlderVersionShortAsync(database, "technical.bollinger-squeeze-breakout", "0.9.0");
        Assert.Null(olderKernelRejectedShort.Decision.Direction);
        Assert.Contains("\"evidenceJson\"", olderKernelRejectedShort.Decision.CanonicalDecisionJson, StringComparison.Ordinal);
        Assert.Contains("direction_not_long", olderKernelRejectedShort.Decision.CanonicalDecisionJson, StringComparison.Ordinal);

        // Also seed a malformed-JSON older recorded run to prove ChoicesAsync never crashes with InvalidOperationException/JsonException.
        var malformed = await SeedMalformedOlderVersionAsync(database, "technical.bollinger-squeeze-breakout", "0.7.0");

        var clock = new CandidateWorkflowTestClock(olderKernelRejectedShort.Request.AsOfUtc.AddMinutes(5));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = Service(database, clock, cache);

        var choices = await service.ChoicesAsync(default);

        // Current catalog entry is NOT labeled "older version" and keeps its catalog "long" direction.
        var currentCatalog = Assert.Single(choices, c => c.CandidateRunId is null && c.Strategy.StrategyId == "technical.bollinger-squeeze-breakout" && c.Strategy.SemanticVersion == "1.0.0");
        Assert.Equal("long", currentCatalog.Direction);
        Assert.DoesNotContain("older version", currentCatalog.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("current", currentCatalog.Mode);

        // Real StrategyDecisionKernel rejected older v0.9.0 short run recovers "short" from nested evidenceJson, labels Name
        // with "older version", and keeps Mode as "backtest" without duplicating "older version".
        var v09 = Assert.Single(choices, c => c.CandidateRunId == olderKernelRejectedShort.RunId);
        Assert.Equal("short", v09.Direction);
        Assert.Contains("older version", v09.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("backtest", v09.Mode);

        // Malformed EvidenceJson (non-string "source": 123 and truncated JSON) degrades cleanly without throwing.
        Assert.Null(DeskStrategyAssessmentService.TryExtractRecordedDirection("""{"rules":[{"source":123,"evidenceJson":"{}"}]}"""));
        Assert.Null(DeskStrategyAssessmentService.TryExtractRecordedDirection("""{"semantic": """));
        var v07 = Assert.Single(choices, c => c.CandidateRunId == malformed.RunId);
        Assert.Null(v07.Direction); // No usable recorded direction: the newer version's direction is never shown as its own.
        Assert.Equal("backtest", v07.Mode);
    }

    // Evaluates an older-version short strategy through the real StrategyDecisionKernel so persisted EvidenceJson uses the exact production schema.
    private static async Task<(Guid RunId, StrategyDecisionRequest Request, StrategyDecisionResult Decision)> SeedRealKernelOlderVersionShortAsync(
        CandidateWorkflowTestDatabase database,
        string strategyId,
        string semanticVersion)
    {
        var baseRequest = StrategyDecisionKernelTests.CreateRequest();
        // Configure as short-only with an unreachable MaxShortEntryRsi so the kernel reaches strategy_entry_rules, sets
        // longRejection = "direction_not_long" and rejects the short entry (semantic.direction == null, nested evidenceJson string).
        var shortStrategy = baseRequest.Strategy with
        {
            StrategyId = strategyId,
            Direction = "short",
            EntryRules = baseRequest.Strategy.EntryRules with
            {
                EnableShort = true,
                ShortSetupType = "momentum",
                MaxShortEntryRsi = 1m
            }
        };
        var identity = new StrategyArtifactIdentity(
            shortStrategy.StrategyId,
            semanticVersion,
            EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(
                shortStrategy.StrategyId,
                semanticVersion,
                shortStrategy,
                baseRequest.AdmissionProfile)));
        var candidateId = Guid.NewGuid();
        var request = baseRequest with
        {
            Strategy = shortStrategy,
            StrategyIdentity = identity,
            Candidate = baseRequest.Candidate with
            {
                CandidateId = candidateId,
                Identity = baseRequest.Candidate.Identity with
                {
                    StrategyContentSha256 = identity.ContentSha256
                }
            }
        };
        var decision = new StrategyDecisionKernel().Evaluate(request);
        var runId = Guid.NewGuid();
        await using var db = database.CreateDbContext();
        db.ProductionRuns.Add(new ProductionRun
        {
            RunId = runId, Profile = "backtest", Status = "completed", StartedAtUtc = request.AsOfUtc,
            ConfigHash = new string('c', 64), CodeVersion = "fixture"
        });
        db.Candidates.Add(new CandidateRecord
        {
            CandidateId = candidateId, RunId = runId, ConfigHash = new string('c', 64), CodeVersion = "fixture",
            Symbol = "RGTI", Horizon = "swing", DiscoverySource = "fixture", SelectedStrategy = identity.StrategyId,
            StrategySemanticVersion = identity.SemanticVersion, StrategyContentSha256 = identity.ContentSha256,
            AdmissionProfileId = "fixture", AdmissionProfileVersion = "1", SetupKey = "fixture",
            DiscoveryWindowStartUtc = request.Candidate.Identity.DiscoveryWindowStartUtc,
            DiscoveryWindowEndUtc = request.Candidate.Identity.DiscoveryWindowEndUtc,
            DiscoveredAtUtc = request.AsOfUtc.AddMinutes(-1), RevalidatedAtUtc = request.AsOfUtc,
            State = decision.State, Version = 1, ExpiresAtUtc = request.AsOfUtc.AddMinutes(5),
            SemanticDecisionSha256 = decision.SemanticDecisionSha256
        });
        db.CandidateTransitions.Add(new CandidateTransitionRecord
        {
            RunId = runId, CandidateId = candidateId, ConfigHash = new string('c', 64), CodeVersion = "fixture",
            Sequence = 1, PreviousState = request.Candidate.State, NewState = decision.State, OccurredAtUtc = request.AsOfUtc,
            Source = "strategy_decision_kernel", SemanticDecisionSha256 = decision.SemanticDecisionSha256,
            EvidenceJson = decision.CanonicalDecisionJson, ReasonCode = decision.NoEntryReason ?? "rejected"
        });
        await db.SaveChangesAsync();
        return (runId, request, decision);
    }

    // Seeds an older-version recorded run carrying malformed non-string "source": 123 EvidenceJson to verify graceful degradation.
    private static async Task<(Guid RunId, DateTimeOffset AsOfUtc)> SeedMalformedOlderVersionAsync(
        CandidateWorkflowTestDatabase database,
        string strategyId,
        string semanticVersion)
    {
        var asOfUtc = new DateTimeOffset(2026, 11, 17, 21, 0, 0, TimeSpan.Zero);
        var runId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var contentSha = new string('7', 64);
        var decisionSha = new string('a', 64);
        await using var db = database.CreateDbContext();
        db.ProductionRuns.Add(new ProductionRun
        {
            RunId = runId, Profile = "backtest", Status = "completed", StartedAtUtc = asOfUtc,
            ConfigHash = new string('c', 64), CodeVersion = "fixture"
        });
        db.Candidates.Add(new CandidateRecord
        {
            CandidateId = candidateId, RunId = runId, ConfigHash = new string('c', 64), CodeVersion = "fixture",
            Symbol = "RGTI", Horizon = "swing", DiscoverySource = "fixture", SelectedStrategy = strategyId,
            StrategySemanticVersion = semanticVersion, StrategyContentSha256 = contentSha,
            AdmissionProfileId = "fixture", AdmissionProfileVersion = "1", SetupKey = "fixture",
            DiscoveryWindowStartUtc = asOfUtc.AddMinutes(-5), DiscoveryWindowEndUtc = asOfUtc,
            DiscoveredAtUtc = asOfUtc.AddMinutes(-1), RevalidatedAtUtc = asOfUtc,
            State = StrategyCandidateState.Rejected, Version = 1, ExpiresAtUtc = asOfUtc.AddMinutes(5),
            SemanticDecisionSha256 = decisionSha
        });
        db.CandidateTransitions.Add(new CandidateTransitionRecord
        {
            RunId = runId, CandidateId = candidateId, ConfigHash = new string('c', 64), CodeVersion = "fixture",
            Sequence = 1, PreviousState = StrategyCandidateState.Discovered, NewState = StrategyCandidateState.Rejected,
            OccurredAtUtc = asOfUtc, Source = "strategy_decision_kernel", SemanticDecisionSha256 = decisionSha,
            EvidenceJson = """{"rules":[{"source":123,"evidenceJson":"not-json"}]}""", ReasonCode = "malformed_fixture"
        });
        await db.SaveChangesAsync();
        return (runId, asOfUtc);
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
