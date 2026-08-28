using System.Text.Json;
using System.Text.Json.Serialization;
using TradingFlow.Domain.Market;

namespace TradingFlow.Data.Csv;

internal sealed record MarketDataCacheManifest(
    int ManifestSchemaVersion,
    int CsvSchemaVersion,
    string ProviderIdentity,
    string DataFeed,
    string AdjustmentPolicy,
    string Ticker,
    string Timeframe,
    DateTimeOffset RequestedStartUtc,
    DateTimeOffset RequestedEndUtc,
    DateTimeOffset CachedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string FreshnessPolicyVersion,
    string RestatementRevision,
    MarketDataCacheBarCoverage BarCoverage,
    string DataFileName,
    string ContentSha256)
{
    internal const int CurrentManifestSchemaVersion = 3;
    internal const int CurrentCsvSchemaVersion = 3;

    internal static MarketDataCacheManifest Create(
        MarketDataCacheSourceDescriptor descriptor,
        string ticker,
        string timeframe,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        IReadOnlyList<OhlcvBar> bars,
        string dataFileName,
        string contentSha256,
        MarketDataCacheFreshnessPolicy freshnessPolicy,
        DateTimeOffset cachedAtUtc) =>
        new(
            CurrentManifestSchemaVersion,
            CurrentCsvSchemaVersion,
            descriptor.ProviderIdentity,
            descriptor.DataFeed,
            descriptor.AdjustmentPolicy,
            ticker,
            timeframe,
            startUtc.ToUniversalTime(),
            endUtc.ToUniversalTime(),
            cachedAtUtc.ToUniversalTime(),
            cachedAtUtc.ToUniversalTime().Add(freshnessPolicy.MaximumAge),
            freshnessPolicy.Version,
            descriptor.RestatementRevision,
            new MarketDataCacheBarCoverage(
                bars.Count,
                bars.Count == 0 ? null : bars[0].Timestamp.ToUniversalTime(),
                bars.Count == 0 ? null : bars[^1].Timestamp.ToUniversalTime()),
            dataFileName,
            contentSha256);

    internal bool MatchesRequest(
        MarketDataCacheSourceDescriptor descriptor,
        string ticker,
        string timeframe,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        MarketDataCacheFreshnessPolicy freshnessPolicy,
        DateTimeOffset nowUtc) =>
        ManifestSchemaVersion == CurrentManifestSchemaVersion &&
        CsvSchemaVersion == CurrentCsvSchemaVersion &&
        String.Equals(ProviderIdentity, descriptor.ProviderIdentity, StringComparison.Ordinal) &&
        String.Equals(DataFeed, descriptor.DataFeed, StringComparison.Ordinal) &&
        String.Equals(AdjustmentPolicy, descriptor.AdjustmentPolicy, StringComparison.Ordinal) &&
        String.Equals(Ticker, ticker, StringComparison.Ordinal) &&
        String.Equals(Timeframe, timeframe, StringComparison.Ordinal) &&
        RequestedStartUtc.ToUniversalTime() == startUtc.ToUniversalTime() &&
        RequestedEndUtc.ToUniversalTime() == endUtc.ToUniversalTime() &&
        String.Equals(FreshnessPolicyVersion, freshnessPolicy.Version, StringComparison.Ordinal) &&
        String.Equals(RestatementRevision, descriptor.RestatementRevision, StringComparison.Ordinal) &&
        CachedAtUtc.ToUniversalTime() <= nowUtc.ToUniversalTime() &&
        ExpiresAtUtc.ToUniversalTime() > nowUtc.ToUniversalTime() &&
        ExpiresAtUtc.ToUniversalTime() - CachedAtUtc.ToUniversalTime() == freshnessPolicy.MaximumAge &&
        BarCoverage is not null &&
        BarCoverage.BarCount >= 0 &&
        IsCoverageShapeValid(BarCoverage) &&
        IsSafeDataFileName(DataFileName, timeframe) &&
        IsSha256(ContentSha256);

    internal static bool IsSafeDataFileName(string value) =>
        !String.IsNullOrWhiteSpace(value) &&
        String.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) &&
        value.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSafeDataFileName(string value, string timeframe)
    {
        if (!IsSafeDataFileName(value) || String.IsNullOrWhiteSpace(timeframe))
        {
            return false;
        }

        var prefix = $"bars_{timeframe}-";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) ||
            value.Length != prefix.Length + 32 + 4)
        {
            return false;
        }

        var transactionId = value[prefix.Length..^4];
        return
            transactionId.Length == 32 &&
            transactionId.All(Uri.IsHexDigit);
    }

    private static bool IsCoverageShapeValid(MarketDataCacheBarCoverage coverage) =>
        coverage.BarCount == 0
            ? coverage.FirstBarUtc is null && coverage.LastBarUtc is null
            : coverage.FirstBarUtc is not null &&
              coverage.LastBarUtc is not null &&
              coverage.FirstBarUtc <= coverage.LastBarUtc;

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

