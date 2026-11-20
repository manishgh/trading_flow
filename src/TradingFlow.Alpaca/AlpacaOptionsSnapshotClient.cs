using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Alpaca;

/// <summary>
/// Read-only Alpaca paid OPRA options chain snapshot client implementing <see cref="IOptionsSentimentSource"/>.
/// Caches successful snapshots per symbol for <see cref="CacheLifetime"/> (sweeping expired entries past 512 symbols and
/// never caching failures), uses fixed 64-stripe symbol locks, runs targeted 20-45 DTE and tight ATM 30-60 DTE follow-ups
/// when dense chains (e.g. SPY/QQQ) truncate after <see cref="MaxPages"/>, and archives raw HTTP payloads before parsing
/// when an <see cref="IRawArchiveWriter"/> is supplied.
/// </summary>
public sealed class AlpacaOptionsSnapshotClient : IOptionsSentimentSource, IDisposable
{
    public const string HttpClientName = "AlpacaOptionsSnapshots";
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    public const int MaxPages = 3;

    private const int GateStripeCount = 64;
    private const int MaxCacheEntriesBeforeSweep = 512;
    private const int SkewFollowUpMaxPages = 2;
    private const int TermFollowUpMaxPages = 1;
    private const int SkewWindowMinDte = 20;
    private const int SkewWindowMaxDte = 45;
    private const int TermFarWindowMinDte = 30;
    private const int TermFarWindowMaxDte = 60;
    private const decimal TightAtmMinMoneyness = 0.95m;
    private const decimal TightAtmMaxMoneyness = 1.05m;

    private sealed record CachedSnapshot(OptionsSentimentSnapshot Snapshot, DateTimeOffset CachedAtUtc);

