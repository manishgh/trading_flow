using System.Globalization;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Screens;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Cli;

/// <summary>
/// Thin CLI adapter over <see cref="ResearchDailyScreenPublisher"/> preserving all <c>research-daily-screen</c>
/// command-line options, diagnostic output formatting, and shared helper signatures.
/// </summary>
public static class DailyScreenCommand
{
    public const string Name = "research-daily-screen";
    public const string SplitInLookbackReason = ResearchDailyScreenPublisher.SplitInLookbackReason;
    public const string SuspectedUnrecordedSplitReason = ResearchDailyScreenPublisher.SuspectedUnrecordedSplitReason;
    public const string SplitExclusionsFileName = ResearchDailyScreenPublisher.SplitExclusionsFileName;
    public const string SuspectedSplitsFileName = ResearchDailyScreenPublisher.SuspectedSplitsFileName;
    public const string IncompleteStocksFileName = ResearchDailyScreenPublisher.IncompleteStocksFileName;

    /// <summary>Parses CLI arguments and delegates screen publication to <see cref="ResearchDailyScreenPublisher"/>.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        var options = Parse(args);
        var store = new FileSystemImmutableArtifactStore(new ImmutableArtifactStoreOptions(Path.GetFullPath(Require(options, "--artifact-root"))));
        var catalog = new SqliteEvidenceCatalog(new EvidenceCatalogOptions(Path.GetFullPath(Require(options, "--catalog")), EvidenceCatalogOpenMode.OpenExisting), store);
        var rules = new DailyScreenRules(
            Number(Require(options, "--min-price")),
            DailyScreenRules.ParsePriceBasis(Require(options, "--price-basis")),
            Int32.Parse(Require(options, "--average-sessions"), CultureInfo.InvariantCulture),
            MinAverageShareVolume: Optional(options, "--min-average-volume"),
            MinAverageDollarVolume: Optional(options, "--min-average-dollar-volume"),
            AtrPeriod: options.TryGetValue("--atr-period", out var period) ? Int32.Parse(period, CultureInfo.InvariantCulture) : null,
            MinAtr: Optional(options, "--min-atr"),
            MinHistorySessions: Int32.Parse(Require(options, "--min-history"), CultureInfo.InvariantCulture),
            MinAverageRangePercent: Optional(options, "--min-average-range-pct"));
        rules.Validate();

        var from = Date(Require(options, "--from"));
        var to = Date(Require(options, "--to"));
        var excludeFile = options.GetValueOrDefault("--exclude-symbols");
        var includeFile = options.GetValueOrDefault("--include-symbols");
        if (includeFile is not null && excludeFile is not null)
        {
            throw new ArgumentException("Use --include-symbols or --exclude-symbols, not both.");
        }

        DailyRankingRules? rankingRules = null;
        string? openingBarsId = null;
        if (options.TryGetValue("--opening-bars", out var datasetId))
        {
            openingBarsId = datasetId;
            rankingRules = new DailyRankingRules(
                Require(options, "--rank-measure"),
                Int32.Parse(Require(options, "--baseline-sessions"), CultureInfo.InvariantCulture),
                DailyRankingRules.ParseAverage(Require(options, "--baseline-average")),
                Number(Require(options, "--min-relative-volume")),
                Int32.Parse(Require(options, "--max-per-session"), CultureInfo.InvariantCulture));
            rankingRules.Validate();
        }

        int? splitLookback = options.TryGetValue("--exclude-split-lookback", out var lookbackText)
            ? Int32.Parse(lookbackText, CultureInfo.InvariantCulture)
            : null;
        DateOnly? requireCompleteFrom = options.TryGetValue("--require-complete-from", out var completeFromText)
            ? Date(completeFromText)
            : null;
        var tier = ReadTier(options);

