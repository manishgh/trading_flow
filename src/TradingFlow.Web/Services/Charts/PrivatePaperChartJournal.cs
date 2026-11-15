using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Web.Services.Charts;

internal sealed record PaperChartIntent(Guid IntentId, Guid RunId, string AccountId, string Symbol, string ClientOrderId,
    string ConfigHash, string CodeVersion, string StrategyId, OrderIntentKind Kind, long? GenerationId,
    Guid? CandidateId, int? TriggeredVersion, string? DecisionHash, string? TriggeredEvidenceHash, string OrderType, decimal? LimitPrice,
    decimal? StopPrice, DateTimeOffset CreatedAtUtc, string RequestJson);

internal sealed record PaperChartLifecycle(long Id, Guid RunId, string ClientOrderId, string? BrokerOrderId,
    string ConfigHash, string CodeVersion, string State, string Source, DateTimeOffset? BrokerAtUtc,
    DateTimeOffset LocalAtUtc, string Payload);

internal sealed record PaperChartReplacement(Guid Id, string Owner, string Root, string Predecessor, string Successor,
    string ReplacementClientOrderId, decimal StopPrice, DateTimeOffset Verified, string Reason);

internal sealed record PaperChartDecision(DateTimeOffset? AtUtc, decimal? RawPrice, string? Reason);
internal sealed record PaperChartFacts(PaperChartBinding Binding, IReadOnlyList<PaperChartIntent> Orders,
    IReadOnlyList<PaperChartLifecycle> Lifecycle, IReadOnlyList<PositionEventRecord> Positions,
    IReadOnlyList<PaperChartReplacement> Replacements, PaperChartDecision? Decision,
    bool ProtectionGenerationProven = false, bool OverBudget = false);

