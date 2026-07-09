using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Regime;

namespace TradingFlow.Backtesting;

// Layer-2 regime wiring (docs/strategy-design-doctrine.md §2): builds a no-lookahead regime
// calendar per strategy that declares an active regime rule. The calendar is consumed as an
// entry gate at trade acceptance, so a strategy takes new entries only on regime-on days. The
// benchmark-daily loading (with resample fallback) is shared with the live path via RegimeGateService.
public sealed partial class BacktestRunner
{
    private readonly RegimeGateService _regimeGate = new();

    private async Task<IReadOnlyDictionary<string, RegimeCalendar>> BuildRegimeCalendarsAsync(
        BacktestRunConfig run,
        IReadOnlyCollection<StrategyDefinition> strategies,
        DateTimeOffset dataStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        var calendars = new Dictionary<string, RegimeCalendar>(StringComparer.OrdinalIgnoreCase);
        var active = strategies.Where(strategy => strategy.Regime is { IsActive: true }).ToArray();
        if (active.Length == 0)
        {
            return calendars;
        }

        IMarketDataProvider? provider = null;
        var barsByBenchmark = new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase);
        try
        {
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

        return calendars;
    }
}
