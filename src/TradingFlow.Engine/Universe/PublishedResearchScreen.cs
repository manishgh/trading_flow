using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Engine.Universe;

/// <summary>The published inputs of a research screen: dataset IDs from the immutable evidence catalog.</summary>
public sealed record ResearchScreenInputs(
    string DailyBarsDatasetId,
    string CorporateActionsDatasetId,
    string? OpeningBarsDatasetId,
    string? ExchangeSessionsDatasetId);

/// <summary>The daily screen rules of a published screen, spelled as the command options (price basis as text).</summary>
public sealed record ResearchScreenRuleValues(decimal MinPrice, string PriceBasis, int AverageSessions, decimal? MinAverageVolume,
    decimal? MinAverageDollarVolume, int? AtrPeriod, decimal? MinAtr, int MinHistorySessions)
{
    /// <summary>Records every value of the shared daily screen rules.</summary>
    public static ResearchScreenRuleValues From(DailyScreenRules rules) => new(rules.MinPrice, DailyScreenRules.PriceBasisName(rules.PriceBasis),
        rules.AverageLookbackSessions, rules.MinAverageShareVolume, rules.MinAverageDollarVolume, rules.AtrPeriod, rules.MinAtr, rules.MinHistorySessions);
}

/// <summary>The ranking rules of a published screen, spelled as the command options (average as text).</summary>
public sealed record ResearchRankingRuleValues(string Measure, int BaselineSessions, string BaselineAverage, decimal MinRelativeVolume, int MaxPerSession)
{
    /// <summary>Records every value of the shared ranking rules.</summary>
    public static ResearchRankingRuleValues From(DailyRankingRules rules) => new(rules.Measure, rules.BaselineSessions,
        DailyRankingRules.AverageName(rules.BaselineAverage), rules.MinRelativeVolume, rules.MaxMembersPerSession);
}

