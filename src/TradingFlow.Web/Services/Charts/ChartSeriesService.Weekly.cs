using TradingFlow.Contracts.V1;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Web.Services.Charts;

public sealed partial class ChartSeriesService
{
    private const int MaximumWeeklyBars = 100;

    // Load native daily evidence, then publish a weekly page with its evaluated gaps and exclusive cursor.
    private async Task<ChartSeriesPage> ReadWeeklyPageAsync(
        ChartSeriesKey key, DateTimeOffset cutoff, bool paged, DateTimeOffset requestedAt, CancellationToken token)
    {
        if (paged && !ChartHistorySource.IsWeeklyCursorBoundary(cutoff))
            throw new ArgumentException("Weekly history requires an exchange session date boundary.", nameof(cutoff));

        ChartHistoryBatch history;
        try
        {
            history = await source.ReadWeeklyAsync(key.Symbol, cutoff, paged, token);
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Weekly chart sources unavailable for {Symbol}.", key.Symbol);
            return Unavailable(key, requestedAt, "Verified exchange calendar or SIP daily history is unavailable.");
        }
        if (history.Sessions.Count == 0)
            return Unavailable(key, requestedAt, history.Limitation ?? "No scheduled exchange sessions were returned for this window.");

        var asOf = clock.GetUtcNow().ToUniversalTime();
        var projection = ReadProjection(key, history, null, null, history.Projector, cutoff, asOf, paged);
        var weekly = projection.Weekly!;
        var first = weekly.Evaluated.FirstOrDefault();
        var next = first is null ? (DateTimeOffset?)null : ChartBarProjector.At(first.FirstSession, TimeOnly.MinValue);
        var last = projection.Visible.LastOrDefault();
        var reconstructed = projection.Visible.Any(candle => candle.Bar.KnownAtUtc is null || candle.AvailableAtUtc > cutoff);
        var gaps = weekly.Evaluated.Where(week => week.Gap is not null)
            .Select(week => new ChartWeeklyGap(week.Monday, week.Gap!)).ToArray();
        var through = weekly.Evaluated.TakeWhile(week => week.Candle is not null).LastOrDefault()?.Candle?.EndUtc;
        var pending = !paged && weekly.Pending;
        var notes = WeeklyNotes(history.Limitation, reconstructed, pending);
        var reason = last is null ? "There are no completed, compatible SIP weeks in this window."
            : "Weekly candles use completed SIP daily candles on the adjusted price basis.";
        var markerRows = first is null ? [] : await ReadWeeklyMarkersAsync(key.Symbol, first.Monday,
            cutoff, paged, asOf, weekly.Evaluated, token);
        var evidence = new ChartSeriesEvidence("alpaca", "sip", "all",
            first is null ? "unavailable" : gaps.Length == 0 ? "complete" : "partial",
            through, last?.EndUtc, projection.Visible.Max(candle => candle.Bar.KnownAtUtc), null,
            reconstructed, notes, projection.BasisBreaks.Where(stamp => stamp >= next && stamp < cutoff).Order().ToArray(),
            new ChartWeeklyPageEvidence(next, history.Revision, pending, gaps));
        var candles = weekly.Evaluated.Where(week => week.Candle is not null).Select(ToWeeklyCandle).ToArray();
        return new ChartSeriesPage(new ChartSeriesSnapshot(key, asOf, last is null ? "unavailable" : "history_only",
            reason, evidence, candles, [], [], markerRows, Indicators: []),
            next < cutoff && next >= requestedAt.AddYears(-10) ? next : null);
    }

    // Apply the display ceiling only after skipping the lower acquisition edge and evaluating whole weeks.
    private static ProjectedChart ProjectWeekly(
        string symbol, ChartHistoryBatch history, DateTimeOffset cutoff, DateTimeOffset asOf)
    {
        var result = history.Projector.Weekly(symbol, history.Bars.Values, history.BasisBreaks, history.Sessions[0], cutoff, asOf,
            history.DailyConflicts);
        var visible = result.Evaluated.Where(week => week.Candle is not null).TakeLast(MaximumWeeklyBars).ToArray();
        if (visible.Length == MaximumWeeklyBars)
            result = result with { Evaluated = result.Evaluated.Where(week => week.Monday >= visible[0].Monday).ToArray() };
        return new ProjectedChart(history.BasisBreaks, visible.Select(week => week.Candle!).ToArray(), [], [],
            result.Evaluated.All(week => week.Gap is null), result);
    }

    // Keep per-session original receipts or reconstruction clocks attached to the aggregate.
    private static ChartCandle ToWeeklyCandle(ChartWeeklyWindow week) => ToCandle(week.Candle!) with
    {
        Weekly = new ChartWeeklyCandleEvidence(week.LastSession, week.Constituents.Select(source =>
            new ChartWeeklyInputEvidence(ChartBarProjector.NewYorkDate(source.Bar.Timestamp), source.SourceKind,
                source.Bar.KnownAtUtc, source.RetrievedAtUtc)).ToArray())
    };

    // Match events to their New York civil week, including holidays and events after the final regular close.
    private async Task<IReadOnlyList<ChartMarker>> ReadWeeklyMarkersAsync(string symbol, DateOnly firstMonday,
        DateTimeOffset cutoff, bool paged, DateTimeOffset asOf, IReadOnlyList<ChartWeeklyWindow> windows, CancellationToken token)
    {
        var start = ChartBarProjector.At(firstMonday, TimeOnly.MinValue);
        var until = paged
            ? ChartBarProjector.At(ChartBarProjector.WeekStart(ChartBarProjector.NewYorkDate(cutoff)), TimeOnly.MinValue)
            : cutoff;
        var rows = await ReadMarkersAsync(symbol, start, paged ? until : null, until, asOf, token);
        var visibleWeeks = windows.Where(week => week.Candle is not null).ToDictionary(week => week.Monday, week => week.FirstSession);
        return rows.Where(row => !paged || row.EventAtUtc < until).Select(row =>
            visibleWeeks.TryGetValue(ChartBarProjector.WeekStart(ChartBarProjector.NewYorkDate(row.EventAtUtc)), out var first)
                ? row with { WeeklySessionDate = first } : row).ToArray();
    }

    // Explain a current unfinished week independently from earlier missing source candles.
    private static IReadOnlyList<string> WeeklyNotes(string? limitation, bool reconstructed, bool pending)
    {
        var notes = new List<string>();
        if (limitation is not null) notes.Add(limitation);
        if (reconstructed) notes.Add("Reconstructed history uses later acquisitions or revisions. See daily constituent receipts.");
        if (pending) notes.Add("The current week remains pending until the final scheduled regular close.");
        return notes;
    }
}
