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
        var splits = Splits(await reader.ReadCorporateActionsAsync(actions, cancellationToken));

        var folder = Path.GetFullPath(Require(options, "--out"));
        Directory.CreateDirectory(folder);
        var counts = new SortedDictionary<DateOnly, int>();
        await using (var eligible = new StreamWriter(Path.Combine(folder, "eligible.csv")))
        {
            await eligible.WriteLineAsync("session_date,ticker,price,average_volume,average_dollar_volume,atr,history_sessions");
            await foreach (var partition in reader.ReadMarketBarPartitionsAsync(bars, cancellationToken))
            {
                foreach (var group in partition.GroupBy(row => row.Symbol, StringComparer.Ordinal))
                {
                    if (excluded.Contains(group.Key)) continue;
                    var series = group.OrderBy(row => row.BarStartUtc).Select(ToBar).ToArray();
                    foreach (var (date, values) in Screen(group.Key, series, splits.GetValueOrDefault(group.Key, []), rules, from, to))
                    {
                        counts[date] = counts.GetValueOrDefault(date) + 1;
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
        return 0;
    }

    /// <summary>Screens each session in the window on which the stock traded, using only sessions before it.</summary>
    public static IEnumerable<(DateOnly Date, DailyScreenValues Values)> Screen(string ticker, IReadOnlyList<OhlcvBar> series,
        IReadOnlyList<ShareSplit> splits, DailyScreenRules rules, DateOnly from, DateOnly to)
    {
        for (var index = 1; index < series.Count; index++)
        {
            var date = DateOnly.FromDateTime(series[index].Timestamp.UtcDateTime);
            if (date < from || date > to) continue;
            var start = Math.Max(0, index - DailyScreenRules.AtrStabilizationSessions);
            var prior = SplitBasis.AsOf(series.Skip(start).Take(index - start).ToArray(), splits, date);
            var result = DailyScreen.Evaluate(ticker, prior, series[index].Open, rules);
            if (result.Eligible) yield return (date, result.Values!);
        }
    }

    // Forward and reverse splits keyed by the symbol they applied to, effective from the ex-date.
    private static Dictionary<string, IReadOnlyList<ShareSplit>> Splits(IEnumerable<CorporateActionEvidenceRow> actions) =>
        actions.Where(action => action.ActionType is CorporateActionEvidenceType.ForwardSplit or CorporateActionEvidenceType.ReverseSplit &&
                action.PrimarySymbol is not null &&
                action.ProviderFields.TryGetValue("old_rate", out var oldRate) && action.ProviderFields.TryGetValue("new_rate", out var newRate) &&
                decimal.TryParse(oldRate, NumberStyles.Number, CultureInfo.InvariantCulture, out var oldValue) && oldValue > 0m &&
                decimal.TryParse(newRate, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            .GroupBy(action => action.PrimarySymbol!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ShareSplit>)group
                .Select(action => new ShareSplit(action.ExDate ?? action.EffectiveDate ?? action.ProcessDate,
                    decimal.Parse(action.ProviderFields["new_rate"], CultureInfo.InvariantCulture) /
                    decimal.Parse(action.ProviderFields["old_rate"], CultureInfo.InvariantCulture)))
                .Distinct().ToArray(), StringComparer.Ordinal);

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
