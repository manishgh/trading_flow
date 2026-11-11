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
    IReadOnlyDictionary<string, string> Outputs)
{
    public const int CurrentSchemaVersion = 1;
    public const string CodeVersionName = "research-daily-screen-manifest-v1";
    public const string FileName = "screen-manifest.json";
    public const string RankedListFileName = "ranked-daily-list.csv";
    public const string RankedCountsFileName = "ranked-daily-counts.csv";

    /// <summary>The output files research-daily-screen may write, in publication order.</summary>
    public static readonly IReadOnlyList<string> OutputFileNames =
        ["eligible.csv", "daily-counts.csv", RankedListFileName, RankedCountsFileName, "ranked-daily-exclusions.csv"];

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Writes the manifest into <paramref name="folder"/> after hashing every output file present there, and returns its
    /// SHA-256. Called once all outputs are complete, so the fingerprints describe the published bytes.
    /// </summary>
    public static async Task<string> PublishAsync(string folder, ResearchScreenInputs inputs, DailyScreenRules screen,
        DailyRankingRules? ranking, DateOnly from, DateOnly to, string issuerIdentityBasis, string? fundExclusionPath,
        CancellationToken cancellationToken)
    {
        var outputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in OutputFileNames.Where(name => File.Exists(Path.Combine(folder, name))))
            outputs[name] = await Sha256FileAsync(Path.Combine(folder, name), cancellationToken);
        var exclusion = fundExclusionPath is null ? null
            : new ResearchScreenFile(Path.GetFileName(fundExclusionPath), await Sha256FileAsync(fundExclusionPath, cancellationToken));
        var manifest = new ResearchScreenManifest(CurrentSchemaVersion, CodeVersionName, from, to, inputs, ResearchScreenRuleValues.From(screen),
            ranking is null ? null : ResearchRankingRuleValues.From(ranking), issuerIdentityBasis, exclusion, outputs);
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
    private PublishedResearchScreen(string folder, string manifestSha256, ResearchScreenManifest manifest,
        IReadOnlyList<RankedSessionMember> members, IReadOnlyDictionary<DateOnly, int> qualified)
    {
        Folder = folder;
        ManifestSha256 = manifestSha256;
        Manifest = manifest;
        Members = members;
        QualifiedCounts = qualified;
    }

    public string Folder { get; }
    public string Name => Path.GetFileName(Folder);
    public string ManifestPath => Path.Combine(Folder, ResearchScreenManifest.FileName);
    public string ManifestSha256 { get; }
    public string RankedListSha256 => Manifest.Outputs[ResearchScreenManifest.RankedListFileName];
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
        if (manifest.SchemaVersion != ResearchScreenManifest.CurrentSchemaVersion || manifest.Outputs is null ||
            !manifest.Outputs.ContainsKey(ResearchScreenManifest.RankedListFileName) || manifest.RankingRules is null)
            throw new InvalidDataException($"Research screen '{Path.GetFileName(full)}' has no ranked list or an unsupported manifest schema.");
        foreach (var (name, sha) in manifest.Outputs)
        {
            var actual = ResearchScreenManifest.Sha256(File.ReadAllBytes(Path.Combine(full, name)));
            if (!actual.Equals(sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException((expectedManifestSha256 is null ? "The research screen changed after publication: "
                    : "The ranked list changed since the plan was saved: ") + $"'{name}' in research screen " +
                    $"'{Path.GetFileName(full)}' has SHA-256 {actual}, its manifest records {sha}.");
        }
        // The counts file is read only when the manifest fingerprints it; an unlisted file there is never trusted.
        return new(full, manifestSha, manifest, ReadMembers(Path.Combine(full, ResearchScreenManifest.RankedListFileName)),
            manifest.Outputs.ContainsKey(ResearchScreenManifest.RankedCountsFileName)
                ? ReadQualified(Path.Combine(full, ResearchScreenManifest.RankedCountsFileName)) : new Dictionary<DateOnly, int>());
    }

    /// <summary>Stocks in the ranked list on at least one session from <paramref name="from"/> to <paramref name="to"/> (New York dates), sorted.</summary>
    public IReadOnlyList<string> PoolSymbols(DateOnly from, DateOnly to)
    {
        RequireWindow(from, to);
        return Members.Where(member => member.Session >= from && member.Session <= to)
            .Select(member => member.Security.Symbol).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
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
