using System.Diagnostics;
using System.Globalization;
using System.Net;
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
    private const int MaxHttpClientAttempts = 2;
    private static readonly TimeSpan HttpClientAttemptTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    // Set once the curl fallback has succeeded in this process, so later downloads try curl first.
    private static volatile bool systemCurlPreferred;

    /// <summary>The graph CSV address of <paramref name="seriesId"/>, optionally starting at <paramref name="from"/> (cosd).</summary>
    public static Uri Url(string seriesId, DateOnly? from = null) => new(
        "https://fred.stlouisfed.org/graph/fredgraph.csv?id=" + Uri.EscapeDataString(seriesId) +
        (from is { } start ? "&cosd=" + start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : ""));

    /// <summary>
    /// Downloads the FRED CSV over HTTP/1.1 exact with a 10-second per-attempt timeout and 1 retry on transient transport,
    /// gateway (502/503/504), or per-attempt timeout failures. When Windows Schannel TLS is reset or tarpitted by FRED
    /// (transport failure or per-attempt timeout while caller cancellation was not requested), falls back to Windows'
    /// built-in <c>curl.exe</c> (OpenSSL TLS) when <paramref name="allowSystemCurlFallback"/> is enabled. 4xx statuses and
    /// caller cancellations throw immediately.
    /// </summary>
    public static async Task<FredDownload> DownloadAsync(HttpClient http, string seriesId, DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken, DateOnly? from = null, bool allowSystemCurlFallback = true, bool preferSystemCurl = false)
    {
        // On Windows, .NET (Schannel) TLS to FRED can hang without honouring its 10-second cancellation, while Windows'
        // own curl.exe answers in about half a second. Hosts pass preferSystemCurl to try curl first; if curl fails, the
        // HttpClient path (with curl as its fallback) is used. Non-Windows hosts never use curl (TryGetSystemCurlPath).
        var url = Url(seriesId, from);
        if ((preferSystemCurl || systemCurlPreferred) && allowSystemCurlFallback && TryGetSystemCurlPath(url, out var preferredCurl))
        {
            try
            {
                return await DownloadWithSystemCurlAsync(preferredCurl, url, requestedAtUtc, cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null && !cancellationToken.IsCancellationRequested)
            {
                // curl stopped working (for example a missing or blocked binary): forget the preference and use HttpClient.
                systemCurlPreferred = false;
            }
        }

        for (var attempt = 1; ; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(HttpClientAttemptTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url)
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                using var response = await http.SendAsync(request, attemptCts.Token);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"FRED returned HTTP {(int)response.StatusCode} for {url}.", null, response.StatusCode);
                var bytes = await response.Content.ReadAsByteArrayAsync(attemptCts.Token);
                return new FredDownload(bytes, url, requestedAtUtc, (int)response.StatusCode);
            }
            catch (HttpRequestException ex) when (IsTransientFailure(ex) && !cancellationToken.IsCancellationRequested)
            {
                if (attempt < MaxHttpClientAttempts)
                {
                    await Task.Delay(RetryDelay, cancellationToken);
                    continue;
                }

                if (ex.StatusCode is null && allowSystemCurlFallback && TryGetSystemCurlPath(url, out var curlPath))
                    return await DownloadWithCurlAndPreferItAsync(curlPath, url, requestedAtUtc, cancellationToken);
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < MaxHttpClientAttempts)
                {
                    await Task.Delay(RetryDelay, cancellationToken);
                    continue;
                }

                if (allowSystemCurlFallback && TryGetSystemCurlPath(url, out var curlPath))
                    return await DownloadWithCurlAndPreferItAsync(curlPath, url, requestedAtUtc, cancellationToken);
                throw;
            }
        }
    }

    // Runs the curl fallback and, when it succeeds, remembers to try curl first on later downloads in this process.
    private static async Task<FredDownload> DownloadWithCurlAndPreferItAsync(
        string curlPath, Uri url, DateTimeOffset requestedAtUtc, CancellationToken cancellationToken)
    {
        var download = await DownloadWithSystemCurlAsync(curlPath, url, requestedAtUtc, cancellationToken);
        systemCurlPreferred = true;
        return download;
    }

    // Classifies transport errors (null HTTP status) and upstream gateway 502/503/504 failures as retryable; 4xx fails fast.
    private static bool IsTransientFailure(HttpRequestException ex) => ex.StatusCode is null
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;

    // Resolves Windows' built-in System32 curl.exe only for fred.stlouisfed.org so non-FRED or non-Windows calls never shell out.
    private static bool TryGetSystemCurlPath(Uri url, out string curlPath)
    {
        curlPath = string.Empty;
        if (!OperatingSystem.IsWindows() || !url.Host.Equals("fred.stlouisfed.org", StringComparison.OrdinalIgnoreCase))
            return false;
        var candidate = Path.Combine(Environment.SystemDirectory, "curl.exe");
        if (!File.Exists(candidate))
            return false;
        curlPath = candidate;
        return true;
    }

    // Downloads the exact FRED CSV bytes using Windows' built-in OpenSSL curl.exe into a run-owned temp file deleted in finally.
    private static async Task<FredDownload> DownloadWithSystemCurlAsync(
        string curlPath, Uri url, DateTimeOffset requestedAtUtc, CancellationToken cancellationToken)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"tf-fred-{Guid.NewGuid():N}.csv");
        try
        {
            var startInfo = new ProcessStartInfo(curlPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[]
            {
                "--silent",
                "--show-error",
                "--location",
                "--http1.1",
                "--max-time", "30",
                "--output", tempFile,
                "--write-out", "%{http_code}",
                url.AbsoluteUri
            })
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start {curlPath} for FRED download of {url}.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                throw;
            }

            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();
            var hasStatus = int.TryParse(stdout, NumberStyles.None, CultureInfo.InvariantCulture, out var statusCode);
            HttpStatusCode? httpStatus = hasStatus && statusCode >= 100 ? (HttpStatusCode)statusCode : null;

            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrEmpty(stderr) ? $"exit code {process.ExitCode}" : $"exit code {process.ExitCode}: {stderr}";
                throw new HttpRequestException($"FRED curl fallback failed for {url} ({detail}).", null, httpStatus);
            }

            if (!hasStatus || statusCode is < 200 or > 299)
                throw new HttpRequestException($"FRED returned HTTP {(hasStatus ? statusCode.ToString(CultureInfo.InvariantCulture) : stdout)} for {url}.", null, httpStatus);

            if (!File.Exists(tempFile))
                throw new HttpRequestException($"FRED curl fallback returned HTTP {statusCode} for {url} without writing output bytes.");

            var bytes = await File.ReadAllBytesAsync(tempFile, cancellationToken);
            return new FredDownload(bytes, url, requestedAtUtc, statusCode);
        }
        finally
        {
            DeleteTempFileBestEffort(tempFile);
        }
    }

    // Terminates an in-flight curl child process when caller cancellation aborts the download.
    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    // Removes the run-owned temporary CSV file created for the curl fallback without masking primary errors.
    private static void DeleteTempFileBestEffort(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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
    ILogger<FredVixCloseSource>? logger = null,
    bool preferSystemCurl = false) : IVixCloseSource, IDisposable
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
                var download = await FredSeriesFile.DownloadAsync(http, RegimeRules.FredVixSeries, now, cancellationToken, from,
                    preferSystemCurl: preferSystemCurl);
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
