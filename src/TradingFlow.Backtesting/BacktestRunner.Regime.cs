using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Backtesting;

// Layer-2 regime wiring: builds a no-lookahead regime
// calendar per strategy that declares an active regime rule. The calendar is consumed as an
// entry gate at trade acceptance, so a strategy takes new entries only on regime-on days. The
// benchmark-daily loading (with resample fallback) is shared with the live path via RegimeGateService.
public sealed partial class BacktestRunner
{
    private readonly RegimeGateService _regimeGate = new();
    private IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? candidateSessionSchedules;
    private IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> factorReferenceBars =
        new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase);

    // Load actual replay source dates for intraday books; other horizons retain their expiry-calendar lookahead.
    private async Task<IReadOnlyDictionary<string, RegimeCalendar>> BuildRegimeCalendarsAsync(
        BacktestRunConfig run,
        IReadOnlyCollection<StrategyDefinition> strategies,
        DateTimeOffset dataStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        var calendars = new Dictionary<string, RegimeCalendar>(StringComparer.OrdinalIgnoreCase);
        var active = strategies.Where(strategy => strategy.Regime is { IsActive: true }).ToArray();

        IMarketDataProvider? provider = null;
        var barsByBenchmark = new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Reuse a provider's explicit calendar for deterministic CSV/local fixtures as well as Alpaca; never infer one from bars.
            provider = CreateProvider(run);
            if (provider is IMarketSessionScheduleProvider scheduleProvider)
            {
                candidateSessionSchedules = await scheduleProvider.LoadMarketSessionSchedulesAsync(
                    DateOnly.FromDateTime(dataStart.UtcDateTime).AddDays(-1),
                    BacktestCalendarBounds.IntradayLastRequiredDate(run, windowEnd, ResolveExchangeTimezone(strategies))
                        ?? DateOnly.FromDateTime(windowEnd.UtcDateTime).AddDays(10), cancellationToken);
                if (candidateSessionSchedules.Count == 0)
                    throw new InvalidOperationException("Authoritative exchange calendar is empty; backtest setup expiry cannot be established.");
            }
            else if (run.Provider.Equals("alpaca", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Authoritative exchange calendar unavailable for Alpaca backtest.");
            foreach (var strategy in active)
            {
                var regime = strategy.Regime!;
                if (!barsByBenchmark.TryGetValue(regime.BenchmarkSymbol, out var bars))
                {
                    provider ??= CreateProvider(run);
                    // Over-fetch so the SMA has enough prior daily closes before the window begins.
                    var fetchStart = dataStart.AddDays(-((regime.SmaPeriod * 2) + 15));
                    bars = await _regimeGate.LoadBenchmarkDailyBarsAsync(
                        provider,
                        regime.BenchmarkSymbol,
                        run.Intervals,
                        fetchStart,
                        windowEnd,
                        cancellationToken);
                    barsByBenchmark[regime.BenchmarkSymbol] = bars;
                }

                if (bars.Count < regime.SmaPeriod + 1)
                    throw new InvalidOperationException($"Benchmark {regime.BenchmarkSymbol} history is unavailable or insufficient for SMA{regime.SmaPeriod}; active market filter cannot be evaluated.");
                calendars[strategy.StrategyId] = RegimeCalendarBuilder.Build(bars, regime.SmaPeriod);
            }
        }
        finally
        {
            if (provider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        // Reuse the regime's native benchmark observations; the assembler truncates them per decision.
        var needed = strategies.Select(strategy => strategy.EntryRules.TechnicalFactor)
            .Where(factor => factor?.Metric == TechnicalFactorRules.RelativeRoc).ToArray();
        foreach (var factor in needed)
            if (!barsByBenchmark.ContainsKey(factor!.BenchmarkSymbol))
                throw new InvalidOperationException("Relative strength requires an active matching benchmark regime.");
        factorReferenceBars = barsByBenchmark;
        return calendars;
    }
}