/// <summary>Reads only exact journal identities within one deferred SQLite snapshot.</summary>
internal sealed class PrivatePaperChartJournal(IDbContextFactory<TradingFlowDbContext> contexts,
    PrivatePaperChartAccess access)
{
    internal const int CandidateLimit = 200;
    private const int MaxPayload = 8192;

    // Preserve the intent's join keys and selected evidence columns, with oversized payloads left unavailable.
    internal static IQueryable<PaperChartIntent> Intents(IQueryable<OrderIntentRecord> source) =>
        source.AsNoTracking().Select(row => new PaperChartIntent(row.IntentId, row.RunId, row.AccountId, row.Symbol,
            row.ClientOrderId, row.ConfigHash, row.CodeVersion, row.StrategyId, row.Kind, row.PositionGenerationEventId,
            row.CandidateId, row.CandidateTriggeredVersion, row.CandidateSemanticDecisionSha256, row.CandidateTriggeredEvidenceSha256, row.OrderType,
            row.LimitPrice, row.StopPrice, row.CreatedAtUtc, row.RequestJson.Length <= MaxPayload ? row.RequestJson : ""));

    // The linked intent, lifecycle and position reads share the same 200 rows plus one overflow sentinel.
    public async Task<PaperChartFacts?> ReadAsync(PaperChartScope scope, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await db.Database.OpenConnectionAsync(token);
        await using var snapshot = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
        db.Database.UseTransaction(snapshot);
        var binding = await access.ResolveBoundAsync(db, scope, token);
        if (binding is null) return null;
        var budget = new Budget(CandidateLimit - (scope.GenerationId.HasValue ? 2 : 1));
        try { return await ReadFactsAsync(db, binding, budget, token); }
        catch (CandidateBudgetExceeded) { return new(binding, [], [], [], [], null, OverBudget: true); }
    }

    // Resolve indexed identities before projecting any payload details or following replacement links.
    private static async Task<PaperChartFacts> ReadFactsAsync(TradingFlowDbContext db, PaperChartBinding binding,
        Budget budget, CancellationToken token)
    {
        var scope = binding.Scope;
        List<PositionEventRecord> positions = scope.GenerationId is { } generation
            ? await budget.ReadAsync(PositionFields(db.PositionEvents.Where(row => row.AccountId == scope.AccountId &&
                row.Symbol == scope.JournalSymbol && row.PositionGenerationEventId == generation &&
                row.PositionEventId != generation)), token)
            : [];
        var orders = new List<PaperChartIntent> { binding.Intent };
        if (scope.GenerationId is { } ownedGeneration)
        {
            var linked = await budget.ReadAsync(Intents(db.OrderIntents.Where(row => row.AccountId == scope.AccountId &&
                row.Symbol == scope.JournalSymbol && row.PositionGenerationEventId == ownedGeneration && row.IntentId != scope.IntentId)), token);
            var runIds = linked.Select(order => order.RunId).Distinct().ToArray();
            var runs = await budget.ReadAsync(RunFields(db.ProductionRuns.Where(row => runIds.Contains(row.RunId))), token);
            var provenance = runs.ToDictionary(run => run.RunId);
            orders.AddRange(linked.Where(order => provenance.TryGetValue(order.RunId, out var run) &&
                PrivatePaperChartAccess.Agrees(order, run) && order.Kind is OrderIntentKind.PositionExit or OrderIntentKind.ProtectiveStop));
            positions.Add(binding.Opening!);
        }
        var lifecycle = await ReadLifecycleAsync(db, orders, budget, token);
        if (scope.GenerationId is null)
            positions = await ReadExactFillsAsync(db, scope, lifecycle, budget, token);
        var protectionGenerationProven = scope.GenerationId.HasValue ||
            await ProveExactProtectionGenerationAsync(db, binding, budget, token);
        List<PaperChartReplacement> replacements = protectionGenerationProven
            ? await ReadReplacementsAsync(db, scope, orders, lifecycle, budget, token) : [];
        var decision = await ReadDecisionAsync(db, binding, budget, token);
        return new(binding, orders, lifecycle, positions, replacements, decision, protectionGenerationProven);
    }

    // An exact stop can read its own replacements after proving the generation opener without exposing the opening order.
    private static async Task<bool> ProveExactProtectionGenerationAsync(TradingFlowDbContext db, PaperChartBinding binding,
        Budget budget, CancellationToken token)
    {
        var order = binding.Intent;
        if (order.Kind != OrderIntentKind.ProtectiveStop || order.GenerationId is not > 0) return false;
        var scope = binding.Scope;
        var generation = order.GenerationId.Value;
        var openings = await budget.ReadAsync(db.PositionEvents.AsNoTracking()
            .Where(row => row.PositionEventId == generation && row.PositionGenerationEventId == generation &&
                row.AccountId == scope.AccountId && row.Symbol == scope.JournalSymbol &&
                row.PositionGenerationClientOrderId == row.ClientOrderId &&
                (row.Source == "broker_stream" || row.Source == "broker_rest") && row.BrokerOrderId != "" && row.ExecutionId != "" &&
                db.OrderIntents.Any(intent => intent.ClientOrderId == row.ClientOrderId && intent.RunId == row.RunId &&
                    intent.AccountId == row.AccountId && intent.Symbol == row.Symbol && intent.ConfigHash == row.ConfigHash &&
                    intent.CodeVersion == row.CodeVersion &&
                    (intent.Kind == OrderIntentKind.StrategyEntry || intent.Kind == OrderIntentKind.OperatorEntry) &&
                    db.ProductionRuns.Any(run => run.RunId == intent.RunId && run.ConfigHash == intent.ConfigHash &&
                        run.CodeVersion == intent.CodeVersion && run.StartedAtUtc != default &&
                        (run.Profile == "paper" || run.Profile == "paper_shadow" || run.Profile == "paper_experiment"))))
            .Select(row => new { row.ClientOrderId, row.QuantityAfter, row.FillQuantity, row.ConfigHash, row.CodeVersion }), token);
        var opening = openings.SingleOrDefault();
        return opening is { QuantityAfter: not 0m, FillQuantity: > 0m } &&
            PrivatePaperChartAccess.HasRecordedProvenance(opening.ConfigHash, opening.CodeVersion) &&
            PrivatePaperChartProjection.IsProtectiveGenerationLink(order, generation, opening.ClientOrderId);
    }

    // The client order ID prefix of the lifecycle index narrows each seek to an already proven durable owner.
    private static async Task<List<PaperChartLifecycle>> ReadLifecycleAsync(TradingFlowDbContext db,
        IReadOnlyList<PaperChartIntent> orders, Budget budget, CancellationToken token)
    {
        var byClient = orders.ToDictionary(order => order.ClientOrderId, StringComparer.Ordinal);
        var ids = byClient.Keys.ToArray();
        var rows = await budget.ReadAsync(db.OrderEvents.AsNoTracking().Where(row => ids.Contains(row.ClientOrderId))
            .Select(row => new PaperChartLifecycle(row.EventId, row.RunId, row.ClientOrderId, row.BrokerOrderId,
                row.ConfigHash, row.CodeVersion, row.NewState, row.Source, row.BrokerTimestampUtc, row.LocalTimestampUtc,
                row.PayloadJson.Length <= MaxPayload ? row.PayloadJson : "")), token);
        return rows.Where(row => byClient.TryGetValue(row.ClientOrderId, out var order) && row.RunId == order.RunId &&
            row.ConfigHash == order.ConfigHash && row.CodeVersion == order.CodeVersion).ToList();
    }

    // A selected order uses already journalled broker IDs and the exact account/broker order index.
    private static async Task<List<PositionEventRecord>> ReadExactFillsAsync(TradingFlowDbContext db, PaperChartScope scope,
        List<PaperChartLifecycle> lifecycle, Budget budget, CancellationToken token)
    {
        var ids = lifecycle.Where(BrokerLinked).Select(row => row.BrokerOrderId!).Distinct().ToArray();
        if (ids.Length == 0) return [];
        var rows = await budget.ReadAsync(PositionFields(db.PositionEvents.Where(row =>
            row.AccountId == scope.AccountId && ids.Contains(row.BrokerOrderId))), token);
        return rows.Where(row => row.RunId == scope.RunId && row.Symbol == scope.JournalSymbol &&
            row.ClientOrderId == scope.ClientOrderId).ToList();
    }

    // Verify owner and symbol after seeking the exact account/root pairs observed in the protective owner's lifecycle.
    private static async Task<List<PaperChartReplacement>> ReadReplacementsAsync(TradingFlowDbContext db,
        PaperChartScope scope, IReadOnlyList<PaperChartIntent> orders, List<PaperChartLifecycle> lifecycle, Budget budget,
        CancellationToken token)
    {
        var owners = orders.Where(order => order.Kind == OrderIntentKind.ProtectiveStop)
            .ToDictionary(order => order.ClientOrderId, StringComparer.Ordinal);
        var roots = lifecycle.Where(row => owners.ContainsKey(row.ClientOrderId) && BrokerLinked(row))
            .Select(row => row.BrokerOrderId!).Distinct().ToArray();
        if (roots.Length == 0) return [];
        var rows = await budget.ReadAsync(db.ProtectiveStopReplacements.AsNoTracking().Where(row =>
            row.AccountId == scope.AccountId && roots.Contains(row.RootBrokerOrderId))
            .Select(row => new
            {
                row.CommandId, row.Symbol, row.OwnerClientOrderId, row.RootBrokerOrderId, row.BrokerOrderId,
                row.VerifiedBrokerOrderId, row.ReplacementClientOrderId, row.StopPrice, row.VerifiedAtUtc, row.State, row.Reason,
                row.RunId, row.ConfigHash, row.CodeVersion
            }), token);
        return rows.Where(row => row.Symbol == scope.JournalSymbol && owners.TryGetValue(row.OwnerClientOrderId, out var owner) &&
                row.RunId == owner.RunId && row.ConfigHash == owner.ConfigHash && row.CodeVersion == owner.CodeVersion &&
                row.State == ProtectiveStopReplacementState.Verified && row.VerifiedAtUtc.HasValue &&
                !String.IsNullOrWhiteSpace(row.VerifiedBrokerOrderId))
            .Select(row => new PaperChartReplacement(row.CommandId, row.OwnerClientOrderId, row.RootBrokerOrderId,
                row.BrokerOrderId, row.VerifiedBrokerOrderId!, row.ReplacementClientOrderId, row.StopPrice,
                row.VerifiedAtUtc!.Value, row.Reason)).ToList();
    }

    // Read frozen intent-keyed admission and its exact candidate transition. A symbol-only audit is insufficient.
    private static async Task<PaperChartDecision?> ReadDecisionAsync(TradingFlowDbContext db, PaperChartBinding binding,
        Budget budget, CancellationToken token)
    {
        var order = binding.Intent;
        var admission = (await budget.ReadAsync(db.Set<OrderAdmissionTelemetryRecord>().AsNoTracking()
            .Where(row => row.IntentId == order.IntentId)
            .Select(row => new { row.DecisionAtUtc, row.DecisionPrice }), token)).SingleOrDefault();
        var transition = order.CandidateId is { } id && order.TriggeredVersion is { } version &&
            order.DecisionHash is { Length: 64 } hash && order.TriggeredEvidenceHash is { Length: 64 }
            ? (await budget.ReadAsync(db.CandidateTransitions.AsNoTracking().Where(row => row.CandidateId == id &&
                row.Sequence == version && row.SemanticDecisionSha256 == hash && row.NewState == StrategyCandidateState.Triggered &&
                row.RunId == order.RunId)
                .Select(row => new { row.OccurredAtUtc, row.ReasonCode, Evidence = row.EvidenceJson.Length <= MaxPayload ? row.EvidenceJson : "" }),
                token)).SingleOrDefault()
            : null;
        if (transition is not null && !MatchesTriggerEvidence(transition.Evidence, order.TriggeredEvidenceHash!)) transition = null;
        return admission is null && transition is null ? null :
            new(admission?.DecisionAtUtc ?? transition?.OccurredAtUtc, admission?.DecisionPrice, transition?.ReasonCode);
    }

    // The order reservation freezes the exact triggered payload hash alongside candidate version and decision identity.
    private static bool MatchesTriggerEvidence(string evidence, string expected) => evidence.Length > 0 &&
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence))).Equals(expected, StringComparison.OrdinalIgnoreCase);

    // Read only the columns needed for exact joins and the existing unit-fill reconstruction.
    internal static IQueryable<PositionEventRecord> PositionFields(IQueryable<PositionEventRecord> source) =>
        source.AsNoTracking().Select(row => new PositionEventRecord
        {
            PositionEventId = row.PositionEventId, PositionGenerationEventId = row.PositionGenerationEventId,
            PositionGenerationClientOrderId = row.PositionGenerationClientOrderId, AccountId = row.AccountId,
            Symbol = row.Symbol, RunId = row.RunId, ConfigHash = row.ConfigHash, CodeVersion = row.CodeVersion,
            ClientOrderId = row.ClientOrderId, BrokerOrderId = row.BrokerOrderId, Source = row.Source,
            QuantityAfter = row.QuantityAfter, FillQuantity = row.FillQuantity, FillPrice = row.FillPrice,
            ExecutionId = row.ExecutionId, PayloadJson = row.PayloadJson.Length <= MaxPayload ? row.PayloadJson : ""
        });

    // Read run identity and provenance without the runtime state or hosting fields.
    internal static IQueryable<ProductionRun> RunFields(IQueryable<ProductionRun> source) =>
        source.AsNoTracking().Select(row => new ProductionRun
        {
            RunId = row.RunId, Profile = row.Profile, ConfigHash = row.ConfigHash, CodeVersion = row.CodeVersion,
            StartedAtUtc = row.StartedAtUtc, UniverseSnapshotId = row.UniverseSnapshotId, DecisionRunId = row.DecisionRunId
        });

    // Only an authoritative broker lifecycle observation can supply a broker order ID for another index seek.
    internal static bool BrokerLinked(PaperChartLifecycle row) =>
        row.Source is "broker_rest" or "broker_stream" && !String.IsNullOrWhiteSpace(row.BrokerOrderId);

    private sealed class Budget(int remaining)
    {
        // The extra SQL row can only signal overflow. Its payload is never decoded or used to find links.
        public async Task<List<T>> ReadAsync<T>(IQueryable<T> query, CancellationToken token)
        {
            var rows = await query.Take(remaining + 1).ToListAsync(token);
            if (rows.Count > remaining) throw new CandidateBudgetExceeded();
            remaining -= rows.Count;
            return rows;
        }
    }

    private sealed class CandidateBudgetExceeded : Exception;
}
