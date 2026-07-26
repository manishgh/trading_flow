using System.Collections.ObjectModel;

namespace TradingFlow.Domain.Research;

/// <summary>
/// Exact source-observation identity carried by every normalized evidence row.
/// </summary>
public sealed record EvidenceRowSourceAddress
{
    public EvidenceRowSourceAddress(string observationId, string observationSha256)
    {
        ObservationId = EvidenceValue.NormalizeRequired(observationId, nameof(observationId));
        ObservationSha256 = EvidenceValue.NormalizeSha256(observationSha256);
    }

    public string ObservationId { get; }

    public string ObservationSha256 { get; }
}

/// <summary>
/// Common contract for normalized rows persisted in immutable Parquet partitions.
/// </summary>
public interface INormalizedEvidenceRow
{
    int SchemaVersion { get; }

    string RunId { get; }

    string ConfigHash { get; }

    string CodeVersion { get; }

    string DataFeed { get; }

    DateTimeOffset SourceTimestampUtc { get; }

    IReadOnlyList<EvidenceRowSourceAddress> Sources { get; }
}

public enum NewsAvailabilityEvidence
{
    ProviderTimestampOnly = 1,
    ProviderUpdatedTimestampOnly = 2,
    ObservedReceiptTime = 3
}

public static class EvidenceFixedDecimal
{
    public const int PriceScale = 6;
    public const long PriceScaleFactor = 1_000_000L;

    public static long ToPriceUnits(decimal value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A price cannot be negative.");
        }

        return checked((long)Decimal.Round(
            value * PriceScaleFactor,
            0,
            MidpointRounding.ToEven));
    }

    public static decimal FromPriceUnits(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Price units cannot be negative.");
        }

        return value / (decimal)PriceScaleFactor;
    }
}

public sealed record MarketBarEvidenceRow : INormalizedEvidenceRow
{
    public MarketBarEvidenceRow(
        int schemaVersion,
        string runId,
        string configHash,
        string codeVersion,
        string dataFeed,
        string securityId,
        string symbol,
        DateTimeOffset barStartUtc,
        DateTimeOffset barEndUtc,
        string timeframe,
        long openPriceUnits,
        long highPriceUnits,
        long lowPriceUnits,
        long closePriceUnits,
        long? vwapPriceUnits,
        long volume,
        long? tradeCount,
        string adjustment,
        string currency,
        DateTimeOffset providerTimestampUtc,
        DateTimeOffset receivedAtUtc,
        IReadOnlyList<EvidenceRowSourceAddress> sources)
    {
        SchemaVersion = EvidenceRowContract.NormalizeSchemaVersion(schemaVersion);
        RunId = EvidenceRowContract.NormalizeRequired(runId, nameof(runId));
        ConfigHash = EvidenceRowContract.NormalizeSha256(configHash);
        CodeVersion = EvidenceRowContract.NormalizeRequired(codeVersion, nameof(codeVersion));
        DataFeed = EvidenceRowContract.NormalizeRequired(dataFeed, nameof(dataFeed)).ToLowerInvariant();
        SecurityId = EvidenceRowContract.NormalizeRequired(securityId, nameof(securityId));
        Symbol = EvidenceRowContract.NormalizeRequired(symbol, nameof(symbol)).ToUpperInvariant();
        EvidenceRowContract.EnsureUtc(barStartUtc, nameof(barStartUtc));
        EvidenceRowContract.EnsureUtc(barEndUtc, nameof(barEndUtc));
        if (barEndUtc <= barStartUtc)
        {
            throw new ArgumentException("Bar end must follow bar start.", nameof(barEndUtc));
        }

        BarStartUtc = barStartUtc;
        BarEndUtc = barEndUtc;
        Timeframe = EvidenceRowContract.NormalizeRequired(timeframe, nameof(timeframe)).ToLowerInvariant();
        EvidenceRowContract.EnsureValidOhlc(
            openPriceUnits,
            highPriceUnits,
            lowPriceUnits,
            closePriceUnits,
            vwapPriceUnits);
        OpenPriceUnits = openPriceUnits;
        HighPriceUnits = highPriceUnits;
        LowPriceUnits = lowPriceUnits;
        ClosePriceUnits = closePriceUnits;
        VwapPriceUnits = vwapPriceUnits;
        if (volume < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(volume));
        }

