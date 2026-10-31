using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Backtesting;

public sealed record NativeDailyAccountingContext(DateTimeOffset StartUtc, DateTimeOffset CutoffUtc,
    IReadOnlyDictionary<DateOnly, MarketSessionSchedule> Sessions, bool AllowOpenAtCutoff);

/// <summary>Values completed native-daily simulations by their source session, retaining original execution stamps.</summary>
public static class NativeDailySessionLedger
{
    public const string ValuationBasis = "marked_to_market_native_daily_source_session_equity";
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    // Native simulation receipts belong to the previous source session, including Friday/Saturday boundaries.
    public static DateOnly SourceSessionDate(DateTimeOffset completedDailyReceipt) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(completedDailyReceipt.AddDays(-1), NewYork).DateTime);

    // Intraday executions keep their actual New York session; native daily receipts retain the original previous-day convention.
    public static DateOnly SourceSessionDate(DateTimeOffset execution, string valuationBasis) =>
        valuationBasis == IntradayPortfolioExecution.ValuationBasis
            ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(execution, NewYork).DateTime)
            : SourceSessionDate(execution);

    // These UTC keys order accounting sessions only; they do not claim an observed intraday execution time.
    private static DateTimeOffset AccountingKey(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    // Unknown executions invalidate their dependent valuation prefix without erasing observed fills or cash.
    public static BacktestPortfolioState MarkUnavailableFrom(BacktestPortfolioState state, DateOnly boundary, string reason)
    {
        if (state.UnavailableFromSession is { } previous && previous < boundary) return state;
        return state with { UnavailableFromSession = boundary, UnavailableReason = reason,
            Sessions = state.Sessions.Select(s => s.SessionDate < boundary ? s : s with
            { Equity = null, PositionValue = null, UnrealizedProfit = null, GrossExposurePct = null,
                UnavailableReason = reason }).ToArray() };
    }

    // Build one sequential fill/cost-basis ledger and reconcile its supported prefix with the generic close valuator.
    public static BacktestPortfolioState Analyze(decimal startingCapital, BacktestPortfolioState state,
        IReadOnlyCollection<OhlcvBar> bars, NativeDailyAccountingContext context)
    {
        var daily = bars.Where(b => b.Timeframe == "1d" && b.Timestamp.AddDays(1) <= context.CutoffUtc &&
                (b.KnownAtUtc is null || b.KnownAtUtc <= b.Timestamp.AddDays(1)))
            .GroupBy(b => (b.Ticker, Date: DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(b.Timestamp, NewYork).DateTime)))
            .ToDictionary(g => g.Key, g => new DailyClose(g.Select(b => b.Close).Distinct().Single(), g.First().Timestamp));
        return AnalyzeMarks(startingCapital, state, daily, context);
    }

    // Recompute saved state from its immutable source marks and fills using the same cost-basis rules.
    public static void VerifyRecordedState(decimal startingCapital, BacktestPortfolioState state, NativeDailyAccountingContext context)
    {
        var daily = state.SessionMarks.Where(m => m.Price is not null && m.OriginalBarTimestampUtc is not null)
            .GroupBy(m => (m.Ticker, Date: m.SessionDate))
            .ToDictionary(g => g.Key, g => new DailyClose(g.Select(m => m.Price!.Value).Distinct().Single(),
                g.Select(m => m.OriginalBarTimestampUtc!.Value).Distinct().Single()));
        if (daily.Any(m => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(m.Value.Timestamp, NewYork).DateTime) != m.Key.Date ||
            MarkKnownAt(m.Value.Timestamp, state.ValuationBasis) > context.CutoffUtc))
            throw new InvalidDataException("Saved native marks have a mismatched source session or exceed the cutoff.");
        var replayed = AnalyzeMarks(startingCapital, state, daily, context);
        if (!replayed.Sessions.SequenceEqual(state.Sessions) || !replayed.OpenPositions.SequenceEqual(state.OpenPositions) ||
            !replayed.SessionMarks.SequenceEqual(state.SessionMarks))
            throw new InvalidDataException("Native recorded session ledger, marks or cutoff positions do not reconcile.");
    }

    // Keep source-clock normalization separate from the single chronological accounting implementation.
    private static BacktestPortfolioState AnalyzeMarks(decimal startingCapital, BacktestPortfolioState state,
        IReadOnlyDictionary<(string Ticker, DateOnly Date), DailyClose> daily, NativeDailyAccountingContext context)
    {
        if (startingCapital <= 0) throw new ArgumentOutOfRangeException(nameof(startingCapital));
        if (state.CutoffUtc != context.CutoffUtc) throw new InvalidDataException("Native book cutoff differs from its valuation context.");
        var dates = ResolveSessions(context);
        var marks = daily.ToDictionary(p => p.Key, p => p.Value.Close);
        var fills = state.Fills.OrderBy(f => f.TimestampUtc).ToArray();
        ValidateFills(state, fills, dates, context);
        var lots = new Dictionary<string, Lot>(StringComparer.Ordinal);
        var observations = new List<BacktestSessionObservation>();
        var positionMarks = new List<BacktestSessionPositionMark>();
        var cash = startingCapital; var realized = 0m; var fees = 0m; var next = 0; string? unavailable = null;
        foreach (var date in dates)
        {
            if (state.UnavailableFromSession is { } boundary && date >= boundary)
                unavailable ??= state.UnavailableReason ?? "Execution evidence is unavailable for this session and its dependent ledger.";
            while (next < fills.Length && fills[next].SourceSessionDate <= date)
            {
                var fill = fills[next++];
                if (!lots.TryGetValue(fill.CandidateId, out var lot)) lots.Add(fill.CandidateId, lot = new(fill.Ticker));
                ApplyFill(lot, fill, ref cash, ref realized, ref fees);
                if (lot.Quantity == 0) lots.Remove(fill.CandidateId);
            }
            CaptureSessionMarks(date, lots, daily, positionMarks);
            observations.Add(ObserveSession(date, lots.Values, marks, startingCapital, cash, realized, fees, ref unavailable));
        }
        VerifyCloseValuations(startingCapital, fills, marks, observations);
        var positions = ProjectCutoffPositions(state, lots, daily, dates[^1]);
        var firstUnavailable = observations.FirstOrDefault(s => s.UnavailableReason is not null);
        return state with { StartUtc = context.StartUtc, OpenPositions = positions, Sessions = observations, SessionMarks = positionMarks,
            UnavailableFromSession = firstUnavailable?.SessionDate, UnavailableReason = firstUnavailable?.UnavailableReason };
    }

    // The saved calendar must explicitly cover every date; absence never means a holiday.
    private static DateOnly[] ResolveSessions(NativeDailyAccountingContext context)
    {
        if (context.CutoffUtc <= context.StartUtc) throw new InvalidDataException("Native valuation needs a later fixed cutoff.");
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(context.StartUtc, NewYork).DateTime);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(context.CutoffUtc.AddTicks(-1), NewYork).DateTime);
        for (var day = first; day <= last; day = day.AddDays(1))
            if (!context.Sessions.TryGetValue(day, out var session) || session.TradeDate != day)
                throw new InvalidDataException($"Native daily valuation calendar is missing {day:yyyy-MM-dd}.");
        var dates = context.Sessions.Values.Where(s => s.IsTradingDay && s.TradeDate >= first && s.TradeDate <= last)
            .Select(s => s.TradeDate).Distinct().Order().ToArray();
        return dates.Length > 0 ? dates : throw new InvalidDataException("Native daily valuation has no authoritative trading sessions.");
    }

    // Validate quantities, costs, identity, side and time even when no mark can be valued.
    private static void ValidateFills(BacktestPortfolioState state, IReadOnlyList<BacktestPortfolioFill> fills,
        IReadOnlyList<DateOnly> dates, NativeDailyAccountingContext context)
    {
        var sessions = dates.ToHashSet();
        var orders = state.AdmittedOrders.ToDictionary(o => o.CandidateId, StringComparer.Ordinal);
        if (orders.Count != state.AdmittedOrderCount || orders.Values.Any(o => o.RequestedQuantity <= 0 ||
            o.ReferencePrice <= 0 || o.Direction is not ("long" or "short")))
            throw new InvalidDataException("Native admitted order plans are inconsistent.");
        foreach (var fill in fills)
        {
            if (!sessions.Contains(fill.SourceSessionDate) || fill.SourceSessionDate != SourceSessionDate(fill.TimestampUtc, state.ValuationBasis) ||
                fill.TimestampUtc.Offset != TimeSpan.Zero || fill.TimestampUtc > context.CutoffUtc ||
                fill.Quantity <= 0 || fill.Price <= 0 || fill.Fees < 0)
                throw new InvalidDataException("A native fill has invalid quantity/cost or lies outside its source-session calendar.");
            if (!orders.TryGetValue(fill.CandidateId, out var order) || order.Ticker != fill.Ticker || order.StrategyId != fill.StrategyId ||
                fill.IsBuy != (fill.IsEntry == (order.Direction == "long")))
                throw new InvalidDataException("A native fill does not match its admitted candidate, symbol, strategy or side.");
        }
    }

    // Preserve each actual holding's source close evidence before projecting session totals.
    private static void CaptureSessionMarks(DateOnly date, IReadOnlyDictionary<string, Lot> lots,
        IReadOnlyDictionary<(string Ticker, DateOnly Date), DailyClose> daily, ICollection<BacktestSessionPositionMark> destination)
    {
        foreach (var pair in lots)
        {
            var lot = pair.Value; daily.TryGetValue((lot.Ticker, date), out var bar);
            destination.Add(new(date, pair.Key, lot.Ticker, lot.Quantity, lot.Basis, bar?.Close, bar?.Timestamp));
        }
    }

    // Missing aligned marks invalidate the dependent suffix without replacing them with stale prices.
    private static BacktestSessionObservation ObserveSession(DateOnly date, ICollection<Lot> open,
        IReadOnlyDictionary<(string Ticker, DateOnly Date), decimal> marks, decimal capital,
        decimal cash, decimal realized, decimal fees, ref string? unavailable)
    {
        foreach (var lot in open)
            if (!marks.TryGetValue((lot.Ticker, date), out var price) || price <= 0)
                unavailable ??= $"Missing aligned completed daily mark for {lot.Ticker} on {date:yyyy-MM-dd}.";
        if (unavailable is not null) return new(date, cash, realized, fees, null, null, null, null, open.Count, unavailable);
        var positionValue = open.Sum(l => l.Quantity * marks[(l.Ticker, date)]);
        var unrealized = open.Sum(l => l.Quantity * (marks[(l.Ticker, date)] - l.Basis));
        var equity = cash + positionValue;
        if (Math.Abs(equity - capital - realized - unrealized) > .000001m)
            throw new InvalidDataException("Native fill cash, realized profit and remaining cost basis do not reconcile.");
        var gross = open.Sum(l => Math.Abs(l.Quantity * marks[(l.Ticker, date)]));
        return new(date, cash, realized, fees, positionValue, unrealized, equity,
            equity > 0 ? gross / equity * 100m : 0m, open.Count, null);
    }

    // A final holding must match its actual remaining fills and the final source-session mark.
    private static BacktestOpenPosition[] ProjectCutoffPositions(BacktestPortfolioState state,
        IReadOnlyDictionary<string, Lot> lots, IReadOnlyDictionary<(string Ticker, DateOnly Date), DailyClose> daily, DateOnly last)
    {
        if (lots.Count != state.OpenPositions.Count || state.OpenPositions.Any(p =>
            !lots.TryGetValue(p.CandidateId, out var lot) || lot.Quantity != p.SignedQuantity || lot.Ticker != p.Ticker))
            throw new InvalidDataException("Cutoff positions do not match actual remaining candidate fills.");
        return state.OpenPositions.Select(p =>
        {
            daily.TryGetValue((p.Ticker, last), out var close);
            var valid = close?.Close > 0;
            var basis = lots[p.CandidateId].Basis;
            return p with { EntryBasis = basis, MarkPrice = valid ? close!.Close : null,
                MarkTimestampUtc = valid ? MarkKnownAt(close!.Timestamp, state.ValuationBasis) : null,
                UnrealizedProfit = valid ? p.SignedQuantity * (close!.Close - basis) : null };
        }).ToArray();
    }

    // Reconcile the complete supported prefix in one generic valuation pass, avoiding repeated history scans.
    private static void VerifyCloseValuations(decimal startingCapital, IReadOnlyList<BacktestPortfolioFill> fills,
        IReadOnlyDictionary<(string Ticker, DateOnly Date), decimal> marks, IReadOnlyList<BacktestSessionObservation> observations)
    {
        var supported = observations.TakeWhile(o => o.UnavailableReason is null).ToArray();
        if (supported.Length == 0) return;
        var final = supported[^1].SessionDate;
        var grid = supported.Select(o => AccountingKey(o.SessionDate)).ToArray();
        var valued = PortfolioCloseValuation.Analyze(startingCapital,
            fills.Where(f => f.SourceSessionDate <= final).Select(f => new PortfolioValuationFill(f.Ticker,
                AccountingKey(f.SourceSessionDate), f.IsBuy, f.Quantity, f.Price, f.Fees)).ToArray(),
            marks.Where(m => m.Key.Date <= final).Select(m => new PortfolioCloseMark(m.Key.Ticker,
                AccountingKey(m.Key.Date), m.Value)).ToArray(), grid, true);
        for (var i = 0; i < supported.Length; i++)
            if (Math.Abs(valued.Observations[i].Equity - supported[i].Equity!.Value) > .000001m)
                throw new InvalidDataException("Close valuator differs from native candidate ledger.");
    }

    // Fees are realized cash expenses when charged; only actual exit quantity realizes trading profit.
    private static void ApplyFill(Lot lot, BacktestPortfolioFill fill, ref decimal cash, ref decimal realized, ref decimal fees)
    {
        var signed = fill.Quantity * (fill.IsBuy ? 1 : -1);
        cash -= signed * fill.Price + fill.Fees; realized -= fill.Fees; fees += fill.Fees;
        if (fill.IsEntry)
        {
            if (lot.Quantity != 0 && Math.Sign(lot.Quantity) != Math.Sign(signed)) throw new InvalidDataException("Entry changes candidate direction.");
            lot.Basis = (Math.Abs(lot.Quantity) * lot.Basis + fill.Quantity * fill.Price) / (Math.Abs(lot.Quantity) + fill.Quantity);
        }
        else
        {
            if (lot.Quantity == 0 || Math.Sign(lot.Quantity) == Math.Sign(signed) || fill.Quantity > Math.Abs(lot.Quantity))
                throw new InvalidDataException("Exit exceeds its admitted candidate's actual remaining quantity.");
            realized += fill.Quantity * Math.Sign(lot.Quantity) * (fill.Price - lot.Basis);
        }
        lot.Quantity += signed;
    }

    private sealed class Lot(string ticker)
    {
        public string Ticker { get; } = ticker;
        public int Quantity { get; set; }
        public decimal Basis { get; set; }
    }

    private sealed record DailyClose(decimal Close, DateTimeOffset Timestamp);

    // Marks store original bar openings; select only the approved bar duration for their completed availability time.
    private static DateTimeOffset MarkKnownAt(DateTimeOffset timestamp, string valuationBasis) =>
        valuationBasis == IntradayPortfolioExecution.ValuationBasis ? timestamp.AddMinutes(5) : timestamp.AddDays(1);
}
