using System.Net;
using System.Security.Claims;
using System.Text.Json;
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
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;
using TradingFlow.Web;

namespace TradingFlow.Tests;

public sealed class CandidateStreamIntegrityTests
{
    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(true, 3, false)]
    [InlineData(true, 1, true)]
    public async Task Projection_OnlyEnrichesTheCapturedDecision(bool sameHash, int sequence, bool expectedEvidence)
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(CandidateAuditIntegrityTests.Now);
        var preview = await UniverseWorkflowIntegrityTests.Preview(database, clock);
        var run = await UniverseWorkflowIntegrityTests.Workflow(database, clock).StartAsync(UniverseWorkflowIntegrityTests.Request(preview));
        var candidate = (await database.Candidates.ListByRunAsync(run.CandidateRunId)).Single();
        candidate.Version = 2;
        candidate.SemanticDecisionSha256 = new string('a', 64);
        var transition = new CandidateTransitionRecord {
            CandidateId = candidate.CandidateId, Sequence = sequence, Source = "strategy_decision_kernel",
            SemanticDecisionSha256 = new string(sameHash ? 'a' : 'b', 64),
            OccurredAtUtc = clock.Now,
            EvidenceJson = """{"rules":[{"code":"later_failure","passed":false,"evidenceJson":"{}"}]}"""
        };
        var repository = new Mock<ICandidateRepository>();
        repository.Setup(item => item.ListByRunAsync(run.CandidateRunId, It.IsAny<CancellationToken>())).ReturnsAsync([candidate]);
        repository.Setup(item => item.GetLatestDecisionsAsync(run.CandidateRunId, It.IsAny<CancellationToken>())).ReturnsAsync([transition]);
        repository.Setup(item => item.GetAsync(candidate.CandidateId, It.IsAny<CancellationToken>())).ReturnsAsync(candidate);
        repository.Setup(item => item.GetTransitionsAsync(candidate.CandidateId, It.IsAny<CancellationToken>())).ReturnsAsync([transition]);
        var query = new CandidateApplicationService(repository.Object, new SqliteGateEvaluationRepository(database),
            new SqliteOrderIntentRepository(database), new SqliteCandidateAuditEvidenceRepository(database), database.Workflows, clock);
        var listed = Assert.Single((await query.GetRunAsync(run.CandidateRunId))!.Candidates);
        var single = (await query.GetCandidateAsync(candidate.CandidateId))!;
        var audit = (await query.GetAsync(candidate.CandidateId))!;
        foreach (var result in new[] { listed, single, audit.Candidate })
        {
            Assert.Equal(2, result.Version);
            Assert.Equal(expectedEvidence, result.Rejections.Any(item => item.Code == "later_failure"));
        }
    }

    [Fact]
    public async Task DurableReplay_IsBoundedRunScopedAndResynchronizesMalformedOrInvalidCursors()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(CandidateAuditIntegrityTests.Now);
        var preview = await UniverseWorkflowIntegrityTests.Preview(database, clock);
        var run = await UniverseWorkflowIntegrityTests.Workflow(database, clock).StartAsync(UniverseWorkflowIntegrityTests.Request(preview));
        var candidate = (await database.Candidates.GetAsync(run.Candidates[0].CandidateId))!;
        var name = $"candidate-runs/{run.CandidateRunId:N}";
        for (var index = 0; index < 115; index++)
        {
            await database.Workflows.AppendAsync(name, "candidate.changed", clock.Now, JsonSerializer.Serialize(candidate));
            await database.Workflows.AppendAsync("candidate-runs/other", "candidate.changed", clock.Now, "{}");
        }
        var stream = new CandidateEventStreamService(database.Workflows, database.Queries(clock));
        var first = await stream.ReadAsync(run.CandidateRunId, 0, 500);
        Assert.Equal(100, first.Events.Count);
        Assert.All(first.Events, item => Assert.Equal(run.CandidateRunId, item.Payload.CandidateRunId));
        var restarted = new CandidateEventStreamService(database.Workflows, database.Queries(clock));
        var rest = await restarted.ReadAsync(run.CandidateRunId, first.Events[^1].EventSequence, 100);
        Assert.Equal(16, rest.Events.Count);
        Assert.True(rest.Events[0].EventSequence > first.Events[^1].EventSequence);
        Assert.True((await stream.ReadAsync(run.CandidateRunId, rest.LatestSequence + 100, 100)).RequiresResync);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stream.ReadAsync(run.CandidateRunId, -1, 100));
        await database.Workflows.AppendAsync(name, "candidate.changed", clock.Now, "{broken");
        Assert.True((await stream.ReadAsync(run.CandidateRunId, rest.Events[^1].EventSequence, 100)).RequiresResync);
        await using var context = database.CreateDbContext();
        var retainedSequence = rest.Events[^1].EventSequence;
        await context.ApplicationEvents.Where(item => item.StreamName == name && item.EventSequence < retainedSequence).ExecuteDeleteAsync();
        Assert.True((await stream.ReadAsync(run.CandidateRunId, first.Events[0].EventSequence, 100)).RequiresResync);
    }

    [Fact]
    public async Task SnapshotWatermark_DoesNotSkipAChangeThatArrivesDuringProjection()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(CandidateAuditIntegrityTests.Now);
        var preview = await UniverseWorkflowIntegrityTests.Preview(database, clock);
        var run = await UniverseWorkflowIntegrityTests.Workflow(database, clock).StartAsync(UniverseWorkflowIntegrityTests.Request(preview));
        var originalCursor = run.LatestEventSequence;
        var candidates = new Mock<ICandidateRepository>();
        candidates.Setup(item => item.GetLatestDecisionsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        candidates.Setup(item => item.ListByRunAsync(run.CandidateRunId, It.IsAny<CancellationToken>())).Returns(async () =>
        {
            var old = await database.Candidates.ListByRunAsync(run.CandidateRunId);
            await using var context = database.CreateDbContext();
            var updated = await context.Candidates.SingleAsync();
            updated.State = StrategyCandidateState.DataWarming;
            updated.Version++;
            await context.SaveChangesAsync();
            await database.Workflows.AppendAsync($"candidate-runs/{run.CandidateRunId:N}", "candidate.changed", clock.Now, JsonSerializer.Serialize(updated));
            return old;
        });
        var query = new CandidateApplicationService(candidates.Object, new SqliteGateEvaluationRepository(database),
            new SqliteOrderIntentRepository(database), new SqliteCandidateAuditEvidenceRepository(database), database.Workflows, clock);
        var snapshot = (await query.GetRunAsync(run.CandidateRunId))!;
        Assert.Equal("Discovered", snapshot.Candidates[0].State);
        Assert.Equal(originalCursor, snapshot.LatestEventSequence);
        var replay = await new CandidateEventStreamService(database.Workflows, database.Queries(clock)).ReadAsync(run.CandidateRunId, snapshot.LatestEventSequence, 100);
        Assert.Equal("DataWarming", Assert.Single(replay.Events).Payload.Candidate.State);
    }

    [Fact]
    public async Task ProductionStream_MatchesExposurePollingReconnectsHeartbeatsAndSignalsResync()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(CandidateAuditIntegrityTests.Now);
        var preview = await UniverseWorkflowIntegrityTests.Preview(database, clock);
        var run = await UniverseWorkflowIntegrityTests.Workflow(database, clock).StartAsync(UniverseWorkflowIntegrityTests.Request(preview));
        var candidate = await CandidateAuditIntegrityTests.PopulateAsync(database, run.Candidates[0].CandidateId);
        var queries = database.Queries(clock);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ICandidateQueryService>(queries);
        builder.Services.AddSingleton<ICandidateEventStreamService>(new CandidateEventStreamService(database.Workflows, queries));
        await using var app = builder.Build();
        app.Use(async (context, next) => { context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "fixture")); await next(context); });
        app.UseAuthorization();
        app.MapGroup("/api/v1").RequireAuthorization().MapCandidateStreamEndpoints(new(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(80), TimeSpan.FromSeconds(2)));
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            var path = $"/api/v1/candidate-runs/{run.CandidateRunId}/events";
            using (var negative = await client.GetAsync(path + "?after=-1")) Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
            using (var future = await client.GetAsync(path + "?after=99999")) Assert.Equal(HttpStatusCode.Conflict, future.StatusCode);
            using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            var first = await ReadDataAsync(reader, "candidate.changed");
            Assert.Equal("in_trade", first.GetProperty("payload").GetProperty("candidate").GetProperty("readiness").GetString());
            var cursor = first.GetProperty("eventSequence").GetInt64();
            await ReadDataAsync(reader, "heartbeat");
            var ownerRun = (await new SqliteOrderIntentRepository(database).GetRunAsync(run.CandidateRunId))!;
            await new SqlitePositionLedgerRepository(database).AppendFillAsync(new PositionFillAppendRequest(ownerRun, "account", "MSFT", candidate.SelectedStrategy!,
                0, 10, 102m, "sell", "closing-order", "closing-client", "close-event", "broker_stream", clock.Now, clock.Now, "{}"));
            var close = await ReadDataAsync(reader, "candidate.changed");
            Assert.Equal("consumed", close.GetProperty("payload").GetProperty("candidate").GetProperty("readiness").GetString());
            Assert.Equal("consumed", (await queries.GetRunAsync(run.CandidateRunId))!.Candidates[0].Readiness);
            using var reconnectRequest = new HttpRequestMessage(HttpMethod.Get, path);
            reconnectRequest.Headers.Add("Last-Event-ID", cursor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var reconnected = await client.SendAsync(reconnectRequest, HttpCompletionOption.ResponseHeadersRead);
            using var resumedReader = new StreamReader(await reconnected.Content.ReadAsStreamAsync());
            var resumed = await ReadDataAsync(resumedReader, "candidate.changed");
            Assert.Equal(close.GetProperty("eventSequence").GetInt64(), resumed.GetProperty("eventSequence").GetInt64());
            await database.Workflows.AppendAsync($"candidate-runs/{run.CandidateRunId:N}", "candidate.changed", clock.Now, "bad-json");
            var resync = await ReadDataAsync(reader, "resync");
            Assert.Equal("event_cursor_expired", resync.GetProperty("code").GetString());
        }
        finally { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await app.StopAsync(stop.Token); }
    }

    [Fact]
    public async Task PositionGenerationChange_NotifiesBothPreviousAndNewOwners()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(CandidateAuditIntegrityTests.Now);
        var preview = await UniverseWorkflowIntegrityTests.Preview(database, clock);
        var workflow = UniverseWorkflowIntegrityTests.Workflow(database, clock);
        var first = await workflow.StartAsync(UniverseWorkflowIntegrityTests.Request(preview));
        var second = await workflow.StartAsync(UniverseWorkflowIntegrityTests.Request(preview) with { IdempotencyKey = "second-owner" });
        var candidate = await CandidateAuditIntegrityTests.PopulateAsync(database, first.Candidates[0].CandidateId);
        await using (var context = database.CreateDbContext())
        {
            var next = await context.Candidates.SingleAsync(item => item.CandidateId == second.Candidates[0].CandidateId);
            next.State = StrategyCandidateState.Consumed;
            context.OrderIntents.Add(new OrderIntentRecord {
                IntentId = Guid.NewGuid(), CandidateId = next.CandidateId, RunId = next.RunId, ConfigHash = next.ConfigHash, CodeVersion = next.CodeVersion,
                Kind = TradingFlow.Domain.Orders.OrderIntentKind.StrategyEntry, ClientOrderId = "new-owner", AccountId = "account",
                StrategyId = next.SelectedStrategy!, Symbol = "MSFT", Side = "sell", OrderType = "market", TimeInForce = "day",
                RequestedQuantity = 15, SessionDate = new DateOnly(2026, 9, 27), SequenceNumber = 2, CreatedAtUtc = clock.Now, RequestJson = "{}"
            });
            await context.SaveChangesAsync();
        }
        var queries = database.Queries(clock);
        var beforeFirst = (await queries.GetRunAsync(first.CandidateRunId))!;
        var beforeSecond = (await queries.GetRunAsync(second.CandidateRunId))!;
        var ownerRun = (await new SqliteOrderIntentRepository(database).GetRunAsync(second.CandidateRunId))!;
        await new SqlitePositionLedgerRepository(database).AppendFillAsync(new PositionFillAppendRequest(ownerRun, "account", "MSFT", candidate.SelectedStrategy!,
            -5, 15, 102m, "sell", "reversal", "new-owner", "reversal-fill", "broker_stream", clock.Now, clock.Now, "{}"));
        var stream = new CandidateEventStreamService(database.Workflows, queries);
        var oldOwner = await stream.ReadAsync(first.CandidateRunId, beforeFirst.LatestEventSequence, 100);
        var newOwner = await stream.ReadAsync(second.CandidateRunId, beforeSecond.LatestEventSequence, 100);
        Assert.Equal("consumed", Assert.Single(oldOwner.Events).Payload.Candidate.Readiness);
        Assert.Equal("in_trade", Assert.Single(newOwner.Events).Payload.Candidate.Readiness);
    }
    private static async Task<JsonElement> ReadDataAsync(StreamReader reader, string wanted)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var name = String.Empty;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];
            if (name == wanted && line.StartsWith("data: ", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(line[6..]);
                return document.RootElement.Clone();
            }
        }
        throw new InvalidOperationException("Stream ended before " + wanted);
    }
}
