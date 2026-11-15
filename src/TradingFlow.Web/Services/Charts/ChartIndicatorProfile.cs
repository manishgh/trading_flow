using TradingFlow.Engine.Indicators;
using TradingFlow.Contracts.V1;

namespace TradingFlow.Web.Services.Charts;

internal sealed record ChartStudy(string Id, string SourceInterval, string Session, ChartFormula? Formula)
{
    public string Name => Id.Split(':')[0];
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

    // Bound and canonicalize the key before cursor signing, cache access or source acquisition.
    public static string Normalize(string? value, string interval, string session)
    {
        if (String.IsNullOrWhiteSpace(value)) return "";
        if (value.Length > MaximumLength) throw new ArgumentException("The chart indicator selection is too long.");
        var ids = value.Split(',', StringSplitOptions.TrimEntries)
            .Select(id => id.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var studies = ids.Select(id => Resolve(id, interval, session)).ToArray();
        if (studies.Count(study => IsPrice(study.Name)) > 4 || studies.Count(IsPane) > 2 ||
            studies.GroupBy(study => (study.Name, study.SourceInterval)).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Choose up to four price studies and two additional panes, with one display per study and source.");
        }
        return String.Join(',', ids);
    }

    // Parse the already validated finite profile without consulting a provider or strategy.
    public static ChartStudy[] Read(ChartSeriesKey key) => key.Indicators.Length == 0
        ? [] : key.Indicators.Split(',').Select(id => Resolve(id, key.Interval, key.SessionView)).ToArray();

    // Daily-cross intervals are restricted to trend overlays; readout placement applies only to ATR.
    private static ChartStudy Resolve(string id, string interval, string session)
    {
        var parts = id.Split(':');
        var name = parts[0];
        var formula = ChartIndicatorMath.Find(name);
        if (formula is null && name is not ("vwap" or "rvol") || parts.Length > 2 ||
            parts.Length == 2 && !(parts[1] == "1d" && IsTrend(name) ||
                parts[1] == "readout" && name is ("atr14" or "atrpct14")))
        {
            throw new ArgumentException("Unsupported chart indicator or source.");
        }
        if (name is "rvol" or "vwap") return new ChartStudy(id, "1m", "regular", null);
        var sourceInterval = parts.Length == 2 && parts[1] == "1d" ? "1d" : interval;
        return new ChartStudy(id, sourceInterval, sourceInterval == "1d" ? "regular" : session, formula);
    }

    // Only the fixed SMA/EMA choices support a distinct daily source.
    private static bool IsTrend(string name) =>
        name.StartsWith("sma", StringComparison.Ordinal) || name.StartsWith("ema", StringComparison.Ordinal);

    // Bands with three lines are one price study.
    private static bool IsPrice(string name) => IsTrend(name) || name is "bb20" or "vwap";

    // Volume already has a base pane and RVOL is a readout. Each optional lower study uses one pane.
    private static bool IsPane(ChartStudy study) =>
        !IsPrice(study.Name) && study.Name != "rvol" && !study.Id.EndsWith(":readout", StringComparison.Ordinal);
}
