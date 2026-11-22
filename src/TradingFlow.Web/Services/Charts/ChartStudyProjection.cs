using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Web.Services.Charts;

internal sealed record MergedChartSource(
    IReadOnlyDictionary<DateTimeOffset, ChartSourceBar> Bars,
    IReadOnlyDictionary<DateTimeOffset, DateTimeOffset> Quiet,
    IReadOnlySet<DateTimeOffset> BasisBreaks);

internal static class ChartStudyProjection
{
    // Materialize only requested source intervals and families. The resulting columns are clipped to this chart page.
    public static IReadOnlyList<ChartIndicatorSeries> Calculate(ChartSeriesKey key, ChartStudy[] studies,
        ChartHistoryBatch history, ChartHistoryBatch? dailyHistory, MergedChartSource merged,
        IReadOnlyList<ChartCompletedBar> visible, ChartHistoryBatch[] warmHistory,
        DateTimeOffset cutoff, DateTimeOffset evidenceCutoff, DateTimeOffset asOf, string revision, CancellationToken token)
    {
        var isDaily = key.Interval == "1d";
        var first = visible[0].Bar.Timestamp;
        var primary = new Lazy<IReadOnlyList<ChartCompletedBar>>(() => isDaily
            ? history.Projector.Daily(merged.Bars.Values, merged.BasisBreaks, cutoff, asOf)
            : history.Projector.Intraday(merged.Bars, merged.Quiet, merged.BasisBreaks, key.Interval,
                key.SessionView == "extended", cutoff, asOf));
        var daily = new Lazy<IReadOnlyList<ChartCompletedBar>>(() => dailyHistory is null ? [] :
            dailyHistory.Projector.Daily(dailyHistory.Bars.Values, dailyHistory.BasisBreaks, cutoff, asOf));
        var rth = new Lazy<RegularInputs>(() => new RegularInputs(history.Projector, merged, warmHistory,
            visible, cutoff, asOf, studies.Any(study => study.Name == "vwap"),
            studies.Any(study => study.Name == "rvol"), token));
        // One resampled series per longer interval, shared by every indicator that uses it.
        var longer = new Dictionary<string, IReadOnlyList<ChartCompletedBar>>(StringComparer.Ordinal);
        var result = new List<ChartIndicatorSeries>(studies.Length);
        foreach (var study in studies)
        {
            token.ThrowIfCancellationRequested();
            if (study.Formula is not { } formula)
            {
                result.Add(isDaily ? study.Unavailable("This study requires completed regular-session intraday minutes.") :
                    ProjectRegular(study, rth.Value, visible, evidenceCutoff, revision));
                continue;
            }
            if (!isDaily && study.SourceInterval != key.Interval && study.SourceInterval != "1d")
            {
                if (!longer.TryGetValue(study.SourceInterval, out var longerBars))
                    longer[study.SourceInterval] = longerBars = history.Projector.Intraday(merged.Bars, merged.Quiet, merged.BasisBreaks,
                        study.SourceInterval, key.SessionView == "extended", cutoff, asOf);
                var minutes = ChartIndicatorProfile.Minutes(study.SourceInterval);
                var values = ChartIndicatorMath.Project(formula, longerBars, history.Projector, history.Sessions[0], false,
                    first.AddDays(-5), token, minutes);
                result.Add(JoinLonger(study, history.Projector, longerBars, values, visible, merged.BasisBreaks, minutes,
                    key.SessionView == "extended", evidenceCutoff, revision));
                continue;
            }
            var cross = !isDaily && study.SourceInterval == "1d";
            var batch = cross ? dailyHistory : history;
            if (batch is null || batch.Sessions.Count == 0)
            {
                result.Add(study.Unavailable("Compatible completed daily history is unavailable."));
                continue;
            }
            var bars = cross ? daily.Value : primary.Value;
            // A daily average can first become eligible during the next displayed exchange session.
            var calculationStart = cross ? first.AddDays(-10) : first;
            var calculated = ChartIndicatorMath.Project(formula, bars, batch.Projector, batch.Sessions[0],
                isDaily || cross, calculationStart, token, ChartIndicatorProfile.Minutes(study.SourceInterval));
            result.Add(cross
                ? JoinDaily(study, batch.Projector, bars, calculated, visible, merged.BasisBreaks, evidenceCutoff, revision)
                : JoinPrimary(study, bars, calculated, visible, evidenceCutoff, revision));
        }
        return result;
    }

