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
        if (catalyst is null)
        {
            return null;
        }
        var news = await context.NewsItems.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == catalyst.ProviderArticleId, cancellationToken);
        var raw = ReadObject(catalyst.RawComponentsJson);
        var provider = news?.Provider ?? ReadString(raw, "provider") ?? "unknown";
        var published = news?.Timestamp ?? ReadDate(raw, "providerPublishedAtUtc") ?? catalyst.EvaluatedAtUtc;
        var received = news?.IngestedAt ?? ReadDate(raw, "firstReceivedAtUtc") ?? catalyst.EvaluatedAtUtc;
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
            news?.Headline,
            news?.Url
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
            news?.Headline,
            news?.Url,
            Sha256(evidence));
    }

    public async Task<CandidateExecutionAuditEvidence?> GetExecutionAsync(
        CandidateRecord candidate,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var intents = await context.OrderIntents.AsNoTracking()
            .Where(item => item.CandidateId == candidate.CandidateId)
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
        var position = await context.PositionEvents.AsNoTracking()
            .Where(item => item.RunId == candidate.RunId &&
                           item.Symbol == candidate.Symbol &&
                           item.StrategyId == candidate.SelectedStrategy)
            .OrderByDescending(item => item.PositionEventId)
            .FirstOrDefaultAsync(cancellationToken);
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
            position?.QuantityAfter > 0m);
    }

    public async Task<IReadOnlySet<Guid>> GetOpenPositionCandidateIdsAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await context.Candidates.AsNoTracking()
            .Where(candidate => candidate.RunId == runId)
            .Select(candidate => new { candidate.CandidateId, candidate.Symbol, candidate.SelectedStrategy })
            .ToArrayAsync(cancellationToken);
        var positions = await context.PositionEvents.AsNoTracking()
            .Where(position => position.RunId == runId)
            .OrderBy(position => position.PositionEventId)
            .ToArrayAsync(cancellationToken);
        var open = positions
            .GroupBy(position => new { position.Symbol, position.StrategyId })
            .Select(group => group.Last())
            .Where(position => position.QuantityAfter > 0m)
            .SelectMany(position => candidates
                .Where(candidate => candidate.Symbol == position.Symbol && candidate.SelectedStrategy == position.StrategyId)
                .Select(candidate => candidate.CandidateId))
            .ToHashSet();
        return open;
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