        if (tradeCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tradeCount));
        }

        Volume = volume;
        TradeCount = tradeCount;
        Adjustment = EvidenceRowContract.NormalizeRequired(adjustment, nameof(adjustment)).ToLowerInvariant();
        Currency = EvidenceRowContract.NormalizeRequired(currency, nameof(currency)).ToUpperInvariant();
        ProviderTimestampUtc = EvidenceRowContract.RequireUtc(providerTimestampUtc, nameof(providerTimestampUtc));
        ReceivedAtUtc = EvidenceRowContract.RequireUtc(receivedAtUtc, nameof(receivedAtUtc));
        if (ReceivedAtUtc < ProviderTimestampUtc)
        {
            throw new ArgumentException("Received timestamp cannot precede the provider timestamp.");
        }

        Sources = EvidenceRowContract.CopySources(sources);
    }

    public int SchemaVersion { get; }
    public string RunId { get; }
    public string ConfigHash { get; }
    public string CodeVersion { get; }
    public string DataFeed { get; }
    public string SecurityId { get; }
    public string Symbol { get; }
    public DateTimeOffset BarStartUtc { get; }
    public DateTimeOffset BarEndUtc { get; }
    public string Timeframe { get; }
    public long OpenPriceUnits { get; }
    public long HighPriceUnits { get; }
    public long LowPriceUnits { get; }
    public long ClosePriceUnits { get; }
    public long? VwapPriceUnits { get; }
    public long Volume { get; }
    public long? TradeCount { get; }
    public string Adjustment { get; }
    public string Currency { get; }
    public DateTimeOffset ProviderTimestampUtc { get; }
    public DateTimeOffset ReceivedAtUtc { get; }
    public IReadOnlyList<EvidenceRowSourceAddress> Sources { get; }
    public DateTimeOffset SourceTimestampUtc => ProviderTimestampUtc;
}

public sealed record NewsRevisionEvidenceRow : INormalizedEvidenceRow
{
    public NewsRevisionEvidenceRow(
        int schemaVersion,
        string runId,
        string configHash,
        string codeVersion,
        string dataFeed,
        NewsAvailabilityEvidence availabilityEvidence,
        string provider,
        string providerArticleId,
        string revisionId,
        string headline,
        string? summary,
        string articleUrl,
        IReadOnlyList<string> symbols,
        IReadOnlyList<string>? categories,
        DateTimeOffset publishedAtUtc,
        DateTimeOffset providerCreatedAtUtc,
        DateTimeOffset providerUpdatedAtUtc,
        DateTimeOffset receivedAtUtc,
        IReadOnlyList<EvidenceRowSourceAddress> sources)
    {
        SchemaVersion = EvidenceRowContract.NormalizeSchemaVersion(schemaVersion);
        RunId = EvidenceRowContract.NormalizeRequired(runId, nameof(runId));
        ConfigHash = EvidenceRowContract.NormalizeSha256(configHash);
        CodeVersion = EvidenceRowContract.NormalizeRequired(codeVersion, nameof(codeVersion));
        DataFeed = EvidenceRowContract.NormalizeRequired(dataFeed, nameof(dataFeed)).ToLowerInvariant();
        if (!Enum.IsDefined(availabilityEvidence))
        {
            throw new ArgumentOutOfRangeException(nameof(availabilityEvidence));
        }

        AvailabilityEvidence = availabilityEvidence;
        Provider = EvidenceRowContract.NormalizeRequired(provider, nameof(provider)).ToLowerInvariant();
        ProviderArticleId = EvidenceRowContract.NormalizeRequired(providerArticleId, nameof(providerArticleId));
        RevisionId = EvidenceRowContract.NormalizeRequired(revisionId, nameof(revisionId));
        Headline = EvidenceRowContract.NormalizeRequired(headline, nameof(headline));
        Summary = String.IsNullOrWhiteSpace(summary) ? null : summary.Trim();
        ArticleUrl = EvidenceRowContract.NormalizeAbsoluteHttpUrl(articleUrl, nameof(articleUrl));
        Symbols = EvidenceRowContract.CopySymbols(symbols);
        Categories = EvidenceRowContract.CopyNormalizedStrings(categories);
        PublishedAtUtc = EvidenceRowContract.RequireUtc(publishedAtUtc, nameof(publishedAtUtc));
        ProviderCreatedAtUtc = EvidenceRowContract.RequireUtc(providerCreatedAtUtc, nameof(providerCreatedAtUtc));
        ProviderUpdatedAtUtc = EvidenceRowContract.RequireUtc(providerUpdatedAtUtc, nameof(providerUpdatedAtUtc));
        ReceivedAtUtc = EvidenceRowContract.RequireUtc(receivedAtUtc, nameof(receivedAtUtc));
        if (ProviderUpdatedAtUtc < ProviderCreatedAtUtc)
        {
            throw new ArgumentException("Provider updated timestamp cannot precede created timestamp.");
        }

        if (ReceivedAtUtc < ProviderCreatedAtUtc)
        {
            throw new ArgumentException("Received timestamp cannot precede provider creation.");
        }

        Sources = EvidenceRowContract.CopySources(sources);
    }

