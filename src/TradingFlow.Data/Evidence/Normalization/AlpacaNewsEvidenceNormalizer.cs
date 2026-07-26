using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence.Normalization;

public sealed class AlpacaNewsEvidenceNormalizer
{
    public IReadOnlyList<NewsRevisionEvidenceRow> Normalize(
        EvidenceSourceObservation observation,
        ReadOnlyMemory<byte> rawBytes,
        AlpacaEvidenceNormalizationContext context,
        NewsAvailabilityEvidence historicalAvailabilityEvidence)
    {
        try
        {
            EvidenceNormalizationGuard.ValidateObservation(
                observation,
                rawBytes,
                context,
                "/v1beta1/news",
                requireRange: true);

            if (historicalAvailabilityEvidence == NewsAvailabilityEvidence.ObservedReceiptTime)
            {
                EvidenceNormalizationGuard.Fail(
                    EvidenceNormalizationFailureCode.HistoricalAvailabilityViolation,
                    observation,
                    "Historical REST news cannot use download receipt as historical availability.");
            }

            if (historicalAvailabilityEvidence is not (
                    NewsAvailabilityEvidence.ProviderTimestampOnly or
                    NewsAvailabilityEvidence.ProviderUpdatedTimestampOnly))
            {
                EvidenceNormalizationGuard.Fail(
                    EvidenceNormalizationFailureCode.InvalidField,
                    observation,
                    $"Unsupported news availability evidence '{historicalAvailabilityEvidence}'.");
            }

            using var document = JsonDocument.Parse(rawBytes);
            var root = EvidenceNormalizationGuard.RequireRootObject(document, observation);
            EvidenceNormalizationGuard.EnsureNoDuplicateJsonProperties(root, observation);
            EvidenceNormalizationGuard.ValidatePagination(root, observation);
            var news = EvidenceNormalizationGuard.RequireProperty(
                root,
                "news",
                JsonValueKind.Array,
                observation);

            var rows = new List<NewsRevisionEvidenceRow>();
            var logicalKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var article in news.EnumerateArray())
            {
                if (article.ValueKind != JsonValueKind.Object)
                {
                    EvidenceNormalizationGuard.Fail(
                        EvidenceNormalizationFailureCode.InvalidField,
                        observation,
                        "Every news revision must be a JSON object.");
                }

                var articleId = RequireArticleId(article, observation);
                var created = EvidenceNormalizationGuard.RequireUtcTimestamp(
                    article,
                    "created_at",
                    observation);
                var updated = EvidenceNormalizationGuard.RequireUtcTimestamp(
                    article,
                    "updated_at",
                    observation);
                if (updated < created)
                {
                    EvidenceNormalizationGuard.Fail(
                        EvidenceNormalizationFailureCode.InvalidField,
                        observation,
                        $"News article '{articleId}' was updated before it was created.");
                }

                if (updated > observation.ReceivedAtUtc)
                {
                    EvidenceNormalizationGuard.Fail(
                        EvidenceNormalizationFailureCode.InvalidField,
                        observation,
                        $"News article '{articleId}' has a provider update after the archived receipt.");
                }

                var availabilityTimestamp =
                    historicalAvailabilityEvidence ==
                    NewsAvailabilityEvidence.ProviderUpdatedTimestampOnly
                        ? updated
                        : created;
                EvidenceNormalizationGuard.EnsureTimestampInRange(
                    availabilityTimestamp,
                    observation,
                    historicalAvailabilityEvidence ==
                    NewsAvailabilityEvidence.ProviderUpdatedTimestampOnly
                        ? "news.updated_at"
                        : "news.created_at");

                var symbols = RequireSymbols(article, observation, context);
                if (observation.RequestedSymbols.Count > 0 &&
                    !symbols.Intersect(observation.RequestedSymbols, StringComparer.Ordinal).Any())
                {
                    EvidenceNormalizationGuard.Fail(
                        EvidenceNormalizationFailureCode.UnexpectedSymbol,
                        observation,
                        $"News article '{articleId}' does not contain a requested symbol.");
                }

                var logicalKey = $"{articleId}|{updated:O}";
                if (!logicalKeys.Add(logicalKey))
                {
                    EvidenceNormalizationGuard.Fail(
                        EvidenceNormalizationFailureCode.DuplicateLogicalKey,
                        observation,
                        $"Duplicate news revision '{logicalKey}'.");
                }

                var revisionHash = Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(article.GetRawText())))
                    .ToLowerInvariant();
                rows.Add(new NewsRevisionEvidenceRow(
                    context.SchemaVersion,
                    observation.RunId,
                    observation.ConfigHash,
                    observation.CodeVersion,
                    context.ExpectedDataFeed,
                    historicalAvailabilityEvidence,
                    "alpaca",
                    articleId,
                    $"{articleId}-{revisionHash[..24]}",
                    EvidenceNormalizationGuard.RequireString(article, "headline", observation),
                    OptionalString(article, "summary", observation),
                    EvidenceNormalizationGuard.RequireString(article, "url", observation),
                    symbols,
                    OptionalStringArray(article, "categories", observation),
                    created,
                    created,
                    updated,
                    observation.ReceivedAtUtc,
                    [EvidenceNormalizationGuard.Source(observation)]));
            }

            return rows
                .OrderBy(row => row.ProviderArticleId, StringComparer.Ordinal)
                .ThenBy(row => row.ProviderUpdatedAtUtc)
                .ToArray();
        }
        catch (Exception exception)
        {
            throw EvidenceNormalizationGuard.Wrap(observation, exception);
        }
    }

    private static string RequireArticleId(
        JsonElement article,
        EvidenceSourceObservation observation)
    {
        if (!article.TryGetProperty("id", out var id))
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.MissingRequiredField,
                observation,
                "Required property 'id' is missing.");
        }

        var value = id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null
        };
        if (String.IsNullOrWhiteSpace(value))
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.InvalidField,
                observation,
                "News article 'id' must be a non-empty string or number.");
        }

        return value.Trim();
    }

    private static IReadOnlyList<string> RequireSymbols(
        JsonElement article,
        EvidenceSourceObservation observation,
        AlpacaEvidenceNormalizationContext context)
    {
        var symbolsElement = EvidenceNormalizationGuard.RequireProperty(
            article,
            "symbols",
            JsonValueKind.Array,
            observation);
        var rawSymbolCount = 0;
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in symbolsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String ||
                String.IsNullOrWhiteSpace(element.GetString()))
            {
                EvidenceNormalizationGuard.Fail(
                    EvidenceNormalizationFailureCode.InvalidField,
                    observation,
                    "News symbols must be non-empty strings.");
            }

            rawSymbolCount++;
            var symbol = element.GetString()!.Trim().ToUpperInvariant();
            if (observation.RequestedSymbols.Count > 0 &&
                !observation.RequestedSymbols.Contains(symbol, StringComparer.Ordinal))
            {
                // Alpaca can tag one article with related securities outside the query.
                // The raw receipt preserves those tags; normalized research rows contain
                // only identities explicitly requested by this evidence collection.
                continue;
            }

            var identity = EvidenceNormalizationGuard.RequireExpectedSymbol(
                symbol,
                observation,
                context,
                requireRequestedSymbol: false);
            symbols.Add(identity.Symbol);
        }

        if (rawSymbolCount == 0)
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.InvalidField,
                observation,
                "A normalized news revision must identify at least one symbol.");
        }

        return symbols.OrderBy(symbol => symbol, StringComparer.Ordinal).ToArray();
    }

    private static string? OptionalString(
        JsonElement parent,
        string name,
        EvidenceSourceObservation observation)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.InvalidField,
                observation,
                $"Property '{name}' must be a string or null.");
        }

        return String.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim();
    }

    private static IReadOnlyList<string> OptionalStringArray(
        JsonElement parent,
        string name,
        EvidenceSourceObservation observation)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.InvalidField,
                observation,
                $"Property '{name}' must be an array or null.");
        }

        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                String.IsNullOrWhiteSpace(item.GetString()))
            {
                EvidenceNormalizationGuard.Fail(
                    EvidenceNormalizationFailureCode.InvalidField,
                    observation,
                    $"Property '{name}' must contain non-empty strings.");
            }

            values.Add(item.GetString()!.Trim());
        }

        return values.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
