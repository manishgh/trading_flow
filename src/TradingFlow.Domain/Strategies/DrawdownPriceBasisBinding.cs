using System.Collections.Immutable;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;

namespace TradingFlow.Domain.Strategies;

/// <summary>
/// Binds a frozen security/revision assertion to the exact ordered, completed daily price prefix.
/// EvidenceSha256 identifies the independently supplied frozen source; ContentSha256 identifies this typed prefix.
/// A matching checksum verifies content consistency, not the truth or authenticity of a provider assertion.
/// </summary>
public sealed record DrawdownPricePrefixBinding(string SecurityId, string AdjustmentVersion,
    DateTimeOffset ThroughSourceTimestamp, DateTimeOffset KnownAtUtc, int BarCount,
    string ContentSha256, string EvidenceSha256);

/// <summary>The independently supplied physical source JSON referenced by a binding's EvidenceSha256.</summary>
public sealed record DrawdownPriceSourceBundle(string SecurityId, string AdjustmentVersion,
    DateTimeOffset KnownAtUtc, ImmutableArray<OhlcvBar> Bars)
{
    public const string ContractVersion = "drawdown-price-source-bundle.v1";
}

public static class DrawdownPriceBasisBinding
{
    /// <summary>Hash the exact ordered prefix. Callers must exclude rows unavailable at their decision time.</summary>
    public static string ComputeSha256(string securityId, string adjustmentVersion, IReadOnlyList<OhlcvBar> bars)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(securityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(adjustmentVersion);
        ArgumentNullException.ThrowIfNull(bars);
        return EvidenceCanonicalJson.ComputeSha256(new { SecurityId = securityId,
            AdjustmentVersion = adjustmentVersion, Bars = bars });
    }

    /// <summary>Returns an explicit unknown reason, or null when the independently bound prefix matches.</summary>
    public static string? Validate(DrawdownPricePrefixBinding? binding, string securityId,
        string adjustmentVersion, IReadOnlyList<OhlcvBar> bars, DateTimeOffset asOfUtc)
    {
        if (binding is null) return "price_prefix_binding_unavailable";
        if (!Utc(binding.KnownAtUtc) || binding.KnownAtUtc > asOfUtc)
            return "price_prefix_binding_not_available_asof";
        if (!Hash(binding.EvidenceSha256) || !Hash(binding.ContentSha256) ||
            String.IsNullOrWhiteSpace(binding.SecurityId) || String.IsNullOrWhiteSpace(binding.AdjustmentVersion))
            return "price_prefix_binding_provenance_unavailable";
        if (binding.SecurityId != securityId || binding.AdjustmentVersion != adjustmentVersion)
            return "price_prefix_binding_identity_or_revision_mismatch";
        if (bars.Count == 0 || !Utc(asOfUtc) || !Utc(binding.ThroughSourceTimestamp) ||
            binding.BarCount != bars.Count || binding.ThroughSourceTimestamp != bars[^1].Timestamp ||
            bars.Any(bar => !Utc(bar.Timestamp) || bar.Timeframe != "1d" || bar.Timestamp.AddDays(1) > asOfUtc ||
                binding.KnownAtUtc < bar.Timestamp.AddDays(1) ||
                bar.KnownAtUtc is { } known && (!Utc(known) || known > asOfUtc || known > binding.KnownAtUtc)))
            return "price_prefix_binding_interval_mismatch";
        for (var index = 1; index < bars.Count; index++)
            if (bars[index].Timestamp <= bars[index - 1].Timestamp)
                return "price_prefix_binding_rows_unordered_or_duplicate";
        if (!ComputeSha256(securityId, adjustmentVersion, bars).Equals(binding.ContentSha256,
                StringComparison.OrdinalIgnoreCase)) return "price_prefix_binding_content_mismatch";
        return null;
    }

    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