    public int SchemaVersion { get; }
    public string RunId { get; }
    public string ConfigHash { get; }
    public string CodeVersion { get; }
    public string DataFeed { get; }
    public NewsAvailabilityEvidence AvailabilityEvidence { get; }
    public string Provider { get; }
    public string ProviderArticleId { get; }
    public string RevisionId { get; }
    public string Headline { get; }
    public string? Summary { get; }
    public string ArticleUrl { get; }
    public IReadOnlyList<string> Symbols { get; }
    public IReadOnlyList<string> Categories { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public DateTimeOffset ProviderCreatedAtUtc { get; }
    public DateTimeOffset ProviderUpdatedAtUtc { get; }
    public DateTimeOffset ReceivedAtUtc { get; }
    public IReadOnlyList<EvidenceRowSourceAddress> Sources { get; }
    public DateTimeOffset SourceTimestampUtc => AvailabilityTimestampUtc;
    public DateTimeOffset AvailabilityTimestampUtc =>
        AvailabilityEvidence switch
        {
            NewsAvailabilityEvidence.ProviderTimestampOnly => PublishedAtUtc,
            NewsAvailabilityEvidence.ProviderUpdatedTimestampOnly => ProviderUpdatedAtUtc,
            NewsAvailabilityEvidence.ObservedReceiptTime => ReceivedAtUtc,
            _ => throw new InvalidOperationException(
                $"Unsupported news availability evidence '{AvailabilityEvidence}'.")
        };
}

public sealed record SecurityMasterEvidenceRow : INormalizedEvidenceRow
{
    public SecurityMasterEvidenceRow(
        int schemaVersion,
        string runId,
        string configHash,
        string codeVersion,
        string dataFeed,
        string provider,
        string securityId,
        string issuerId,
        string symbol,
        string exchange,
        string assetClass,
        string currency,
        string status,
        DateTimeOffset validFromUtc,
        DateTimeOffset? validToUtc,
        DateTimeOffset providerUpdatedAtUtc,
        DateTimeOffset receivedAtUtc,
        IReadOnlyList<EvidenceRowSourceAddress> sources)
    {
        SchemaVersion = EvidenceRowContract.NormalizeSchemaVersion(schemaVersion);
        RunId = EvidenceRowContract.NormalizeRequired(runId, nameof(runId));
        ConfigHash = EvidenceRowContract.NormalizeSha256(configHash);
        CodeVersion = EvidenceRowContract.NormalizeRequired(codeVersion, nameof(codeVersion));
        DataFeed = EvidenceRowContract.NormalizeRequired(dataFeed, nameof(dataFeed)).ToLowerInvariant();
        Provider = EvidenceRowContract.NormalizeRequired(provider, nameof(provider)).ToLowerInvariant();
        SecurityId = EvidenceRowContract.NormalizeRequired(securityId, nameof(securityId));
        IssuerId = EvidenceRowContract.NormalizeRequired(issuerId, nameof(issuerId));
        Symbol = EvidenceRowContract.NormalizeRequired(symbol, nameof(symbol)).ToUpperInvariant();
        Exchange = EvidenceRowContract.NormalizeRequired(exchange, nameof(exchange)).ToUpperInvariant();
        AssetClass = EvidenceRowContract.NormalizeRequired(assetClass, nameof(assetClass)).ToLowerInvariant();
        Currency = EvidenceRowContract.NormalizeRequired(currency, nameof(currency)).ToUpperInvariant();
        Status = EvidenceRowContract.NormalizeRequired(status, nameof(status)).ToLowerInvariant();
        ValidFromUtc = EvidenceRowContract.RequireUtc(validFromUtc, nameof(validFromUtc));
        ValidToUtc = EvidenceRowContract.OptionalUtc(validToUtc, nameof(validToUtc));
        if (ValidToUtc <= ValidFromUtc)
        {
            throw new ArgumentException("Security validity end must follow its start.", nameof(validToUtc));
        }

        ProviderUpdatedAtUtc = EvidenceRowContract.RequireUtc(providerUpdatedAtUtc, nameof(providerUpdatedAtUtc));
        ReceivedAtUtc = EvidenceRowContract.RequireUtc(receivedAtUtc, nameof(receivedAtUtc));
        if (ReceivedAtUtc < ProviderUpdatedAtUtc)
        {
            throw new ArgumentException("Received timestamp cannot precede provider update.");
        }

        Sources = EvidenceRowContract.CopySources(sources);
    }

