using TradingFlow.Domain.Orders;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Orders;

/// <summary>
/// Projects catalyst, order and position truth without inferring exposure from
/// candidate consumption. Missing provider fields remain null/unknown rather
/// than being manufactured for the API.
/// </summary>
public sealed class SqliteCandidateAuditEvidenceRepository(
    IDbContextFactory<TradingFlowDbContext> contextFactory) : ICandidateAuditEvidenceRepository
{
    public async Task<CandidateCatalystAuditEvidence?> GetCatalystAsync(
        CandidateRecord candidate,
        CancellationToken cancellationToken = default)
    {
        if (candidate.CatalystResultId is not { } catalystId)
        {
            return null;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var catalyst = await context.CatalystResults.AsNoTracking()
            .SingleOrDefaultAsync(item => item.CatalystResultId == catalystId, cancellationToken);
        if (catalyst is null || catalyst.RunId != candidate.RunId || catalyst.Symbol != candidate.Symbol || (catalyst.CandidateId is { } owner && owner != candidate.CandidateId))
        {
            return null;
        }
        var news = await context.NewsItems.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == catalyst.ProviderArticleId, cancellationToken);
        // A later revision cannot become evidence for an earlier decision.
        if (news is not null && (news.Timestamp > catalyst.EvaluatedAtUtc || news.IngestedAt > catalyst.EvaluatedAtUtc))
            news = null;
        var raw = ReadObject(catalyst.RawComponentsJson);
        var provider = ReadString(raw, "provider") ?? news?.Provider ?? "unknown";
        var published = ReadDate(raw, "providerPublishedAtUtc") ?? news?.Timestamp;
        var received = ReadDate(raw, "firstReceivedAtUtc");
        var canonical = ReadString(raw, "revisionId") is not null;
        var headline = canonical ? ReadString(raw, "headline") : news?.Headline;
        var url = canonical ? ReadString(raw, "url") : news?.Url;
        var evidence = JsonSerializer.Serialize(new
        {
            catalyst.CatalystResultId,
            catalyst.ProviderArticleId,
            provider,
            published,
            received,
            catalyst.Category,
            catalyst.Direction,
            catalyst.CompositeScore,
            Headline = headline,
            Url = url
        });
        return new CandidateCatalystAuditEvidence(
            catalyst.CatalystResultId,
            provider,
            catalyst.ProviderArticleId,
            published,
            received,
            catalyst.Category,
            catalyst.Direction,
            catalyst.CompositeScore,
            headline,
            url,
            Sha256(evidence + (ReadString(raw, "revisionId") is null ? "" : catalyst.RawComponentsJson)));
    }

    public async Task<CandidateExecutionAuditEvidence?> GetExecutionAsync(
        CandidateRecord candidate,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var intents = await context.OrderIntents.AsNoTracking()
            .Where(item => item.CandidateId == candidate.CandidateId && item.RunId == candidate.RunId && item.Kind == OrderIntentKind.StrategyEntry)
            .ToArrayAsync(cancellationToken);
        var intent = intents
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.IntentId)
            .FirstOrDefault();
        if (intent is null)
        {
            return null;
        }
        var latest = await context.OrderEvents.AsNoTracking()
            .Where(item => item.ClientOrderId == intent.ClientOrderId)
            .OrderByDescending(item => item.EventId)
            .FirstOrDefaultAsync(cancellationToken);
        var openCandidates = await ReadOpenCandidatesAsync(context, candidate.RunId, cancellationToken);
        var rejection = latest is null ? null : ReadString(ReadObject(latest.PayloadJson), "rejectionCode");
        return new CandidateExecutionAuditEvidence(
            intent.IntentId,
            intent.ClientOrderId,
            latest?.BrokerOrderId,
            latest?.NewState ?? "pending",
            intent.RequestedQuantity,
            latest?.FilledQuantity,
            latest?.FillPrice,
            latest?.LocalTimestampUtc ?? intent.CreatedAtUtc,
            rejection,
            openCandidates.Contains(candidate.CandidateId));
    }

    public async Task<IReadOnlySet<Guid>> GetOpenPositionCandidateIdsAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await ReadOpenCandidatesAsync(context, runId, cancellationToken);
    }

    private static async Task<IReadOnlySet<Guid>> ReadOpenCandidatesAsync(
        TradingFlowDbContext context, Guid runId, CancellationToken cancellationToken)
    {
        // Account/symbol latest state is authoritative, including closes from another
        // run. The generation's entry order identifies exactly which candidate owns it.
        var ids = await (
            from position in context.PositionEvents.AsNoTracking()
            join intent in context.OrderIntents.AsNoTracking()
                on position.PositionGenerationClientOrderId equals intent.ClientOrderId
            where position.QuantityAfter != 0m && intent.RunId == runId && intent.CandidateId != null &&
                  intent.Kind == OrderIntentKind.StrategyEntry && intent.AccountId == position.AccountId && intent.Symbol == position.Symbol &&
                  !context.PositionEvents.Any(later => later.AccountId == position.AccountId && later.Symbol == position.Symbol &&
                      later.PositionEventId > position.PositionEventId)
            select intent.CandidateId!.Value).Distinct().ToArrayAsync(cancellationToken);
        return ids.ToHashSet();
    }
    private static JsonElement? ReadObject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement? element, string property) =>
        element is { } value && value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()
            : null;

    private static DateTimeOffset? ReadDate(JsonElement? element, string property) =>
        DateTimeOffset.TryParse(ReadString(element, property), out var value) ? value.ToUniversalTime() : null;

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