        var request = new ResearchDailyScreenRequest(
            DailyBarsDatasetId: Require(options, "--bars"),
            CorporateActionsDatasetId: Require(options, "--actions"),
            From: from,
            To: to,
            OutputFolder: Path.GetFullPath(Require(options, "--out")),
            Rules: rules,
            ExcludedSymbolsFile: excludeFile,
            IncludedSymbolsFile: includeFile,
            SplitLookbackSessions: splitLookback,
            RequireCompleteFrom: requireCompleteFrom,
            ExchangeSessionsDatasetId: options.GetValueOrDefault("--sessions"),
            SecurityMasterDatasetId: options.GetValueOrDefault("--security-master"),
            OpeningBarsDatasetId: openingBarsId,
            RankingRules: rankingRules,
            FredVixSeries: options.GetValueOrDefault("--fred-vix"),
            LiquidityTier: tier);

        var publisher = new ResearchDailyScreenPublisher(catalog, store);
        var result = await publisher.PublishAsync(request, diagnosticOutput: output, cancellationToken: cancellationToken);

        output.WriteLine($"Screened {result.SessionCount} sessions from {result.From:yyyy-MM-dd} to {result.To:yyyy-MM-dd}; excluded {result.ExcludedSymbolCount} listed symbols; output {result.OutputFolder}.");
        foreach (var pair in result.SessionCounts.TakeLast(5))
        {
            output.WriteLine($"{pair.Key:yyyy-MM-dd}: {pair.Value} eligible");
        }

        if (result.RankedSessions is not null)
        {
            foreach (var session in result.RankedSessions.TakeLast(5))
            {
                output.WriteLine($"{session.Session:yyyy-MM-dd}: {session.QualifiedCount} at or above the minimum relative volume, {session.Members.Count} selected");
            }
        }

        if (splitLookback is not null)
        {
            output.WriteLine($"Left out {result.SplitExcludedCount} eligible stock-days whose {splitLookback}-session look-back contains a split ({SplitInLookbackReason}) and " +
                $"{result.SuspectedSplitExcludedCount} whose look-back holds an overnight jump with no recorded split ({SuspectedUnrecordedSplitReason}).");
        }

        if (result.RequireCompleteFrom is { } completeFrom)
        {
            output.WriteLine($"Complete stocks from {completeFrom:yyyy-MM-dd}: {result.CompleteStocksCount} kept, {result.IncompleteStocksCount} left out ({IncompleteStocksFileName}).");
        }

        if (tier is not null)
        {
            output.WriteLine($"Rank tier: ranks {tier.FromRank}-{tier.ToRank} by {tier.Measure} of earlier sessions.");
        }