    private readonly Func<HttpClient> createClient;
    private readonly AlpacaOptions options;
    private readonly TimeProvider clock;
    private readonly AlpacaRawResponseArchiver? rawArchiver;
    private readonly ILogger<AlpacaOptionsSnapshotClient>? logger;
    private readonly Uri marketDataBaseUri;
    private readonly ConcurrentDictionary<string, CachedSnapshot> cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim[] gateStripes = Enumerable.Range(0, GateStripeCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

    /// <summary>Creates a client backed by <paramref name="createClient"/> and the profile's Alpaca market-data REST endpoint.</summary>
    public AlpacaOptionsSnapshotClient(
        Func<HttpClient> createClient,
        AlpacaOptions options,
        TimeProvider? timeProvider = null,
        IRawArchiveWriter? rawArchiveWriter = null,
        ILogger<AlpacaOptionsSnapshotClient>? logger = null)
    {
        this.createClient = createClient ?? throw new ArgumentNullException(nameof(createClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        clock = timeProvider ?? TimeProvider.System;
        rawArchiver = rawArchiveWriter is null ? null : new AlpacaRawResponseArchiver(rawArchiveWriter);
        this.logger = logger;
        marketDataBaseUri = AlpacaEndpointResolver.Resolve(options.Profile).MarketDataRest;
    }

    /// <summary>
    /// Fetches (or serves from the 5-minute in-memory cache) the OPRA options sentiment summary for <paramref name="symbol"/>.
    /// </summary>
    public async Task<OptionsSentimentSnapshot> GetSnapshotAsync(
        string symbol,
        decimal? spotPriceHint = null,
        decimal? adr20Pct = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        var now = clock.GetUtcNow().ToUniversalTime();

        if (string.IsNullOrWhiteSpace(options.KeyId) || string.IsNullOrWhiteSpace(options.SecretKey))
        {
            return OptionsSentimentSnapshot.Unavailable(
                normalizedSymbol,
                now,
                "Alpaca market-data credentials are not configured; live OPRA options sentiment is unavailable.");
        }

        if (TryGetFreshCached(normalizedSymbol, now, adr20Pct, out var cached))
        {
            return cached;
        }

        var gate = GateFor(normalizedSymbol);
        await gate.WaitAsync(cancellationToken);
        try
        {
            now = clock.GetUtcNow().ToUniversalTime();
            if (TryGetFreshCached(normalizedSymbol, now, adr20Pct, out cached))
            {
                return cached;
            }

            return await FetchAndCacheAsync(normalizedSymbol, spotPriceHint, adr20Pct, now, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Releases all fixed striped concurrency locks owned by this client.</summary>
    public void Dispose()
    {
        foreach (var stripe in gateStripes)
        {
            stripe.Dispose();
        }
    }

    // Maps a normalized equity ticker deterministically to one of the 64 fixed lock stripes for O(1) memory.
    private SemaphoreSlim GateFor(string normalizedSymbol) =>
        gateStripes[(uint)StringComparer.Ordinal.GetHashCode(normalizedSymbol) % GateStripeCount];

    // Returns a cached snapshot when within CacheLifetime, recomputing implied/ADR ratio if adr20Pct differs.
    private bool TryGetFreshCached(
        string normalizedSymbol,
        DateTimeOffset now,
        decimal? adr20Pct,
        out OptionsSentimentSnapshot snapshot)
    {
        if (cache.TryGetValue(normalizedSymbol, out var entry) && now - entry.CachedAtUtc < CacheLifetime)
        {
            snapshot = OptionsSentimentCalculator.WithAdr20Pct(entry.Snapshot, adr20Pct);
            return true;
        }

        snapshot = null!;
        return false;
    }

    // Evicts expired cache entries once cache size exceeds MaxCacheEntriesBeforeSweep to keep long-running hosts bounded.
    private void SweepExpiredCacheEntries(DateTimeOffset now)
    {
        if (cache.Count <= MaxCacheEntriesBeforeSweep)
        {
            return;
        }

        foreach (var pair in cache)
        {
            if (now - pair.Value.CachedAtUtc >= CacheLifetime)
            {
                cache.TryRemove(new KeyValuePair<string, CachedSnapshot>(pair.Key, pair.Value));
            }
        }
    }

    private readonly record struct WindowFetchOutcome(
        bool Succeeded,
        int? FailedStatusCode,
        string? RemainingPageToken,
        int PagesFetched);

    // Executes the stock-spot fallback (when needed), primary 2-60 DTE fetch, and targeted dense-chain follow-ups.
    private async Task<OptionsSentimentSnapshot> FetchAndCacheAsync(
        string normalizedSymbol,
        decimal? spotPriceHint,
        decimal? adr20Pct,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var http = createClient();
        try
        {
            var resolvedSpot = spotPriceHint is > 0m
                ? spotPriceHint
                : await TryFetchStockSpotAsync(http, normalizedSymbol, cancellationToken);

            var observedDateNy = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(now, VixRegime.NewYorkTimeZone).DateTime);
            var quotesBySymbol = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);

            var primaryFrom = observedDateNy.AddDays(OptionsSentimentCalculator.MinCalendarDte);
            var primaryTo = observedDateNy.AddDays(OptionsSentimentCalculator.MaxCalendarDte);
            var primaryOutcome = await FetchOptionsWindowPagesAsync(
                http,
                normalizedSymbol,
                primaryFrom,
                primaryTo,
                resolvedSpot,
                OptionsSentimentCalculator.MinStrikeMoneyness,
                OptionsSentimentCalculator.MaxStrikeMoneyness,
                MaxPages,
                windowTag: "primary",
                quotesBySymbol,
                cancellationToken);
            if (!primaryOutcome.Succeeded)
            {
                return OptionsSentimentSnapshot.Unavailable(
                    normalizedSymbol,
                    now,
                    $"Alpaca OPRA returned HTTP {primaryOutcome.FailedStatusCode} for {normalizedSymbol}; options context unavailable.");
            }

            // Dense chains (SPY, QQQ, mega-caps with daily expiries) can fill 3,000 contracts inside 2-12 DTE.
            // Run targeted horizon follow-ups only when the primary fetch actually truncated with a remaining page token.
            if (!string.IsNullOrWhiteSpace(primaryOutcome.RemainingPageToken))
            {
                var ranFollowUp = false;
                var skewStart = observedDateNy.AddDays(SkewWindowMinDte);
                var skewEnd = observedDateNy.AddDays(SkewWindowMaxDte);
                if (!HasContractInExpiryWindow(quotesBySymbol.Values, skewStart, skewEnd))
                {
                    ranFollowUp = true;
                    var maxSeen = MaxSeenExpiry(quotesBySymbol.Values);
                    var targetedSkewFrom = maxSeen is { } seen && seen.AddDays(1) > skewStart
                        ? seen.AddDays(1)
                        : skewStart;
                    if (targetedSkewFrom <= skewEnd)
                    {
                        var skewOutcome = await FetchOptionsWindowPagesAsync(
                            http,
                            normalizedSymbol,
                            targetedSkewFrom,
                            skewEnd,
                            resolvedSpot,
                            OptionsSentimentCalculator.MinStrikeMoneyness,
                            OptionsSentimentCalculator.MaxStrikeMoneyness,
                            SkewFollowUpMaxPages,
                            windowTag: "skew20-45",
                            quotesBySymbol,
                            cancellationToken);
                        if (!skewOutcome.Succeeded)
                        {
                            return OptionsSentimentSnapshot.Unavailable(
                                normalizedSymbol,
                                now,
                                $"Alpaca OPRA returned HTTP {skewOutcome.FailedStatusCode} for {normalizedSymbol} (20-45 DTE skew window); options context unavailable.");
                        }

                        if (!string.IsNullOrWhiteSpace(skewOutcome.RemainingPageToken))
                        {
                            logger?.LogWarning(
                                "Alpaca OPRA 20-45 DTE skew follow-up truncated for {Symbol} after {Pages} page(s) with next_page_token still present.",
                                normalizedSymbol, skewOutcome.PagesFetched);
                        }
                    }
                }

                var termFarStart = observedDateNy.AddDays(TermFarWindowMinDte);
                var termFarEnd = observedDateNy.AddDays(TermFarWindowMaxDte);
                if (!HasContractInExpiryWindow(quotesBySymbol.Values, termFarStart, termFarEnd))
                {
                    ranFollowUp = true;
                    var termOutcome = await FetchOptionsWindowPagesAsync(
                        http,
                        normalizedSymbol,
                        termFarStart,
                        termFarEnd,
                        resolvedSpot,
                        TightAtmMinMoneyness,
                        TightAtmMaxMoneyness,
                        TermFollowUpMaxPages,
                        windowTag: "term30-60",
                        quotesBySymbol,
                        cancellationToken);
                    if (!termOutcome.Succeeded)
                    {
                        return OptionsSentimentSnapshot.Unavailable(
                            normalizedSymbol,
                            now,
                            $"Alpaca OPRA returned HTTP {termOutcome.FailedStatusCode} for {normalizedSymbol} (30-60 DTE term window); options context unavailable.");
                    }

                    if (!string.IsNullOrWhiteSpace(termOutcome.RemainingPageToken))
                    {
                        logger?.LogWarning(
                            "Alpaca OPRA 30-60 DTE tight ATM follow-up truncated for {Symbol} after {Pages} page(s) with next_page_token still present.",
                            normalizedSymbol, termOutcome.PagesFetched);
                    }
                }

                if (!ranFollowUp)
                {
                    logger?.LogWarning(
                        "Alpaca OPRA primary snapshot fetch truncated for {Symbol} after {Pages} page(s) with next_page_token still present.",
                        normalizedSymbol, primaryOutcome.PagesFetched);
                }
            }

            var quotes = quotesBySymbol.Values.ToArray();
            var snapshot = OptionsSentimentCalculator.Calculate(normalizedSymbol, now, resolvedSpot, quotes, adr20Pct);
            if (!snapshot.RegimeLabel.Equals(OptionsSentimentRegime.Unavailable, StringComparison.OrdinalIgnoreCase))
            {
                SweepExpiredCacheEntries(now);
                cache[normalizedSymbol] = new CachedSnapshot(snapshot, now);
            }

            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "Alpaca OPRA options snapshot read or parse failed for {Symbol} ({ErrorType}): {ErrorMessage}.",
                normalizedSymbol, ex.GetType().Name, ex.Message);
            return OptionsSentimentSnapshot.Unavailable(
                normalizedSymbol,
                now,
                $"OPRA options sentiment request failed for {normalizedSymbol} ({ex.GetType().Name}).");
        }
    }

    // Fetches up to maxPages for one bounded expiry/strike window, deduplicating contracts by OCC symbol.
    private async Task<WindowFetchOutcome> FetchOptionsWindowPagesAsync(
        HttpClient http,
        string normalizedSymbol,
        DateOnly expiryFrom,
        DateOnly expiryTo,
        decimal? spotPrice,
        decimal minMoneyness,
        decimal maxMoneyness,
        int maxPages,
        string windowTag,
        Dictionary<string, OptionContractQuote> quotesBySymbol,
        CancellationToken cancellationToken)
    {
        string? nextPageToken = null;
        var pageQuotes = new List<OptionContractQuote>();
        var pagesFetched = 0;

        for (var page = 1; page <= maxPages; page++)
        {
            var uri = BuildOptionsSnapshotsUri(
                normalizedSymbol,
                expiryFrom,
                expiryTo,
                spotPrice,
                minMoneyness,
                maxMoneyness,
                nextPageToken);
            var payload = await SendMarketDataGetAsync(
                http,
                uri,
                artifactType: "opra_options_snapshots",
                providerRecordId: $"{normalizedSymbol}-{windowTag}-p{page}",
                cancellationToken);

            if (!payload.IsSuccessStatusCode)
            {
                logger?.LogWarning(
                    "Alpaca OPRA options snapshot request failed for {Symbol} ({Window}, page {Page}) with HTTP {StatusCode}.",
                    normalizedSymbol, windowTag, page, payload.StatusCode);
                return new WindowFetchOutcome(false, payload.StatusCode, null, pagesFetched);
            }

            pagesFetched++;
            pageQuotes.Clear();
            nextPageToken = ParseOptionsSnapshotPage(payload.Body, pageQuotes);
            foreach (var quote in pageQuotes)
            {
                quotesBySymbol[quote.ContractSymbol] = quote;
            }

            if (string.IsNullOrWhiteSpace(nextPageToken))
            {
                break;
            }
        }

        return new WindowFetchOutcome(true, null, nextPageToken, pagesFetched);
    }

    // Checks whether any collected contract expires inside the inclusive [minExpiry, maxExpiry] date range.
    private static bool HasContractInExpiryWindow(IEnumerable<OptionContractQuote> quotes, DateOnly minExpiry, DateOnly maxExpiry)
    {
        foreach (var quote in quotes)
        {
            if (quote.ExpirationDate >= minExpiry && quote.ExpirationDate <= maxExpiry)
            {
                return true;
            }
        }

        return false;
    }

    // Returns the latest expiration date seen so far across collected contracts, or null when empty.
    private static DateOnly? MaxSeenExpiry(IEnumerable<OptionContractQuote> quotes)
    {
        DateOnly? max = null;
        foreach (var quote in quotes)
        {
            if (max is null || quote.ExpirationDate > max.Value)
            {
                max = quote.ExpirationDate;
            }
        }

        return max;
    }

    // Queries Alpaca's SIP stock snapshot endpoint when the caller did not supply a positive spot price hint.
    private async Task<decimal?> TryFetchStockSpotAsync(
        HttpClient http,
        string normalizedSymbol,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(marketDataBaseUri, $"/v2/stocks/{Uri.EscapeDataString(normalizedSymbol)}/snapshot?feed=sip");
        var payload = await SendMarketDataGetAsync(
            http,
            uri,
            artifactType: "stock_snapshot_spot_hint",
            providerRecordId: normalizedSymbol,
            cancellationToken);

        if (!payload.IsSuccessStatusCode)
        {
            logger?.LogWarning(
                "Alpaca stock snapshot spot-hint request failed for {Symbol} with HTTP {StatusCode}; falling back to option chain parity.",
                normalizedSymbol, payload.StatusCode);
            return null;
        }

        return ParseStockSpotPrice(payload.Body);
    }

    // Builds the bounded OPRA options snapshots URI for explicit expiry bounds, strike moneyness band, limit=1000, and page token.
    internal Uri BuildOptionsSnapshotsUri(
        string normalizedSymbol,
        DateOnly expiryFrom,
        DateOnly expiryTo,
        decimal? spotPrice,
        decimal minMoneyness,
        decimal maxMoneyness,
        string? pageToken)
    {
        var invariant = CultureInfo.InvariantCulture;
        var query = new StringBuilder()
            .Append("/v1beta1/options/snapshots/")
            .Append(Uri.EscapeDataString(normalizedSymbol))
            .Append("?feed=opra&expiration_date_gte=").Append(expiryFrom.ToString("yyyy-MM-dd", invariant))
            .Append("&expiration_date_lte=").Append(expiryTo.ToString("yyyy-MM-dd", invariant))
            .Append("&limit=1000");

        if (spotPrice is > 0m)
        {
            var strikeMin = Math.Round(spotPrice.Value * minMoneyness, 2);
            var strikeMax = Math.Round(spotPrice.Value * maxMoneyness, 2);
            query.Append("&strike_price_gte=").Append(strikeMin.ToString("0.##", invariant))
                .Append("&strike_price_lte=").Append(strikeMax.ToString("0.##", invariant));
        }

        if (!string.IsNullOrWhiteSpace(pageToken))
        {
            query.Append("&page_token=").Append(Uri.EscapeDataString(pageToken));
        }

        return new Uri(marketDataBaseUri, query.ToString());
    }

    private readonly record struct RawHttpResult(bool IsSuccessStatusCode, int StatusCode, byte[] Body);

    // Sends an authenticated Alpaca market-data GET request, archiving raw bytes before returning when configured.
    private async Task<RawHttpResult> SendMarketDataGetAsync(
        HttpClient http,
        Uri uri,
        string artifactType,
        string providerRecordId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("APCA-API-KEY-ID", options.KeyId);
        request.Headers.TryAddWithoutValidation("APCA-API-SECRET-KEY", options.SecretKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (rawArchiver is not null)
        {
            var archived = await rawArchiver.ArchiveAsync(
                response,
                artifactType,
                providerRecordId,
                cancellationToken: cancellationToken);
            return new RawHttpResult(archived.IsSuccessStatusCode, (int)archived.StatusCode, archived.Payload);
        }

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return new RawHttpResult(response.IsSuccessStatusCode, (int)response.StatusCode, body);
    }

    // Extracts spot price from an Alpaca v2 stock snapshot (latestTrade.p -> quote midpoint -> dailyBar.c -> prevDailyBar.c).
    internal static decimal? ParseStockSpotPrice(byte[] jsonUtf8)
    {
        if (jsonUtf8.Length == 0)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(jsonUtf8);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("latestTrade", out var trade) && TryGetPositiveDecimal(trade, "p", out var tradePrice))
        {
            return tradePrice;
        }

        if (root.TryGetProperty("latestQuote", out var quote) &&
            TryGetPositiveDecimal(quote, "bp", out var bid) &&
            TryGetPositiveDecimal(quote, "ap", out var ask) &&
            ask >= bid)
        {
            return (bid + ask) / 2m;
        }

        if (root.TryGetProperty("dailyBar", out var dailyBar) && TryGetPositiveDecimal(dailyBar, "c", out var dailyClose))
        {
            return dailyClose;
        }

        if (root.TryGetProperty("prevDailyBar", out var prevBar) && TryGetPositiveDecimal(prevBar, "c", out var prevClose))
        {
            return prevClose;
        }

        return null;
    }

    // Parses one Alpaca v1beta1 OPRA options snapshots page into OptionContractQuote records and returns next_page_token.
    internal static string? ParseOptionsSnapshotPage(byte[] jsonUtf8, List<OptionContractQuote> destination)
    {
        if (jsonUtf8.Length == 0)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(jsonUtf8);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("snapshots", out var snapshots) && snapshots.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in snapshots.EnumerateObject())
            {
                if (!OptionsSentimentCalculator.TryParseOccSymbol(entry.Name, out var occ) ||
                    entry.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var node = entry.Value;
                decimal bid = 0m;
                decimal ask = 0m;
                if (node.TryGetProperty("latestQuote", out var quote) && quote.ValueKind == JsonValueKind.Object)
                {
                    bid = TryGetDecimal(quote, "bp") ?? 0m;
                    ask = TryGetDecimal(quote, "ap") ?? 0m;
                }

                decimal? iv = TryGetDecimal(node, "impliedVolatility");
                decimal? delta = null;
                decimal? gamma = null;
                decimal? theta = null;
                decimal? vega = null;
                if (node.TryGetProperty("greeks", out var greeks) && greeks.ValueKind == JsonValueKind.Object)
                {
                    delta = TryGetDecimal(greeks, "delta");
                    gamma = TryGetDecimal(greeks, "gamma");
                    theta = TryGetDecimal(greeks, "theta");
                    vega = TryGetDecimal(greeks, "vega");
                }

                decimal dailyVolume = 0m;
                if (node.TryGetProperty("dailyBar", out var dailyBar) && dailyBar.ValueKind == JsonValueKind.Object)
                {
                    dailyVolume = Math.Max(0m, TryGetDecimal(dailyBar, "v") ?? 0m);
                }

                destination.Add(new OptionContractQuote(
                    ContractSymbol: entry.Name.Trim().ToUpperInvariant(),
                    UnderlyingSymbol: occ.UnderlyingSymbol,
                    ExpirationDate: occ.ExpirationDate,
                    IsCall: occ.IsCall,
                    Strike: occ.Strike,
                    Bid: bid,
                    Ask: ask,
                    ImpliedVolatility: iv,
                    Delta: delta,
                    Gamma: gamma,
                    Theta: theta,
                    Vega: vega,
                    DailyVolume: dailyVolume));
            }
        }

        return root.TryGetProperty("next_page_token", out var tokenElement) &&
               tokenElement.ValueKind == JsonValueKind.String
            ? tokenElement.GetString()
            : null;
    }

    // Safely reads a numeric JSON property as decimal, returning null when absent, non-numeric, or out of range.
    private static decimal? TryGetDecimal(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number)
        {
            if (prop.TryGetDecimal(out var dec))
            {
                return dec;
            }

            if (prop.TryGetDouble(out var dbl) && double.IsFinite(dbl))
            {
                try
                {
                    return Convert.ToDecimal(dbl, CultureInfo.InvariantCulture);
                }
                catch (OverflowException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static bool TryGetPositiveDecimal(JsonElement obj, string propertyName, out decimal value)
    {
        if (TryGetDecimal(obj, propertyName) is { } parsed && parsed > 0m)
        {
            value = parsed;
            return true;
        }

        value = 0m;
        return false;
    }
}
