using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using TradingFlow.Domain.Logging;
using TradingFlow.Domain.ScreenCaptures;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Finviz;

public sealed partial class FinvizClient
{
    public const string ScheduledExportFormat = "finviz-default-export-csv";
    public const int ScheduledPayloadLimit = 16 * 1024 * 1024;
    private const string ScheduledArtifactType = "scheduled_screener_csv";
    private const string ArchivePattern = "*-scheduled_screener_csv.csv.manifest.json";
    private static readonly JsonSerializerOptions CaptureManifestJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] BaselineColumns =
        ["no", "ticker", "company", "sector", "industry", "country", "marketcap", "pe", "price", "change", "volume"];

    // Freeze only stock selection parameters and discard view, ordering, paging and credentials.
    public static string CanonicalizeScheduledSelection(string query)
    {
        var normalized = NormalizeScreenerFilterQuery(query);
        var values = System.Web.HttpUtility.ParseQueryString(normalized);
        return String.Join('&', new[] { "f", "s", "t" }
            .Where(key => !String.IsNullOrWhiteSpace(values[key]))
            .Select(key => $"{key}={Uri.EscapeDataString(values[key]!.Trim())}"));
    }

    // Fetch one explicitly configured scheduled export, archiving every bounded received payload before inspection.
    public async Task<FinvizExportObservation> CaptureScreenerExportAsync(
        FinvizExportRequest request, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!IsConfigured || request.Format != ScheduledExportFormat || String.IsNullOrWhiteSpace(request.Query)
            || request.Query != CanonicalizeScheduledSelection(request.Query)
            || request.QuerySha256 != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Query))))
            return Failure("The saved screen has no supported selection query or authenticated export format.");
        if (clock.GetUtcNow() >= request.DeadlineUtc)
            return Failure("The capture window closed before the provider request started.");

        var requestUri = ScheduledRequestUri(request.Query);
        var url = $"{requestUri.PathAndQuery}&auth={Uri.EscapeDataString(_options.AuthToken)}";
        var (response, dispatchedAt, failure) = await TrySendScheduledAsync(url, requestUri, request.DeadlineUtc, clock, cancellationToken);
        if (response is null)
            return Failure(failure ?? "Finviz did not provide an HTTP response.") with { DispatchedAtUtc = dispatchedAt };

        using (response)
        {
            var headersAt = clock.GetUtcNow();
            var body = await ReadScheduledBodyAsync(response.Content, cancellationToken);
            var receivedAt = clock.GetUtcNow();
            var receipt = await ArchiveScheduledExportAsync(request, requestUri, response, body, dispatchedAt!.Value, headersAt, receivedAt);
            return await ObserveScheduledArchiveAsync(receipt, request, cancellationToken);
        }
    }

    // Search only scheduled manifests in this attempt's UTC date range and require one exact committed receipt.
    public async Task<FinvizExportObservation?> RecoverScreenerExportAsync(
        FinvizExportRequest request, RawArchiveOptions archive, CancellationToken cancellationToken)
    {
        RawArchiveReceipt? match = null;
        var lastDate = request.DeadlineUtc.AddMinutes(1).UtcDateTime.Date;
        for (var date = request.RequestedAtUtc.UtcDateTime.Date; date <= lastDate; date = date.AddDays(1))
        {
            var directory = Path.Combine(archive.RootPath, "finviz", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (!Directory.Exists(directory))
                continue;

            foreach (var path in Directory.EnumerateFiles(directory, ArchivePattern))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCandidateReceiptPath(path, request))
                    continue;

                var manifest = await ReadCaptureManifestAsync(path, cancellationToken);
                if (manifest is null || manifest.CorrelationId != request.CorrelationId.ToString("N"))
                    continue;
                if (!MatchesScheduledRequest(manifest, request, path) || match is not null)
                    return DescribeReceipt(manifest) with { Reason = "A scheduled raw receipt conflicted with the durable attempt identity." };

                match = new RawArchiveReceipt(Path.Combine(directory, manifest.PayloadFileName), path, manifest);
            }
        }
        return match is null ? null : await ObserveScheduledArchiveAsync(match, request, cancellationToken);
    }

    // Stamp dispatch after the shared Finviz queue and record profiling without credential-bearing exception strings.
    private async Task<(HttpResponseMessage? Response, DateTimeOffset? DispatchedAt, string? Failure)> TrySendScheduledAsync(
        string url, Uri requestUri, DateTimeOffset deadline, TimeProvider clock, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        DateTimeOffset? dispatchedAt = null;
        HttpResponseMessage? response = null;
        string? failure = null;
        try
        {
            response = await _bulkhead.ExecuteAsync(DispatchAsync, cancellationToken);
        }
        catch (HttpRequestException)
        {
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested || dispatchedAt is not null)
        {
            failure = dispatchedAt is null ? "The capture window closed before the provider request started."
                : "Finviz did not provide an HTTP response before the request timed out.";
        }
        catch (Polly.Bulkhead.BulkheadRejectedException)
        {
            failure = "The shared Finviz request queue could not admit the attempt.";
        }
        finally
        {
            ApiProfiler.RecordRequest("Finviz", requestUri.PathAndQuery, "GET", started.Elapsed.TotalMilliseconds,
                response?.IsSuccessStatusCode == true, response is null ? failure ?? "No HTTP response" : null);
        }
        return (response, dispatchedAt, failure);

        // The bulkhead may queue this call behind existing manual Finviz requests.
        Task<HttpResponseMessage> DispatchAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var now = clock.GetUtcNow();
            if (now >= deadline)
                throw new OperationCanceledException("The scheduled export window elapsed before dispatch.");
            dispatchedAt = now;
            return _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        }
    }

    // Reject announced oversize before opening the body, and stop unknown-length streams at the first excess byte.
    private static async Task<ScheduledBody> ReadScheduledBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var declared = content.Headers.ContentLength;
        if (declared > ScheduledPayloadLimit)
            return new([], "oversized", declared, null);

        using var buffer = new MemoryStream();
        var block = new byte[32 * 1024];
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            while (true)
            {
                var toRead = Math.Min(block.Length, ScheduledPayloadLimit - (int)buffer.Length + 1);
                var count = await stream.ReadAsync(block.AsMemory(0, toRead), cancellationToken);
                if (count == 0)
                {
                    var disposition = declared is null || declared == buffer.Length ? "complete" : "incomplete";
                    return new(buffer.ToArray(), disposition, declared, buffer.Length);
                }
                if (buffer.Length + count > ScheduledPayloadLimit)
                    return new([], "oversized", declared, buffer.Length + count);
                buffer.Write(block, 0, count);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
        {
            return new(buffer.ToArray(), "incomplete", declared, buffer.Length);
        }
    }

    // An oversized response gets a metadata-only receipt with an explicit body disposition.
    private Task<RawArchiveReceipt> ArchiveScheduledExportAsync(
        FinvizExportRequest request, Uri requestUri, HttpResponseMessage response, ScheduledBody body,
        DateTimeOffset dispatchedAt, DateTimeOffset headersAt, DateTimeOffset receivedAt)
    {
        var attributes = new Dictionary<string, string>
        {
            ["exportFormat"] = request.Format,
            ["requestedAtUtc"] = request.RequestedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["dispatchedAtUtc"] = dispatchedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["headersReceivedAtUtc"] = headersAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["bodyDisposition"] = body.Disposition
        };
        if (body.DeclaredBytes is long declared)
            attributes["declaredBytes"] = declared.ToString(CultureInfo.InvariantCulture);
        if (body.MinimumObserved is long observed)
            attributes["minimumBytesObserved"] = observed.ToString(CultureInfo.InvariantCulture);

        return _rawArchiveWriter.ArchiveAsync(
            new RawArchiveRequest("finviz", ScheduledArtifactType, receivedAt, "csv",
                response.Content.Headers.ContentType?.ToString(), PresetId: request.PresetId.ToString("N"),
                CorrelationId: request.CorrelationId.ToString("N"), ConfigHash: request.QuerySha256,
                Http: new RawArchiveHttpMetadata(requestUri, (int)response.StatusCode, CollectResponseHeaders(response)),
                Attributes: attributes),
            body.Bytes, CancellationToken.None);
    }

    // Constrain recovery reads to generated filenames within the approximate attempt clock range.
    private static bool IsCandidateReceiptPath(string path, FinvizExportRequest request)
    {
        var name = Path.GetFileName(path);
        return name.Length > 23
            && DateTimeOffset.TryParseExact(name[..23], "yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var receivedAt)
            && receivedAt >= request.RequestedAtUtc && receivedAt <= request.DeadlineUtc.AddMinutes(1);
    }

    // Read only bounded immutable manifests. An unreadable manifest never authorizes publication.
    private static async Task<RawArchiveManifest?> ReadCaptureManifestAsync(string path, CancellationToken token)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return stream.Length > 1024 * 1024 ? null
                : await JsonSerializer.DeserializeAsync<RawArchiveManifest>(stream, CaptureManifestJson, token);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Check identity, request metadata and generated paths before touching archived payload bytes.
    private bool MatchesScheduledRequest(RawArchiveManifest manifest, FinvizExportRequest request, string manifestPath)
    {
        var expectedPayloadName = $"{manifest.ArchiveId}-{ScheduledArtifactType}.csv";
        return manifest.SchemaVersion == 1 && manifest.Source == "finviz" && manifest.ArtifactType == ScheduledArtifactType
            && manifest.PresetId == request.PresetId.ToString("N")
            && manifest.CorrelationId == request.CorrelationId.ToString("N")
            && manifest.ConfigHash == request.QuerySha256 && manifest.Http?.RequestUri == ScheduledRequestUri(request.Query)
            && manifest.Http.StatusCode is >= 100 and <= 599
            && manifest.Attributes?.GetValueOrDefault("exportFormat") == request.Format
            && ReadArchiveClock(manifest, "requestedAtUtc") == request.RequestedAtUtc
            && ReadArchiveClock(manifest, "dispatchedAtUtc") is { } dispatchedAt
            && dispatchedAt >= request.RequestedAtUtc && dispatchedAt < request.DeadlineUtc
            && ReadArchiveClock(manifest, "headersReceivedAtUtc") is { } headersAt
            && headersAt >= dispatchedAt && headersAt <= manifest.ReceivedAtUtc
            && manifest.ReceivedAtUtc >= request.RequestedAtUtc
            && expectedPayloadName.StartsWith($"{manifest.ReceivedAtUtc.ToUniversalTime():yyyyMMddTHHmmssfffffffZ}-", StringComparison.Ordinal)
            && manifest.ByteLength is >= 0 and <= ScheduledPayloadLimit
            && manifest.PayloadFileName == expectedPayloadName && Path.GetFileName(expectedPayloadName) == expectedPayloadName
            && Path.GetFileName(manifestPath) == expectedPayloadName + ".manifest.json";
    }

    // Hash the exact committed bytes and treat late, oversized and incomplete receipts explicitly.
    private async Task<FinvizExportObservation> ObserveScheduledArchiveAsync(
        RawArchiveReceipt receipt, FinvizExportRequest request, CancellationToken cancellationToken)
    {
        var manifest = receipt.Manifest;
        var observation = DescribeReceipt(manifest);
        if (!MatchesScheduledRequest(manifest, request, receipt.ManifestPath))
            return observation;

        var bytes = await ReadVerifiedPayloadAsync(receipt, cancellationToken);
        if (bytes is null)
            return observation;

        return manifest.Attributes.GetValueOrDefault("bodyDisposition") switch
        {
            "oversized" => observation with { Reason = "Finviz response exceeded the 16 MiB ceiling. Only response metadata was retained." },
            "incomplete" => observation with { State = FinvizCaptureState.Partial, Reason = "Finviz response ended before its body could be read in full." },
            "complete" => InspectScheduledCsv(observation with { CompletePayload = true }, bytes, manifest.ContentType, request.DeadlineUtc),
            _ => observation
        };
    }

    // Keep even a conflicting archive's identity traceable without asserting its payload was verified.
    private static FinvizExportObservation DescribeReceipt(RawArchiveManifest manifest) =>
        Failure("The scheduled raw receipt failed its exact identity or byte verification.") with
        {
            ReceivedAtUtc = manifest.ReceivedAtUtc,
            HeadersReceivedAtUtc = ReadArchiveClock(manifest, "headersReceivedAtUtc"),
            DispatchedAtUtc = ReadArchiveClock(manifest, "dispatchedAtUtc"),
            HttpStatus = manifest.Http?.StatusCode,
            ArchiveId = manifest.ArchiveId,
            Sha256 = manifest.Sha256,
            ArchivedBytes = manifest.ByteLength,
            DeclaredBytes = ReadArchiveLength(manifest, "declaredBytes"),
            MinimumBytesObserved = ReadArchiveLength(manifest, "minimumBytesObserved"),
            RetryAfterUtc = ReadRetryAfter(manifest)
        };

    // The manifest length bounds allocation even if a file changed between receipt publication and recovery.
    private static async Task<byte[]?> ReadVerifiedPayloadAsync(RawArchiveReceipt receipt, CancellationToken token)
    {
        try
        {
            await using var stream = File.OpenRead(receipt.PayloadPath);
            if (stream.Length != receipt.Manifest.ByteLength)
                return null;
            var bytes = new byte[(int)receipt.Manifest.ByteLength];
            await stream.ReadExactlyAsync(bytes, token);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return stream.ReadByte() == -1 && hash.Equals(receipt.Manifest.Sha256, StringComparison.Ordinal) ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Only HTTP 200 and structurally valid CSV can establish a captured export. Row membership remains unverified.
    private static FinvizExportObservation InspectScheduledCsv(
        FinvizExportObservation observation, byte[] bytes, string? contentType, DateTimeOffset deadlineUtc)
    {
        if (observation.HttpStatus != (int)HttpStatusCode.OK)
            return observation with { Reason = $"Finviz returned HTTP {observation.HttpStatus}." };
        if (contentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
            return observation with { Reason = "Finviz returned HTML, so no CSV was captured." };

        string[]? headers = null;
        var rows = 0;
        try
        {
            using var data = new MemoryStream(bytes, writable: false);
            using var parser = new TextFieldParser(data, ScheduledCsvEncoding(contentType), detectEncoding: false)
            {
                Delimiters = [","], HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false
            };
            headers = parser.ReadFields();
            if (headers is { Length: > 0 })
                headers[0] = headers[0].TrimStart('\uFEFF');
            var normalized = headers?.Select(NormalizeHeader).ToArray() ?? [];
            var ticker = Array.IndexOf(normalized, "ticker");
            if (ticker < 0 || normalized.Any(String.IsNullOrEmpty) || normalized.Distinct().Count() != normalized.Length)
                return observation with { Headers = headers, Reason = "Finviz returned no usable CSV header with a unique Ticker column." };

            var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (parser.ReadFields() is { } fields)
            {
                if (fields.Length != normalized.Length || !IsExportTicker(fields[ticker]) || !symbols.Add(fields[ticker].Trim()))
                    return PartialCsv(observation, headers, rows, "Finviz CSV contained an incomplete, invalid or duplicate ticker row.");
                rows++;
            }

            var missing = BaselineColumns.Except(normalized).ToArray();
            if (missing.Length > 0)
                return PartialCsv(observation, headers, rows, $"Finviz CSV omitted expected default-export columns ({String.Join(", ", missing)}).")
                    with { ColumnCoverage = FinvizColumnCoverage.Reduced };

            var additions = normalized.Except(BaselineColumns).Any();
            var observed = observation with
            {
                Headers = headers, RowCount = rows,
                ColumnCoverage = additions ? FinvizColumnCoverage.Unexpected : FinvizColumnCoverage.Baseline
            };
            if (observation.ReceivedAtUtc >= deadlineUtc)
                return observed with { State = FinvizCaptureState.Partial, Reason = "A valid Finviz CSV was received after the capture window." };
            var coverage = rows == 0 ? "Header-only CSV. Matching-row membership and empty-screen status are unverified."
                : "Matching-row membership is unverified.";
            return observed with
            {
                State = FinvizCaptureState.Captured,
                Reason = additions ? $"Finviz included unexpected columns. {coverage}" : coverage
            };
        }
        catch (Exception ex) when (ex is MalformedLineException or DecoderFallbackException)
        {
            return PartialCsv(observation, headers, rows, "Finviz CSV had malformed quoting or invalid text encoding.");
        }
    }

    // Respect a declared character set and reject invalid byte sequences instead of inserting replacement characters.
    private static Encoding ScheduledCsvEncoding(string? contentType)
    {
        var charset = MediaTypeHeaderValue.TryParse(contentType, out var parsed) ? parsed.CharSet : null;
        var encoding = ResolveEncoding(charset);
        return Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    // The CSV must contain a plausible exchange symbol in each row and no blank ticker or footer rows.
    private static bool IsExportTicker(string ticker)
    {
        var value = ticker.Trim();
        return value.Length is > 0 and <= 40 && value.Any(Char.IsAsciiLetterOrDigit)
            && value.All(character => Char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '/' or '^');
    }

    // Persist the headers and count of rows that were validated before a malformed record.
    private static FinvizExportObservation PartialCsv(
        FinvizExportObservation observation, string[]? headers, int rows, string reason) =>
        observation with { State = FinvizCaptureState.Partial, Headers = headers, RowCount = rows, Reason = reason };

    // A no-response failure has no fabricated HTTP or archive values.
    private static FinvizExportObservation Failure(string reason) => new(FinvizCaptureState.Failed, reason);

    // The catalog identity contains only a canonical stock selection, never the authentication query.
    private Uri ScheduledRequestUri(string query) => new(_options.BaseUrl, $"/export?{query}");

    // Parse the archived HTTP Retry-After header using its original receipt clock for delay values.
    private static DateTimeOffset? ReadRetryAfter(RawArchiveManifest manifest)
    {
        if (manifest.Http?.StatusCode is not (429 or 503))
            return null;
        var value = manifest.Http.Headers?.FirstOrDefault(header =>
            header.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault();
        if (!RetryConditionHeaderValue.TryParse(value, out var parsed))
            return null;
        if (parsed.Date is { } date)
            return date;
        return parsed.Delta is { } delay ? manifest.ReceivedAtUtc + delay : null;
    }

    // Raw receipt attributes are serialized with an invariant UTC round-trip representation.
    private static DateTimeOffset? ReadArchiveClock(RawArchiveManifest manifest, string key) =>
        DateTimeOffset.TryParseExact(manifest.Attributes?.GetValueOrDefault(key), "O", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var timestamp) ? timestamp : null;

    // Optional body size evidence stays unknown when the provider did not declare or send it.
    private static long? ReadArchiveLength(RawArchiveManifest manifest, string key) =>
        Int64.TryParse(manifest.Attributes?.GetValueOrDefault(key), NumberStyles.None, CultureInfo.InvariantCulture,
            out var count) ? count : null;

    private sealed record ScheduledBody(byte[] Bytes, string Disposition, long? DeclaredBytes, long? MinimumObserved);
}