        output.WriteLine($"Manifest {ResearchScreenManifest.FileName} SHA-256 {result.ManifestSha256}.");
        return 0;
    }

    /// <summary>Downloads and fingerprints a frozen FRED VIXCLS copy beside the screen's outputs.</summary>
    public static Task WriteFrozenVixAsync(
        string? series,
        string folder,
        DateOnly screenEnd,
        HttpClient http,
        DateTimeOffset requestedAtUtc,
        TextWriter output,
        CancellationToken cancellationToken) =>
        ResearchDailyScreenPublisher.WriteFrozenVixAsync(series, folder, screenEnd, http, requestedAtUtc, output, cancellationToken);

    /// <summary>Computes the 12-1 month return at <paramref name="index"/> from split-adjusted prior sessions.</summary>
    public static decimal? Return12Minus1(IReadOnlyList<OhlcvBar> series, IReadOnlyList<ShareSplit> splits, int index) =>
        ResearchDailyScreenPublisher.Return12Minus1(series, splits, index);

    /// <summary>Parses a 1-based inclusive rank tier ("from-to").</summary>
    public static ResearchScreenLiquidityTier ParseTier(string text) =>
        ResearchDailyScreenPublisher.ParseTier(text);

    /// <summary>Filters eligible.csv by the configured rank tier and returns updated daily counts.</summary>
    public static Task<SortedDictionary<DateOnly, int>> ApplyLiquidityTierAsync(
        string eligiblePath,
        ResearchScreenLiquidityTier tier,
        CancellationToken cancellationToken) =>
        ResearchDailyScreenPublisher.ApplyLiquidityTierAsync(eligiblePath, tier, cancellationToken);

    /// <summary>Returns session dates with large overnight jumps lacking a recorded corporate-action split.</summary>
    public static DateOnly[] UnrecordedSplitSessions(IReadOnlyList<OhlcvBar> series, IReadOnlyList<ShareSplit> splits) =>
        ResearchDailyScreenPublisher.UnrecordedSplitSessions(series, splits);

    /// <summary>Returns suspected unrecorded split events with price and volume diagnostics.</summary>
    public static IReadOnlyList<SuspectedSplitEvent> SuspectedSplitEvents(IReadOnlyList<OhlcvBar> series, IReadOnlyList<ShareSplit> splits) =>
        ResearchDailyScreenPublisher.SuspectedSplitEvents(series, splits);

    /// <summary>Returns why a symbol fails completeness over <paramref name="required"/> sessions, or null when complete.</summary>
    public static string? IncompleteReason(
        IReadOnlyCollection<DateOnly> sessions,
        SortedSet<DateOnly> required,
        IReadOnlyList<ShareSplit> splits,
        IReadOnlyCollection<DateOnly> suspects) =>
        ResearchDailyScreenPublisher.IncompleteReason(sessions, required, splits, suspects);

    /// <summary>True when a suspected unrecorded split falls inside the preceding look-back sessions.</summary>
    public static bool SuspectInLookback(IReadOnlyList<DateOnly> sessions, DateOnly date, IReadOnlyList<DateOnly> suspects, int lookbackSessions) =>
        ResearchDailyScreenPublisher.SuspectInLookback(sessions, date, suspects, lookbackSessions);

    /// <summary>True when a recorded split known on or before <paramref name="date"/> falls inside its look-back sessions.</summary>
    public static bool SplitInLookback(IReadOnlyList<DateOnly> sessions, DateOnly date, IReadOnlyList<ShareSplit> splits, int lookbackSessions) =>
        ResearchDailyScreenPublisher.SplitInLookback(sessions, date, splits, lookbackSessions);

    /// <summary>Screens each traded session in [from, to] using strictly prior sessions adjusted to that date's split basis.</summary>
    public static IEnumerable<(DateOnly Date, DailyScreenValues Values)> Screen(
        string ticker,
        IReadOnlyList<OhlcvBar> series,
        IReadOnlyList<ShareSplit> splits,
        DailyScreenRules rules,
        DateOnly from,
        DateOnly to) =>
        ResearchDailyScreenPublisher.Screen(ticker, series, splits, rules, from, to);

    // Parses optional --liquidity-tier / --rank-tier + --rank-by options.
    private static ResearchScreenLiquidityTier? ReadTier(IReadOnlyDictionary<string, string> options)
    {
        var liquidity = options.TryGetValue("--liquidity-tier", out var liquidityText);
        var ranked = options.TryGetValue("--rank-tier", out var rankText);
        var measure = options.TryGetValue("--rank-by", out var rankBy) ? rankBy : null;
        if (liquidity && ranked) throw new ArgumentException("Use --liquidity-tier or --rank-tier, not both.");
        if (measure is not null && !ranked) throw new ArgumentException("--rank-by needs --rank-tier.");
        if (liquidity) return ParseTier(liquidityText!);
        if (!ranked) return null;
        measure ??= ResearchScreenLiquidityTier.AverageDollarVolume;
        if (measure is not (ResearchScreenLiquidityTier.AverageDollarVolume or ResearchScreenLiquidityTier.Return12Minus1))
        {
            throw new ArgumentException($"--rank-by must be {ResearchScreenLiquidityTier.AverageDollarVolume} or {ResearchScreenLiquidityTier.Return12Minus1}; got '{measure}'.");
        }

        return ParseTier(rankText!) with { Measure = measure };
    }

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
            ? value
            : throw new ArgumentException($"{name} is required.");
}
