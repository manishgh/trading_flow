using System.Globalization;
using System.Text.Json;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Normalization;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;
using TradingFlow.Research.Orchestration;

namespace TradingFlow.Cli;

/// <summary>
/// Frozen intraday bar collection requests for the stocks-in-play study, run by the existing evidence-collect-normalize
/// command. Contains no download or price logic. Two inputs share one planner:
/// - evidence-plan-opening-bars: the 09:30-09:35 New York five-minute bar for every daily-eligible stock on its eligible
///   sessions and on the earlier sessions its relative-volume baseline reads (from research-daily-screen eligible.csv).
/// - evidence-plan-minute-bars: one-minute bars 09:30-16:00 New York for each session's selected stocks only (from
///   research-daily-screen ranked-daily-list.csv), so the order of entry and stop inside a five-minute bar becomes known.
///   With --timeframe 5Min --prior-sessions n: full-session five-minute bars of the selected stock-sessions and of the n
///   earlier traded sessions their ATR and opening-volume baselines read.
/// </summary>
public static class IntradayBarsCollectionPlanCommand
{
    public const string OpeningBarsName = "evidence-plan-opening-bars";
    public const string MinuteBarsName = "evidence-plan-minute-bars";
    private const string CodeVersion = "evidence-plan-intraday-bars-v1";
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeOnly RegularOpen = new(9, 30);
    private static readonly TimeOnly RegularClose = new(16, 0);

    /// <summary>One collection window: a timeframe and a New York time span requested on every listed session.</summary>
    public sealed record IntradayWindow(string Name, string Timeframe, TimeOnly From, TimeOnly To);

    /// <summary>The first five minutes, one bar per stock and session.</summary>
    public static readonly IntradayWindow OpeningFiveMinutes = new("opening-bars", "5Min", RegularOpen, new TimeOnly(9, 35));

    /// <summary>The regular session in one-minute bars; an early close simply returns fewer bars.</summary>
    public static readonly IntradayWindow RegularSessionMinutes = new("minute-bars", "1Min", RegularOpen, RegularClose);

