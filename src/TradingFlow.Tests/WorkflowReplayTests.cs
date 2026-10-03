using System.Text.Json;
using TradingFlow.Domain.Discovery;
using System.Collections.Immutable;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using TradingFlow.Application.Candidates;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Orders;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Strategies;
using TradingFlow.Web;

namespace TradingFlow.Tests;

public sealed class WorkflowReplayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealDecisionReservationFillsExitAndRestart_AgreeWithHostedAuditAndReplay(bool withNews)
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var original = StrategyDecisionKernelTests.CreateRequest();
        if (withNews) {
            var news = StrategyDecisionKernelTests.CreateParityScenario("long_regular_catalyst").Catalysts[0];
            news = news with { DecisionKnownAtUtc = original.AsOfUtc.AddMinutes(-5),
                Event = news.Event with { Url = "https://example.com/attached-article", DecisionAvailableAt = original.AsOfUtc.AddMinutes(-5) } };
            var delayedRevision = news with { RevisionId = "revision-after-evaluated-bar", DecisionKnownAtUtc = original.AsOfUtc.AddMinutes(-1),
                Event = news.Event with { Headline = "Later revision must not replace the evaluated article", SentimentScore = -0.4m,
                    DecisionAvailableAt = original.AsOfUtc.AddMinutes(-1) } };
            original = original with { Catalysts = [news, delayedRevision] };
        }
        var clock = new CandidateWorkflowTestClock(original.AsOfUtc.AddSeconds(1));
        var source = new Mock<IUniverseSourceResolver>();
        source.Setup(item => item.ResolveAsync(It.IsAny<UniverseSourceSelectionRequest>(), "swing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UniverseSourceCapture("wishlist", "replay", original.AsOfUtc.AddMinutes(-10),
                original.AsOfUtc.AddMinutes(5), [new DiscoverySymbolObservation("RGTI"), new DiscoverySymbolObservation("MSFT")]));
        var discovery = new UniverseDiscoveryService(source.Object, database.Discovery, database.Workflows, database.Workflows, clock);
        var preview = await discovery.PreviewAsync(new(ContractVersions.VersionOne, "swing", original.AsOfUtc, [new("wishlist", "replay")]));
        var aggregate = (await database.Discovery.GetActiveAsync(preview.UniverseSnapshotId, original.AsOfUtc)).Single(item => item.Symbol == "RGTI");
        var run = new ProductionRun {
            RunId = Guid.NewGuid(), UniverseSnapshotId = preview.UniverseSnapshotId, SchemaVersion = 1,
            ConfigHash = new string('a', 64), CodeVersion = new string('b', 40),
            Profile = "paper_shadow", Status = "running", StartedAtUtc = original.AsOfUtc.AddMinutes(-15)
        };
        run.DecisionRunId = run.RunId;
        var request = original with { Candidate = original.Candidate with {
            State = StrategyCandidateState.Discovered, Version = 0,
            Identity = original.Candidate.Identity with { DiscoveryWindowStartUtc = aggregate.FirstDiscoveredAtUtc, DiscoveryWindowEndUtc = aggregate.ExpiresAtUtc },
            Discovery = new StrategyDiscoveryEvidence(aggregate.AggregateId, aggregate.Version, false, true,
                aggregate.FirstDiscoveredAtUtc, aggregate.ExpiresAtUtc,
                aggregate.Sources.Select(item => item.SourceKind + ":" + item.SourceKey).ToImmutableArray(),
                TradingFlow.Domain.Research.EvidenceCanonicalJson.ComputeSha256(aggregate))
        }};
        var orchestrator = new StrategyCandidateDecisionOrchestrator(new StrategyDecisionKernel(), database.Candidates);
        var triggered = await orchestrator.EvaluateAsync(run, request);
        Assert.True(triggered.IsNewTrigger);
        Assert.Equal(new[] { StrategyCandidateState.DataWarming, StrategyCandidateState.Qualified,
            StrategyCandidateState.Armed, StrategyCandidateState.Triggered },
            (await database.Candidates.GetTransitionsAsync(request.Candidate.CandidateId)).Select(item => item.NewState));
        Assert.True((await orchestrator.EvaluateAsync(run, request)).WasAlreadyTerminal);
        // A separate ticker uses the same engine, but its own identity and run.
        var otherRun = new ProductionRun { RunId = Guid.NewGuid(), UniverseSnapshotId = preview.UniverseSnapshotId,
            SchemaVersion = 1, ConfigHash = run.ConfigHash, CodeVersion = run.CodeVersion, Profile = run.Profile,
            Status = "running", StartedAtUtc = run.StartedAtUtc };
        var other = request with { Catalysts = [],
            Candidate = request.Candidate with { CandidateId = Guid.NewGuid(),
                Identity = request.Candidate.Identity with { Symbol = "MSFT" },
                MarketState = request.Candidate.MarketState with { IsWarm = false } },
            BarsByTimeframe = request.BarsByTimeframe.ToImmutableDictionary(item => item.Key,
                item => item.Value.Select(bar => bar with { Ticker = "MSFT" }).ToImmutableArray()),
            SnapshotsByTimeframe = request.SnapshotsByTimeframe.ToImmutableDictionary(item => item.Key,
                item => item.Value.Select(snapshot => snapshot with { Ticker = "MSFT" }).ToImmutableArray())
        };
        await orchestrator.EvaluateAsync(otherRun, other);
        var rejectedRequest = other with { Candidate = other.Candidate with {
            CandidateId = Guid.NewGuid(), Identity = other.Candidate.Identity with { SetupKey = "rejected-setup" },
            MarketState = other.Candidate.MarketState with { IsWarm = true },
            Universe = other.Candidate.Universe with { IsEligible = false }
        }};
        var rejected = await orchestrator.EvaluateAsync(otherRun, rejectedRequest);
        Assert.Equal(StrategyCandidateState.Rejected, rejected.Candidate.State);
        var rejectedState = await database.Queries(clock).GetCandidateAsync(rejected.Candidate.CandidateId);
        Assert.NotEmpty(rejectedState!.Rejections);
        var intents = new SqliteOrderIntentRepository(database, clock);
        var events = new SqliteOrderEventRepository(database);
        var lifecycle = new OrderLifecycleService(events, clock);
        var candidate = triggered.Candidate;
        var reservation = new OrderIntentReservation(
            Guid.NewGuid(), OrderIntentKind.StrategyEntry, candidate.CandidateId,
            "replay-account", candidate.SelectedStrategy!, candidate.Symbol, "buy", "limit", "day",
            10m, 10.2m, 9.2m, DateOnly.FromDateTime(clock.Now.UtcDateTime), clock.Now,
            """{"symbol":"RGTI","quantity":10,"takeProfitPrice":12.2,"submitOutsideRegularHours":false}""",
            clock.Now.AddMinutes(1),
            new PortfolioRiskReservationRequest("replay-account", "swing", 100000m, 100000m, 0m, 0m,
                new HashSet<string>(StringComparer.Ordinal), 102m, 10m, 100000m, 2000m, 20, clock.Now, clock.Now),
            candidate.Version, candidate.SemanticDecisionSha256);
        var entry = (await intents.ReserveAsync(run, reservation)).Intent;
        Assert.False((await intents.ReserveAsync(run, reservation)).Created);
        Assert.Equal("consumed", (await database.Queries(clock).GetAsync(candidate.CandidateId))!.Candidate.Readiness);
        await Submitted(events, entry);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IUniverseDiscoveryService>(Mock.Of<IUniverseDiscoveryService>());
        builder.Services.AddSingleton<ICandidateWorkflow>(Mock.Of<ICandidateWorkflow>());
        builder.Services.AddSingleton<ICandidateQueryService>(database.Queries(clock));
        builder.Services.AddSingleton<IDecisionAuditQuery>(database.Queries(clock));
        await using var app = builder.Build();
        app.MapGroup("/api/v1").MapCandidateWorkflowEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            async Task<CandidateAuditResponse> Audit() =>
                (await client.GetFromJsonAsync<CandidateAuditResponse>($"/api/v1/candidates/{candidate.CandidateId}/audit"))!;
            var pending = await Audit();
            Assert.Equal("consumed", pending.Candidate.Readiness);
            Assert.Equal(candidate.SemanticDecisionSha256, pending.SemanticDecisionSha256);
            Assert.Equal(entry.IntentId, pending.ExecutionEvidence!.OrderIntentId);
            Assert.Equal(preview.Members.Single(item => item.Symbol == "RGTI").Sources,
                pending.Candidate.Sources);
            Assert.Equal(20, pending.Candidate.RelativeVolumeSampleCount);
            if (withNews) {
                var attached = original.Catalysts[0];
                Assert.Equal(attached.ProviderArticleId, pending.CatalystEvidence!.ProviderArticleId);
                Assert.Equal(attached.FirstReceivedAtUtc, pending.CatalystEvidence.FirstReceivedAtUtc);
                Assert.Equal(attached.Event.Headline, pending.CatalystEvidence.Headline);
                Assert.Equal(attached.Event.Url, pending.CatalystEvidence.Url);
                await using var evidence = database.CreateDbContext();
                var row = await evidence.CatalystResults.SingleAsync(item => item.CandidateId == candidate.CandidateId);
                Assert.Contains(attached.RevisionId, row.RawComponentsJson);
                Assert.DoesNotContain("revision-after-evaluated-bar", row.RawComponentsJson);
            } else Assert.Null(pending.CatalystEvidence);
            Assert.NotEmpty(pending.GateEvaluations);
            Assert.Equal(20, pending.MarketEvidence!.RelativeVolumeSampleCount);
            Assert.Equal("1d", pending.MarketEvidence.Timeframe);
            Assert.True(pending.MarketEvidence.LatestCompletedCandleAtUtc <= request.AsOfUtc);
            var cursor = (await database.Queries(clock).GetRunAsync(run.RunId))!.LatestEventSequence;
            var partial = Update(entry, OrderStatus.PartiallyFilled, 4m, 10.2m, 3);
            var partialFill = new OrderFillProjection(candidate.Symbol, "buy", 4m, 10.2m, "entry-part",
                AuthoritativePositionQuantity: 4m, AuthoritativeLastFillQuantity: 4m, AuthoritativeLastFillPrice: 10.2m);
            clock.Now = entry.CreatedAtUtc.AddSeconds(5);
            await lifecycle.ApplyBrokerUpdateWithFillAsync(partial, partialFill);
            clock.Now = entry.CreatedAtUtc.AddSeconds(5);
            await lifecycle.ApplyBrokerUpdateWithFillAsync(partial, partialFill);
            var filledAudit = await Audit();
            Assert.Equal("in_trade", filledAudit.Candidate.Readiness);
            await VerifyBrowserFixtureAsync(filledAudit, withNews);
            await lifecycle.ApplyBrokerUpdateWithFillAsync(Update(entry, OrderStatus.Filled, 10m, 10.2m, 4),
                new OrderFillProjection(candidate.Symbol, "buy", 10m, 10.2m, "entry-final",
                    AuthoritativePositionQuantity: 10m, AuthoritativeLastFillQuantity: 6m, AuthoritativeLastFillPrice: 10.2m));
            long generation;
            await using (var context = database.CreateDbContext())
            {
                var fills = await context.PositionEvents.OrderBy(item => item.PositionEventId).ToArrayAsync();
                Assert.Equal(new[] { 4m, 6m }, fills.Select(item => item.FillQuantity));
                Assert.Equal(102m, fills.Sum(item => item.FillQuantity * item.FillPrice));
                generation = fills[0].PositionEventId;
            }
            // Reopen every persistence adapter before exiting, as after a service restart.
            intents = new SqliteOrderIntentRepository(database, clock);
            events = new SqliteOrderEventRepository(database);
            lifecycle = new OrderLifecycleService(events, clock);
            var exit = (await intents.ReserveAsync(run, reservation with {
                IntentId = Guid.NewGuid(), Kind = OrderIntentKind.PositionExit, CandidateId = null,
                CandidateExpectedVersion = null, CandidateSemanticDecisionSha256 = null,
                Side = "sell", OrderType = "market", LimitPrice = null, StopPrice = null,
                PortfolioRisk = null, DispatchExpiresAtUtc = null, PositionGenerationEventId = generation,
                RequestJson = """{"symbol":"RGTI","quantity":10,"reason":"deterministic_exit"}"""
            })).Intent;
            await Submitted(events, exit);
            clock.Now = entry.CreatedAtUtc.AddSeconds(20);
            var exitFill = new OrderFillProjection(candidate.Symbol, "sell", 10m, 11.2m, "exit-final",
                AuthoritativePositionQuantity: 0m, AuthoritativeLastFillQuantity: 10m, AuthoritativeLastFillPrice: 11.2m);
            await lifecycle.ApplyBrokerUpdateWithFillAsync(Update(exit, OrderStatus.Filled, 10m, 11.2m, 10), exitFill);
            await lifecycle.ApplyBrokerUpdateWithFillAsync(Update(exit, OrderStatus.Filled, 10m, 11.2m, 10), exitFill);
            Assert.Equal("consumed", (await Audit()).Candidate.Readiness);
            var restarted = database.Queries(clock);
            var projection = (await restarted.GetRunAsync(run.RunId))!;
            Assert.Equal(run.UniverseSnapshotId, projection.Provenance.UniverseSnapshotId);
            Assert.Equal(run.RunId, projection.Provenance.DecisionRunId);
            Assert.Equal("consumed", Assert.Single(projection.Candidates).Readiness);
            var replay = await new CandidateEventStreamService(database.Workflows, restarted).ReadAsync(run.RunId, cursor, 100);
            Assert.NotEmpty(replay.Events);
            Assert.All(replay.Events, item => Assert.Equal("consumed", item.Payload.Candidate.Readiness));
            Assert.Equal("warming", (await restarted.GetRunAsync(otherRun.RunId))!.Candidates.Single(item => item.CandidateId == other.Candidate.CandidateId).Readiness);
            await using var verification = database.CreateDbContext();
            Assert.Equal(2, await verification.OrderIntents.CountAsync());
            Assert.Equal(3, await verification.PositionEvents.CountAsync());
        }
        finally { await app.StopAsync(); }
    }

    private static async Task VerifyBrowserFixtureAsync(CandidateAuditResponse audit, bool withNews)
    {
        var json = JsonSerializer.Serialize(audit, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        // Identity relationships and hashes are asserted against their durable rows above.
        // Normalize only per-capture identities for the browser's frozen API projection.
        json = System.Text.RegularExpressions.Regex.Replace(json,
            @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "22222222-2222-2222-2222-222222222222");
        json = System.Text.RegularExpressions.Regex.Replace(json, @"[0-9a-f]{64}", new string('a', 64));
        json = System.Text.RegularExpressions.Regex.Replace(json, @"STRATEGYKE-B-RGTI-20260611-001-[0-9a-f]{8}", "STRATEGYKE-B-RGTI-20260611-001-aaaaaaaa");
        var file = "decision_replay_" + (withNews ? "news" : "no_news") + ".json";
        if (Environment.GetEnvironmentVariable("TRADINGFLOW_WRITE_REPLAY_FIXTURE") == "1")
            await File.WriteAllTextAsync(Path.Combine(TestRepository.FindRoot(), "src", "TradingFlow.Tests", "Fixtures", "Contracts", file), json);
        else
        {
            var expected = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Contracts", file));
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(expected), System.Text.Json.Nodes.JsonNode.Parse(json)), json);
        }
    }
    private static async Task Submitted(SqliteOrderEventRepository events, OrderIntentRecord intent)
    {
        await events.TransitionAsync(new(intent.ClientOrderId, OrderState.Intent, OrderState.Submitted, "engine",
            intent.CreatedAtUtc, PayloadJson: intent.RequestJson));
        await events.TransitionAsync(new(intent.ClientOrderId, OrderState.Submitted, OrderState.Acked, "broker_rest",
            intent.CreatedAtUtc.AddSeconds(1), intent.CreatedAtUtc.AddSeconds(1), "fixture-" + intent.IntentId));
    }
    private static OrderUpdate Update(OrderIntentRecord intent, OrderStatus status, decimal quantity, decimal price, int seconds) =>
        new("fixture-" + intent.IntentId, intent.ClientOrderId, intent.Symbol, intent.Side, status, quantity, price, 0,
            null, null, intent.CreatedAtUtc.AddSeconds(seconds), BrokerUpdateSource.BrokerRest);
}
