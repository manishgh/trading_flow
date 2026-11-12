using System.Globalization;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Cli;

/// <summary>
/// Applies the shared <see cref="DailyScreen"/> to a published as-traded daily bar dataset for every
/// session in a window, using only earlier sessions put on each decision date's split basis.
/// Writes per-day counts and the eligible list with each stock's measured values.
/// </summary>
public static class DailyScreenCommand
{
    public const string Name = "research-daily-screen";

    /// <summary>
    /// Usage: research-daily-screen --catalog path --artifact-root path --bars datasetId --actions datasetId
    /// --from yyyy-MM-dd --to yyyy-MM-dd --out folder [--exclude-symbols file] with the stocks-in-play rules:
    /// --min-price 5 --price-basis session_open --average-sessions 14 --min-average-volume 1000000 --atr-period 14 --min-atr 0.5 --min-history 14.
    /// Optional ranking of each session's eligible stocks (published five-minute bar dataset with the 09:30 New York bars):
    /// --opening-bars datasetId --rank-measure first_five_minute_relative_volume --baseline-sessions 14 --baseline-average mean
    /// --min-relative-volume 1 --max-per-session 20.
    /// Optional --sessions datasetId names the XNYS exchange-session dataset backtests of this screen read; it is recorded in
    /// screen-manifest.json, which is always written with every input, rule and output fingerprint. Optional
    /// --exclude-split-lookback N leaves a stock out on any day whose N-session look-back contains a split known by that day,
    /// listing each such day in split-exclusions.csv (reason split_in_lookback) and the count in the manifest.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        var options = Parse(args);
        var store = new FileSystemImmutableArtifactStore(new ImmutableArtifactStoreOptions(Path.GetFullPath(Require(options, "--artifact-root"))));
        var catalog = new SqliteEvidenceCatalog(new EvidenceCatalogOptions(Path.GetFullPath(Require(options, "--catalog")), EvidenceCatalogOpenMode.OpenExisting), store);
        var reader = new ParquetEvidencePartitionDataReader(new EvidenceParquetCodec(), store);
        var bars = await Manifest(catalog, Require(options, "--bars"), cancellationToken);
        var actions = await Manifest(catalog, Require(options, "--actions"), cancellationToken);
        var rules = new DailyScreenRules(
            Number(Require(options, "--min-price")),
            DailyScreenRules.ParsePriceBasis(Require(options, "--price-basis")),
            Int32.Parse(Require(options, "--average-sessions"), CultureInfo.InvariantCulture),
            MinAverageShareVolume: Optional(options, "--min-average-volume"),
            MinAverageDollarVolume: Optional(options, "--min-average-dollar-volume"),
            AtrPeriod: options.TryGetValue("--atr-period", out var period) ? Int32.Parse(period, CultureInfo.InvariantCulture) : null,
            MinAtr: Optional(options, "--min-atr"),
            MinHistorySessions: Int32.Parse(Require(options, "--min-history"), CultureInfo.InvariantCulture));
        rules.Validate();
        var from = Date(Require(options, "--from"));
        var to = Date(Require(options, "--to"));
        var excluded = options.TryGetValue("--exclude-symbols", out var excludeFile)
            ? File.ReadLines(Path.GetFullPath(excludeFile)).Select(line => line.Trim().ToUpperInvariant()).Where(line => line.Length > 0).ToHashSet(StringComparer.Ordinal)
            : [];
        var splits = ShareSplits.FromCorporateActions(await reader.ReadCorporateActionsAsync(actions, cancellationToken));
        var ranking = await RankingAsync(options, catalog, reader, cancellationToken);