    /// <summary>
    /// Usage: evidence-plan-opening-bars --catalog path --artifact-root path --bars dailyDatasetId --eligible eligible.csv
    /// --baseline-sessions 14 --batch n --out file.
    /// </summary>
    public static async Task<int> RunOpeningBarsAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        var options = Parse(args);
        var store = new FileSystemImmutableArtifactStore(new ImmutableArtifactStoreOptions(Path.GetFullPath(Require(options, "--artifact-root"))));
        var catalog = new SqliteEvidenceCatalog(new EvidenceCatalogOptions(Path.GetFullPath(Require(options, "--catalog")),
            EvidenceCatalogOpenMode.OpenExisting), store);
        var datasetId = Require(options, "--bars");
        var bars = await catalog.GetDatasetAsync(datasetId, cancellationToken) ?? throw new InvalidDataException($"Dataset '{datasetId}' was not found.");
        var eligible = ReadSessionSymbols(Require(options, "--eligible"), "ticker");
        var baselineSessions = Int32.Parse(Require(options, "--baseline-sessions"), CultureInfo.InvariantCulture);
        var traded = await TradedSessionsAsync(new ParquetEvidencePartitionDataReader(new EvidenceParquetCodec(), store), bars,
            eligible.Values.SelectMany(symbols => symbols).ToHashSet(StringComparer.Ordinal), cancellationToken);
        var needed = WithBaselineSessions(eligible, traded, baselineSessions);
        return await WriteAsync(options, output, CreateRequest(needed, OpeningFiveMinutes, Batch(options),
            new Dictionary<string, string> { ["daily_bars_dataset_id"] = datasetId, ["baseline_sessions"] = baselineSessions.ToString(CultureInfo.InvariantCulture) }),
            cancellationToken);
    }

    /// <summary>The regular session in five-minute bars, for the selected stock-sessions and the sessions their baselines read.</summary>
    public static readonly IntradayWindow RegularSessionFiveMinutes = new("selected-session-bars", "5Min", RegularOpen, RegularClose);

    /// <summary>
    /// The regular session in clock-aligned hourly bars (Alpaca 1Hour). Requested from 09:30, so the first bar returned is the
    /// 10:00 bar; the 14:00 bar's close is the 15:00 decision price and the 15:00 bar is the last hour. An early close returns
    /// fewer bars.
    /// </summary>
    public static readonly IntradayWindow RegularSessionHours = new("hourly-bars", "1Hour", RegularOpen, RegularClose);

    /// <summary>
    /// Usage: evidence-plan-minute-bars --ranked ranked-daily-list.csv --batch n --out file (one-minute bars of each session's
    /// selected stocks), or --eligible eligible.csv --timeframe 1Hour --catalog path --artifact-root path --bars dailyDatasetId
    /// [--from yyyy-MM-dd --to yyyy-MM-dd] --batch n --out file (hourly bars of every eligible stock-session of an eligible-list
    /// research screen; see <see cref="RunHourlyBarsAsync"/>), or with --timeframe 5Min --prior-sessions 15 --catalog path --artifact-root path --bars dailyDatasetId:
    /// full-session five-minute bars for each selected stock-session plus its 15 prior traded sessions (from the daily bars).
    /// Both record ranked_list_sha256, the SHA-256 of the ranked list the requests were made from.
    /// </summary>
    public static async Task<int> RunMinuteBarsAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        var options = Parse(args);
        if (options.ContainsKey("--eligible")) return await RunHourlyBarsAsync(options, output, cancellationToken);
        var ranked = Path.GetFullPath(Require(options, "--ranked"));
        var selected = ReadSessionSecurities(ranked);
        var attributes = new Dictionary<string, string> { ["ranked_list_sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ranked))) };
        var timeframe = options.TryGetValue("--timeframe", out var value) ? value : RegularSessionMinutes.Timeframe;
        if (timeframe == RegularSessionMinutes.Timeframe)
            return await WriteAsync(options, output, CreateRequest(selected, RegularSessionMinutes, Batch(options), attributes), cancellationToken);
        if (timeframe != RegularSessionFiveMinutes.Timeframe)
            throw new ArgumentException($"--timeframe must be {RegularSessionMinutes.Timeframe} or {RegularSessionFiveMinutes.Timeframe}.");
        var needed = await WithPriorTradedSessionsAsync(options, selected, attributes, cancellationToken);
        return await WriteAsync(options, output, CreateRequest(needed, RegularSessionFiveMinutes, Batch(options), attributes), cancellationToken);
    }

    /// <summary>
    /// Hourly bars 09:30-16:00 New York for every stock-session of an eligible-list screen (eligible.csv) inside the optional
    /// --from/--to window, batched per session. Security identifiers come from the published daily bar dataset (--bars). Records
    /// ranked_list_sha256 and eligible_list_sha256 (the SHA-256 of eligible.csv, the list identity research-screen runs match
    /// datasets on) and, when screen-manifest.json lies beside it, screen_manifest_sha256.
    /// </summary>
    private static async Task<int> RunHourlyBarsAsync(IReadOnlyDictionary<string, string> options, TextWriter output, CancellationToken cancellationToken)
    {
        if (!String.Equals(Require(options, "--timeframe"), RegularSessionHours.Timeframe, StringComparison.Ordinal))
            throw new ArgumentException($"--eligible plans {RegularSessionHours.Timeframe} bars; pass --timeframe {RegularSessionHours.Timeframe}.");
        var eligiblePath = Path.GetFullPath(Require(options, "--eligible"));
        var from = options.TryGetValue("--from", out var fromText) ? Date(fromText) : DateOnly.MinValue;
        var to = options.TryGetValue("--to", out var toText) ? Date(toText) : DateOnly.MaxValue;
        var eligible = ReadSessionSymbols(eligiblePath, "ticker").Where(pair => pair.Key >= from && pair.Key <= to)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var store = new FileSystemImmutableArtifactStore(new ImmutableArtifactStoreOptions(Path.GetFullPath(Require(options, "--artifact-root"))));
        var catalog = new SqliteEvidenceCatalog(new EvidenceCatalogOptions(Path.GetFullPath(Require(options, "--catalog")),
            EvidenceCatalogOpenMode.ReadOnly), store);
        var datasetId = Require(options, "--bars");
        var bars = await catalog.GetDatasetAsync(datasetId, cancellationToken) ?? throw new InvalidDataException($"Dataset '{datasetId}' was not found.");
        var traded = await TradedSessionsAsync(new ParquetEvidencePartitionDataReader(new EvidenceParquetCodec(), store), bars,
            eligible.Values.SelectMany(symbols => symbols).ToHashSet(StringComparer.Ordinal), cancellationToken);
        // --non-eligible-sessions true plans the complement: the sessions a position opened on an eligible day may still be held.
        var complement = options.TryGetValue("--non-eligible-sessions", out var flag) && Boolean.Parse(flag);
        var securities = complement ? NonEligibleSecurities(eligible, traded, to) : EligibleSecurities(eligible, traded);
        var attributes = HourlyAttributes(eligiblePath, datasetId);
        if (complement) attributes["session_scope"] = "non_eligible_traded_sessions";
        return await WriteAsync(options, output, CreateRequest(securities, RegularSessionHours, Batch(options), attributes), cancellationToken);
    }

    /// <summary>
    /// For each listed stock, its traded sessions from its first eligible session to <paramref name="to"/> on which it is not
    /// eligible: a position bought on an eligible day can be held on these days and needs hourly bars to exit in the last
    /// hour or to check its stop. Together with <see cref="EligibleSecurities"/> this covers every traded session once.
    /// </summary>
    public static IReadOnlyDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>> NonEligibleSecurities(
        IReadOnlyDictionary<DateOnly, IReadOnlyCollection<string>> eligible, IReadOnlyDictionary<string, (string SecurityId, DateOnly[] Sessions)> traded,
        DateOnly to)
    {
        var eligibleDays = eligible.SelectMany(pair => pair.Value.Select(symbol => (symbol, pair.Key))).ToHashSet();
        var firstEligible = eligibleDays.GroupBy(item => item.symbol, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Min(item => item.Key), StringComparer.Ordinal);
        var needed = new SortedDictionary<DateOnly, List<EvidenceSecurityIdentity>>();
        foreach (var (symbol, first) in firstEligible)
        {
            var history = traded.TryGetValue(symbol, out var found) ? found : throw new InvalidDataException($"{symbol} has no daily bars in the dataset.");
            foreach (var day in history.Sessions.Where(day => day > first && day <= to && !eligibleDays.Contains((symbol, day))))
            {
                if (!needed.TryGetValue(day, out var list)) needed[day] = list = [];
                list.Add(new EvidenceSecurityIdentity(history.SecurityId, symbol));
            }
        }
        return needed.ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<EvidenceSecurityIdentity>)pair.Value);
    }

    /// <summary>Each eligible stock-session with the stock's security identifier; a stock missing from the daily dataset fails.</summary>
    public static IReadOnlyDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>> EligibleSecurities(
        IReadOnlyDictionary<DateOnly, IReadOnlyCollection<string>> eligible, IReadOnlyDictionary<string, (string SecurityId, DateOnly[] Sessions)> traded) =>
        eligible.ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<EvidenceSecurityIdentity>)pair.Value.Select(symbol =>
            traded.TryGetValue(symbol, out var history) ? new EvidenceSecurityIdentity(history.SecurityId, symbol)
                : throw new InvalidDataException($"{symbol} has no daily bars in the dataset.")).ToArray());

    /// <summary>The list fingerprints an hourly request records: eligible.csv's SHA-256 and, when present, its screen manifest's.</summary>
    public static Dictionary<string, string> HourlyAttributes(string eligiblePath, string dailyDatasetId)
    {
        var listSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(eligiblePath)));
        var attributes = new Dictionary<string, string>
        {
            ["ranked_list_sha256"] = listSha, ["eligible_list_sha256"] = listSha, ["daily_bars_dataset_id"] = dailyDatasetId
        };
        var manifest = Path.Combine(Path.GetDirectoryName(eligiblePath)!, "screen-manifest.json");
        if (File.Exists(manifest))
            attributes["screen_manifest_sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(manifest)));
        return attributes;
    }

    /// <summary>
    /// Adds to every selected stock-session the stock's own --prior-sessions earlier traded sessions, read from the published
    /// daily bar dataset (--catalog, --artifact-root, --bars), and records the dataset and count in <paramref name="attributes"/>.
    /// </summary>
    private static async Task<IReadOnlyDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>>> WithPriorTradedSessionsAsync(
        IReadOnlyDictionary<string, string> options, IReadOnlyDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>> selected,
        Dictionary<string, string> attributes, CancellationToken cancellationToken)
    {
        var priorSessions = Int32.Parse(Require(options, "--prior-sessions"), CultureInfo.InvariantCulture);
        var store = new FileSystemImmutableArtifactStore(new ImmutableArtifactStoreOptions(Path.GetFullPath(Require(options, "--artifact-root"))));
        var catalog = new SqliteEvidenceCatalog(new EvidenceCatalogOptions(Path.GetFullPath(Require(options, "--catalog")),
            EvidenceCatalogOpenMode.OpenExisting), store);
        var datasetId = Require(options, "--bars");
        var bars = await catalog.GetDatasetAsync(datasetId, cancellationToken) ?? throw new InvalidDataException($"Dataset '{datasetId}' was not found.");
        var symbols = selected.ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<string>)pair.Value.Select(item => item.Symbol).ToArray());
        var traded = await TradedSessionsAsync(new ParquetEvidencePartitionDataReader(new EvidenceParquetCodec(), store), bars,
            symbols.Values.SelectMany(list => list).ToHashSet(StringComparer.Ordinal), cancellationToken);
        attributes["daily_bars_dataset_id"] = datasetId;
        attributes["prior_sessions"] = priorSessions.ToString(CultureInfo.InvariantCulture);
        return WithBaselineSessions(symbols, traded, priorSessions);
    }

    /// <summary>
    /// Builds the frozen request: for each New York session, the listed securities in symbol order and batches of at most
    /// <paramref name="batchSize"/>, each asking for <paramref name="window"/> as a half-open UTC range on that session.
    /// The symbol mapping date is the session itself, so a later ticker change cannot redirect an earlier request.
    /// </summary>
    public static EvidenceDatasetCollectionRequest CreateRequest(
        IReadOnlyDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>> securitiesBySession, IntradayWindow window, int batchSize,
        IReadOnlyDictionary<string, string> sourceAttributes)
    {
        if (batchSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (securitiesBySession.Count == 0 || securitiesBySession.Values.All(list => list.Count == 0))
            throw new InvalidDataException("No stock sessions to collect.");
        var requests = new List<EvidenceCollectionRequest>();
        foreach (var (session, securities) in securitiesBySession.OrderBy(pair => pair.Key))
        {
            var symbols = securities.Select(security => security.Symbol).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            foreach (var (chunk, index) in symbols.Chunk(batchSize).Select((chunk, index) => (chunk, index)))
                requests.Add(new EvidenceCollectionRequest($"{window.Name}-{session:yyyyMMdd}-{index:000}", "alpaca", "/v2/stocks/bars", chunk,
                    At(session, window.From), At(session, window.To), "sip", "raw", "USD", session,
                    new Dictionary<string, string> { ["timeframe"] = window.Timeframe }));
        }

        var first = securitiesBySession.Keys.Min();
        var last = securitiesBySession.Keys.Max();
        var identities = securitiesBySession.Values.SelectMany(list => list).DistinctBy(security => security.Symbol, StringComparer.Ordinal)
            .OrderBy(security => security.Symbol, StringComparer.Ordinal).ToArray();
        var sessions = securitiesBySession.OrderBy(pair => pair.Key)
            .Select(pair => new { session = pair.Key, symbols = pair.Value.Select(item => item.Symbol).Order(StringComparer.Ordinal).ToArray() });
        var configHash = EvidenceCanonicalJson.ComputeSha256(new { CodeVersion, window, batchSize, sessions, sourceAttributes });
        var attributes = new Dictionary<string, string>(sourceAttributes)
        {
            ["stock_sessions"] = securitiesBySession.Values.Sum(list => list.Count).ToString(CultureInfo.InvariantCulture),
            ["new_york_window"] = $"{window.From:HH\\:mm}-{window.To:HH\\:mm}"
        };
        return new EvidenceDatasetCollectionRequest(At(last, RegularClose), $"{window.Name}-as-traded-{first:yyyyMMdd}-{last:yyyyMMdd}", configHash,
            CodeVersion, "alpaca-intraday-bars-collection-v1", "alpaca-intraday-bars-partition-v1", EvidenceDatasetKind.MarketBarsAsTraded, 1,
            "alpaca-bars-normalizer-v1", $"normalized/{window.Name}-as-traded", requests, identities, attributes: attributes);
    }

    /// <summary>
    /// Adds to each eligible stock-session the stock's own preceding traded sessions that its baseline averages, so the
    /// opening bars of those earlier sessions are collected too. A stock with fewer earlier sessions gets what exists.
    /// </summary>
    public static IReadOnlyDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>> WithBaselineSessions(
        IReadOnlyDictionary<DateOnly, IReadOnlyCollection<string>> eligible,
        IReadOnlyDictionary<string, (string SecurityId, DateOnly[] Sessions)> traded, int baselineSessions)
    {
        if (baselineSessions < 1) throw new ArgumentOutOfRangeException(nameof(baselineSessions));
        var needed = new SortedDictionary<DateOnly, Dictionary<string, EvidenceSecurityIdentity>>();
        foreach (var (session, symbols) in eligible)
        {
            foreach (var symbol in symbols)
            {
                if (!traded.TryGetValue(symbol, out var history)) throw new InvalidDataException($"{symbol} has no daily bars in the dataset.");
                var index = Array.BinarySearch(history.Sessions, session);
                if (index < 0) throw new InvalidDataException($"{symbol} was eligible on {session:yyyy-MM-dd} without a daily bar that day.");
                foreach (var day in history.Sessions.Skip(Math.Max(0, index - baselineSessions)).Take(Math.Min(index, baselineSessions) + 1))
                {
                    if (!needed.TryGetValue(day, out var list)) needed[day] = list = new(StringComparer.Ordinal);
                    list[symbol] = new EvidenceSecurityIdentity(history.SecurityId, symbol);
                }
            }
        }

        return needed.ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<EvidenceSecurityIdentity>)pair.Value.Values.ToArray());
    }

    // Each wanted symbol's security identifier and traded session dates (New York), ascending, from a daily bar dataset.
    private static async Task<Dictionary<string, (string SecurityId, DateOnly[] Sessions)>> TradedSessionsAsync(
        ParquetEvidencePartitionDataReader reader, EvidenceDatasetManifest bars, IReadOnlySet<string> wanted, CancellationToken cancellationToken)
    {
        var traded = new Dictionary<string, (string, DateOnly[])>(StringComparer.Ordinal);
        await foreach (var partition in reader.ReadMarketBarPartitionsAsync(bars, cancellationToken))
            foreach (var group in partition.Where(row => wanted.Contains(row.Symbol)).GroupBy(row => row.Symbol, StringComparer.Ordinal))
                traded[group.Key] = (group.First().SecurityId,
                    group.Select(row => DateOnly.FromDateTime(row.BarStartUtc.UtcDateTime)).Distinct().Order().ToArray());
        return traded;
    }

    // Session date and symbol columns of a screen output file, grouped by session.
    private static Dictionary<DateOnly, IReadOnlyCollection<string>> ReadSessionSymbols(string path, string symbolColumn)
    {
        var rows = ReadCsv(path, "session_date", symbolColumn);
        return rows.GroupBy(row => Date(row[0])).ToDictionary(group => group.Key,
            group => (IReadOnlyCollection<string>)group.Select(row => row[1].Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray());
    }

    // Session date, symbol and security identifier of a published ranked list, grouped by session.
    private static Dictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>> ReadSessionSecurities(string path) =>
        ReadCsv(path, "session_date", "symbol", "issuer_id").GroupBy(row => Date(row[0])).ToDictionary(group => group.Key,
            group => (IReadOnlyCollection<EvidenceSecurityIdentity>)group.Select(row => new EvidenceSecurityIdentity(row[2], row[1])).ToArray());

    // Selected columns of a simple comma-separated file with a header row; fails when a column is missing.
    private static IEnumerable<string[]> ReadCsv(string path, params string[] columns)
    {
        using var lines = File.ReadLines(Path.GetFullPath(path)).GetEnumerator();
        if (!lines.MoveNext()) throw new InvalidDataException($"{path} is empty.");
        var header = lines.Current.Split(',');
        var indexes = columns.Select(column => Array.IndexOf(header, column) is var index and >= 0
            ? index : throw new InvalidDataException($"{path} has no {column} column.")).ToArray();
        var rows = new List<string[]>();
        while (lines.MoveNext())
        {
            if (lines.Current.Length == 0) continue;
            var cells = lines.Current.Split(',');
            rows.Add(indexes.Select(index => cells[index]).ToArray());
        }

        return rows;
    }

    // Writes the request as the JSON evidence-collect-normalize reads, and prints its size.
    private static async Task<int> WriteAsync(IReadOnlyDictionary<string, string> options, TextWriter output,
        EvidenceDatasetCollectionRequest request, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(Require(options, "--out"));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), cancellationToken);
        output.WriteLine(JsonSerializer.Serialize(new { Request = path, Requests = request.Requests.Count, Securities = request.Securities.Count,
            StockSessions = request.Attributes["stock_sessions"] }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    // A New York wall-clock time on a session as UTC, honouring daylight saving time.
    private static DateTimeOffset At(DateOnly session, TimeOnly time)
    {
        var local = session.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, NewYork.GetUtcOffset(local)).ToUniversalTime();
    }

    private static int Batch(IReadOnlyDictionary<string, string> options) => Int32.Parse(Require(options, "--batch"), CultureInfo.InvariantCulture);
    private static DateOnly Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

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
