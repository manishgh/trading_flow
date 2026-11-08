using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Tests;

public sealed class CandidateAuditIntegrityTests
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T12:00:00Z");

    [Fact]
    public async Task PartialEvidence_DoesNotInventTimestampsAndParsesNumbersInvariantly()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(Now);
        var preview = await UniverseWorkflowIntegrityTests.Preview(database, clock);
        var run = await UniverseWorkflowIntegrityTests.Workflow(database, clock).StartAsync(UniverseWorkflowIntegrityTests.Request(preview));
        var candidate = await PopulateAsync(database, run.Candidates[0].CandidateId);
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var audit = await database.Queries(clock).GetAsync(candidate.CandidateId);
            Assert.NotNull(audit!.MarketEvidence);
            Assert.Null(audit.MarketEvidence.LatestCompletedCandleAtUtc);
            Assert.Equal(101.2m, audit.MarketEvidence.BidPrice);
            Assert.Equal(101.3m, audit.MarketEvidence.AskPrice);
            Assert.Equal(60, audit.MarketEvidence.RelativeVolumeSampleCount);
            Assert.NotNull(audit.CatalystEvidence);
            Assert.Null(audit.CatalystEvidence.ProviderPublishedAtUtc);
            Assert.Null(audit.CatalystEvidence.FirstReceivedAtUtc);
            Assert.Equal(101.5m, audit.ExecutionEvidence!.AverageFillPrice); // cumulative, not the last fill at 102
            Assert.Equal("in_trade", audit.Candidate.Readiness);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("Rejected")]
    public async Task Exposure_BelongsOnlyToTheGenerationEntryCandidate(string otherOrderState)
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(Now);
        var preview = await UniverseWorkflowIntegrityTests.Preview(database, clock);
        var run = await UniverseWorkflowIntegrityTests.Workflow(database, clock).StartAsync(UniverseWorkflowIntegrityTests.Request(preview));
        var owner = await PopulateAsync(database, run.Candidates[0].CandidateId);
        var other = JsonSerializer.Deserialize<CandidateRecord>(JsonSerializer.Serialize(owner))!;
        other.CandidateId = Guid.NewGuid();
        other.SetupKey += ":other";
        other.CatalystResultId = null;
        await using (var context = database.CreateDbContext())
        {
            context.Candidates.Add(other);
            context.OrderIntents.Add(Intent(other, "other-entry", 2));
            context.OrderEvents.Add(new OrderEventRecord
            {
                RunId = other.RunId, ConfigHash = other.ConfigHash, CodeVersion = other.CodeVersion,
                ClientOrderId = "other-entry", NewState = otherOrderState, Source = "test", LocalTimestampUtc = Now, PayloadJson = "{}"
            });
            await context.SaveChangesAsync();
        }
        var queries = database.Queries(clock);
        var projected = await queries.GetRunAsync(owner.RunId);
        Assert.Equal("in_trade", projected!.Candidates.Single(item => item.CandidateId == owner.CandidateId).Readiness);
        Assert.Equal("consumed", projected.Candidates.Single(item => item.CandidateId == other.CandidateId).Readiness);
        Assert.Equal("consumed", (await queries.GetCandidateAsync(other.CandidateId))!.Readiness);
        Assert.Equal("consumed", (await queries.GetAsync(other.CandidateId))!.Candidate.Readiness);
        await using (var context = database.CreateDbContext())
        {
            var close = Position(owner, 0m, "close");
            // A protection/reconciliation run can close the same account position.
            var closingRun = new ProductionRun { RunId = Guid.NewGuid(), Profile = "protection", Status = "complete", StartedAtUtc = Now,
                ConfigHash = owner.ConfigHash, CodeVersion = owner.CodeVersion };
            context.ProductionRuns.Add(closingRun);
            close.RunId = closingRun.RunId;
            context.PositionEvents.Add(close);
            await context.SaveChangesAsync();
        }
        Assert.Equal("consumed", (await queries.GetCandidateAsync(owner.CandidateId))!.Readiness);
        Assert.All((await queries.GetRunAsync(owner.RunId))!.Candidates, item => Assert.Equal("consumed", item.Readiness));
    }

    [Fact]
    public async Task Catalyst_RejectsAnotherCandidatesClassification()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(Now);
        var preview = await UniverseWorkflowIntegrityTests.Preview(database, clock);
        var run = await UniverseWorkflowIntegrityTests.Workflow(database, clock).StartAsync(UniverseWorkflowIntegrityTests.Request(preview));
        var candidate = await PopulateAsync(database, run.Candidates[0].CandidateId);
        await using (var context = database.CreateDbContext())
        {
            var catalyst = await context.CatalystResults.SingleAsync();
            catalyst.CandidateId = Guid.NewGuid();
            await context.SaveChangesAsync();
        }
        Assert.Null((await database.Queries(clock).GetAsync(candidate.CandidateId))!.CatalystEvidence);
    }

    [Fact]
    public async Task Catalyst_LaterNewsReingestionCannotRewriteEarlierReceiptEvidence()
    {
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var clock = new CandidateWorkflowTestClock(Now);
        var preview = await UniverseWorkflowIntegrityTests.Preview(database, clock);
        var run = await UniverseWorkflowIntegrityTests.Workflow(database, clock).StartAsync(UniverseWorkflowIntegrityTests.Request(preview));
        var candidate = await PopulateAsync(database, run.Candidates[0].CandidateId);
        await using (var context = database.CreateDbContext())
        {
            (await context.Candidates.SingleAsync()).RevalidatedAtUtc = Now.AddMinutes(2);
            context.NewsItems.Add(new TradingFlow.Domain.News.PersistedNewsItem
            {
                Id = "story", Ticker = "MSFT", Timestamp = Now.AddMinutes(-1), IngestedAt = Now.AddMinutes(1),
                Provider = "later-provider", Headline = "Later revision", Url = "https://example.invalid/later"
            });
            await context.SaveChangesAsync();
        }
        var audit = await database.Queries(clock).GetAsync(candidate.CandidateId);
        Assert.NotNull(audit!.CatalystEvidence);
        Assert.Null(audit.CatalystEvidence.FirstReceivedAtUtc);
        Assert.Null(audit.CatalystEvidence.Headline);
        Assert.Null(audit.CatalystEvidence.Url);
        Assert.Null(audit.CatalystEvidence.ProviderPublishedAtUtc);
        Assert.Equal("test-news", audit.CatalystEvidence.Provider);
    }
    internal static async Task<CandidateRecord> PopulateAsync(CandidateWorkflowTestDatabase database, Guid id)
    {
        await using var context = database.CreateDbContext();
        var candidate = await context.Candidates.SingleAsync(item => item.CandidateId == id);
        candidate.State = StrategyCandidateState.Consumed;
        candidate.Version = 1;
        candidate.LastPrice = 101.25m;
        candidate.SpreadBps = 2m;
        candidate.SameTimeRvol = 1.5m;
        var scores = JsonNode.Parse(candidate.SetupScoresJson)!;
        scores["bidPrice"] = 101.2m;
        scores["askPrice"] = 101.3m;
        scores["relativeVolumeSampleCount"] = 60;
        scores["timeframe"] = "1d";
        scores["session"] = "regular_market";
        candidate.SetupScoresJson = scores.ToJsonString();
        candidate.CatalystResultId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        context.CatalystResults.Add(new CatalystResultRecord
        {
            CatalystResultId = candidate.CatalystResultId.Value, CandidateId = id, RunId = candidate.RunId,
            ConfigHash = candidate.ConfigHash, CodeVersion = candidate.CodeVersion, Symbol = candidate.Symbol,
            ProviderArticleId = "story", Category = "earnings", Direction = "positive", CompositeScore = 0.7m,
            EvaluatedAtUtc = Now, RawComponentsJson = "{\"provider\":\"test-news\"}"
        });
        context.CandidateTransitions.Add(new CandidateTransitionRecord
        {
            RunId = candidate.RunId, ConfigHash = candidate.ConfigHash, CodeVersion = candidate.CodeVersion,
            CandidateId = id, Sequence = 1, PreviousState = StrategyCandidateState.Triggered, NewState = StrategyCandidateState.Consumed,
            OccurredAtUtc = Now, ReasonCode = "entry_reserved", Source = "engine", SemanticDecisionSha256 = candidate.SemanticDecisionSha256,
            EvidenceJson = "{}"
        });
        context.GateEvaluations.Add(new GateEvaluationRecord
        {
            RunId = candidate.RunId, ConfigHash = candidate.ConfigHash, CodeVersion = candidate.CodeVersion,
            CandidateId = id, GateOrder = 1, GateName = "spread", Passed = true, EvaluatedAtUtc = Now, InputsJson = "{\"spreadBps\":2}"
        });
        var intent = Intent(candidate, "owner-entry", 1);
        context.OrderIntents.Add(intent);
        context.OrderEvents.Add(new OrderEventRecord
        {
            RunId = candidate.RunId, ConfigHash = candidate.ConfigHash, CodeVersion = candidate.CodeVersion,
            ClientOrderId = intent.ClientOrderId, BrokerOrderId = "broker-entry", NewState = "Filled", Source = "broker_stream",
            LocalTimestampUtc = Now, FilledQuantity = 10m, FillPrice = 101.5m, PayloadJson = "{}"
        });
        context.PositionEvents.Add(Position(candidate, 10m, "fill"));
        await context.SaveChangesAsync();
        return candidate;
    }

    private static OrderIntentRecord Intent(CandidateRecord candidate, string clientId, int sequence) => new()
    {
        IntentId = clientId == "owner-entry" ? Guid.Parse("33333333-3333-3333-3333-333333333333") : Guid.NewGuid(),
        CandidateId = candidate.CandidateId, RunId = candidate.RunId, ConfigHash = candidate.ConfigHash, CodeVersion = candidate.CodeVersion,
        Kind = OrderIntentKind.StrategyEntry, ClientOrderId = clientId, AccountId = "account", StrategyId = candidate.SelectedStrategy!,
        Symbol = candidate.Symbol, Side = "buy", OrderType = "market", TimeInForce = "day", RequestedQuantity = 10,
        SessionDate = DateOnly.FromDateTime(Now.UtcDateTime), SequenceNumber = sequence, CreatedAtUtc = Now, RequestJson = "{}"
    };
    private static PositionEventRecord Position(CandidateRecord candidate, decimal quantity, string execution) => new()
    {
        RunId = candidate.RunId, ConfigHash = candidate.ConfigHash, CodeVersion = candidate.CodeVersion, AccountId = "account",
        Symbol = candidate.Symbol, StrategyId = candidate.SelectedStrategy!, ExecutionStrategyId = candidate.SelectedStrategy!,
        PositionGenerationClientOrderId = "owner-entry", PositionGenerationEventId = 1, QuantityAfter = quantity,
        FillQuantity = 10m, FillPrice = 102m, Side = quantity == 0 ? "sell" : "buy", BrokerOrderId = "broker-entry",
        ClientOrderId = "owner-entry", ExecutionId = execution, Source = "broker_stream", BrokerTimestampUtc = Now,
        LocalTimestampUtc = Now, PayloadJson = "{}"
    };
}
