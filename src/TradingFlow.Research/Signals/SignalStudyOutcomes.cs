using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Sessions;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Research.Signals;

/// <summary>Where a studied signal enters: the next session's open, or the signal day's own close (the declared upper bound).</summary>
public enum SignalStudyEntry
{
    NextOpen,
    SameClose
}

/// <summary>
/// One stock's study inputs: its completed as-traded daily bars in time order (never after the study window), the indicator
/// snapshots the shared indicator engine computed over them (aligned by index) and its split ex-dates.
/// </summary>
public sealed record SignalStudyStock(string Ticker, IReadOnlyList<OhlcvBar> Bars, IReadOnlyList<IndicatorSnapshot> Snapshots,
    IReadOnlySet<DateOnly> SplitExDates);

/// <summary>The declared study settings: entry, cost per side as a fraction (0.001 = 10 bps), window and warm-up bars.</summary>
public sealed record SignalStudySettings(SignalStudyEntry Entry, double CostPerSide, DateOnly From, DateOnly To, int WarmupBars);

/// <summary>A studied signal: the analyzer's outcome plus the dates and exit reason published for each trade.</summary>
public sealed record StudiedSignal(SignalOutcome Outcome, string Direction, DateOnly EntryDate, DateOnly? ExitDate, string ExitReason);

/// <summary>
/// Turns signals of the shared decision path into <see cref="SignalOutcome"/>s for <see cref="SignalStudyAnalyzer"/>: the
/// setup signal (SignalGenerator) and entry direction (StrategyDecisionKernel.ResolveEntryDirection) on each completed daily
/// bar, then the strategy's own exit rules (TechnicalExecutionEngine) on later completed bars, exiting at the following open.
/// Nothing here re-implements a strategy rule.
/// </summary>
public static class SignalStudyOutcomes
{
    /// <summary>Count: signals followed (on an eligible stock-day).</summary>
    public const string SignalsCount = "signals";
    /// <summary>Count: signals on a stock-day the screen did not list.</summary>
    public const string IneligibleCount = "signals_on_ineligible_days";
    /// <summary>Count: trades closed at the last open before a split ex-date.</summary>
    public const string SplitClosedCount = "split_closed_before_ex_date";
    /// <summary>Count: signals left out because a split ex-date fell before the first holding day could complete.</summary>
    public const string SplitAtEntryCount = "split_at_entry_skipped";
    /// <summary>Count: trades still open at the window end (outcome unknown, never read past the window).</summary>
    public const string OpenAtWindowEndCount = "open_at_window_end";
    /// <summary>Count: trades holding a date on which no eligible stock had a return (outcome treated as unknown).</summary>
    public const string NoUniverseDayCount = "holding_day_without_eligible_universe";
    /// <summary>Exit reason of a trade closed before a split's ex-date.</summary>
    public const string SplitExitReason = "split_exit_before_ex_date";
    /// <summary>Exit reason of a trade still open at the window end.</summary>
    public const string OpenAtWindowEndReason = "open_at_window_end";

    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>
    /// Refuses strategies whose rules this study cannot follow on completed daily bars alone: other timeframes, confirmation
    /// bars, confluence, regime gates, technical factors, drawdown gates, news, opening ranges, and intrabar orders (stops,
    /// targets, trailing stops, VWAP confirmations, log-price fades). Each is named in the refusal.
    /// </summary>
    public static void RequireSupported(StrategyDefinition strategy)
    {
        var entry = strategy.EntryRules;
        var exits = strategy.ExitRules;
        var unsupported = new (bool Present, string Name)[]
        {
            (strategy.Timeframe != "1d" || strategy.Execution.Timeframe != "1d", "a timeframe other than 1d"),
            (strategy.Execution.EffectiveConfirmation.Enabled, "execution confirmation bars"),
            (strategy.Confluence.Enabled, "confluence"),
            (strategy.Regime is { IsActive: true }, "a market regime gate"),
            (entry.TechnicalFactor is not null, "a technical factor"),
            (entry.DrawdownQualification is not null, "a drawdown gate"),
            (entry.HistoricalNewsDiagnostic is not null || entry.RequirePositiveNews, "news rules"),
            (entry.OpeningRangeBreakout is not null, "an opening range"),
            (exits.HasProtectiveStop || exits.HasProfitTarget || exits.EnableAtrTrailingStop, "intrabar stop or target orders"),
            (exits.EnableConfirmedVwapExit || exits.ExitOnLogPriceFade, "VWAP-confirmation or log-price-fade exits")
        }.Where(item => item.Present).Select(item => item.Name).ToArray();
        if (unsupported.Length > 0)
            throw new NotSupportedException($"{strategy.StrategyName}: the signal study follows completed daily bars only and cannot apply " +
                String.Join(", ", unsupported) + ".");
    }

