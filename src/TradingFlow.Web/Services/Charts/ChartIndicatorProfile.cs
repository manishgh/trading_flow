using TradingFlow.Engine.Indicators;
using TradingFlow.Contracts.V1;

namespace TradingFlow.Web.Services.Charts;

internal sealed record ChartStudy(string Id, string SourceInterval, string Session, ChartFormula? Formula)
{
    public string Name => Id.Split(':')[0];
    public bool IsReadout => Id.EndsWith(":readout", StringComparison.Ordinal);
    public string[] Columns => Formula?.Columns ?? ["value"];
    public string Parameters => Formula?.Parameters ?? (Name == "rvol" ? "20 regular sessions; cumulative same-time median" : "HLC3; regular session reset");
    public string Version => Formula is not null ? ChartFormula.PackageVersion
        : Name == "rvol" ? MarketEvidenceProfile.ProductionVersion : "bar-vwap-1m-v1";

    // Preserve the selected row and its provenance even when no value can be produced.
    public ChartIndicatorSeries Unavailable(string reason, string status = "unavailable") =>
        new(Id, SourceInterval, Session, "all", Version, Parameters, null, null, null,
            false, null, status, reason, Columns, [], [], [], Formula?.SeedObservations, Formula?.WarmObservations,
            [], Name == "rvol" ? MarketEvidenceProfile.DefaultLookbackSessions : null, Name == "rvol" ? 0 : null);
}

internal static class ChartIndicatorProfile
{
    private const int MaximumLength = 128;
    private const int MaximumStudies = 5;
    private const int MaximumPanes = 2;

    // Bound and canonicalize the key before cursor signing, cache access or source acquisition.
    public static string Normalize(string? value, string interval, string session)
    {
        if (String.IsNullOrWhiteSpace(value)) return "";
        if (value.Length > MaximumLength) throw new ArgumentException("The chart indicator selection is too long.");
        var ids = value.Split(',', StringSplitOptions.TrimEntries)
            .Select(id => id.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var studies = ids.Select(id => Resolve(id, interval, session)).ToArray();
        if (studies.Length > MaximumStudies || studies.Count(IsPane) > MaximumPanes ||
            studies.GroupBy(study => (study.Name, study.SourceInterval)).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Choose up to five indicators, at most two of them in their own pane, each once per interval.");
        }
        return String.Join(',', ids);
    }

    // Parse the already validated finite profile without consulting a provider or strategy.
    public static ChartStudy[] Read(ChartSeriesKey key) => key.Indicators.Length == 0
        ? [] : key.Indicators.Split(',').Select(id => Resolve(id, key.Interval, key.SessionView)).ToArray();

    // Reads "name[:interval][:readout]". Session studies have no interval of their own, OBV follows the candles, and only ATR
    // values can be shown as a number without a pane. An interval below the candle interval follows the candles.
    private static ChartStudy Resolve(string id, string interval, string session)
    {
        var parts = id.Split(':');
        var name = parts[0];
        var formula = ChartIndicatorMath.Find(name);
        var options = parts[1..];
        var readout = options.Length > 0 && options[^1] == "readout";
        if (readout) options = options[..^1];
        var isSession = name is "vwap" or "rvol";
        if (formula is null && !isSession || options.Length > 1 ||
            readout && formula?.Family is not ("atr" or "atrpct") ||
            options.Length == 1 && (isSession || formula!.Family == "obv" || !IntervalMinutes.ContainsKey(options[0])))
        {
            throw new ArgumentException("Unsupported chart indicator or source.");
        }
        if (isSession) return new ChartStudy(id, "1m", "regular", null);
        var sourceInterval = options.Length == 1 && Minutes(options[0]) > Minutes(interval) ? options[0] : interval;
        return new ChartStudy(id, sourceInterval, sourceInterval == "1d" ? "regular" : session, formula);
    }

    // The intervals an indicator may have of its own. The longest is one daily candle.
    private static readonly Dictionary<string, int> IntervalMinutes = new(StringComparer.Ordinal)
    {
        ["5m"] = 5, ["15m"] = 15, ["1h"] = 60, ["1d"] = 1_440
    };

    /// <summary>Length of one candle in minutes; a one-minute candle is the shortest.</summary>
    internal static int Minutes(string interval) => IntervalMinutes.GetValueOrDefault(interval, 1);

    // Averages, bands and VWAP share the price scale; RVOL and readouts show only a number. Everything else needs a pane.
    private static bool IsPane(ChartStudy study) =>
        study.Name is not ("vwap" or "rvol") && study.Formula?.Family is not ("sma" or "ema" or "bb") && !study.IsReadout;
}
