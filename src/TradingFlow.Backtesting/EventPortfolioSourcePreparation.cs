using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Backtesting;

public sealed record PreparedEventPortfolioSource(string ManifestPath, string ManifestSha256,
    DateTimeOffset EvaluationStartUtc, DateTimeOffset EvaluationEndUtc,
    IReadOnlyList<SipMetadataCapture> Metadata);

/// <summary>Prepare ordinary saved-plan SIP execution sources using the existing raw archive collector and calendar captures.</summary>
public sealed class EventPortfolioSourcePreparation(SipExecutionArchiveCollector archives,
    SipExecutionMetadataCollector metadata)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Freeze full regular-session quotes/trades and a quote prefix in a new retained attempt directory.</summary>
    public async Task<PreparedEventPortfolioSource> PrepareAsync(BacktestRunConfig run,
        DateTimeOffset evaluationCutoffUtc, string attemptDirectory, int quoteMaxAgeMilliseconds,
        decimal maxEntrySpreadBps, CancellationToken cancellationToken = default)
    {
        if (run.Engine.ExecutionReplayPolicyId != ExecutionReplayPolicy.IntradayRthFiveMinuteQuoteMarketV1 ||
            !run.Provider.Equals("alpaca", StringComparison.OrdinalIgnoreCase) ||
            !run.Providers.Alpaca.DataFeed.Equals("sip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Automatic quote execution preparation requires the intraday SIP policy and Alpaca SIP inputs.");
        if (run.Tickers.Count == 0 || run.Tickers.Any(String.IsNullOrWhiteSpace) || quoteMaxAgeMilliseconds <= 0 || maxEntrySpreadBps <= 0)
            throw new InvalidDataException("Quote execution preparation requires stocks and resolved positive quote-age/spread controls.");
        var end = (run.TimeWindow.End ?? evaluationCutoffUtc).ToUniversalTime();
        var start = run.TimeWindow.Type.Equals("rolling", StringComparison.OrdinalIgnoreCase)
            ? run.TimeWindow.Start?.ToUniversalTime() ?? end.AddDays(-run.TimeWindow.LookbackDays)
            : run.TimeWindow.Start?.ToUniversalTime() ?? throw new InvalidDataException("Fixed execution preparation needs its evaluation start.");
        if (end <= start || end > DateTimeOffset.UtcNow)
            throw new InvalidDataException("Historical quote execution requires a completed positive evaluation interval.");
        var root = Path.GetFullPath(attemptDirectory);
        if (Directory.Exists(root))
            throw new InvalidOperationException("Execution source preparation requires a new attempt directory; previous captures remain retained.");
        Directory.CreateDirectory(root);
        await WriteAsync(Path.Combine(root, "preparation-request.json"), new {
            SchemaVersion = 1, CapturedAtUtc = DateTimeOffset.UtcNow, EvaluationStartUtc = start, EvaluationEndUtc = end,
            Symbols = run.Tickers, Feed = "sip", CandleAdjustment = "raw", ExecutionPrices = "provider_raw",
            QuoteMaxAgeMilliseconds = quoteMaxAgeMilliseconds, MaxEntrySpreadBps = maxEntrySpreadBps,
            HistoricalReceiptClaim = false, HistoricalBorrowProven = false
        }, cancellationToken);
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime);
            var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(end.AddTicks(-1), zone).DateTime);
            var calendarCapture = await metadata.CaptureCalendarAsync(first, last, Path.Combine(root, "calendar"), cancellationToken);
            var calendar = FrozenExchangeCalendar.Read(calendarCapture.ReceiptPath, calendarCapture.ReceiptSha256);
            var conditions = await metadata.CaptureConditionsAsync(Path.Combine(root, "condition-metadata"), cancellationToken);
            var policy = BuildSourcePolicy(conditions);
            var sessions = new List<SipPortfolioSessionArchive>();
            foreach (var session in calendar.Sessions.Where(x => x.CloseUtc > start && x.OpenUtc < end))
            {
                // A study ending within a session cannot certify its later same-day liquidation.
                if (session.CloseUtc > end)
                    throw new InvalidDataException($"Evaluation end does not cover the complete {session.Date:yyyy-MM-dd} regular session and its liquidation evidence.");
                foreach (var symbol in run.Tickers.Select(x => x.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                    sessions.Add(await PrepareSessionAsync(symbol, session, run, quoteMaxAgeMilliseconds, cancellationToken));
            }
            if (sessions.Count == 0) throw new InvalidDataException("Captured calendar has no regular exchange session in the requested evaluation interval.");
            // Both source adapters explicitly request raw prices and share quantities. No corporate-action multiplier is inferred.
            var assumptions = new EventPortfolioExecutionAssumptions(ExecutionProtectionActivation.CustomPerFill,
                checked((long)quoteMaxAgeMilliseconds * 1_000_000), 0, true, 1m, 1m,
                "alpaca-sip-raw-quotes-trades-and-raw-bars-v1") { MaxEntrySpreadBps = maxEntrySpreadBps };
            var manifest = new EventPortfolioSourceManifest(1, policy, sessions, assumptions,
                calendarCapture.ReceiptPath, calendarCapture.ReceiptSha256);
            _ = SipPortfolioExecutionSource.Create(sessions, policy);
            var manifestPath = Path.Combine(root, "event-source-manifest.json");
            await WriteAsync(manifestPath, manifest, cancellationToken);
            var allMetadata = new[] { calendarCapture }.Concat(conditions).ToArray();
            await WriteAsync(Path.Combine(root, "preparation-completed.json"), new {
                CompletedAtUtc = DateTimeOffset.UtcNow, Sessions = sessions.Count, Metadata = allMetadata,
                ManifestSha256 = await HashFileAsync(manifestPath, cancellationToken)
            }, cancellationToken);
            return new(manifestPath, await HashFileAsync(manifestPath, cancellationToken), start, end, allMetadata);
        }
        catch (Exception error)
        {
            // Failure artifacts are retained alongside any genuine partial downloads; they never become a completed manifest.
            await WriteAsync(Path.Combine(root, "preparation-failed.json"), new {
                FailedAtUtc = DateTimeOffset.UtcNow, ErrorType = error.GetType().Name, error.Message
            }, CancellationToken.None);
            throw;
        }
    }

    /// <summary>Use each captured session's actual boundaries, including early closes and DST changes.</summary>
    private async Task<SipPortfolioSessionArchive> PrepareSessionAsync(string symbol, FrozenExchangeSession session,
        BacktestRunConfig run, int quoteAgeMilliseconds, CancellationToken token)
    {
        var start = Ns(session.OpenUtc); var end = Ns(session.CloseUtc);
        var mappingDate = session.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var request = new SipArchiveRequest(symbol, "quotes", SipNanosecondTime.Format(start), SipNanosecondTime.Format(end), mappingDate);
        var prefix = request with { StartInclusive = SipNanosecondTime.Format(checked(start - (long)quoteAgeMilliseconds * 1_000_000)), EndExclusive = request.StartInclusive };
        var tradeRequest = request with { Kind = "trades" };
        var quoteDirectory = ArchiveDirectory(run, request);
        var tradeDirectory = ArchiveDirectory(run, tradeRequest);
        var prefixDirectory = ArchiveDirectory(run, prefix);
        await archives.CollectAsync(prefix, prefixDirectory, cancellationToken: token);
        await archives.CollectAsync(request, quoteDirectory, cancellationToken: token);
        await archives.CollectAsync(tradeRequest, tradeDirectory, cancellationToken: token);
        return new(symbol + ":" + mappingDate, request, quoteDirectory, await HashFileAsync(Path.Combine(quoteDirectory, "manifest.json"), token),
            tradeDirectory, await HashFileAsync(Path.Combine(tradeDirectory, "manifest.json"), token), prefix,
            prefixDirectory, await HashFileAsync(Path.Combine(prefixDirectory, "manifest.json"), token));
    }

    /// <summary>Keep the previously reviewed ordinary-print policy only where freshly captured vocabulary still matches.</summary>
    internal static SipPortfolioSourcePolicy BuildSourcePolicy(IReadOnlyList<SipMetadataCapture> captures) =>
        SipReviewedConditionPolicy.Bind(captures);

    /// <summary>The existing collector verifies/resumes exact use-cache requests; refresh creates another immutable version.</summary>
    private static string ArchiveDirectory(BacktestRunConfig run, SipArchiveRequest request)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, Json)));
        var version = run.CachePolicy.Equals("refresh", StringComparison.OrdinalIgnoreCase) ? Guid.NewGuid().ToString("N") : "cached-v1";
        return Path.Combine(Path.GetFullPath(run.RawRoot), "execution-sip", "archives", key, version);
    }

    private static long Ns(DateTimeOffset time) => checked((time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100);
    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
    }
    private static async Task WriteAsync<T>(string path, T value, CancellationToken token)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, value, Json, token);
    }
}