    /// <summary>
    /// Follows every signal of one stock inside the window: a completed-bar signal admitted by the shared entry rules, on a
    /// stock-day the screen lists (the entry day for next-open entries, the signal day for same-close entries). Counts each
    /// case in <paramref name="counts"/>. Reads no bar after the window end (the caller loads none).
    /// </summary>
    public static IReadOnlyList<StudiedSignal> ForStock(StrategyDefinition strategy, SignalStudyStock stock, Func<string, DateOnly, bool> isEligible,
        SignalStudySettings settings, IDictionary<string, int> counts)
    {
        RequireAligned(stock);
        var generator = new SignalGenerator();
        var clock = new StrategySessionClock();
        var dates = stock.Bars.Select(bar => SessionDate(bar.Timestamp)).ToArray();
        var studied = new List<StudiedSignal>();
        for (var index = Math.Max(settings.WarmupBars - 1, 1); index < stock.Bars.Count && dates[index] <= settings.To; index++)
        {
            if (dates[index] < settings.From) continue;
            if (SignalDirection(strategy, stock, index, generator, clock) is not { } direction) continue;
            var entryIndex = settings.Entry == SignalStudyEntry.NextOpen ? index + 1 : index;
            if (entryIndex >= stock.Bars.Count) continue; // the next session lies after the window
            if (!isEligible(stock.Ticker, dates[entryIndex]))
            {
                Count(counts, IneligibleCount);
                continue;
            }
            Count(counts, SignalsCount);
            if (Follow(strategy, stock, dates, index, direction, settings, counts) is { } signal) studied.Add(signal);
        }
        return studied;
    }

    /// <summary>
    /// The equal-weight open-to-open return of every stock the screen lists on each date of the window (from its open to its
    /// next session's open, both inside the window): the benchmark the analyzer subtracts.
    /// </summary>
    public static IReadOnlyList<EligibleUniverseDay> Universe(IEnumerable<SignalStudyStock> stocks, Func<string, DateOnly, bool> isEligible,
        DateOnly from, DateOnly to)
    {
        var byDate = new SortedDictionary<DateOnly, (double Sum, int Count)>();
        foreach (var stock in stocks)
        {
            var dates = stock.Bars.Select(bar => SessionDate(bar.Timestamp)).ToArray();
            for (var index = 0; index + 1 < stock.Bars.Count; index++)
            {
                if (dates[index] < from || dates[index + 1] > to || !isEligible(stock.Ticker, dates[index])) continue;
                var value = Return(stock.Bars[index].Open, stock.Bars[index + 1].Open);
                var (sum, count) = byDate.GetValueOrDefault(dates[index]);
                byDate[dates[index]] = (sum + value, count + 1);
            }
        }
        return byDate.Select(pair => new EligibleUniverseDay(pair.Key, pair.Value.Sum / pair.Value.Count)).ToArray();
    }

    /// <summary>
    /// Marks as unknown (counted) every trade holding a date absent from the eligible universe, which the analyzer could not
    /// benchmark; returns the signals with those outcomes replaced.
    /// </summary>
    public static IReadOnlyList<StudiedSignal> RequireUniverseDays(IReadOnlyList<StudiedSignal> signals, IReadOnlyList<EligibleUniverseDay> universe,
        IDictionary<string, int> counts)
    {
        var dates = universe.Select(day => day.Date).ToHashSet();
        return signals.Select(signal =>
        {
            if (!signal.Outcome.OutcomeKnown || signal.Outcome.HoldingDays.All(day => dates.Contains(day.Date))) return signal;
            Count(counts, NoUniverseDayCount);
            return signal with { Outcome = signal.Outcome with { OutcomeKnown = false } };
        }).ToArray();
    }