    // A same-interval value belongs to the completed observation that produced it, with its actual dependency receipt.
    private static ChartIndicatorSeries JoinPrimary(ChartStudy study, IReadOnlyList<ChartCompletedBar> source,
        ChartCalculatedIndicator calculated, IReadOnlyList<ChartCompletedBar> visible, DateTimeOffset evidenceCutoff, string revision)
    {
        var builder = new SeriesBuilder(study, visible, evidenceCutoff);
        var input = 0;
        for (var index = 0; index < visible.Count; index++)
        {
            var candle = visible[index];
            while (input < source.Count && source[input].Bar.Timestamp < candle.Bar.Timestamp) input++;
            if (input < source.Count && source[input].Bar.Timestamp == candle.Bar.Timestamp &&
                calculated.Clocks[input] is { } clock)
                builder.Add(index, clock, calculated.Columns, input);
        }
        return builder.Finish(source.LastOrDefault()?.EndUtc, revision, WarmupReason(study, source));
    }

    // Gives each candle the value of the latest longer candle completed by the candle's own end: one that ended less than one
    // longer interval ago in the same session segment, or the previous session's last one until the first of today completes.
    // A price-basis break between the two leaves a gap.
    private static ChartIndicatorSeries JoinLonger(ChartStudy study, ChartBarProjector projector,
        IReadOnlyList<ChartCompletedBar> source, ChartCalculatedIndicator calculated, IReadOnlyList<ChartCompletedBar> visible,
        IReadOnlySet<DateTimeOffset> breaks, int minutes, bool extended, DateTimeOffset evidenceCutoff, string revision)
    {
        var builder = new SeriesBuilder(study, visible, evidenceCutoff);
        var length = TimeSpan.FromMinutes(minutes);
        var latest = -1;
        for (var index = 0; index < visible.Count; index++)
        {
            var candle = visible[index];
            while (latest + 1 < source.Count && source[latest + 1].EndUtc <= candle.EndUtc) latest++;
            if (latest < 0 || calculated.Clocks[latest] is not { } clock) continue;
            var input = source[latest];
            var isCurrent = input.SessionDate == candle.SessionDate
                ? candle.EndUtc - input.EndUtc < length
                : input.SessionDate == projector.PriorTradingDates(candle.SessionDate, 1).LastOrDefault() &&
                    candle.EndUtc - SessionStart(projector, candle.SessionDate, extended) < length;
            if (isCurrent && !breaks.Any(point => point > input.Bar.Timestamp && point <= candle.Bar.Timestamp) && IsDrawable(clock, candle.AvailableAtUtc))
                builder.Add(index, clock, calculated.Columns, latest);
        }
        return builder.Finish(source.LastOrDefault()?.EndUtc, revision, WarmupReason(study, source));
    }

    // The first minute of the displayed session: 04:00 New York with extended hours, else the regular open.
    private static DateTimeOffset SessionStart(ChartBarProjector projector, DateOnly date, bool extended) =>
        extended ? ChartBarProjector.At(date, new TimeOnly(4, 0)) : projector.Regular(date)?.StartUtc ?? DateTimeOffset.MinValue;