/// <summary>
/// The split rule of a screen: a stock is left out on any day whose <see cref="LookbackSessions"/>-session look-back contains
/// a split known by that day (reason split_in_lookback); <see cref="ExcludedStockDays"/> counts those eligible days.
/// <see cref="SuspectedUnrecordedSplitDays"/> counts eligible days left out because the same look-back holds an overnight
/// jump (open 45% below or 90% above the previous close) with no recorded split (reason suspected_unrecorded_split).
/// </summary>
public sealed record ResearchScreenSplitRule(int? LookbackSessions, int ExcludedStockDays, int SuspectedUnrecordedSplitDays = 0,
    // Complete-stocks rule (--require-complete-from): only stocks with a bar on every exchange session from this date to the
    // window end, no recorded split and no suspected unrecorded split are screened; omitted when the rule was not used.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    DateOnly? CompleteStocksFrom = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    int? CompleteStocks = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    int? IncompleteStocks = null);

/// <summary>
/// A liquidity tier: on each session only eligible stocks ranked <see cref="FromRank"/> to <see cref="ToRank"/> (1 = highest)
/// by the screen's average dollar volume of earlier sessions are kept; ties go by symbol. Ranks use only earlier sessions.
/// </summary>
public sealed record ResearchScreenLiquidityTier(int FromRank, int ToRank, string Measure = "average_dollar_volume");

/// <summary>A file named in the manifest with its SHA-256 (lower-case hex of its exact bytes).</summary>
public sealed record ResearchScreenFile(string FileName, string Sha256);

/// <summary>
/// screen-manifest.json, written by research-daily-screen beside its outputs: schema and code version, input dataset IDs,
/// every screen and ranking rule value, the window, the fund-exclusion file with its SHA-256 and the SHA-256 of every output
/// file. Holds no clock reading, so identical inputs publish identical bytes.
/// </summary>
public sealed record ResearchScreenManifest(
    int SchemaVersion,
    string CodeVersion,
    DateOnly From,
    DateOnly To,
    ResearchScreenInputs Inputs,
    ResearchScreenRuleValues ScreenRules,
    ResearchRankingRuleValues? RankingRules,
    string IssuerIdentityBasis,
    ResearchScreenFile? FundExclusion,
    IReadOnlyDictionary<string, string> Outputs,
    // The split look-back rule and how many eligible stock-days it left out; omitted when the rule was not used.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ResearchScreenSplitRule? SplitRule = null,
    // The liquidity tier applied after the screen rules (ranks by 20-day average dollar volume); omitted when not used.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ResearchScreenLiquidityTier? LiquidityTier = null,
    // The fixed symbol list a screen was limited to (--include-symbols), with its SHA-256; omitted when not used.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ResearchScreenFile? IncludedSymbols = null)
{
    public const int CurrentSchemaVersion = 1;
    public const string CodeVersionName = "research-daily-screen-manifest-v1";
    public const string FileName = "screen-manifest.json";
    public const string RankedListFileName = "ranked-daily-list.csv";
    public const string RankedCountsFileName = "ranked-daily-counts.csv";

    /// <summary>The output files research-daily-screen may write, in publication order.</summary>
    public static readonly IReadOnlyList<string> OutputFileNames =
        ["eligible.csv", "daily-counts.csv", RankedListFileName, RankedCountsFileName, "ranked-daily-exclusions.csv", "split-exclusions.csv",
            "incomplete-stocks.csv"];

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Writes the manifest into <paramref name="folder"/> after hashing every output file present there, and returns its
    /// SHA-256. Called once all outputs are complete, so the fingerprints describe the published bytes. A split rule is
    /// recorded only when it was applied.
    /// </summary>
    public static async Task<string> PublishAsync(string folder, ResearchScreenInputs inputs, DailyScreenRules screen,
        DailyRankingRules? ranking, DateOnly from, DateOnly to, string issuerIdentityBasis, string? fundExclusionPath,
        CancellationToken cancellationToken, ResearchScreenSplitRule? splitRule = null, ResearchScreenLiquidityTier? liquidityTier = null,
        string? includedSymbolsPath = null)
    {
        var outputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in OutputFileNames.Where(name => File.Exists(Path.Combine(folder, name))))
            outputs[name] = await Sha256FileAsync(Path.Combine(folder, name), cancellationToken);
        var exclusion = fundExclusionPath is null ? null
            : new ResearchScreenFile(Path.GetFileName(fundExclusionPath), await Sha256FileAsync(fundExclusionPath, cancellationToken));
        var manifest = new ResearchScreenManifest(CurrentSchemaVersion, CodeVersionName, from, to, inputs, ResearchScreenRuleValues.From(screen),
            ranking is null ? null : ResearchRankingRuleValues.From(ranking), issuerIdentityBasis, exclusion, outputs,
            splitRule is { LookbackSessions: not null } ? splitRule : null, liquidityTier,
            includedSymbolsPath is null ? null
                : new ResearchScreenFile(Path.GetFileName(includedSymbolsPath), await Sha256FileAsync(includedSymbolsPath, cancellationToken)));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        await File.WriteAllBytesAsync(Path.Combine(folder, FileName), bytes, cancellationToken);
        return Sha256(bytes);
    }

    /// <summary>Lower-case hex SHA-256 of a file's exact bytes, read as a stream.</summary>
    public static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    /// <summary>Lower-case hex SHA-256 of bytes.</summary>
    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>
/// A published research screen read back for backtests: the manifest, its SHA-256 and the ranked list. Every output named in
/// the manifest is re-hashed and must match, so a changed byte anywhere refuses the screen. Only reads files.
/// </summary>
public sealed class PublishedResearchScreen
{
    private const string EligibleFileName = "eligible.csv";
    private readonly IReadOnlyDictionary<string, HashSet<DateOnly>> eligibleDays;

    private PublishedResearchScreen(string folder, string manifestSha256, ResearchScreenManifest manifest,
        IReadOnlyList<RankedSessionMember> members, IReadOnlyDictionary<DateOnly, int> qualified,
        IReadOnlyDictionary<string, HashSet<DateOnly>> eligibleDays)
    {
        Folder = folder;
        ManifestSha256 = manifestSha256;
        Manifest = manifest;
        Members = members;
        QualifiedCounts = qualified;
        this.eligibleDays = eligibleDays;
    }

    public string Folder { get; }
    public string Name => Path.GetFileName(Folder);
    public string ManifestPath => Path.Combine(Folder, ResearchScreenManifest.FileName);
    public string ManifestSha256 { get; }

    /// <summary>True when the screen ranks each session (stocks in play); otherwise members are each day's eligible stocks.</summary>
    public bool IsRanked => Manifest.RankingRules is not null;

    /// <summary>The file that decides membership: the ranked list, or eligible.csv for a screen without a ranking.</summary>
    public string MembershipListFileName => IsRanked ? ResearchScreenManifest.RankedListFileName : EligibleFileName;

    /// <summary>SHA-256 of the membership list (the ranked list, or eligible.csv when the screen does not rank).</summary>
    public string RankedListSha256 => Manifest.Outputs[MembershipListFileName];
    public ResearchScreenManifest Manifest { get; }

    /// <summary>Every row of the ranked list, in file order (session, then rank).</summary>
    public IReadOnlyList<RankedSessionMember> Members { get; }

    /// <summary>Per session, how many stocks met the minimum relative volume (from ranked-daily-counts.csv).</summary>
    public IReadOnlyDictionary<DateOnly, int> QualifiedCounts { get; }

    /// <summary>True when the folder holds a manifest; reading it may still fail its fingerprint checks.</summary>
    public static bool IsPublished(string folder) => File.Exists(Path.Combine(folder, ResearchScreenManifest.FileName));

    /// <summary>
    /// Reads and verifies a published screen. Refuses a manifest whose SHA-256 differs from <paramref name="expectedManifestSha256"/>
    /// (when given), an output whose bytes differ from the manifest, and a screen without a ranked list.
    /// </summary>
    public static PublishedResearchScreen Read(string folder, string? expectedManifestSha256 = null)
    {
        var full = Path.GetFullPath(folder);
        var manifestBytes = File.ReadAllBytes(Path.Combine(full, ResearchScreenManifest.FileName));
        var manifestSha = ResearchScreenManifest.Sha256(manifestBytes);
        if (expectedManifestSha256 is not null && !manifestSha.Equals(expectedManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The ranked list changed since the plan was saved: research screen '{Path.GetFileName(full)}' " +
                $"now has manifest SHA-256 {manifestSha}, the plan recorded {expectedManifestSha256}.");
        var manifest = JsonSerializer.Deserialize<ResearchScreenManifest>(manifestBytes, ResearchScreenManifest.Json)
            ?? throw new InvalidDataException($"{ResearchScreenManifest.FileName} in '{full}' is empty.");
        var listFile = manifest.RankingRules is null ? EligibleFileName : ResearchScreenManifest.RankedListFileName;
        if (manifest.SchemaVersion != ResearchScreenManifest.CurrentSchemaVersion || manifest.Outputs is null || !manifest.Outputs.ContainsKey(listFile))
            throw new InvalidDataException($"Research screen '{Path.GetFileName(full)}' has no {listFile} or an unsupported manifest schema.");
        foreach (var (name, sha) in manifest.Outputs)
        {
            var actual = ResearchScreenManifest.Sha256(File.ReadAllBytes(Path.Combine(full, name)));
            if (!actual.Equals(sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException((expectedManifestSha256 is null ? "The research screen changed after publication: "
                    : "The ranked list changed since the plan was saved: ") + $"'{name}' in research screen " +
                    $"'{Path.GetFileName(full)}' has SHA-256 {actual}, its manifest records {sha}.");
        }
        if (manifest.RankingRules is null)
            return new(full, manifestSha, manifest, [], new Dictionary<DateOnly, int>(), ReadEligibleDays(Path.Combine(full, EligibleFileName)));
        // The counts file is read only when the manifest fingerprints it; an unlisted file there is never trusted.
        return new(full, manifestSha, manifest, ReadMembers(Path.Combine(full, ResearchScreenManifest.RankedListFileName)),
            manifest.Outputs.ContainsKey(ResearchScreenManifest.RankedCountsFileName)
                ? ReadQualified(Path.Combine(full, ResearchScreenManifest.RankedCountsFileName)) : new Dictionary<DateOnly, int>(),
            new Dictionary<string, HashSet<DateOnly>>());
    }

    /// <summary>Stocks selected (ranked, or eligible when the screen does not rank) on at least one session from <paramref name="from"/> to <paramref name="to"/> (New York dates), sorted.</summary>
    /// <summary>
    /// For an eligible-list screen: each pool stock's first eligible session in [from, to], and the number of the screen's
    /// sessions from that day through <paramref name="to"/> summed over the pool (the stock-sessions of daily bars a run
    /// requests after warm-up, since each stock is requested only from its first eligible day). The screen's sessions are the
    /// dates on which it lists at least one stock. Empty and zero for a ranked screen.
    /// </summary>
    public (IReadOnlyDictionary<string, DateOnly> FirstEligible, long StockSessions) EligibleSpan(DateOnly from, DateOnly to)
    {
        RequireWindow(from, to);
        if (IsRanked) return (new Dictionary<string, DateOnly>(), 0);
        var sessions = eligibleDays.Values.SelectMany(days => days).Where(day => day >= from && day <= to).Distinct().Order().ToArray();
        var first = eligibleDays.Select(pair => (pair.Key, Days: pair.Value.Where(day => day >= from && day <= to).ToArray()))
            .Where(item => item.Days.Length > 0).ToDictionary(item => item.Key, item => item.Days.Min(), StringComparer.Ordinal);
        var total = first.Values.Sum(day => (long)(sessions.Length - Array.BinarySearch(sessions, day)));
        return (first, total);
    }

    public IReadOnlyList<string> PoolSymbols(DateOnly from, DateOnly to)
    {
        RequireWindow(from, to);
        var symbols = IsRanked
            ? Members.Where(member => member.Session >= from && member.Session <= to).Select(member => member.Security.Symbol)
            : eligibleDays.Where(pair => pair.Value.Any(day => day >= from && day <= to)).Select(pair => pair.Key);
        return symbols.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Refuses a window whose New York dates fall outside the screen's published From..To: sessions there were never ranked,
    /// so reading them as sessions without members would silently drop every stock.
    /// </summary>
    public void RequireWindow(DateOnly from, DateOnly to)
    {
        if (from < Manifest.From || to > Manifest.To)
            throw new InvalidOperationException($"The window {from:yyyy-MM-dd} to {to:yyyy-MM-dd} falls outside research screen '{Name}', " +
                $"which ranked sessions {Manifest.From:yyyy-MM-dd} to {Manifest.To:yyyy-MM-dd} only.");
    }

    /// <summary>
    /// The same per-session membership the in-run ranking builds: each session's ranked members from <paramref name="from"/>
    /// to <paramref name="to"/>, the published ranked lists, and membership known only from 09:35 New York. Exclusion reasons
    /// stay in the screen's ranked-daily-exclusions.csv, identified by the manifest SHA-256, rather than being copied.
    /// </summary>
    public UniverseMembership ToMembership(DateOnly from, DateOnly to)
    {
        RequireWindow(from, to);
        if (!IsRanked) return EligibleMembership(from, to);
        var window = Members.Where(member => member.Session >= from && member.Session <= to).ToArray();
        var days = window.GroupBy(member => member.Security.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(member => member.Session).ToHashSet(), StringComparer.OrdinalIgnoreCase);
        // Every ranked session, including those where no stock met the minimum (they appear only in the counts file).
        var bySession = window.ToLookup(member => member.Session);
        var rankings = QualifiedCounts.Keys.Where(day => day >= from && day <= to).Union(bySession.Select(group => group.Key)).Order()
            .Select(day => new SessionRanking(day, bySession[day].OrderBy(member => member.Rank).ToArray(),
                QualifiedCounts.GetValueOrDefault(day, bySession[day].Count()), [])).ToArray();
        return new UniverseMembership(days)
        {
            SessionRankings = rankings,
            KnownFromNewYorkTime = HistoricalScreenerUniverseProvider.OpeningVolumeKnownAt
        };
    }

    /// <summary>
    /// Membership of a screen without a ranking: each session's eligible stocks from eligible.csv. It is decided from earlier
    /// sessions (and the session open when the rules use it), so no 09:35 restriction applies; the runner checks the price
    /// basis against the strategy's fills.
    /// </summary>
    private UniverseMembership EligibleMembership(DateOnly from, DateOnly to) => new(eligibleDays
        .Select(pair => (pair.Key, Days: pair.Value.Where(day => day >= from && day <= to).ToHashSet())).Where(item => item.Days.Count > 0)
        .ToDictionary(item => item.Key, item => item.Days, StringComparer.OrdinalIgnoreCase));

    /// <summary>Streams eligible.csv (session_date, ticker, ...) into each stock's eligible sessions; a malformed row fails.</summary>
    private static IReadOnlyDictionary<string, HashSet<DateOnly>> ReadEligibleDays(string path)
    {
        using var lines = File.ReadLines(path).GetEnumerator();
        if (!lines.MoveNext()) throw new InvalidDataException($"{path} is empty.");
        var header = lines.Current.Split(',');
        var (dateIndex, tickerIndex) = (Array.IndexOf(header, "session_date"), Array.IndexOf(header, "ticker"));
        if (dateIndex < 0 || tickerIndex < 0) throw new InvalidDataException($"{path} has no session_date or ticker column.");
        var days = new Dictionary<string, HashSet<DateOnly>>(StringComparer.OrdinalIgnoreCase);
        while (lines.MoveNext())
        {
            if (lines.Current.Length == 0) continue;
            var cells = lines.Current.Split(',');
            if (!days.TryGetValue(cells[tickerIndex], out var set)) days[cells[tickerIndex]] = set = [];
            set.Add(Date(cells[dateIndex]));
        }
        return days;
    }

    /// <summary>Parses ranked-daily-list.csv by its header names; a missing column or malformed value fails.</summary>
    private static IReadOnlyList<RankedSessionMember> ReadMembers(string path)
    {
        var rows = ReadCsv(path, "session_date", "rank", "symbol", "issuer_id", "opening_volume", "baseline_volume", "relative_volume",
            "proven_zero_baseline_sessions");
        return rows.Select(row => new RankedSessionMember(Date(row[0]), Int(row[1]), new UniverseSecurityIdentity(row[2], row[3], row[3]),
            Number(row[4]), Number(row[5]), Number(row[6]), Int(row[7]))).ToArray();
    }

    /// <summary>Per-session qualified counts; an absent counts file leaves the counts empty.</summary>
    private static IReadOnlyDictionary<DateOnly, int> ReadQualified(string path) => !File.Exists(path)
        ? new Dictionary<DateOnly, int>()
        : ReadCsv(path, "session_date", "qualified").ToDictionary(row => Date(row[0]), row => Int(row[1]));

    /// <summary>Selected columns of a header-first comma-separated file.</summary>
    private static IEnumerable<string[]> ReadCsv(string path, params string[] columns)
    {
        using var lines = File.ReadLines(path).GetEnumerator();
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

    private static DateOnly Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static int Int(string value) => Int32.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static decimal Number(string value) => Decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
}