        var folder = Path.GetFullPath(Require(options, "--out"));
        Directory.CreateDirectory(folder);
        var counts = new SortedDictionary<DateOnly, int>();
        int? splitLookback = options.TryGetValue("--exclude-split-lookback", out var lookbackText)
            ? Int32.Parse(lookbackText, CultureInfo.InvariantCulture) : null;
        var splitExcluded = 0;
        var suspectedExcluded = 0;
        var required = await RequiredSessionsAsync(options, catalog, reader, to, cancellationToken);
        var completeStocks = 0;
        var incompleteStocks = 0;
        await using (var incomplete = required is null ? null : new StreamWriter(Path.Combine(folder, IncompleteStocksFileName)))
        await using (var eligible = new StreamWriter(Path.Combine(folder, "eligible.csv")))
        await using (var splitDays = splitLookback is null ? null : new StreamWriter(Path.Combine(folder, SplitExclusionsFileName)))
        await using (var suspectList = splitLookback is null ? null : new StreamWriter(Path.Combine(folder, SuspectedSplitsFileName)))
        {
            await eligible.WriteLineAsync("session_date,ticker,price,average_volume,average_dollar_volume,atr,history_sessions");
            if (splitDays is not null) await splitDays.WriteLineAsync("session_date,ticker,reason");
            if (suspectList is not null) await suspectList.WriteLineAsync("session_date,ticker,prior_close,open,move,volume_before,volume_after");
            if (incomplete is not null) await incomplete.WriteLineAsync("ticker,reason");
            await foreach (var partition in reader.ReadMarketBarPartitionsAsync(bars, cancellationToken))
            {
                foreach (var group in partition.GroupBy(row => row.Symbol, StringComparer.Ordinal))
                {
                    if (excluded.Contains(group.Key)) continue;
                    var series = group.OrderBy(row => row.BarStartUtc).Select(ToBar).ToArray();
                    var sessions = series.Select(bar => DateOnly.FromDateTime(bar.Timestamp.UtcDateTime)).ToArray();
                    var tradedSessions = ranking is null ? [] : sessions;
                    var stockSplits = splits.GetValueOrDefault(group.Key, []);
                    var events = splitLookback is null && required is null ? [] : SuspectedSplitEvents(series, stockSplits);
                    var suspects = events.Select(item => item.Session).ToArray();
                    if (suspectList is not null)
                        foreach (var item in events)
                            await suspectList.WriteLineAsync(string.Join(',', item.Session.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), group.Key,
                            Text(item.PriorClose), Text(item.Open), Text(item.Move), Text(item.VolumeBefore), Text(item.VolumeAfter)));
                    if (required is not null)
                    {
                        if (IncompleteReason(sessions, required, stockSplits, suspects) is { } reason)
                        {
                            incompleteStocks++;
                            await incomplete!.WriteLineAsync($"{group.Key},{reason}");
                            continue;
                        }
                        completeStocks++;
                    }
                    foreach (var (date, values) in Screen(group.Key, series, stockSplits, rules, from, to))
                    {
                        if (splitLookback is { } lookback && SplitInLookback(sessions, date, stockSplits, lookback))
                        {
                            splitExcluded++;
                            await splitDays!.WriteLineAsync($"{date:yyyy-MM-dd},{group.Key},{SplitInLookbackReason}");
                            continue;
                        }
                        if (splitLookback is { } window && SuspectInLookback(sessions, date, suspects, window))
                        {
                            suspectedExcluded++;
                            await splitDays!.WriteLineAsync($"{date:yyyy-MM-dd},{group.Key},{SuspectedUnrecordedSplitReason}");
                            continue;
                        }
                        counts[date] = counts.GetValueOrDefault(date) + 1;
                        ranking?.Add(date, group.First().SecurityId, group.Key, tradedSessions);
                        await eligible.WriteLineAsync(string.Join(',', date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), values.Ticker,
                            Text(values.Price), Text(values.AverageShareVolume), Text(values.AverageDollarVolume), values.Atr is { } atr ? Text(atr) : "",
                            values.HistorySessions.ToString(CultureInfo.InvariantCulture)));
                    }
                }
            }
        }

        await File.WriteAllLinesAsync(Path.Combine(folder, "daily-counts.csv"),
            new[] { "session_date,eligible" }.Concat(counts.Select(pair => $"{pair.Key:yyyy-MM-dd},{pair.Value}")), cancellationToken);
        output.WriteLine($"Screened {counts.Count} sessions from {from:yyyy-MM-dd} to {to:yyyy-MM-dd}; excluded {excluded.Count} listed symbols; output {folder}.");
        foreach (var pair in counts.TakeLast(5)) output.WriteLine($"{pair.Key:yyyy-MM-dd}: {pair.Value} eligible");
        if (ranking is not null)
        {
            var ranked = await ranking.WriteAsync(folder, cancellationToken);
            foreach (var session in ranked.TakeLast(5))
                output.WriteLine($"{session.Session:yyyy-MM-dd}: {session.QualifiedCount} at or above the minimum relative volume, {session.Members.Count} selected");
        }
        if (splitLookback is not null)
            output.WriteLine($"Left out {splitExcluded} eligible stock-days whose {splitLookback}-session look-back contains a split ({SplitInLookbackReason}) and " +
                $"{suspectedExcluded} whose look-back holds an overnight jump with no recorded split ({SuspectedUnrecordedSplitReason}).");
        if (required is not null)
            output.WriteLine($"Complete stocks from {required.Min():yyyy-MM-dd}: {completeStocks} kept, {incompleteStocks} left out ({IncompleteStocksFileName}).");
        var manifestSha = await PublishManifestAsync(options, catalog, rules, ranking?.Rules, from, to, folder, excludeFile,
            new ResearchScreenSplitRule(splitLookback, splitExcluded, suspectedExcluded, required?.Min(),
                required is null ? null : completeStocks, required is null ? null : incompleteStocks), cancellationToken);
        output.WriteLine($"Manifest {ResearchScreenManifest.FileName} SHA-256 {manifestSha}.");
        return 0;
    }

    /// <summary>
    /// Writes screen-manifest.json after every output is complete: input dataset IDs (daily bars, corporate actions, opening
    /// bars, and the XNYS exchange-session dataset given with --sessions for backtests that read this screen), every rule
    /// value, the window, the fund-exclusion file's SHA-256 and the SHA-256 of each output file. Returns the manifest SHA-256.
    /// </summary>
    private static async Task<string> PublishManifestAsync(IReadOnlyDictionary<string, string> options, SqliteEvidenceCatalog catalog,
        DailyScreenRules rules, DailyRankingRules? ranking, DateOnly from, DateOnly to, string folder, string? excludeFile,
        ResearchScreenSplitRule splitRule, CancellationToken cancellationToken)
    {
        string? sessions = null;
        if (options.TryGetValue("--sessions", out var sessionsId))
        {
            var manifest = await Manifest(catalog, sessionsId, cancellationToken);
            if (manifest.Kind != EvidenceDatasetKind.ExchangeSessions)
                throw new InvalidDataException($"--sessions dataset '{sessionsId}' is a {manifest.Kind} dataset, not exchange sessions.");
            sessions = manifest.DatasetId;
        }
        var inputs = new ResearchScreenInputs(Require(options, "--bars"), Require(options, "--actions"),
            options.TryGetValue("--opening-bars", out var opening) ? opening : null, sessions);
        return await ResearchScreenManifest.PublishAsync(folder, inputs, rules, ranking, from, to, DailyRankingOutput.IssuerIdentityBasis,
            excludeFile is null ? null : Path.GetFullPath(excludeFile), cancellationToken, splitRule);
    }

    /// <summary>Reason written for an eligible stock-day left out because a split lies inside its look-back.</summary>
    public const string SplitInLookbackReason = "split_in_lookback";

    /// <summary>Reason written for an eligible stock-day whose look-back holds an overnight jump that looks like an unrecorded split.</summary>
    public const string SuspectedUnrecordedSplitReason = "suspected_unrecorded_split";

    /// <summary>
    /// Sessions on which the stock opened 45% or more below, or 90% or more above, the previous session's close while the
    /// corporate actions record no split with that ex-date. Such a jump looks like a split the dataset does not list, so prices
    /// before it may be on another share basis. Input bars are one stock's daily bars in session order.
    /// </summary>
    public static DateOnly[] UnrecordedSplitSessions(IReadOnlyList<OhlcvBar> series, IReadOnlyList<ShareSplit> splits) =>
        SuspectedSplitEvents(series, splits).Select(item => item.Session).ToArray();

    /// <summary>One suspected unrecorded split: the overnight move and the mean share volume of up to 20 sessions on each side.</summary>
    public sealed record SuspectedSplitEvent(DateOnly Session, decimal PriorClose, decimal Open, decimal Move, decimal VolumeBefore, decimal VolumeAfter);

    /// <summary>
    /// The jumps <see cref="UnrecordedSplitSessions"/> reports, with the values needed to tell a split from a real crash or
    /// spike afterwards: a split moves the price by a simple ratio and share volume by its inverse; a real move does neither.
    /// </summary>
    public static IReadOnlyList<SuspectedSplitEvent> SuspectedSplitEvents(IReadOnlyList<OhlcvBar> series, IReadOnlyList<ShareSplit> splits)
    {
        var recorded = splits.Select(split => split.ExDate).ToHashSet();
        var suspects = new List<SuspectedSplitEvent>();
        for (var i = 1; i < series.Count; i++)
        {
            if (series[i - 1].Close <= 0) continue;
            var move = series[i].Open / series[i - 1].Close;
            var session = DateOnly.FromDateTime(series[i].Timestamp.UtcDateTime);
            if ((move <= 0.55m || move >= 1.90m) && !recorded.Contains(session))
                suspects.Add(new(session, series[i - 1].Close, series[i].Open, move, MeanVolume(series, Math.Max(0, i - 20), i),
                    MeanVolume(series, i, Math.Min(series.Count, i + 20))));
        }
        return suspects;
    }

    // Mean share volume of bars [from, to); zero for an empty range.
    private static decimal MeanVolume(IReadOnlyList<OhlcvBar> series, int from, int to) =>
        to > from ? Enumerable.Range(from, to - from).Average(index => series[index].Volume) : 0m;

    /// <summary>Stocks left out by --require-complete-from, one row per stock with the first reason found.</summary>
    public const string IncompleteStocksFileName = "incomplete-stocks.csv";

    /// <summary>
    /// The exchange sessions every kept stock must have a bar on under --require-complete-from yyyy-MM-dd: the XNYS sessions
    /// (from the --sessions dataset, which is then required) from that date to the window end. Null when the option is absent.
    /// </summary>
    private static async Task<SortedSet<DateOnly>?> RequiredSessionsAsync(IReadOnlyDictionary<string, string> options,
        SqliteEvidenceCatalog catalog, ParquetEvidencePartitionDataReader reader, DateOnly to, CancellationToken cancellationToken)
    {
        if (!options.TryGetValue("--require-complete-from", out var fromText)) return null;
        var from = Date(fromText);
        var sessions = await Manifest(catalog, Require(options, "--sessions"), cancellationToken);
        var days = (await reader.ReadExchangeSessionsAsync(sessions, cancellationToken))
            .Select(row => row.TradeDate).Where(day => day >= from && day <= to).ToArray();
        // Both ends must be covered; a calendar starting late would silently shorten the span every stock must cover.
        if (days.Length == 0 || days.Max() < to.AddDays(-7) || days.Min() > from.AddDays(7))
            throw new InvalidDataException($"Exchange sessions dataset '{sessions.DatasetId}' does not cover {from:yyyy-MM-dd} to {to:yyyy-MM-dd}.");
        return new SortedSet<DateOnly>(days);
    }

    /// <summary>
    /// Why a stock fails the complete-stocks rule, or null when it passes: it must have a daily bar on every required session,
    /// no recorded split and no suspected unrecorded split dated within the required sessions. <paramref name="sessions"/> are
    /// the stock's own bar dates (New York session dates).
    /// </summary>
    public static string? IncompleteReason(IReadOnlyCollection<DateOnly> sessions, SortedSet<DateOnly> required,
        IReadOnlyList<ShareSplit> splits, IReadOnlyCollection<DateOnly> suspects)
    {
        var first = required.Min;
        var last = required.Max;
        var missing = required.Count - sessions.Count(day => day >= first && day <= last && required.Contains(day));
        if (missing > 0) return $"missing_sessions_{missing}";
        if (splits.Any(split => split.ExDate >= first && split.ExDate <= last)) return "split_recorded";
        if (suspects.Any(day => day >= first && day <= last)) return SuspectedUnrecordedSplitReason;
        return null;
    }

    /// <summary>Diagnostic list of every suspected unrecorded split (not read by backtests): one row per stock and jump.</summary>
    public const string SuspectedSplitsFileName = "suspected-splits.csv";

    /// <summary>
    /// True when a suspected unrecorded split falls inside the <paramref name="lookbackSessions"/> sessions before
    /// <paramref name="date"/>. The jump into <paramref name="date"/> itself is not counted: its open is not known before the
    /// open, when a prior-close screen is decided.
    /// </summary>
    public static bool SuspectInLookback(IReadOnlyList<DateOnly> sessions, DateOnly date, IReadOnlyList<DateOnly> suspects, int lookbackSessions)
    {
        if (suspects.Count == 0) return false;
        var index = sessions is DateOnly[] array ? Array.BinarySearch(array, date) : sessions.ToList().BinarySearch(date);
        if (index < 0) index = ~index;
        var first = sessions[Math.Max(0, index - lookbackSessions)];
        return suspects.Any(session => session > first && session < date);
    }

    /// <summary>File listing every stock-day left out under --exclude-split-lookback.</summary>
    public const string SplitExclusionsFileName = "split-exclusions.csv";

    /// <summary>
    /// True when a split ex-date known by <paramref name="date"/> (on or before it) falls after the first of the
    /// <paramref name="lookbackSessions"/> sessions before it: those earlier prices and volumes are on another share basis.
    /// </summary>
    public static bool SplitInLookback(IReadOnlyList<DateOnly> sessions, DateOnly date, IReadOnlyList<ShareSplit> splits, int lookbackSessions)
    {
        if (splits.Count == 0) return false;
        var index = sessions is DateOnly[] array ? Array.BinarySearch(array, date) : sessions.ToList().BinarySearch(date);
        if (index < 0) index = ~index;
        var first = sessions[Math.Max(0, index - lookbackSessions)];
        return splits.Any(split => split.ExDate > first && split.ExDate <= date);
    }

    /// <summary>
    /// Builds the optional ranking step from --opening-bars and the ranking options; every ranking option is required so the
    /// published list never depends on a code default. Null when no opening-bar dataset is given. Also reads which
    /// stock-sessions that dataset's collection completed, the only ones on which a missing opening bar proves zero volume.
    /// </summary>
    private static async Task<DailyRankingOutput?> RankingAsync(IReadOnlyDictionary<string, string> options, SqliteEvidenceCatalog catalog,
        ParquetEvidencePartitionDataReader reader, CancellationToken cancellationToken)
    {
        if (!options.TryGetValue("--opening-bars", out var datasetId)) return null;
        var rules = new DailyRankingRules(Require(options, "--rank-measure"),
            Int32.Parse(Require(options, "--baseline-sessions"), CultureInfo.InvariantCulture),
            DailyRankingRules.ParseAverage(Require(options, "--baseline-average")),
            Number(Require(options, "--min-relative-volume")),
            Int32.Parse(Require(options, "--max-per-session"), CultureInfo.InvariantCulture));
        rules.Validate();
        var openingBars = await Manifest(catalog, datasetId, cancellationToken);
        var volumes = await DailyRankingOutput.ReadOpeningVolumesAsync(reader, openingBars, cancellationToken);
        return new DailyRankingOutput(rules, volumes, await DailyRankingOutput.ReadCollectedSessionsAsync(catalog, openingBars, cancellationToken));
    }

    /// <summary>
    /// Screens each session in the window on which the stock traded, using only sessions before it. Each day reads as many
    /// earlier sessions as its rules need (<see cref="DailyScreenRules.PriorSessionsNeeded"/>), so a 200-session history
    /// minimum can pass; a fixed 100-session window made such a minimum unreachable.
    /// </summary>
    public static IEnumerable<(DateOnly Date, DailyScreenValues Values)> Screen(string ticker, IReadOnlyList<OhlcvBar> series,
        IReadOnlyList<ShareSplit> splits, DailyScreenRules rules, DateOnly from, DateOnly to)
    {
        var window = rules.PriorSessionsNeeded;
        for (var index = 1; index < series.Count; index++)
        {
            var date = DateOnly.FromDateTime(series[index].Timestamp.UtcDateTime);
            if (date < from || date > to) continue;
            var start = Math.Max(0, index - window);
            var prior = SplitBasis.AsOf(series.Skip(start).Take(index - start).ToArray(), splits, date);
            var result = DailyScreen.Evaluate(ticker, prior, series[index].Open, rules);
            if (result.Eligible) yield return (date, result.Values!);
        }
    }

    private static OhlcvBar ToBar(MarketBarEvidenceRow row) => new(row.Symbol, row.BarStartUtc, row.Timeframe,
        EvidenceFixedDecimal.FromPriceUnits(row.OpenPriceUnits), EvidenceFixedDecimal.FromPriceUnits(row.HighPriceUnits),
        EvidenceFixedDecimal.FromPriceUnits(row.LowPriceUnits), EvidenceFixedDecimal.FromPriceUnits(row.ClosePriceUnits), row.Volume);

    private static async Task<EvidenceDatasetManifest> Manifest(SqliteEvidenceCatalog catalog, string datasetId, CancellationToken cancellationToken) =>
        await catalog.GetDatasetAsync(datasetId, cancellationToken) ?? throw new InvalidDataException($"Dataset '{datasetId}' was not found.");

    private static string Text(decimal value) => decimal.Round(value, 4).ToString(CultureInfo.InvariantCulture);
    private static DateOnly Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static decimal Number(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
    private static decimal? Optional(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) ? Number(value) : null;

    private static Dictionary<string, string> Parse(string[] args)
    {
        if (args.Length % 2 != 0) throw new ArgumentException("Options must be --name value pairs.");
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2) options[args[index]] = args[index + 1];
        return options;
    }

    private static string Require(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !String.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"{name} is required.");
}