    /// <summary>The New York session date of a daily bar's opening stamp.</summary>
    public static DateOnly SessionDate(DateTimeOffset timestamp) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, NewYork).DateTime);

    // The shared decision path on one completed bar: the setup signal, the session window and the entry rules; the admitted
    // direction or null.
    private static string? SignalDirection(StrategyDefinition strategy, SignalStudyStock stock, int index, SignalGenerator generator,
        StrategySessionClock clock)
    {
        var signal = generator.CreateTradeSignal(strategy, stock.Bars, stock.Snapshots, index);
        if (signal is null || !clock.ValidateExecutionWindow(stock.Bars[index].Timestamp, strategy.Timeframe, strategy.Session)) return null;
        return StrategyDecisionKernel.ResolveEntryDirection(strategy, signal, stock.Snapshots[index]).Direction;
    }

    // Follows one signal from its entry to the strategy's exit (at the open after the exit bar), closing before a split
    // ex-date; null when a split leaves no complete holding day. An exit after the window end gives an unknown outcome.
    private static StudiedSignal? Follow(StrategyDefinition strategy, SignalStudyStock stock, DateOnly[] dates, int signalIndex, string direction,
        SignalStudySettings settings, IDictionary<string, int> counts)
    {
        var nextOpen = settings.Entry == SignalStudyEntry.NextOpen;
        var entryIndex = nextOpen ? signalIndex + 1 : signalIndex;
        var (exitIndex, reason) = ExitByRule(strategy, stock, signalIndex, direction, nextOpen);
        var splitIndex = LastOpenBeforeSplit(stock, dates, signalIndex, exitIndex ?? stock.Bars.Count - 1);
        if (splitIndex is { } split)
        {
            if (split <= signalIndex + 1)
            {
                Count(counts, SplitAtEntryCount);
                return null;
            }
            (exitIndex, reason) = (split, SplitExitReason);
            Count(counts, SplitClosedCount);
        }
        if (exitIndex is not { } exit)
        {
            Count(counts, OpenAtWindowEndCount);
            return new(Outcome(stock.Ticker, dates[signalIndex], [], settings.CostPerSide, known: false), direction, dates[entryIndex], null,
                OpenAtWindowEndReason);
        }
        var days = HoldingDays(stock, dates, signalIndex, exit, nextOpen, direction == "short");
        return new(Outcome(stock.Ticker, dates[signalIndex], days, settings.CostPerSide, known: true), direction, dates[entryIndex], dates[exit], reason!);
    }

    // The strategy's own technical or time exit checked on each completed bar after entry (from the fill bar itself when the
    // strategy checks exits from the fill day and enters at the next open); the exit fills at the following open. Returns the
    // exit open's index and reason, or (null, null) when no exit fills inside the loaded bars.
    private static (int? ExitIndex, string? Reason) ExitByRule(StrategyDefinition strategy, SignalStudyStock stock, int signalIndex,
        string direction, bool nextOpen)
    {
        var first = nextOpen && !strategy.ExitRules.CheckExitsFromFillDay ? signalIndex + 2 : signalIndex + 1;
        var entryStamp = stock.Bars[nextOpen ? signalIndex + 1 : signalIndex].Timestamp;
        var engine = new TechnicalExecutionEngine();
        for (var index = first; index + 1 < stock.Bars.Count; index++)
        {
            var barsHeld = index - first + 1;
            var snapshot = stock.Snapshots[index];
            var previous = stock.Snapshots[index - 1];
            var reason = direction == "short"
                ? TechnicalExecutionEngine.GetShortTechnicalOrTimeExitReason(strategy, snapshot, barsHeld, stock.Bars[index].Timestamp, entryStamp, previous)
                : engine.GetTechnicalOrTimeExitReason(strategy, snapshot, barsHeld, stock.Bars[index].Timestamp, entryStamp, previousSnapshot: previous);
            if (reason is not null) return (index + 1, reason);
        }
        return (null, null);
    }

    // The index of the last open before the first split ex-date after the signal day and up to the planned exit open; null
    // when no split falls in the hold.
    private static int? LastOpenBeforeSplit(SignalStudyStock stock, DateOnly[] dates, int signalIndex, int lastIndex)
    {
        var exDate = stock.SplitExDates.Where(date => date > dates[signalIndex] && date <= dates[lastIndex]).Order().Cast<DateOnly?>().FirstOrDefault();
        if (exDate is not { } ex) return null;
        var index = Array.FindLastIndex(dates, date => date < ex);
        return index;
    }

    // Open-to-open returns from the entry to the exit open, keyed by each starting session; a same-close entry's first day runs
    // from the signal close to the following session's next open. A short earns the negated return.
    private static SignalHoldingDay[] HoldingDays(SignalStudyStock stock, DateOnly[] dates, int signalIndex, int exitIndex, bool nextOpen, bool isShort)
    {
        var days = new List<SignalHoldingDay>();
        for (var index = signalIndex + 1; index < exitIndex; index++)
        {
            var start = !nextOpen && index == signalIndex + 1 ? stock.Bars[signalIndex].Close : stock.Bars[index].Open;
            var value = Return(start, stock.Bars[index + 1].Open);
            days.Add(new(dates[index], isShort ? -value : value));
        }
        return days.ToArray();
    }

    // A signal outcome keyed by its stock (also standing in for the issuer) and signal date.
    private static SignalOutcome Outcome(string ticker, DateOnly signalDate, IReadOnlyList<SignalHoldingDay> days, double cost, bool known) =>
        new(ticker, ticker, signalDate, days, cost, known);

    // The simple return from one price to another; a non-positive starting price is invalid evidence.
    private static double Return(decimal from, decimal to) => from > 0m
        ? (double)(to / from) - 1d
        : throw new InvalidDataException($"A non-positive price {from} cannot start a return.");

    // Bars and snapshots must be the same completed observations in the same order.
    private static void RequireAligned(SignalStudyStock stock)
    {
        if (stock.Bars.Count != stock.Snapshots.Count || stock.Bars.Where((bar, index) => bar.Timestamp != stock.Snapshots[index].Timestamp).Any())
            throw new InvalidDataException($"{stock.Ticker}: bars and indicator snapshots are not aligned.");
    }

    // Adds one to a named count.
    private static void Count(IDictionary<string, int> counts, string name) => counts[name] = counts.TryGetValue(name, out var value) ? value + 1 : 1;
}