    public int SchemaVersion { get; }
    public string RunId { get; }
    public string ConfigHash { get; }
    public string CodeVersion { get; }
    public string DataFeed { get; }
    public string Provider { get; }
    public string SecurityId { get; }
    public string IssuerId { get; }
    public string Symbol { get; }
    public string Exchange { get; }
    public string AssetClass { get; }
    public string Currency { get; }
    public string Status { get; }
    public DateTimeOffset ValidFromUtc { get; }
    public DateTimeOffset? ValidToUtc { get; }
    public DateTimeOffset ProviderUpdatedAtUtc { get; }
    public DateTimeOffset ReceivedAtUtc { get; }
    public IReadOnlyList<EvidenceRowSourceAddress> Sources { get; }
    public DateTimeOffset SourceTimestampUtc => ProviderUpdatedAtUtc;
}

public sealed record UniverseMembershipEvidenceRow : INormalizedEvidenceRow
{
    public UniverseMembershipEvidenceRow(
        int schemaVersion,
        string runId,
        string configHash,
        string codeVersion,
        string dataFeed,
        string provider,
        string universeId,
        string snapshotId,
        string providerQuery,
        string snapshotContentSha256,
        DateOnly effectiveSessionDate,
        string securityId,
        string issuerId,
        string symbol,
        DateTimeOffset asOfUtc,
        bool included,
        int? rank,
        string selectionReason,
        DateTimeOffset providerTimestampUtc,
        DateTimeOffset receivedAtUtc,
        IReadOnlyList<EvidenceRowSourceAddress> sources)
    {
        SchemaVersion = EvidenceRowContract.NormalizeSchemaVersion(schemaVersion);
        RunId = EvidenceRowContract.NormalizeRequired(runId, nameof(runId));
        ConfigHash = EvidenceRowContract.NormalizeSha256(configHash);
        CodeVersion = EvidenceRowContract.NormalizeRequired(codeVersion, nameof(codeVersion));
        DataFeed = EvidenceRowContract.NormalizeRequired(dataFeed, nameof(dataFeed)).ToLowerInvariant();
        Provider = EvidenceRowContract.NormalizeRequired(provider, nameof(provider)).ToLowerInvariant();
        UniverseId = EvidenceRowContract.NormalizeRequired(universeId, nameof(universeId));
        SnapshotId = EvidenceRowContract.NormalizeRequired(snapshotId, nameof(snapshotId));
        ArgumentException.ThrowIfNullOrWhiteSpace(providerQuery);
        ProviderQuery = providerQuery;
        SnapshotContentSha256 = EvidenceRowContract.NormalizeSha256(snapshotContentSha256);
        EffectiveSessionDate = effectiveSessionDate;
        SecurityId = EvidenceRowContract.NormalizeRequired(securityId, nameof(securityId));
        IssuerId = EvidenceRowContract.NormalizeRequired(issuerId, nameof(issuerId));
        Symbol = EvidenceRowContract.NormalizeRequired(symbol, nameof(symbol)).ToUpperInvariant();
        AsOfUtc = EvidenceRowContract.RequireUtc(asOfUtc, nameof(asOfUtc));
        Included = included;
        if (included && rank is null)
        {
            throw new ArgumentException(
                "Included universe membership requires a positive rank.",
                nameof(rank));
        }

        if (!included && rank is not null)
        {
            throw new ArgumentException(
                "Excluded universe membership cannot carry a rank.",
                nameof(rank));
        }

        if (rank <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rank));
        }

        Rank = rank;
        SelectionReason = EvidenceRowContract.NormalizeRequired(selectionReason, nameof(selectionReason));
        ProviderTimestampUtc = EvidenceRowContract.RequireUtc(providerTimestampUtc, nameof(providerTimestampUtc));
        ReceivedAtUtc = EvidenceRowContract.RequireUtc(receivedAtUtc, nameof(receivedAtUtc));
        if (ProviderTimestampUtc > AsOfUtc || ReceivedAtUtc < ProviderTimestampUtc)
        {
            throw new ArgumentException(
                "Universe timestamps must satisfy provider <= as-of and provider <= received.");
        }

