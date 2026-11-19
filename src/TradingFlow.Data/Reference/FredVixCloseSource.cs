using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Regime;

namespace TradingFlow.Data.Reference;

/// <summary>One download of a FRED series file: the exact bytes, the URL, when it was requested (UTC) and the HTTP status.</summary>
public sealed record FredDownload(byte[] Bytes, Uri Url, DateTimeOffset RequestedAtUtc, int HttpStatus);

/// <summary>
/// Reads FRED's public graph CSV for a daily series (https://fred.stlouisfed.org/graph/fredgraph.csv?id=VIXCLS). No API key is
/// needed. A non-success HTTP status throws with the status code, so callers can tell a failed request from missing values.
/// </summary>
public static class FredSeriesFile
{
    /// <summary>The graph CSV address of <paramref name="seriesId"/>, optionally starting at <paramref name="from"/> (cosd).</summary>
    public static Uri Url(string seriesId, DateOnly? from = null) => new(
        "https://fred.stlouisfed.org/graph/fredgraph.csv?id=" + Uri.EscapeDataString(seriesId) +
        (from is { } start ? "&cosd=" + start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : ""));

    /// <summary>Downloads the file once and returns its exact bytes with the request time and HTTP status.</summary>
    public static async Task<FredDownload> DownloadAsync(HttpClient http, string seriesId, DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken, DateOnly? from = null)
    {
        var url = Url(seriesId, from);
        using var response = await http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"FRED returned HTTP {(int)response.StatusCode} for {url}.", null, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return new FredDownload(bytes, url, requestedAtUtc, (int)response.StatusCode);
    }
}

/// <summary>
/// Paper trading and the Desk: FRED's latest published VIX closes (the last 40 calendar days, which always include the
/// previous session). A successful read is reused for <see cref="CacheLifetime"/> so each stock's check does not repeat the
/// request; a failed read is not cached, logs structured diagnostics (URL, status or parse failure), and throws so the entry
/// is refused as vix_lookup_failed and retried next time.
/// </summary>
public sealed class FredVixCloseSource(
    Func<HttpClient> createClient,
    TimeProvider clock,
    ILogger<FredVixCloseSource>? logger = null) : IVixCloseSource, IDisposable
{
    /// <summary>Name of the configured HTTP client (timeout, user agent) the host registers for FRED.</summary>
    public const string HttpClientName = "fred";

    /// <summary>How long one successful read is reused (FRED updates VIXCLS at most once per day).</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim gate = new(1, 1);
    private (VixCloseSeries Closes, DateTimeOffset ReadAtUtc)? cached;

    /// <summary>Returns the cached closes while fresh, otherwise downloads and parses FRED's file once.</summary>
    public async Task<VixCloseSeries> LoadLatestAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = clock.GetUtcNow();
            if (cached is { } hit && now - hit.ReadAtUtc < CacheLifetime) return hit.Closes;
            var from = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-40);
            var url = FredSeriesFile.Url(RegimeRules.FredVixSeries, from);
            try
            {
                using var http = createClient();
                var download = await FredSeriesFile.DownloadAsync(http, RegimeRules.FredVixSeries, now, cancellationToken, from);
                var closes = VixCloseSeries.ParseFredCsv(Encoding.UTF8.GetString(download.Bytes));
                cached = (closes, now);
                return closes;
            }
            catch (HttpRequestException ex)
            {
                logger?.LogWarning(ex,
                    "FRED VIXCLS fetch failed for {Url} with HTTP status {HttpStatus}: {ErrorMessage}. Entries requiring VIX sizing will be refused as {Reason}.",
                    url, (int?)ex.StatusCode, ex.Message, VixRegime.LookupFailedReason);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger?.LogWarning(ex,
                    "FRED VIXCLS read or parse failed for {Url} ({ErrorType}): {ErrorMessage}. Entries requiring VIX sizing will be refused as {Reason}.",
                    url, ex.GetType().Name, ex.Message, VixRegime.LookupFailedReason);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Releases the internal fetch lock.</summary>
    public void Dispose() => gate.Dispose();
}
