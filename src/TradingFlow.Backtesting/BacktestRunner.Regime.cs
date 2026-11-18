using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Indicators;
using Microsoft.Extensions.Logging;

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

    // Load the authoritative calendar once for regime and replay decisions, including the sampled account's initial anchor.
    private async Task<IReadOnlyDictionary<string, RegimeCalendar>> BuildRegimeCalendarsAsync(
        BacktestRunConfig run,
        IReadOnlyCollection<StrategyDefinition> strategies,
        DateTimeOffset dataStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken,
        DateTimeOffset? evaluationStart = null)
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
                    BacktestCalendarBounds.CandidateFirstRequiredDate(run, dataStart, evaluationStart ?? dataStart),
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
                    var fetchStart = dataStart.AddDays(-((regime.RequiredCloses * 2) + 15));
                    bars = await _regimeGate.LoadBenchmarkDailyBarsAsync(
                        provider,
                        regime.BenchmarkSymbol,
                        run.Intervals,
                        fetchStart,
                        windowEnd,
                        cancellationToken);
                    barsByBenchmark[regime.BenchmarkSymbol] = bars;
                }

                if (bars.Count < regime.RequiredCloses + 1)
                    throw new InvalidOperationException($"Benchmark {regime.BenchmarkSymbol} history is unavailable or insufficient for SMA{regime.SmaPeriod} (rising over {regime.SmaRisingLookbackBars} sessions); active market filter cannot be evaluated.");
                var calendar = RegimeCalendarBuilder.Build(bars, regime.SmaPeriod, regime.SmaRisingLookbackBars);
                if (regime.UsesNasdaqHomeMarket)
                    calendar = await WithNasdaqHomeMarketAsync(run, provider!, regime, calendar, dataStart, windowEnd, cancellationToken);
                calendars[strategy.StrategyId] = calendar;
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

    // Option A of the 18 Nov 2026 design: adds the home-market fund's calendar (QQQ, same rule) and every symbol's listing
    // exchange from the run's frozen listing source. A run whose data source cannot name listings is refused, not guessed.
    private async Task<RegimeCalendar> WithNasdaqHomeMarketAsync(BacktestRunConfig run, IMarketDataProvider provider, RegimeRules regime,
        RegimeCalendar calendar, DateTimeOffset dataStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        var nasdaqBars = await _regimeGate.LoadBenchmarkDailyBarsAsync(provider, regime.NasdaqListingBenchmark!, run.Intervals,
            dataStart.AddDays(-((regime.RequiredCloses * 2) + 15)), windowEnd, cancellationToken);
        if (nasdaqBars.Count < regime.RequiredCloses + 1)
            throw new InvalidOperationException($"Home-market fund {regime.NasdaqListingBenchmark} history is unavailable or insufficient for SMA{regime.SmaPeriod} (rising over {regime.SmaRisingLookbackBars} sessions).");
        var exchanges = await LoadListingExchangesAsync(run, provider, regime, cancellationToken);
        if (exchanges.Count == 0)
            throw new InvalidOperationException("The listing-exchange source returned no listings; the Nasdaq home-market rule cannot be evaluated.");
        RecordListingCoverage(run, exchanges);
        return calendar.WithNasdaqHomeMarket(
            RegimeCalendarBuilder.Build(nasdaqBars, regime.SmaPeriod, regime.SmaRisingLookbackBars), exchanges);
    }

    // Listings come from the run's own data source. Prepared and cached runs wrap that source, so when the wrapper cannot
    // name listings the unwrapped configured source (for example the evidence catalog's frozen security master) is opened
    // for this one lookup and disposed. A source that cannot name listings refuses the run instead of guessing.
    private async Task<IReadOnlyDictionary<string, string>> LoadListingExchangesAsync(BacktestRunConfig run, IMarketDataProvider provider,
        RegimeRules regime, CancellationToken cancellationToken)
    {
        if (provider is IListingExchangeSource direct) return await direct.LoadListingExchangesAsync(cancellationToken);
        var configured = _dataProviders.CreateMarket(run, RequireRawArchiveWriter(), _csvSourceRoot);
        try
        {
            if (configured is IListingExchangeSource listings) return await listings.LoadListingExchangesAsync(cancellationToken);
            throw new InvalidOperationException($"The Nasdaq home-market rule ({regime.NasdaqListingBenchmark}) needs a data source that names listing exchanges; provider '{run.Provider}' does not.");
        }
        finally
        {
            (configured as IDisposable)?.Dispose();
        }
    }

    // Counts the run's stocks without a usable listing (absent from the snapshot or ambiguous); they are refused as
    // listing_exchange_unknown. The count is logged for the run evidence; above 5% of the run's stocks it is a warning to
    // escalate to the operator, since that many refusals would change which stocks the test covers.
    private void RecordListingCoverage(BacktestRunConfig run, IReadOnlyDictionary<string, string> exchanges)
    {
        var stocks = run.Tickers.Select(ticker => ticker.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        if (stocks.Length == 0)
        {
            _logger.LogWarning("Nasdaq home-market rule: listing coverage was not measured because the run names no stocks before the run starts.");
            return;
        }
        var unknown = stocks.Count(ticker => !exchanges.ContainsKey(ticker));
        var share = (decimal)unknown / stocks.Length * 100m;
        if (share > 5m)
            _logger.LogWarning("Nasdaq home-market rule: {Unknown} of {Stocks} stocks ({Share:F1}%) have no usable listing and will be refused as listing_exchange_unknown; above the 5% escalation threshold.",
                unknown, stocks.Length, share);
        else
            _logger.LogInformation("Nasdaq home-market rule: {Unknown} of {Stocks} stocks ({Share:F1}%) have no usable listing and will be refused as listing_exchange_unknown.",
                unknown, stocks.Length, share);
    }
}