    // A recorded receipt is respected: the value cannot be drawn before it arrived. Values computed from history that was
    // downloaded later are drawn at their scheduled completion and marked as reconstructed, like the candles themselves.
    private static bool IsDrawable(ChartIndicatorClock clock, DateTimeOffset drawnAt) =>
        clock.Reconstructed || clock.AvailableAtUtc <= drawnAt;

    // The latest scheduled daily close governs the join. A recorded late receipt cannot be drawn before its real observation.
    private static ChartIndicatorSeries JoinDaily(ChartStudy study, ChartBarProjector projector,
        IReadOnlyList<ChartCompletedBar> source, ChartCalculatedIndicator calculated,
        IReadOnlyList<ChartCompletedBar> visible, IReadOnlySet<DateTimeOffset> breaks, DateTimeOffset evidenceCutoff, string revision)
    {
        var builder = new SeriesBuilder(study, visible, evidenceCutoff);
        var byDate = source.Select((bar, index) => (bar.SessionDate, index)).ToDictionary(pair => pair.SessionDate, pair => pair.index);
        var previousBreak = breaks.Where(stamp => stamp < visible[^1].EndUtc).DefaultIfEmpty().Min();
        var reason = WarmupReason(study, source);
        if (previousBreak != default)
            reason = "The daily price basis could not be joined to the intraday candles across a source break.";
        else if (calculated.Clocks.Any(clock => clock is not null))
            reason = "Daily values are unavailable on earlier intraday candles until the completed source was actually observed.";

        var previous = default(DateOnly);
        var session = default(DateOnly);
        for (var index = 0; index < visible.Count; index++)
        {
            var candle = visible[index];
            if (candle.Bar.Timestamp >= previousBreak && previousBreak != default) continue;
            if (session != candle.SessionDate)
            {
                session = candle.SessionDate;
                previous = projector.PriorTradingDates(session, 1).LastOrDefault();
            }
            var date = projector.Regular(session)?.EndUtc <= candle.Bar.Timestamp ? session : previous;
            if (byDate.TryGetValue(date, out var input) && calculated.Clocks[input] is { } clock &&
                IsDrawable(clock, candle.Bar.Timestamp))
                builder.Add(index, clock, calculated.Columns, input);
        }
        return builder.Finish(source.LastOrDefault()?.EndUtc, revision, reason);
    }

    // Only completed RTH candles are eligible for a bar-based VWAP or matching-time cumulative volume.
    private static ChartIndicatorSeries ProjectRegular(ChartStudy study, RegularInputs input,
        IReadOnlyList<ChartCompletedBar> visible, DateTimeOffset evidenceCutoff, string revision)
    {
        var builder = new SeriesBuilder(study, visible, evidenceCutoff);
        var relative = study.Name == "rvol";
        int? sampleCount = relative ? 0 : null;
        DateTimeOffset? lastRth = null;
        for (var index = 0; index < visible.Count; index++)
        {
            var candle = visible[index];
            if (candle.Segment != "regular" || !input.Prefixes.TryGetValue(candle.SessionDate, out var prefix)) continue;
            var offset = (int)(candle.EndUtc - prefix.StartUtc).TotalMinutes - 1;
            if (offset < 0 || offset >= prefix.Volume.Length) continue;
            lastRth = candle.EndUtc;
            var clock = new ChartIndicatorClock(prefix.Available[offset], prefix.StartUtc, prefix.Reconstructed[offset]);
            if (study.Name == "vwap")
            {
                if (prefix.Vwap?[offset] is { } vwap) builder.Add(index, clock, (double)vwap);
                continue;
            }
            var match = input.ReadRelativeVolume(candle.SessionDate, prefix, offset, clock);
            sampleCount = match.Count;
            if (match.Value is { } value) builder.Add(index, match.Clock, (double)value);
        }
        var reason = relative ? "Insufficient baseline. Need 20 verified compatible prior sessions with a positive median at the same regular-session time."
            : "VWAP needs a covered regular-session prefix and positive cumulative volume.";
        return builder.Finish(lastRth, revision, reason, sampleCount);
    }

