namespace TradingFlow.Domain.Backtesting;

/// <summary>
/// Immutable evidence of the exact provider response used to build a universe for a session.
/// The content hash is SHA-256 of the archived provider payload, not a hash of parsed symbols.
/// </summary>
public sealed record UniverseProviderSnapshot
{
    public UniverseProviderSnapshot(
        string providerName,
        DateTimeOffset observedAtUtc,
        DateOnly effectiveSessionDate,
        string query,
        string contentSha256)
    {
        ProviderName = NormalizeProviderName(providerName);
        EnsureUtc(observedAtUtc, nameof(observedAtUtc));
        ObservedAtUtc = observedAtUtc;
        EffectiveSessionDate = effectiveSessionDate;
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        Query = query;
        ContentSha256 = NormalizeSha256(contentSha256);
        Reference = new UniverseSnapshotReference(ProviderName, EffectiveSessionDate, ContentSha256);
    }

    public string ProviderName { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public DateOnly EffectiveSessionDate { get; }

    public string Query { get; }

    public string ContentSha256 { get; }

    public UniverseSnapshotReference Reference { get; }

    private static string NormalizeProviderName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant();
    }

    internal static string NormalizeSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("The content hash must be a 64-character SHA-256 hexadecimal value.", nameof(value));
        }

        return normalized;
    }

    internal static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must use an explicit UTC offset.", parameterName);
        }
    }
}

/// <summary>
/// Deterministic reference used by membership decisions to identify their source snapshot.
/// </summary>
public sealed record UniverseSnapshotReference
{
    public UniverseSnapshotReference(string providerName, DateOnly effectiveSessionDate, string contentSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ProviderName = providerName.Trim().ToLowerInvariant();
        EffectiveSessionDate = effectiveSessionDate;
        ContentSha256 = UniverseProviderSnapshot.NormalizeSha256(contentSha256);
    }

    public string ProviderName { get; }

    public DateOnly EffectiveSessionDate { get; }

    public string ContentSha256 { get; }
}
