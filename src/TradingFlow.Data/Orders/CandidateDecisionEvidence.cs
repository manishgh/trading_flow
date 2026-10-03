using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Orders;

/// <summary>Copies exact known-at decision evidence into the existing immutable catalyst journal.</summary>
internal static class CandidateDecisionEvidence
{
    internal static async Task PersistCatalystAsync(TradingFlowDbContext context, CandidateRecord candidate,
        CandidateTransitionAppendRequest decision, string semanticHash, CancellationToken token)
    {
        if (decision.Source != "strategy_decision_kernel") return;
        using var document = JsonDocument.Parse(decision.EvidenceJson);
        if (!document.RootElement.TryGetProperty("attachedCatalyst", out var catalyst)) return;
        if (catalyst.ValueKind != JsonValueKind.Object) { candidate.CatalystResultId = null; return; }
        var known = catalyst.GetProperty("decisionKnownAtUtc").GetDateTimeOffset();
        var published = catalyst.GetProperty("providerPublishedAtUtc").GetDateTimeOffset();
        if (known > decision.OccurredAtUtc || published > decision.OccurredAtUtc)
            throw new InvalidOperationException("Future catalyst evidence cannot be persisted for an earlier decision.");
        var item = catalyst.GetProperty("event");
        if (item.GetProperty("ticker").GetString() != candidate.Symbol)
            throw new InvalidOperationException("Catalyst evidence belongs to a different symbol.");
        var article = catalyst.GetProperty("providerArticleId").GetString()!;
        var revision = catalyst.GetProperty("revisionId").GetString()!;
        var raw = JsonSerializer.Serialize(new {
            provider = catalyst.GetProperty("provider").GetString(), providerArticleId = article, revisionId = revision,
            providerPublishedAtUtc = published, firstReceivedAtUtc = catalyst.GetProperty("firstReceivedAtUtc"),
            decisionKnownAtUtc = known, classificationVersion = catalyst.GetProperty("classificationVersion"),
            availabilityEvidence = catalyst.GetProperty("availabilityEvidence"),
            headline = item.GetProperty("headline").GetString(), url = item.GetProperty("url").GetString(),
            sentimentScore = item.GetProperty("sentimentScore").GetDecimal()
        });
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.CandidateId + ":" + semanticHash + ":" + raw)).AsSpan(0, 16));
        if (await context.CatalystResults.FindAsync([id], token) is null)
        {
            var score = item.GetProperty("sentimentScore").GetDecimal();
            context.CatalystResults.Add(new CatalystResultRecord {
                CatalystResultId = id, CandidateId = candidate.CandidateId, ProviderArticleId = article,
                Symbol = candidate.Symbol, Category = item.GetProperty("type").GetString()!,
                Direction = score > 0 ? "positive" : score < 0 ? "negative" : "neutral",
                CompositeScore = score, RawComponentsJson = raw,
                Stage1ProvenanceJson = catalyst.GetRawText(), EvaluatedAtUtc = decision.OccurredAtUtc,
                RunId = candidate.RunId, ConfigHash = candidate.ConfigHash, CodeVersion = candidate.CodeVersion,
                SchemaVersion = candidate.SchemaVersion
            });
        }
        candidate.CatalystResultId = id;
    }
}