    // Says how many unbroken completed candles the indicator needs and how many there are, instead of showing a zero.
    private static string WarmupReason(ChartStudy study, IReadOnlyList<ChartCompletedBar> source)
    {
        var needed = study.Formula!.WarmObservations;
        var available = 0;
        for (var index = source.Count - 1; index >= 0 && source[index].Continuity == source[^1].Continuity; index--) available++;
        var text = available < needed
            ? $"Needs {needed} completed {study.SourceInterval} candles without a gap; {available} are available."
            : $"Earlier candles have fewer than {needed} completed {study.SourceInterval} candles before them.";
        return study.Formula.Family == "adx" ? text + " ADX also needs price movement in one direction." : text;
    }

    private sealed class SeriesBuilder(ChartStudy study, IReadOnlyList<ChartCompletedBar> visible, DateTimeOffset evidenceCutoff)
    {
        private readonly double?[][] samples = study.Columns.Select(_ => new double?[visible.Count]).ToArray();
        private readonly List<int> segments = [];
        private readonly List<DateTimeOffset?> anchors = [];
        private readonly List<long[]> availability = [];
        private int previous = -2;
        private int count;
        private ChartIndicatorClock? last;
        private bool reconstructed;

        // Retain a new line segment after a gap, basis boundary or fixed seed switch.
        private void Observe(int index, ChartIndicatorClock clock)
        {
            if (index != previous + 1 || visible[index].Continuity != visible[previous].Continuity ||
                study.Formula?.Recursive != false && last?.AnchorUtc != clock.AnchorUtc)
            {
                segments.Add(index);
                anchors.Add(study.Formula?.Recursive == false ? null : clock.AnchorUtc);
            }
            if (clock.AvailableAtUtc != visible[index].AvailableAtUtc)
            {
                var millis = clock.AvailableAtUtc.ToUnixTimeMilliseconds();
                var latest = availability.LastOrDefault();
                if (latest is not null && latest[0] + latest[1] == index && latest[2] == millis) latest[1]++;
                else availability.Add([index, 1, millis]);
            }
            reconstructed |= clock.Reconstructed || clock.AvailableAtUtc > evidenceCutoff;
            previous = index;
            last = clock;
            count++;
        }

        // Copy a formula row into the shared bar index rather than repeating timestamps in every column.
        public void Add(int index, ChartIndicatorClock clock, double?[][] columns, int input)
        {
            Observe(index, clock);
            for (var column = 0; column < samples.Length; column++)
                samples[column][index] = Rounded(columns[column][input]);
        }

        // A regular-session study has one numeric component.
        public void Add(int index, ChartIndicatorClock clock, double value)
        {
            Observe(index, clock);
            samples[0][index] = Rounded(value);
        }

        // Six fractional digits bound repetitive floating-point wire tails while retaining chart precision.
        private static double? Rounded(double? value) => value is { } finite ? Math.Round(finite, 6) : null;

        // Empty studies retain explicit reason and provenance but allocate no array of null values on the wire.
        public ChartIndicatorSeries Finish(DateTimeOffset? cutoff, string revision, string reason, int? sampleCount = null) =>
            new(study.Id, study.SourceInterval, study.Session, "all", study.Version, study.Parameters,
                cutoff, study.Formula?.Recursive == false ? null : last?.AnchorUtc, revision, reconstructed, last?.AvailableAtUtc,
                count == visible.Count ? "ready" : count == 0 ? "unavailable" : "partial",
                count == visible.Count ? null : reason, study.Columns, count == 0 ? [] : samples, segments, availability,
                study.Formula?.SeedObservations, study.Formula?.WarmObservations, anchors,
                sampleCount.HasValue ? MarketEvidenceProfile.DefaultLookbackSessions : null, sampleCount);
    }

