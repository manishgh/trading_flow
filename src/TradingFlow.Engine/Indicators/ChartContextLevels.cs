using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Indicators;

public sealed record ChartReferencePoint(DateTimeOffset StartUtc, decimal Value, DateTimeOffset AvailableAtUtc);
public sealed record ChartReferenceLevel(string Key, string Label, decimal Value, DateTimeOffset AvailableAtUtc,
    string SourceTimeframe, DateOnly? SourceDate, IReadOnlyList<ChartReferencePoint>? Points = null);

/// <summary>Projects covered display-only ranges and averages from the approved source series.</summary>
public static class ChartContextLevels
{
    // Project completed daily ranges and averages without filling a missing session.
    public static IReadOnlyList<ChartReferenceLevel> Daily(
        IReadOnlyList<ChartCompletedBar> daily, IReadOnlyList<DateOnly> scheduled, DateOnly session, DateTimeOffset firstVisible)
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

        foreach (var period in new[] { 50, 200 })
        {
            var points = MovingAverage(daily, period, firstVisible);
            if (points.Count > 0 && points[^1].StartUtc == current.Bar.Timestamp)
            {
                var last = points[^1];
                result.Add(new ChartReferenceLevel($"sma{period}", $"Daily SMA{period}", last.Value,
                    last.AvailableAtUtc, "1d", current.SessionDate, points));
            }
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
        AddVwap(result, visible, regular, source, quiet, breaks, session, asOf);
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

    // Compute the shared VWAP only on covered regular minutes from the same session and basis.
    private static void AddVwap(
        List<ChartReferenceLevel> result, IReadOnlyList<ChartCompletedBar> visible, ChartTimeRange session,
        IReadOnlyDictionary<DateTimeOffset, ChartSourceBar> source, IReadOnlyDictionary<DateTimeOffset, DateTimeOffset> quiet,
        IReadOnlySet<DateTimeOffset> breaks, DateOnly date, DateTimeOffset asOf)
    {
        var display = visible.Where(bar => bar.SessionDate == date && bar.Segment == "regular").ToArray();
        if (display.Length == 0 || ChartBarProjector.ReadCoveredMinutes(
                new ChartTimeRange(session.StartUtc, display[^1].EndUtc), source, quiet, breaks, asOf) is not { Bars.Count: > 0 } covered)
        {
            return;
        }

        var input = covered.Bars.Select(item => item.Bar).ToArray();
        var vwap = new IndicatorEngine().ComputeVwap(input);
        var points = new List<ChartReferencePoint>();
        var index = 0;
        var nextMinute = session.StartUtc;
        var observedThrough = nextMinute;
        foreach (var bar in display)
        {
            // Each point uses its verified prefix rather than inheriting a later bar's receipt.
            while (nextMinute < bar.EndUtc)
            {
                var observed = source.TryGetValue(nextMinute, out var minute) && minute.AvailableAtUtc <= asOf
                    ? minute.AvailableAtUtc : quiet[nextMinute];
                if (observed > observedThrough)
                {
                    observedThrough = observed;
                }
                nextMinute = nextMinute.AddMinutes(1);
            }
            if (bar.EndUtc > observedThrough)
            {
                observedThrough = bar.EndUtc;
            }
            while (index + 1 < input.Length && input[index + 1].Timestamp < bar.EndUtc)
            {
                index++;
            }
            if (input[index].Timestamp < bar.EndUtc && vwap[index] is { } value)
            {
                points.Add(new ChartReferencePoint(bar.Bar.Timestamp, value, observedThrough));
            }
        }
        if (points.Count > 0)
        {
            var latest = points[^1];
            result.Add(new ChartReferenceLevel("vwap", "Bar-based regular-session VWAP", latest.Value,
                latest.AvailableAtUtc, "1m", date, points));
        }
    }

    // Compute averages of completed daily prices, restarting the full warm-up on a gap or basis break.
    private static IReadOnlyList<ChartReferencePoint> MovingAverage(
        IReadOnlyList<ChartCompletedBar> bars, int period, DateTimeOffset firstVisible)
    {
        var points = new List<ChartReferencePoint>();
        for (var index = period - 1; index < bars.Count; index++)
        {
            var oldest = bars[index - period + 1];
            var current = bars[index];
            if (current.Bar.Timestamp < firstVisible || oldest.Continuity != current.Continuity)
            {
                continue;
            }
            var total = 0m;
            var available = current.AvailableAtUtc;
            for (var member = index - period + 1; member <= index; member++)
            {
                total += bars[member].Bar.Close;
                if (bars[member].AvailableAtUtc > available)
                {
                    available = bars[member].AvailableAtUtc;
                }
            }
            points.Add(new ChartReferencePoint(current.Bar.Timestamp, total / period, available));
        }
        return points;
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