/// <summary>
/// Explicit cache lifetime for adjusted historical data. Even when a provider
/// cannot expose a corporate-action revision ID, finite expiry guarantees that
/// split/dividend restatements are eventually re-fetched rather than cached forever.
/// </summary>
public sealed class MarketDataCacheFreshnessPolicy
{
    public static MarketDataCacheFreshnessPolicy ProductionDefault { get; } = new(
        "adjusted_history_24h_v1",
        TimeSpan.FromHours(24),
        TimeProvider.System);

    public MarketDataCacheFreshnessPolicy(
        string version,
        TimeSpan maximumAge,
        TimeProvider? timeProvider = null)
    {
        Version = String.IsNullOrWhiteSpace(version)
            ? throw new ArgumentException("A cache freshness policy version is required.", nameof(version))
            : version.Trim().ToLowerInvariant();
        if (maximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge), "Cache maximum age must be positive.");
        }

        MaximumAge = maximumAge;
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Version { get; }

    public TimeSpan MaximumAge { get; }

    internal TimeProvider TimeProvider { get; }
}

internal sealed record MarketDataCacheBarCoverage(
    int BarCount,
    DateTimeOffset? FirstBarUtc,
    DateTimeOffset? LastBarUtc);

internal sealed record MarketSessionCalendarCacheManifest(
    int SchemaVersion,
    string ProviderIdentity,
    DateOnly CoverageStart,
    DateOnly CoverageEnd,
    DateTimeOffset RetrievedAtUtc,
    int DateCount,
    string DataFileName,
    string ContentSha256)
{
    internal const int CurrentSchemaVersion = 1;

    internal bool FullyCovers(
        string providerIdentity,
        DateOnly requestedStart,
        DateOnly requestedEnd,
        DateTimeOffset nowUtc) =>
        SchemaVersion == CurrentSchemaVersion &&
        String.Equals(ProviderIdentity, providerIdentity, StringComparison.Ordinal) &&
        CoverageStart <= requestedStart &&
        CoverageEnd >= requestedEnd &&
        RetrievedAtUtc != default &&
        RetrievedAtUtc.ToUniversalTime() <= nowUtc.ToUniversalTime() &&
        DateCount == CoverageEnd.DayNumber - CoverageStart.DayNumber + 1 &&
        IsSafeDataFileName(DataFileName) &&
        ContentSha256 is { Length: 64 } &&
        ContentSha256.All(Uri.IsHexDigit);

    internal static bool IsSafeDataFileName(string? value)
    {
        if (String.IsNullOrWhiteSpace(value) ||
            !String.Equals(Path.GetFileName(value), value, StringComparison.Ordinal))
        {
            return false;
        }

        const string prefix = "sessions-";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) ||
            value.Length != prefix.Length + 32 + 5 ||
            !value.EndsWith(".json", StringComparison.Ordinal))
        {
            return false;
        }

        return value[prefix.Length..^5].All(Uri.IsHexDigit);
    }
}

internal sealed record MarketSessionCalendarCacheDocument(
    int SchemaVersion,
    DateOnly CoverageStart,
    DateOnly CoverageEnd,
    IReadOnlyList<TradingFlow.Engine.Indicators.MarketSessionSchedule>? Sessions);

internal static class MarketDataCacheManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static string GetManifestPath(string dataPath) => $"{dataPath}.manifest.json";

    internal static async Task<MarketDataCacheManifest?> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<MarketDataCacheManifest>(
            stream,
            JsonOptions,
            cancellationToken);
    }

    internal static async Task WriteTempAsync(
        string path,
        MarketDataCacheManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }
}

internal static class MarketSessionCalendarCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    internal static async Task WriteAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }
}
