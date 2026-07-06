# Finviz-Sourced Universe Screening

How to build a broad, non-hand-picked candidate pool from a Finviz Elite screener
and run it through the no-lookahead point-in-time universe screen. Companion to
`docs/edge-recovery-master-plan.md` phase 0.1.

## How it fits together

```
Finviz screener export  ->  candidate pool (broad, structural)
        |
        v
HistoricalScreenerUniverseProvider  ->  per-as-of selection using only
   (min price / min $volume /            daily bars dated < evaluation start
    prior return, ranked, capped)
        |
        v
BacktestRunner replaces run.Tickers with the screened list
```

The Finviz screen supplies the *pool*. The no-lookahead screen governs *selection*.

## Config

```yaml
universe:
  mode: historical_screener
  candidate_source: finviz            # or "static" (default) to use the tickers: list
  candidate_screener_query: "https://elite.finviz.com/export.ashx?v=111&f=...&auth=..."
  min_price: 5.0                      # applied point-in-time from prior daily bars
  min_avg_dollar_volume: 5000000
  lookback_days: 20
  max_symbols: 40                     # keep the most liquid N after screening
validation:
  bias_risk:
    universe_source: historical_screener   # runner overrides to historical_screener/finviz
```

`FINVIZ_API_KEY` must be set (Finviz Elite auth token). The screener query may be a
full Elite export URL or a bare filter string; `FinvizClient` normalizes both.

## Honesty boundary (read this)

Finviz returns a **current** screen — there is no historical as-of. Consequences:

- **Paper / live / forward test:** fully correct. Screening today for today's
  candidates is real trading. This is the intended home for the researched
  screeners, and it is the same brain the backtest validates.
- **Historical backtest:** the pool carries **delisting survivorship** (a name that
  died mid-window is not in today's screen). The no-lookahead screen keeps
  *within-window selection* clean, but cannot restore delisted names to the pool.

Mitigation, enforced by convention: **use structural / liquidity screens, not
outcome screens.** Good pool filters are price, average volume, market cap, sector,
optionable, shares outstanding. Bad pool filters are top gainers, new highs,
unusual volume today, or "% change" — those import the very hindsight phase 0.1
removes. The runner records `universe_source = historical_screener/finviz` and the
existing survivorship warning so the bias is visible in the validation report.

## Researched screener starting points

Replace with your own Finviz Elite screens. These are structural templates.

**Swing candidate pool** (liquid mid/large caps able to trend for days):

```
price >= $5, avg volume >= 1M shares, market cap >= small,
optionable, US common stock, not ETF
```
Example filter fragment: `f=cap_smallover,sh_avgvol_o1000,sh_price_o5,sh_opt_option,geo_usa`

**Intraday candidate pool** (liquid, volatile enough to move within a session):

```
price >= $3, avg volume >= 2M shares, relative volume elevated (structural, not
today's gainer), ATR / range sufficient
```
Example filter fragment: `f=sh_avgvol_o2000,sh_price_o3,ta_volatility_mo2,geo_usa`

Keep both screens **structural** (capacity to move), never **outcome** (already moved).

## Known remaining limitations

- **Per-run, not per-day.** Selection happens once at the evaluation start. A
  ticker that qualifies mid-window is treated as eligible for the whole window.
  True per-day membership (an entry gate keyed on as-of-day screen membership) is
  the next increment.
- **Daily bars required.** Screening needs `1d` bars from the market data provider.
  Alpaca serves these natively (the intended path). Pure-CSV runs without daily
  files resolve to zero and throw a clear error rather than silently trading nothing.
- **Candidate pool survivorship** as described above; only historical
  index-constituent data (a future data step) fully removes it.
