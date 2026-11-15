using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Indicators;

public sealed record ChartReferencePoint(DateTimeOffset StartUtc, decimal Value, DateTimeOffset AvailableAtUtc);
public sealed record ChartReferenceLevel(string Key, string Label, decimal Value, DateTimeOffset AvailableAtUtc,
    string SourceTimeframe, DateOnly? SourceDate, IReadOnlyList<ChartReferencePoint>? Points = null);

/// <summary>Projects covered display-only ranges and averages from the approved source series.</summary>
public static class ChartContextLevels
{
    // Project completed daily ranges without filling a missing session.
    public static IReadOnlyList<ChartReferenceLevel> Daily(
        IReadOnlyList<ChartCompletedBar> daily, IReadOnlyList<DateOnly> scheduled, DateOnly session)
    {
        var result = new List<ChartReferenceLevel>();
        if (daily.Count == 0)
        {
            return result;
        }
        var current = daily[^1];
        var byDate = daily.ToDictionary(bar => bar.SessionDate);
        var prior = scheduled.LastOrDefault(date => date < session);
        if (byDate.TryGetValue(prior, out var priorBar) && priorBar.Continuity == current.Continuity)
        {
            AddRange(result, "prior_session", "Previous daily session", [priorBar.Bar], priorBar.AvailableAtUtc, "1d", prior);
        }

        var previousWeek = PreviousWeek(scheduled, session);
        var weekBars = previousWeek.Where(date => byDate.ContainsKey(date)).Select(date => byDate[date]).ToArray();
        if (weekBars.Length > 0 && weekBars.Length == previousWeek.Count && weekBars.All(bar => bar.Continuity == current.Continuity))
        {
            AddRange(result, "prior_week", "Previous daily week", weekBars.Select(bar => bar.Bar).ToArray(),
                weekBars.Max(bar => bar.AvailableAtUtc), "1d", weekBars[^1].SessionDate);
        }

        return result;
    }

    // Compare optional premarket and RTH ranges only after their own complete minute windows.
    public static IReadOnlyList<ChartReferenceLevel> Intraday(
        ChartBarProjector projector, IReadOnlyList<DateOnly> scheduled, DateOnly session,
        IReadOnlyDictionary<DateTimeOffset, ChartSourceBar> source,
        IReadOnlyDictionary<DateTimeOffset, DateTimeOffset> quiet,
        IReadOnlySet<DateTimeOffset> breaks, IReadOnlyList<ChartCompletedBar> visible, DateTimeOffset cutoff, DateTimeOffset asOf)
    {
        var result = new List<ChartReferenceLevel>();
        var comparisonEnd = visible[^1].EndUtc;
        var prior = scheduled.LastOrDefault(date => date < session);
        if (projector.Regular(prior) is { } previousSession)
        {
            AddCoveredRange(result, "prior_session", "Previous regular session", [previousSession], source, quiet, breaks, cutoff, asOf, prior, comparisonEnd);
        }
        var previousWeek = PreviousWeek(scheduled, session);
        var weekRanges = previousWeek.Select(projector.Regular).OfType<ChartTimeRange>().ToArray();
        if (weekRanges.Length == previousWeek.Count && weekRanges.Length > 0)
        {
            AddCoveredRange(result, "prior_week", "Previous regular week", weekRanges, source, quiet, breaks, cutoff, asOf, previousWeek[^1], comparisonEnd);
        }
        if (projector.Regular(session) is not { } regular)
        {
            return result;
        }

        var openingEnd = regular.StartUtc.AddMinutes(15);
        if (openingEnd <= regular.EndUtc)
        {
            AddCoveredRange(result, "opening_range", "First 15 minutes", [new ChartTimeRange(regular.StartUtc, openingEnd)],
                source, quiet, breaks, cutoff, asOf, session, comparisonEnd);
        }
        AddCoveredRange(result, "premarket", "Premarket", [new ChartTimeRange(ChartBarProjector.At(session, new TimeOnly(4, 0)), regular.StartUtc)],
            source, quiet, breaks, cutoff, asOf, session, comparisonEnd);
        return result;
    }

    // Query all scheduled periods, suppressing the range if one minute or a basis transition is unresolved.
    private static void AddCoveredRange(
        List<ChartReferenceLevel> result, string key, string label, IReadOnlyList<ChartTimeRange> ranges,
        IReadOnlyDictionary<DateTimeOffset, ChartSourceBar> source, IReadOnlyDictionary<DateTimeOffset, DateTimeOffset> quiet,
        IReadOnlySet<DateTimeOffset> breaks, DateTimeOffset cutoff, DateTimeOffset asOf, DateOnly date,
        DateTimeOffset comparisonEnd)
    {
        var lastRelevant = ranges[^1].EndUtc > comparisonEnd ? ranges[^1].EndUtc : comparisonEnd;
        if (ranges.Any(range => range.EndUtc > cutoff) ||
            breaks.Any(point => point >= ranges[0].StartUtc && point < lastRelevant))
        {
            return;
        }
        var covered = ranges.Select(range => ChartBarProjector.ReadCoveredMinutes(range, source, quiet, breaks, asOf)).ToArray();
        if (covered.All(window => window is not null))
        {
            AddRange(result, key, label, covered.SelectMany(window => window!.Bars).Select(item => item.Bar).ToArray(),
                covered.Max(window => window!.AvailableAtUtc), "1m", date);
        }
    }

    // Retain real prices even if no prints qualified in some proven-covered slots.
    private static void AddRange(List<ChartReferenceLevel> result, string key, string label,
        IReadOnlyList<OhlcvBar> bars, DateTimeOffset available, string timeframe, DateOnly date)
    {
        if (bars.Count == 0)
        {
            return;
        }
        result.Add(new ChartReferenceLevel(key + "_high", label + " high", bars.Max(bar => bar.High), available, timeframe, date));
        result.Add(new ChartReferenceLevel(key + "_low", label + " low", bars.Min(bar => bar.Low), available, timeframe, date));
    }

    // Define the previous week by New York Monday-to-Sunday scheduled sessions.
    private static IReadOnlyList<DateOnly> PreviousWeek(IReadOnlyList<DateOnly> scheduled, DateOnly session)
    {
        var monday = session.AddDays(-((int)session.DayOfWeek + 6) % 7);
        return scheduled.Where(date => date >= monday.AddDays(-7) && date < monday).ToArray();
    }
}
