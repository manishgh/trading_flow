using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence.Normalization;

internal static class AlpacaIdentityNormalizationGuard
{
    public static void ValidateObservation(
        EvidenceSourceObservation observation,
        ReadOnlyMemory<byte> rawBytes,
        string endpointSuffix,
        string expectedDataFeed,
        bool requireRange)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!observation.Provider.Equals("alpaca", StringComparison.Ordinal))
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.UnexpectedProvider,
                observation,
                $"Expected provider 'alpaca', received '{observation.Provider}'.");
        }

        if (!observation.Endpoint.EndsWith(endpointSuffix, StringComparison.OrdinalIgnoreCase))
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.UnexpectedEndpoint,
                observation,
                $"Observation endpoint '{observation.Endpoint}' is not '{endpointSuffix}'.");
        }

        if (observation.TransportKind != EvidenceTransportKind.Http ||
            observation.TransportStatusCode is < 200 or >= 300)
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.UnexpectedTransportStatus,
                observation,
                "Identity normalization requires a successful archived HTTP response.");
        }

        if (!observation.DataFeed.Equals(expectedDataFeed, StringComparison.Ordinal))
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.UnexpectedFeed,
                observation,
                $"Expected feed '{expectedDataFeed}', received '{observation.DataFeed}'.");
        }

        if (!observation.Adjustment.Equals("raw", StringComparison.Ordinal))
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.UnexpectedAdjustment,
                observation,
                "Identity evidence requires raw provider observations.");
        }

        if (!observation.Currency.Equals("USD", StringComparison.Ordinal))
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.UnexpectedCurrency,
                observation,
                $"Expected currency 'USD', received '{observation.Currency}'.");
        }

        var receiptDate = DateOnly.FromDateTime(observation.ReceivedAtUtc.UtcDateTime);
        if (observation.AsOfDate is null || observation.AsOfDate > receiptDate)
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.UnexpectedAsOfDate,
                observation,
                "Identity evidence requires a non-future as-of date relative to UTC receipt.");
        }

        if (requireRange != observation.RequestedStartUtc.HasValue ||
            requireRange != observation.RequestedEndUtc.HasValue)
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.ObservationMismatch,
                observation,
                requireRange
                    ? "Corporate-action evidence requires its requested UTC range."
                    : "Current asset-master evidence cannot carry a historical range.");
        }

        if (observation.Artifact.Content.ByteLength != rawBytes.Length)
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.ArtifactMismatch,
                observation,
                "Archived bytes do not match the observation byte length.");
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(rawBytes.Span)).ToLowerInvariant();
        if (!actualHash.Equals(observation.Artifact.Content.Sha256, StringComparison.Ordinal))
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.ArtifactMismatch,
                observation,
                "Archived bytes do not match the observation SHA-256.");
        }
    }

    public static void EnsureOnlyProperties(
        JsonElement value,
        IReadOnlySet<string> allowed,
        EvidenceSourceObservation observation,
        string path)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                EvidenceNormalizationGuard.Fail(
                    EvidenceNormalizationFailureCode.InvalidField,
                    observation,
                    $"Unsupported provider property '{path}.{property.Name}'.");
            }
        }
    }

    public static bool ReadRequiredBoolean(
        JsonElement parent,
        string name,
        EvidenceSourceObservation observation)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.InvalidField,
                observation,
                $"Property '{name}' must be a boolean.");
        }

        return value.GetBoolean();
    }

    public static string? OptionalString(
        JsonElement parent,
        string name,
        EvidenceSourceObservation observation)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || String.IsNullOrWhiteSpace(value.GetString()))
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.InvalidField,
                observation,
                $"Property '{name}' must be a non-empty string or null.");
        }

        return value.GetString()!.Trim();
    }

    public static decimal? OptionalDecimal(
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
}
