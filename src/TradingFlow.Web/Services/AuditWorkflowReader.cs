using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Web.Services;

public sealed record AuditWorkflowCount(string Label, long? Count, string Evidence);
public sealed record AuditWorkflowGate(int Order, string Name, int Reached, int Passed, int Rejected);
public sealed record AuditWorkflowEvidence(IReadOnlyList<AuditWorkflowCount> Counts,
    IReadOnlyList<AuditWorkflowGate> Gates, string Detail);
public interface IAuditWorkflowReader
{
    Task<AuditWorkflowEvidence> ReadAsync(string runName, CancellationToken token);
}

/// <summary>Observes existing journals; an accepted signal never stands in for a broker fill.</summary>
public sealed class AuditWorkflowReader(IDbContextFactory<TradingFlowDbContext> contexts,
    SimpleYamlReader yaml, ProjectPaths paths) : IAuditWorkflowReader
{
    public async Task<AuditWorkflowEvidence> ReadAsync(string runName, CancellationToken token)
    {
        await using var context = await contexts.CreateDbContextAsync(token);
        await context.Database.OpenConnectionAsync(token);
        // Deferred reads observe one WAL snapshot without reserving the writer slot.
        await using var snapshot = ((SqliteConnection)context.Database.GetDbConnection()).BeginTransaction(deferred: true);
        context.Database.UseTransaction(snapshot);
        var matches = await context.Jobs.AsNoTracking().Where(job => job.RunName == runName &&
            (job.JobType == "paper" || job.JobType == "backtest")).Take(2).ToArrayAsync(token);
        if (matches.Length != 1) return Missing("No unique retained job matches this exact run name. Journal lineage is unavailable.");
        var job = matches[0];
        if (job.JobType == "backtest")
        {
            if (job.Status != "completed" || String.IsNullOrWhiteSpace(job.ResultReference))
                return Missing("This backtest has no retained completed publication; no fills or candidate counts inferred.");
            try
            {
                AuditEquityReader.Confine(paths.ConfigsRoot, job.ConfigPath);
                var root = yaml.ReadBacktestRun(job.ConfigPath).ResultsRoot;
                var published = await AuditEquityReader.ReadResultAsync(runName, root, job.ResultReference, token);
                return published is null ? Missing("Published result manifest unavailable.") : FromPublished(published.Value.Result, published.Value.Manifest);
            }
            catch (Exception error) when (error is IOException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            {
                return Missing("Published workflow evidence could not be verified or exceeds its read limits.");
            }
        }
        // All counters observe one database snapshot rather than different moments of a running job.
        var run = await context.ProductionRuns.AsNoTracking().SingleOrDefaultAsync(item => item.RunId == job.Id, token);
        if (run is null) return Missing("The named paper job has no persisted execution run yet.");
        var candidateRun = run.DecisionRunId ?? run.RunId;
        var candidates = await context.Candidates.CountAsync(item => item.RunId == candidateRun, token);
        var entryIntents = context.OrderIntents.AsNoTracking().Where(intent => intent.RunId == run.RunId &&
            (intent.Kind == OrderIntentKind.StrategyEntry || intent.Kind == OrderIntentKind.OperatorEntry));
        var exitIntents = context.OrderIntents.AsNoTracking().Where(intent => intent.RunId == run.RunId &&
            (intent.Kind == OrderIntentKind.PositionExit || intent.Kind == OrderIntentKind.ProtectiveStop));
        var ledger = context.PositionEvents.AsNoTracking().Where(fill => fill.FillQuantity > 0 &&
            (fill.Source == "broker_stream" || fill.Source == "broker_rest"));
        var filledEntries = entryIntents.Where(intent => ledger.Any(fill => fill.RunId == intent.RunId &&
            fill.ClientOrderId == intent.ClientOrderId && fill.AccountId == intent.AccountId && fill.Symbol == intent.Symbol));
        var filledExits = exitIntents.Where(intent => ledger.Any(fill => fill.RunId == intent.RunId &&
            fill.ClientOrderId == intent.ClientOrderId && fill.AccountId == intent.AccountId && fill.Symbol == intent.Symbol));
        var filledCount = await filledEntries.CountAsync(token);
        var reservations = context.PortfolioRiskReservations.AsNoTracking();
        var filledWithQuantity = filledEntries.Where(intent => reservations.Any(quantity =>
            quantity.RunId == intent.RunId && quantity.ClientOrderId == intent.ClientOrderId && quantity.AccountId == intent.AccountId &&
            quantity.CumulativeFilledQuantity > 0));
        var quantityCoverage = await filledWithQuantity.CountAsync(token);
        long? partialCount = quantityCoverage == filledCount ? await filledEntries.CountAsync(intent => reservations.Any(quantity =>
            quantity.RunId == intent.RunId && quantity.ClientOrderId == intent.ClientOrderId && quantity.AccountId == intent.AccountId &&
            quantity.CumulativeFilledQuantity > 0 && quantity.CumulativeFilledQuantity < intent.RequestedQuantity), token) : null;
        var generations = ledger.Where(first => first.PositionEventId == first.PositionGenerationEventId && first.QuantityAfter != 0 &&
            entryIntents.Any(intent => intent.RunId == first.RunId && intent.ClientOrderId == first.ClientOrderId &&
                intent.AccountId == first.AccountId && intent.Symbol == first.Symbol));
        var closed = await generations.CountAsync(first => ledger.Any(close => close.PositionGenerationEventId == first.PositionGenerationEventId &&
            close.PositionEventId > first.PositionEventId && close.AccountId == first.AccountId && close.Symbol == first.Symbol && close.QuantityAfter == 0), token);
        var gates = await context.GateEvaluations.AsNoTracking().Where(item => item.RunId == run.RunId)
            .GroupBy(item => new { item.GateOrder, item.GateName })
            .Select(group => new { Order = group.Key.GateOrder, Name = group.Key.GateName, Reached = group.Count(),
                Passed = group.Count(item => item.Passed), Rejected = group.Count(item => !item.Passed) })
            .OrderBy(item => item.Order).ThenBy(item => item.Name).Take(65).ToArrayAsync(token);
        var detail = "Recorded counts use different units: candidates, signal evaluations, gate attempts, order intents and filled orders. They are not one declining cohort. " +
            (run.DecisionRunId is null && candidates == 0 ? "Candidate lineage is unavailable. " : "Candidates use the explicit decision/run identity. ") +
            (gates.Length == 0 ? "Ordered gate traces unavailable; no survivor sequence inferred. " : "Gate order comes from persisted traces; passed/rejected are reached attempts, including repeat evaluations. Skipped gates have no recorded attempt. ") +
            (gates.Length > 64 ? "Only the first 64 distinct gate rows are shown. " : "") +
            "Fills require an authoritative position-ledger event matching run, order, account and symbol. Completed generations include later closes by protection/reconciliation owners; reopened generations stay distinct.";
        return new([
            new("Discovered candidates", run.DecisionRunId is not null || candidates > 0 ? candidates : null, "Exact persisted candidate-run identity"),
            new("Entry intents", await entryIntents.CountAsync(token), "Includes rejected and unfilled commands"),
            new("Filled entry orders", filledCount, "Distinct entry intents with authoritative fills; includes partial fills"),
            new("Currently partial entry orders", partialCount, "Recorded cumulative filled quantity below requested quantity; unavailable when quantity coverage is incomplete"),
            new("Exit/protection intents", await exitIntents.CountAsync(token), "Includes untriggered protective stops and rejected exits"),
            new("Filled exit/protection orders", await filledExits.CountAsync(token), "Distinct intents with authoritative fills"),
            new("Completed position generations", closed, "Recorded flat close of a generation opened by this run")
        ], gates.Take(64).Select(gate => new AuditWorkflowGate(gate.Order, gate.Name, gate.Reached, gate.Passed, gate.Rejected)).ToArray(), detail);
    }

    internal static AuditWorkflowEvidence FromPublished(TradingFlow.Domain.Backtesting.BacktestResult result,
        TradingFlow.Domain.Backtesting.BacktestArtifactManifest manifest)
    {
        var portfolio = result.UnifiedPortfolio;
        if (!result.EconomicResultsComplete || portfolio is null || !portfolio.EconomicResultsComplete ||
            !manifest.PublicationComplete || manifest.Coverage.FailedWorkItemCount != 0 || manifest.Coverage.ExecutionFailureCount != 0 ||
            portfolio.CompletedTrades.Count != portfolio.AcceptedTradeCount)
            return Missing("Complete unified portfolio evidence unavailable; winner-strategy totals are not substituted.");
        var histories = manifest.Artifacts.Where(item => item.Kind == "candidate_decisions" && item.Complete && item.RecordCount >= 0).ToArray();
        return new([
            new("Published candidate histories", histories.Length == 1 ? histories[0].RecordCount : null, "Published manifest record count; history is not loaded into the browser"),
            new("Candidate orders", portfolio.CandidateTradeCount, "Unified portfolio, across all strategies"),
            new("Rejected candidate orders", portfolio.RejectedTradeCount, "Unified portfolio, across all strategies"),
            new("Filled entries", portfolio.CompletedTrades.Count, "Complete retained unified trades; each contains entry and exit evidence"),
            new("Partially filled entries", portfolio.CompletedTrades.All(trade => trade.ShareQuantity > 0 && trade.RequestedShareQuantity >= trade.ShareQuantity)
                ? portfolio.CompletedTrades.Count(trade => trade.RequestedShareQuantity > trade.ShareQuantity) : null, "Requested versus recorded entry quantity; unavailable without requested-quantity coverage"),
            new("Completed trades / exits", portfolio.AcceptedTradeCount, "Complete unified portfolio; not the winner summary")
        ], [], "Completed published backtest result, verified against its manifest hash. Candidate-history count is declared by the publication manifest. Ordered entry-gate attempt traces are unavailable in this artifact; no survivor sequence inferred. Simulated trades are not broker orders.");
    }

    internal static AuditWorkflowEvidence Missing(string detail) => new([
        new("Discovered candidates", null, "Unavailable"), new("Filled entries", null, "Unavailable"), new("Completed trades / exits", null, "Unavailable")
    ], [], detail);
}
