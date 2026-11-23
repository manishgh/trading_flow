using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Engine.Market;

/// <summary>
/// One stored daily options reading for one stock (step 4 of docs/design-review/2026-11-23/vix-and-options-plan.md).
/// <see cref="Reading"/> is the calculator output the Desk options strip starts from (a stock without listed options is
/// <c>ok</c> with the <c>no_liquid_options</c> label). <see cref="Status"/> is <c>failed</c> when the provider returned
/// an error, the request threw, or keys were missing; then <see cref="Failure"/> holds the reason and no values are written.
/// <see cref="RegularClose"/> is the session's 16:00 (or early) close passed as the price that picks the at-the-money
/// options; <see cref="SpotBasis"/> says whether the reading used it (<c>regular_close</c>) or the client's own price, the
/// latest trade or a Desk reading from the shared 5-minute cache (<c>provider_latest</c>).
/// </summary>
public sealed record OptionsDailyReading(
    DateOnly SessionDate,
    string Symbol,
    string Status,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    OptionsSentimentSnapshot? Reading,
    string? Failure,
    decimal? RegularClose = null,
    string? SpotBasis = null);

/// <summary>
/// Counts of one collection pass: newly stored lines by status, stocks already stored as <c>ok</c>, stocks left out because
/// they used up their attempts, and the elapsed time.
/// </summary>
public sealed record OptionsDailyCollectionResult(
    DateOnly SessionDate, int Symbols, int AlreadyStored, int Ok, int Failed, int AttemptsUsedUp, TimeSpan Elapsed, string FilePath);

/// <summary>
/// Stores one options reading per stock and New York session date as JSON lines in
/// <c>&lt;root&gt;/&lt;yyyy&gt;/&lt;yyyy-MM-dd&gt;.jsonl</c>. Requests go one at a time through the same
/// <see cref="IOptionsSentimentSource"/> as the Desk, whose client archives each raw provider response before parsing.
/// The folder is sealed research data: no strategy, gate or Desk rule reads it.
/// </summary>
public sealed class OptionsDailyCollector(IOptionsSentimentSource source, TimeProvider? clock = null, ILogger? logger = null)
{
    public const string Ok = "ok";
    public const string Failed = "failed";

    /// <summary>A stock is asked at most this many times per session; every attempt keeps its own line.</summary>
    public const int MaximumAttempts = 3;

    public const string RegularCloseBasis = "regular_close";
    public const string ProviderLatestBasis = "provider_latest";

    private static readonly JsonSerializerOptions LineOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly ILogger log = logger ?? NullLogger.Instance;

    /// <summary>The day file for <paramref name="sessionDate"/> under <paramref name="root"/>.</summary>
    public static string FilePath(string root, DateOnly sessionDate) =>
        Path.Combine(root, sessionDate.Year.ToString("0000", System.Globalization.CultureInfo.InvariantCulture),
            sessionDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl");

    /// <summary>
    /// Requests every symbol without an <c>ok</c> line in the day file that has attempts left, appending and flushing one line
    /// per answer, so a pass cut short by a restart resumes where it stopped and a failed stock is asked again on a later pass.
    /// Readers take the last line per stock. <paramref name="regularCloses"/> (optional) gives each stock's session close as
    /// the price input. Cancellation stops between symbols and keeps what was written.
    /// </summary>
    public async Task<OptionsDailyCollectionResult> CollectAsync(string root, DateOnly sessionDate, IReadOnlyCollection<string> symbols,
        IReadOnlyDictionary<string, decimal>? regularCloses, CancellationToken token)
    {
        var path = FilePath(root, sessionDate);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var (stored, failures) = ReadStoredState(path);
        var requested = symbols.Select(symbol => symbol.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        var usedUp = requested.Count(symbol => !stored.Contains(symbol) && failures.GetValueOrDefault(symbol) >= MaximumAttempts);
        var pending = requested.Where(symbol => !stored.Contains(symbol) && failures.GetValueOrDefault(symbol) < MaximumAttempts).ToArray();
        var started = Stopwatch.StartNew();
        int ok = 0, failed = 0;
        var endsTorn = EndsWithoutNewline(path);
        await using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(file, new UTF8Encoding(false));
        if (endsTorn)
            await writer.WriteLineAsync();
        foreach (var symbol in pending)
        {
            token.ThrowIfCancellationRequested();
            decimal? regularClose = regularCloses is not null && regularCloses.TryGetValue(symbol, out var close) ? close : null;
            var reading = await ReadOneAsync(sessionDate, symbol, regularClose, token);
            await writer.WriteLineAsync(JsonSerializer.Serialize(reading, LineOptions).AsMemory(), token);
            await writer.FlushAsync(token);
            if (reading.Status == Ok) ok++;
            else failed++;
        }

        var result = new OptionsDailyCollectionResult(sessionDate, requested.Length,
            requested.Count(stored.Contains), ok, failed, usedUp, started.Elapsed, path);
        log.LogInformation(
            "Options daily readings for {SessionDate}: {Ok} ok, {Failed} failed, {AlreadyStored} already stored, in {ElapsedSeconds:F0} s ({Path}).",
            sessionDate, ok, failed, result.AlreadyStored, started.Elapsed.TotalSeconds, path);
        return result;
    }

    // One request, timed on both sides. The client reports provider errors as an unavailable reading; that and a thrown
    // error both become a failed line with the reason.
    private async Task<OptionsDailyReading> ReadOneAsync(DateOnly sessionDate, string symbol, decimal? regularClose, CancellationToken token)
    {
        var requestedAt = time.GetUtcNow();
        try
        {
            var snapshot = await source.GetSnapshotAsync(symbol, regularClose, cancellationToken: token);
            if (snapshot.RegimeLabel == OptionsSentimentRegime.Unavailable)
                return new OptionsDailyReading(sessionDate, symbol, Failed, requestedAt, time.GetUtcNow(), null, snapshot.SummaryNote, regularClose);
            var basis = regularClose is not null && snapshot.SpotPrice == regularClose ? RegularCloseBasis : ProviderLatestBasis;
            return new OptionsDailyReading(sessionDate, symbol, Ok, requestedAt, time.GetUtcNow(), snapshot, null, regularClose, basis);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            log.LogWarning(exception, "Options daily reading for {Symbol} on {SessionDate} failed.", symbol, sessionDate);
            return new OptionsDailyReading(sessionDate, symbol, Failed, requestedAt, time.GetUtcNow(), null,
                $"{exception.GetType().Name}: {exception.Message}", regularClose);
        }
    }

    // True when an earlier pass stopped mid-line; the next line then starts on a fresh line instead of joining the torn one.
    private static bool EndsWithoutNewline(string path)
    {
        if (!File.Exists(path))
            return false;
        using var stream = File.OpenRead(path);
        if (stream.Length == 0)
            return false;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() != '\n';
    }

    // Stocks with an ok line, and the number of failed lines per stock. A torn last line (power loss mid-write) is ignored
    // so that stock is asked again.
    private static (HashSet<string> Ok, Dictionary<string, int> Failures) ReadStoredState(string path)
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return (symbols, failures);
        foreach (var line in File.ReadLines(path))
        {
            if (String.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                var reading = JsonSerializer.Deserialize<OptionsDailyReading>(line, LineOptions);
                if (reading?.Status == Ok)
                    symbols.Add(reading.Symbol);
                else if (reading is not null)
                    failures[reading.Symbol] = failures.GetValueOrDefault(reading.Symbol) + 1;
            }
            catch (JsonException)
            {
            }
        }
        return (symbols, failures);
    }
}