    private sealed class RegularInputs
    {
        private readonly ChartBarProjector calendar;
        private readonly SortedSet<DateTimeOffset> breaks;
        public Dictionary<DateOnly, ChartRegularPrefix> Prefixes { get; } = [];

        // Use primary evidence where present and only attach warm slices with matching overlapping adjusted SIP prices.
        public RegularInputs(ChartBarProjector calendar, MergedChartSource merged, ChartHistoryBatch[] warm,
            IReadOnlyList<ChartCompletedBar> visible, DateTimeOffset cutoff, DateTimeOffset asOf, bool vwap, bool rvol,
            CancellationToken token)
        {
            this.calendar = calendar;
            breaks = new SortedSet<DateTimeOffset>(merged.BasisBreaks);
            var display = visible.Where(bar => bar.Segment == "regular").Select(bar => bar.SessionDate).Distinct().ToArray();
            var prior = rvol ? display.SelectMany(date =>
                calendar.PriorTradingDates(date, MarketEvidenceProfile.DefaultLookbackSessions)).ToHashSet() : [];
            var compatible = prior.Count == 0 ? [] : warm.Where(batch => SharesBasis(batch, merged)).ToArray();
            foreach (var date in display.Concat(prior).Distinct())
            {
                token.ThrowIfCancellationRequested();
                if (calendar.Regular(date) is not { } range) continue;
                var prefix = ChartRegularSessionStudies.Read(range, cutoff, asOf,
                    merged.Bars, merged.Quiet, merged.BasisBreaks, vwap && display.Contains(date));
                if (prior.Contains(date) && !display.Contains(date) && prefix.Volume.Length == 0 &&
                    compatible.FirstOrDefault(batch => batch.AcquiredSessions.Contains(date)) is { } earlier)
                {
                    prefix = ChartRegularSessionStudies.Read(range, cutoff, asOf,
                        earlier.Bars, earlier.Quiet, earlier.BasisBreaks, false);
                    breaks.UnionWith(earlier.BasisBreaks);
                }
                Prefixes.Add(date, prefix);
            }
        }

        // All exact prior sessions must have a comparable completed prefix, with no basis seam between cohorts.
        public (decimal? Value, ChartIndicatorClock Clock, int Count) ReadRelativeVolume(DateOnly date, ChartRegularPrefix current,
            int offset, ChartIndicatorClock clock)
        {
            var previous = calendar.PriorTradingDates(date, MarketEvidenceProfile.DefaultLookbackSessions);
            if (previous.Count != MarketEvidenceProfile.DefaultLookbackSessions ||
                calendar.Regular(previous[0]) is not { } first ||
                breaks.GetViewBetween(first.StartUtc.AddTicks(1), current.StartUtc).Count > 0)
                return (null, clock, 0);
            var volumes = new List<decimal>(previous.Count);
            for (var index = 0; index < previous.Count; index++)
            {
                if (!Prefixes.TryGetValue(previous[index], out var other) || other.Volume.Length <= offset)
                    continue;
                volumes.Add(other.Volume[offset]);
                clock = clock with
                {
                    AvailableAtUtc = other.Available[offset] > clock.AvailableAtUtc ? other.Available[offset] : clock.AvailableAtUtc,
                    Reconstructed = clock.Reconstructed || other.Reconstructed[offset]
                };
            }
            return (ChartIndicatorMath.CumulativeRelativeVolume(current.Volume[offset], volumes), clock, volumes.Count);
        }

        // A second loaded archive can join only after observing at least one equal-price overlap and no conflict.
        private static bool SharesBasis(ChartHistoryBatch older, MergedChartSource primary)
        {
            var matched = false;
            foreach (var (stamp, source) in older.Bars)
            {
                if (!primary.Bars.TryGetValue(stamp, out var newer)) continue;
                if (!ChartHistorySource.SamePrices(source.Bar, newer.Bar)) return false;
                matched = true;
            }
            return matched;
        }
    }
}
