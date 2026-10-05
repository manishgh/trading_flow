using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class AuditWorkflowReaderTests
{
    [Fact]
    public async Task JournalCountsSeparateIntentsPartialFillsAndClosedGenerations()
    {
        await using var database = new CandidateWorkflowTestDatabase(); await database.InitializeAsync();
        var run = Guid.NewGuid(); var otherRun = Guid.NewGuid(); var decision = Guid.NewGuid();
        await using (var context = database.CreateDbContext())
        {
            context.Jobs.Add(new PersistedJob { Id = run, RunName = "paper", JobType = "paper" });
            context.ProductionRuns.AddRange(new ProductionRun { RunId = run, DecisionRunId = decision },
                new ProductionRun { RunId = otherRun }, new ProductionRun { RunId = decision });
            var candidate = new CandidateRecord { CandidateId = Guid.NewGuid(), RunId = decision, Symbol = "AAA" };
            context.Candidates.Add(candidate);
            context.GateEvaluations.AddRange(new GateEvaluationRecord { RunId = run, CandidateId = candidate.CandidateId, GateName = "Risk", GateOrder = 4, Passed = true },
                new GateEvaluationRecord { RunId = run, CandidateId = candidate.CandidateId, GateName = "Risk", GateOrder = 4, Passed = false });
            var partial = Intent(run, "partial", 1); var reopened = Intent(run, "reopened", 2);
            context.OrderIntents.AddRange(partial, reopened, Intent(run, "rejected", 3), Intent(run, "unfilled", 4),
                Intent(run, "exit", 5, OrderIntentKind.PositionExit), Intent(otherRun, "unrelated", 6));
            context.PortfolioRiskReservations.AddRange(Quantity(partial, 4), Quantity(reopened, 10));
            // Two fills of one order remain one partially filled entry.
            context.PositionEvents.AddRange(Fill(run, "partial", 1, 1, 2), Fill(run, "partial", 2, 1, 4),
                // Wrong-account event cannot turn an unfilled entry into a fill.
                Fill(run, "unfilled", 3, 3, 10, "wrong-account"),
                Fill(run, "reopened", 4, 4, 10),
                // Protection can close the first generation from another owner.
                Fill(otherRun, "protection-close", 5, 1, 0),
                // Later reopen has another generation and must not close generation 4.
                Fill(otherRun, "unrelated", 6, 6, 5), Fill(otherRun, "unrelated-exit", 7, 6, 0),
                Fill(run, "exit", 8, 4, 5));
            await context.SaveChangesAsync();
        }
        var reader = Reader(database); var result = await reader.ReadAsync("paper", default);
        Assert.Equal(1, Count(result, "Discovered candidates")); Assert.Equal(4, Count(result, "Entry intents"));
        Assert.Equal(2, Count(result, "Filled entry orders")); Assert.Equal(1, Count(result, "Currently partial entry orders"));
        Assert.Equal(1, Count(result, "Filled exit/protection orders")); Assert.Equal(1, Count(result, "Completed position generations"));
        var gate = Assert.Single(result.Gates); Assert.Equal(4, gate.Order); Assert.Equal(2, gate.Reached); Assert.Equal(1, gate.Passed); Assert.Equal(1, gate.Rejected);
        await using (var context = database.CreateDbContext())
        {
            context.Jobs.Add(new PersistedJob { Id = Guid.NewGuid(), RunName = "paper", JobType = "backtest" }); await context.SaveChangesAsync();
        }
        Assert.All((await reader.ReadAsync("paper", default)).Counts, row => Assert.Null(row.Count));
    }

    [Fact]
    public async Task MissingLineageGateTracesAndFillQuantityStayUnavailable()
    {
        await using var database = new CandidateWorkflowTestDatabase(); await database.InitializeAsync(); var run = Guid.NewGuid();
        await using (var context = database.CreateDbContext())
        {
            context.Jobs.Add(new PersistedJob { Id = run, RunName = "sparse", JobType = "paper" });
            context.ProductionRuns.Add(new ProductionRun { RunId = run }); context.OrderIntents.Add(Intent(run, "entry", 1));
            context.PositionEvents.Add(Fill(run, "entry", 1, 1, 2)); await context.SaveChangesAsync();
        }
        var result = await Reader(database).ReadAsync("sparse", default);
        Assert.Null(Count(result, "Discovered candidates")); Assert.Empty(result.Gates); Assert.Contains("traces unavailable", result.Detail);
        Assert.Equal(1, Count(result, "Filled entry orders")); Assert.Null(Count(result, "Currently partial entry orders"));
    }

    [Fact]
    public void PublishedBacktestUsesUnifiedPortfolioInsteadOfWinnerCounts()
    {
        var now = DateTimeOffset.UtcNow;
        var trade = new BacktestTrade("AAA", "second-strategy", "long", now.AddDays(-1), now, 4, 10, 11, 9, 12, "target", 4, 0, 4, RequestedShareQuantity: 10);
        var portfolio = new UnifiedPortfolioBacktestResult(10000,10008,8,.08m,0,8,2,6,2,0,[trade,trade with { CandidateId = "other" }]);
        var result = new BacktestResult("run","unused",now,now,10000,10004,4,.04m,0,1,0,null,100,1,1,0,1,0,[],[],null!,[trade],[],[],[],UnifiedPortfolio:portfolio);
        var manifest = new BacktestArtifactManifest(1,"set","run",now,true,new(2,2,0,[]),[new(1,"candidate_decisions","candidates.json",7,100,new string('a',64),true)]);
        var evidence = AuditWorkflowReader.FromPublished(result,manifest);
        Assert.Equal(7,Count(evidence,"Published candidate histories")); Assert.Equal(8,Count(evidence,"Candidate orders"));
        Assert.Equal(2,Count(evidence,"Filled entries")); Assert.Equal(2,Count(evidence,"Completed trades / exits")); Assert.Empty(evidence.Gates);
        var legacy = AuditWorkflowReader.FromPublished(result with { UnifiedPortfolio=portfolio with { CompletedTrades=[trade with { RequestedShareQuantity=0 },trade] } },manifest);
        Assert.Null(Count(legacy,"Partially filled entries")); Assert.Equal(2,Count(legacy,"Filled entries"));
        Assert.All(AuditWorkflowReader.FromPublished(result with { UnifiedPortfolio=null },manifest).Counts,row=>Assert.Null(row.Count));
    }

    private static AuditWorkflowReader Reader(CandidateWorkflowTestDatabase database) => new(database,new SimpleYamlReader(),new ProjectPaths(TestRepository.FindRoot()));
    private static long? Count(AuditWorkflowEvidence result,string name) => result.Counts.Single(row=>row.Label==name).Count;
    private static OrderIntentRecord Intent(Guid run,string client,int sequence,OrderIntentKind kind=OrderIntentKind.OperatorEntry) => new()
    {RunId=run,IntentId=Guid.NewGuid(),ClientOrderId=client,AccountId="account",Symbol="AAA",StrategyId="strategy",SequenceNumber=sequence,RequestedQuantity=10,Kind=kind};
    private static PortfolioRiskReservationRecord Quantity(OrderIntentRecord intent,decimal filled) => new()
    {RunId=intent.RunId,ReservationId=Guid.NewGuid(),IntentId=intent.IntentId,ClientOrderId=intent.ClientOrderId,AccountId=intent.AccountId,Symbol=intent.Symbol,State=PortfolioRiskReservationState.Released,CumulativeFilledQuantity=filled};
    private static PositionEventRecord Fill(Guid run,string client,long id,long generation,decimal after,string account="account") => new()
    {RunId=run,PositionEventId=id,PositionGenerationEventId=generation,PositionGenerationClientOrderId=generation.ToString(),ClientOrderId=client,AccountId=account,Symbol="AAA",ExecutionId=id.ToString(),FillQuantity=2,QuantityAfter=after,Source="broker_stream"};
}