        Sources = EvidenceRowContract.CopySources(sources);
        if (!Sources.Any(source =>
                source.ObservationSha256.Equals(
                    SnapshotContentSha256,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Snapshot content hash must identify one exact row-lineage source.",
                nameof(snapshotContentSha256));
        }
    }

    public int SchemaVersion { get; }
    public string RunId { get; }
    public string ConfigHash { get; }
    public string CodeVersion { get; }
    public string DataFeed { get; }
    public string Provider { get; }
    public string UniverseId { get; }
    public string SnapshotId { get; }
    public string ProviderQuery { get; }
    public string SnapshotContentSha256 { get; }
    public DateOnly EffectiveSessionDate { get; }
    public string SecurityId { get; }
    public string IssuerId { get; }
    public string Symbol { get; }
    public DateTimeOffset AsOfUtc { get; }
    public bool Included { get; }
    public int? Rank { get; }
    public string SelectionReason { get; }
    public DateTimeOffset ProviderTimestampUtc { get; }
    public DateTimeOffset ReceivedAtUtc { get; }
    public IReadOnlyList<EvidenceRowSourceAddress> Sources { get; }
    public DateTimeOffset SourceTimestampUtc => ProviderTimestampUtc;

    public Backtesting.UniverseProviderSnapshot ToProviderSnapshot() =>
        new(
            Provider,
            ReceivedAtUtc,
            EffectiveSessionDate,
            ProviderQuery,
            SnapshotContentSha256);
}

internal static class EvidenceRowContract
{
    public static int NormalizeSchemaVersion(int value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return value;
    }

    public static string NormalizeRequired(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    public static string NormalizeSha256(string value) =>
        EvidenceValue.NormalizeSha256(value);

    public static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        EnsureUtc(value, parameterName);
        return value;
    }

    public static DateTimeOffset? OptionalUtc(DateTimeOffset? value, string parameterName)
    {
        if (value.HasValue)
        {
            EnsureUtc(value.Value, parameterName);
        }

        return value;
    }

    public static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be a non-default UTC value.", parameterName);
        }
    }

    public static IReadOnlyList<EvidenceRowSourceAddress> CopySources(
        IReadOnlyList<EvidenceRowSourceAddress> sources)
    {
        var normalized = (sources ?? throw new ArgumentNullException(nameof(sources)))
            .Select(source => source ?? throw new ArgumentException("Sources cannot contain null.", nameof(sources)))
            .OrderBy(source => source.ObservationId, StringComparer.Ordinal)
            .ThenBy(source => source.ObservationSha256, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("At least one source observation is required.", nameof(sources));
        }

        if (normalized.Select(source => source.ObservationId).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new ArgumentException("Source observation identifiers must be unique.", nameof(sources));
        }

        return new ReadOnlyCollection<EvidenceRowSourceAddress>(normalized);
    }

    public static IReadOnlyList<string> CopySymbols(IReadOnlyList<string> values)
    {
        var symbols = (values ?? throw new ArgumentNullException(nameof(values)))
            .Select(value => NormalizeRequired(value, nameof(values)).ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (symbols.Length == 0)
        {
            throw new ArgumentException("At least one symbol is required.", nameof(values));
        }

        return new ReadOnlyCollection<string>(symbols);
    }

    public static IReadOnlyList<string> CopyNormalizedStrings(IReadOnlyList<string>? values) =>
        new ReadOnlyCollection<string>(
            (values ?? Array.Empty<string>())
            .Select(value => NormalizeRequired(value, nameof(values)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray());

    public static string NormalizeAbsoluteHttpUrl(string value, string parameterName)
    {
        var normalized = NormalizeRequired(value, parameterName);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Article URL must be an absolute HTTP(S) URL.", parameterName);
        }

        return uri.AbsoluteUri;
    }

    public static void EnsureValidOhlc(
        long open,
        long high,
        long low,
        long close,
        long? vwap)
    {
        if (open < 0 || high < 0 || low < 0 || close < 0 || vwap < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(open), "Prices cannot be negative.");
        }

        if (high < low ||
            high < open ||
            high < close ||
            low > open ||
            low > close)
        {
            throw new ArgumentException("OHLC values violate high/low bounds.");
        }
    }
}
