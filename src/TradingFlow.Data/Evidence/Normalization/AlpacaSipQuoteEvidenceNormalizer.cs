using System.Text.Json;
using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence.Normalization;

public sealed class AlpacaSipQuoteEvidenceNormalizer
{
    public IReadOnlyList<SipQuoteEvidenceRow> Normalize(
        EvidenceSourceObservation observation,
        ReadOnlyMemory<byte> rawBytes,
        AlpacaEvidenceNormalizationContext context)
    {
        try
        {
            EvidenceNormalizationGuard.ValidateObservation(
                observation,
                rawBytes,
                context,
                "/v2/stocks/quotes",
                requireRange: true);
            if (!context.ExpectedDataFeed.Equals("sip", StringComparison.Ordinal))
            {
                EvidenceNormalizationGuard.Fail(
                    EvidenceNormalizationFailureCode.UnexpectedFeed,
                    observation,
                    "Executable quote normalization requires the consolidated SIP feed.");
            }

            using var document = JsonDocument.Parse(rawBytes);
            var root = EvidenceNormalizationGuard.RequireRootObject(document, observation);
            EvidenceNormalizationGuard.EnsureNoDuplicateJsonProperties(root, observation);
            EvidenceNormalizationGuard.ValidatePagination(root, observation);
            var quotesBySymbol = EvidenceNormalizationGuard.RequireProperty(
                root,
                "quotes",
                JsonValueKind.Object,
                observation);

            var rows = new List<SipQuoteEvidenceRow>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var symbolProperty in quotesBySymbol.EnumerateObject())
            {
                var identity = EvidenceNormalizationGuard.RequireExpectedSymbol(
                    symbolProperty.Name,
                    observation,
                    context,
                    requireRequestedSymbol: true);
                if (symbolProperty.Value.ValueKind != JsonValueKind.Array)
                {
                    EvidenceNormalizationGuard.Fail(
                        EvidenceNormalizationFailureCode.InvalidField,
                        observation,
                        $"Quotes for '{identity.Symbol}' must be an array.");
                }

                foreach (var quote in symbolProperty.Value.EnumerateArray())
                {
                    if (quote.ValueKind != JsonValueKind.Object)
                    {
                        EvidenceNormalizationGuard.Fail(
                            EvidenceNormalizationFailureCode.InvalidField,
                            observation,
                            $"A quote for '{identity.Symbol}' is not an object.");
                    }

                    var timestamp = EvidenceNormalizationGuard.RequireUtcTimestamp(
                        quote,
                        "t",
                        observation);
                    EvidenceNormalizationGuard.EnsureTimestampInRange(
                        timestamp,
                        observation,
                        "quote.t");
                    var key = $"{identity.Symbol}|{timestamp:O}";
                    if (!keys.Add(key))
                    {
                        EvidenceNormalizationGuard.Fail(
                            EvidenceNormalizationFailureCode.DuplicateLogicalKey,
                            observation,
                            $"Duplicate SIP quote '{key}'.");
                    }

                    var bidPrice = OptionalDecimal(quote, "bp", observation);
                    var askPrice = OptionalDecimal(quote, "ap", observation);
                    var bidSize = OptionalInt64(quote, "bs", observation);
                    var askSize = OptionalInt64(quote, "as", observation);
                    ValidateSide("bid", bidPrice, bidSize, quote, "bx", observation);
                    ValidateSide("ask", askPrice, askSize, quote, "ax", observation);

                    rows.Add(new SipQuoteEvidenceRow(
                        context.SchemaVersion,
                        observation.RunId,
                        observation.ConfigHash,
                        observation.CodeVersion,
                        identity.SecurityId,
                        identity.Symbol,
                        timestamp,
                        bidPrice is null ? null : EvidenceFixedDecimal.ToPriceUnits(bidPrice.Value),
                        askPrice is null ? null : EvidenceFixedDecimal.ToPriceUnits(askPrice.Value),
                        bidSize,
                        askSize,
                        OptionalString(quote, "bx", observation) ?? String.Empty,
                        OptionalString(quote, "ax", observation) ?? String.Empty,
                        context.ExpectedDataFeed,
                        context.ExpectedCurrency,
                        OptionalStringArray(quote, "c", observation),
                        timestamp,
                        observation.ReceivedAtUtc,
                        [EvidenceNormalizationGuard.Source(observation)]));
                }
            }

            return rows
                .OrderBy(row => row.Symbol, StringComparer.Ordinal)
                .ThenBy(row => row.QuoteTimestampUtc)
                .ToArray();
        }
        catch (Exception exception)
        {
            throw EvidenceNormalizationGuard.Wrap(observation, exception);
        }
    }

    private static void ValidateSide(
        string side,
        decimal? price,
        long? size,
        JsonElement quote,
        string exchangeField,
        EvidenceSourceObservation observation)
    {
        if (price.HasValue != size.HasValue)
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.InvalidField,
                observation,
                $"The {side} price and size must both be present or both be absent.");
        }

        var exchange = OptionalString(quote, exchangeField, observation);
        if (price.HasValue && String.IsNullOrWhiteSpace(exchange))
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.MissingRequiredField,
                observation,
                $"A present {side} side requires exchange '{exchangeField}'.");
        }

        if (!price.HasValue && !String.IsNullOrWhiteSpace(exchange))
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.InvalidField,
                observation,
                $"Exchange '{exchangeField}' cannot be present without a {side} side.");
        }
    }

    private static decimal? OptionalDecimal(
        JsonElement parent,
        string name,
        EvidenceSourceObservation observation)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        decimal parsed = default;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out parsed))
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.InvalidField,
                observation,
                $"Property '{name}' must be a decimal number or null.");
        }

        return parsed;
    }

    private static long? OptionalInt64(
        JsonElement parent,
        string name,
        EvidenceSourceObservation observation)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        long parsed = default;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out parsed))
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.InvalidField,
                observation,
                $"Property '{name}' must be a 64-bit integer or null.");
        }

        return parsed;
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

        var conditions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var condition in value.EnumerateArray())
        {
            if (condition.ValueKind != JsonValueKind.String ||
                String.IsNullOrWhiteSpace(condition.GetString()))
            {
                EvidenceNormalizationGuard.Fail(
                    EvidenceNormalizationFailureCode.InvalidField,
                    observation,
                    $"Property '{name}' must contain non-empty strings.");
            }

            conditions.Add(condition.GetString()!.Trim());
        }

        return conditions.OrderBy(item => item, StringComparer.Ordinal).ToArray();
    }
}
