using System.Diagnostics;
using System.Text.Json;
using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence.Normalization;

public sealed class AlpacaMarketBarEvidenceNormalizer
{
    public IReadOnlyList<MarketBarEvidenceRow> Normalize(
        EvidenceSourceObservation observation,
        ReadOnlyMemory<byte> rawBytes,
        AlpacaEvidenceNormalizationContext context,
        string expectedAdjustment,
        string providerTimeframe)
    {
        try
        {
            EvidenceNormalizationGuard.ValidateObservation(
                observation,
                rawBytes,
                context,
                "/v2/stocks/bars",
                requireRange: true);

            var adjustment = NormalizeAdjustment(expectedAdjustment, observation);
            if (!observation.Adjustment.Equals(adjustment, StringComparison.Ordinal))
            {
                EvidenceNormalizationGuard.Fail(
                    EvidenceNormalizationFailureCode.UnexpectedAdjustment,
                    observation,
                    $"Expected adjustment '{adjustment}', received '{observation.Adjustment}'.");
            }

            var timeframe = ParseTimeframe(providerTimeframe, observation);
            using var document = JsonDocument.Parse(rawBytes);
            var root = EvidenceNormalizationGuard.RequireRootObject(document, observation);
            EvidenceNormalizationGuard.EnsureNoDuplicateJsonProperties(root, observation);
            EvidenceNormalizationGuard.ValidatePagination(root, observation);
            var barsBySymbol = EvidenceNormalizationGuard.RequireProperty(
                root,
                "bars",
                JsonValueKind.Object,
                observation);

            var rows = new List<MarketBarEvidenceRow>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var symbolProperty in barsBySymbol.EnumerateObject())
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
                        $"Bars for '{identity.Symbol}' must be an array.");
                }

                foreach (var bar in symbolProperty.Value.EnumerateArray())
                {
                    if (bar.ValueKind != JsonValueKind.Object)
                    {
                        EvidenceNormalizationGuard.Fail(
                            EvidenceNormalizationFailureCode.InvalidField,
                            observation,
                            $"A bar for '{identity.Symbol}' is not an object.");
                    }

                    var start = EvidenceNormalizationGuard.RequireUtcTimestamp(bar, "t", observation);
                    EvidenceNormalizationGuard.EnsureTimestampInRange(start, observation, "bar.t");
                    var key = $"{identity.Symbol}|{timeframe.Normalized}|{start:O}";
                    if (!keys.Add(key))
                    {
                        EvidenceNormalizationGuard.Fail(
                            EvidenceNormalizationFailureCode.DuplicateLogicalKey,
                            observation,
                            $"Duplicate market bar '{key}'.");
                    }

                    var vwap = OptionalDecimal(bar, "vw", observation);
                    rows.Add(new MarketBarEvidenceRow(
                        context.SchemaVersion,
                        observation.RunId,
                        observation.ConfigHash,
                        observation.CodeVersion,
                        context.ExpectedDataFeed,
                        identity.SecurityId,
                        identity.Symbol,
                        start,
                        start.Add(timeframe.Duration),
                        timeframe.Normalized,
                        EvidenceFixedDecimal.ToPriceUnits(
                            EvidenceNormalizationGuard.RequireDecimal(bar, "o", observation)),
                        EvidenceFixedDecimal.ToPriceUnits(
                            EvidenceNormalizationGuard.RequireDecimal(bar, "h", observation)),
                        EvidenceFixedDecimal.ToPriceUnits(
                            EvidenceNormalizationGuard.RequireDecimal(bar, "l", observation)),
                        EvidenceFixedDecimal.ToPriceUnits(
                            EvidenceNormalizationGuard.RequireDecimal(bar, "c", observation)),
                        vwap is null ? null : EvidenceFixedDecimal.ToPriceUnits(vwap.Value),
                        EvidenceNormalizationGuard.RequireInt64(bar, "v", observation),
                        OptionalInt64(bar, "n", observation),
                        adjustment,
                        context.ExpectedCurrency,
                        start,
                        observation.ReceivedAtUtc,
                        [EvidenceNormalizationGuard.Source(observation)]));
                }
            }

            return rows
                .OrderBy(row => row.Symbol, StringComparer.Ordinal)
                .ThenBy(row => row.BarStartUtc)
                .ToArray();
        }
        catch (Exception exception)
        {
            throw EvidenceNormalizationGuard.Wrap(observation, exception);
        }
    }

    private static string NormalizeAdjustment(
        string expectedAdjustment,
        EvidenceSourceObservation observation)
    {
        var adjustment = expectedAdjustment?.Trim().ToLowerInvariant();
        if (adjustment is not ("raw" or "all"))
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.UnexpectedAdjustment,
                observation,
                "Market-bar normalization supports explicit 'raw' or 'all' adjustment only.");
        }

        return adjustment!;
    }

    private static (string Normalized, TimeSpan Duration) ParseTimeframe(
        string providerTimeframe,
        EvidenceSourceObservation observation)
    {
        var value = providerTimeframe?.Trim();
        if (String.IsNullOrWhiteSpace(value))
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.InvalidField,
                observation,
                "A provider timeframe is required.");
        }

        if (value!.EndsWith("Min", StringComparison.Ordinal) &&
            Int32.TryParse(value[..^3], out var minutes) &&
            minutes > 0)
        {
            return ($"{minutes}m", TimeSpan.FromMinutes(minutes));
        }

        if (value.EndsWith("Hour", StringComparison.Ordinal) &&
            Int32.TryParse(value[..^4], out var hours) &&
            hours > 0)
        {
            return ($"{hours}h", TimeSpan.FromHours(hours));
        }

        if (value.EndsWith("Day", StringComparison.Ordinal) &&
            Int32.TryParse(value[..^3], out var days) &&
            days > 0)
        {
            return ($"{days}d", TimeSpan.FromDays(days));
        }

        EvidenceNormalizationGuard.Fail(
            EvidenceNormalizationFailureCode.InvalidField,
            observation,
            $"Unsupported Alpaca timeframe '{providerTimeframe}'.");
        throw new UnreachableException();
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
}
