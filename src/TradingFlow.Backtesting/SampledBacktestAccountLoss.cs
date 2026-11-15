using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Execution;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Backtesting;

internal sealed record BacktestAccountLossInputs(BacktestAccountLossReplayConfig Settings,
    IReadOnlyDictionary<DateOnly, MarketSessionSchedule> Sessions, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

internal sealed record AccountLossReplayBoundary(DateOnly Session, DateTimeOffset TimestampUtc, bool IsOpen, bool IsClose);

internal sealed record AccountLossReplayRefusal(string Code, string Detail);

/// <summary>Supplies pure account-loss policy evidence from one causal five-minute execution book.</summary>
internal sealed class SampledBacktestAccountLoss
{
    private const string MissingOpenMark = "loss_mark_unavailable_at_open";
    private const string MissingMark = "loss_mark_unavailable";
    private const string MissingBaseline = "loss_close_baseline_unavailable";
    private const string UnprovedIoc = "loss_ioc_terminal_unproved";
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeSpan Interval = BacktestAccountLossReplayConfig.MaximumSnapshotAge;
    private readonly BacktestAccountLossInputs _inputs;
    private readonly AccountLossGuardOptions _options;
    private readonly decimal _startingCapital;
    private readonly Dictionary<string, int> _quantities = new(StringComparer.OrdinalIgnoreCase);
    private decimal _cash;
    private AccountLossState? _state;
    private AccountClosingEquity? _latestClose, _priorClose, _weekAnchor;
    private DateOnly? _session;
    private DateTimeOffset? _lastMarkedAtUtc;
    private decimal? _lastMarkedEquity;
    private long _observations, _unavailable, _completedSweeps;
    private bool _openingOrdersTerminal = true;

    // All input timestamps come from the replay. Starting capital belongs to this book's isolated, initially flat account.
    public SampledBacktestAccountLoss(BacktestAccountLossInputs inputs, decimal startingCapital)
    {
        inputs.Settings.Validate("backtest", ExecutionReplayPolicy.DailyRthFiveMinuteV1);
        if (!inputs.Settings.Enabled || startingCapital <= 0m || inputs.EndUtc <= inputs.StartUtc)
            throw new ArgumentException("Sampled account loss requires an enabled policy, positive capital and ordered evaluation bounds.");
        _inputs = inputs;
        _startingCapital = _cash = startingCapital;
        _options = new("simulated", inputs.Settings.Limits, Interval);
    }

    public BacktestAccountLossBookSummary Summary => new(_observations, _unavailable, _completedSweeps,
        _lastMarkedAtUtc, _lastMarkedEquity, _state);

    // Yield only authoritative regular boundaries. Closed holidays carry the last verified close into the next session.
    public IEnumerable<AccountLossReplayBoundary> Boundaries()
    {
        var first = ExchangeSessionCalendar.SessionDate(_inputs.StartUtc);
        var last = ExchangeSessionCalendar.SessionDate(_inputs.EndUtc);
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            var schedule = RequireSchedule(date);
            if (!schedule.IsTradingDay) continue;
            var open = At(date, schedule.RegularOpen);
            var close = At(date, schedule.RegularClose);
            if (close <= open || (close - open).Ticks % Interval.Ticks != 0)
                throw new InvalidDataException($"The regular session on {date:yyyy-MM-dd} has no exact five-minute grid.");
            for (var at = open; at <= close && at <= _inputs.EndUtc; at += Interval)
            {
                if (at >= _inputs.StartUtc) yield return new(date, at, at == open, at == close);
            }
        }
    }

    // Debit or credit each actual simulated fill and its fees once, retaining signed quantities only while held.
    public void RecordFill(SimulatedExecutionEvent fill)
    {
        var quantity = fill.Side == SimulatedOrderSide.Buy ? fill.LastFillQuantity : -fill.LastFillQuantity;
        _cash -= quantity * fill.FillPrice!.Value + fill.Fees;
        var position = checked(_quantities.GetValueOrDefault(fill.Symbol) + quantity);
        if (position == 0) _quantities.Remove(fill.Symbol);
        else _quantities[fill.Symbol] = position;
    }

    // Observe once after all same-boundary fills. Invalid observations preserve latches and invalidate any unproved close.
    public void Observe(AccountLossReplayBoundary boundary, Func<string, DateTimeOffset, decimal?> exactMark,
        bool openingOrdersTerminal, bool executionUnavailable)
    {
        StartSession(boundary.Session);
        _observations++;
        var equity = executionUnavailable ? null : MarkedEquity(boundary, exactMark);
        if (equity is not null)
        {
            _lastMarkedAtUtc = boundary.TimestampUtc;
            _lastMarkedEquity = equity;
        }

        var issue = equity is null ? (boundary.IsOpen ? MissingOpenMark : MissingMark) : null;
        AccountLossEvidence? evidence = null;
        if (issue is null)
        {
            if (_priorClose is { Equity: > 0m } prior && _weekAnchor is { Equity: > 0m } week)
            {
                // A prior close already sampled by the policy needs no replay; a recovered baseline may advance its peak.
                var completed = _state?.LastTrustedObservationUtc < prior.CloseUtc ? new[] { prior } : [];
                evidence = new(boundary.Session, prior, week, completed, FundingIssue: null);
            }
            else issue = MissingBaseline;
        }
        if (issue is not null) _unavailable++;
        _state = AccountLossLimitPolicy.Observe(_state,
            new(boundary.TimestampUtc, boundary.TimestampUtc, equity.GetValueOrDefault(), evidence, issue), _options.Limits);
        _openingOrdersTerminal = openingOrdersTerminal;
        if (_state.CancellationSweepPending && openingOrdersTerminal)
        {
            _state = _state with { CancellationSweepPending = false };
            _completedSweeps++;
        }
        if (boundary.IsClose)
            _latestClose = equity is { } marked ? new(boundary.Session, boundary.TimestampUtc, marked) : null;
    }

    // Use the current sample and the shared latch/refusal rules at the actual admission clock.
    public AccountLossReplayRefusal? Refusal(DateTimeOffset commandUtc)
    {
        if (!_openingOrdersTerminal)
            return new(UnprovedIoc, "The prior opening IOC remainder is not terminal in the simulation book.");
        if (_state is { EvidenceIssue: MissingOpenMark } state && state.ObservationUtc == commandUtc)
            return new(MissingOpenMark, "Held exposure has no completed mark in this session at its official open.");
        var reason = AccountLossLimitPolicy.Refusal(_state, _options, ExchangeSessionCalendar.SessionDate(commandUtc), commandUtc);
        return reason switch
        {
            null => null,
            MissingOpenMark => new(MissingOpenMark, "Held exposure has no completed mark in this session at its official open."),
            MissingMark => new(MissingMark, "A held-symbol mark or execution record is unavailable at the completed boundary."),
            MissingBaseline => new(MissingBaseline, "A verified positive prior-session or week-opening closing equity is unavailable."),
            _ => new("account_loss_replay_refused", reason)
        };
    }

    // Require an exact mark for every nonzero holding. Flat cash is directly observable, including at the official open.
    private decimal? MarkedEquity(AccountLossReplayBoundary boundary, Func<string, DateTimeOffset, decimal?> exactMark)
    {
        if (boundary.IsOpen && _quantities.Count > 0) return null;
        var equity = _cash;
        foreach (var (symbol, quantity) in _quantities)
        {
            var mark = exactMark(symbol, boundary.TimestampUtc);
            if (mark is not > 0m) return null;
            equity += quantity * mark.Value;
        }
        return equity;
    }

    // Freeze the prior close per session and the week anchor per New York week. The synthetic seed is used only at inception.
    private void StartSession(DateOnly session)
    {
        if (_session == session) return;
        var week = AccountLossLimitPolicy.WeekOf(session);
        if (_session is null)
        {
            _latestClose = SyntheticCloseBefore(session);
            _weekAnchor = SyntheticCloseBefore(week);
        }
        else if (AccountLossLimitPolicy.WeekOf(_session.Value) != week)
            _weekAnchor = _latestClose;

        _priorClose = _latestClose;
        _latestClose = null;
        _session = session;
    }

    // The new account was flat before replay. Use real calendar identities with the explicitly synthetic equity amount.
    private AccountClosingEquity SyntheticCloseBefore(DateOnly before)
    {
        for (var days = 1; days <= 14; days++)
        {
            var date = before.AddDays(-days);
            var schedule = RequireSchedule(date);
            if (schedule.IsTradingDay)
                return new(date, At(date, schedule.RegularClose), _startingCapital);
        }
        throw new InvalidDataException($"No authoritative trading session before {before:yyyy-MM-dd} anchors the initial flat replay account.");
    }

    // Missing dates cannot be read as holidays or an inferred trading schedule.
    private MarketSessionSchedule RequireSchedule(DateOnly date) =>
        _inputs.Sessions.TryGetValue(date, out var schedule) && schedule.TradeDate == date
            ? schedule : throw new InvalidDataException($"Account-loss replay calendar has no verified session for {date:yyyy-MM-dd}.");

    // Convert authoritative exchange session times with the New York offset on that date.
    private static DateTimeOffset At(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, NewYork.GetUtcOffset(local)).ToUniversalTime();
    }
}
