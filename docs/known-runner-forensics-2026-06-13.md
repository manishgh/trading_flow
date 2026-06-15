# Known Runner Forensics - 2026-06-13

## Scope

This analysis uses the user-provided intraday runner basket only:

- ASTC 2026-05-27
- HUBC 2026-05-29
- STI 2026-06-04
- INHD 2026-06-08
- SUNE 2026-06-08
- AZI 2026-06-09
- DSY 2026-06-10
- VSME 2026-06-10
- MASK 2026-06-11

Swing strategies are excluded. These names are intraday runner examples.

## No Lookahead Rule

The strategy may only act on bars available at the decision time. Later peak price is used only for forensic analysis. It must not be used by the strategy to select entries, exits, or ticker universe during a realistic test.

## What Failed

The engine was usually not missing the ticker completely. It often entered early, got shaken out, and then was blocked from a second entry by `max_entries_per_ticker_per_day: 1`.

Examples from V3 structural exit:

| Ticker | Date | Regular Move | First Entry NY | First Exit NY | Exit | Later Peak From Entry |
|---|---:|---:|---:|---:|---|---:|
| ASTC | 2026-05-27 | 166.53% | 09:34 | 09:35 | stop_loss | 166.21% |
| INHD | 2026-06-08 | 3807.21% | 09:55 | 10:02 | stop_loss | 1681.91% |
| SUNE | 2026-06-08 | 217.11% | 09:34 | 09:42 | confirmed_vwap_failure | 210.87% |
| AZI | 2026-06-09 | 138.91% | 09:33 | 09:34 | trailing_stop | 90.43% |
| VSME | 2026-06-10 | 45.80% | 09:32 | 09:36 | trailing_stop | 20.44% |

The common failure was opening volatility, not lack of a bullish signal. That supports a controlled second-chance model.

## Confirmed VWAP Failure

Instant `technical_exit_below_vwap` was too fragile for runners. It is now treated as a warning when `enable_confirmed_vwap_exit` is true.

The confirmed VWAP failure rule exits only when all are true:

- Minimum hold bars have passed.
- The trade has not already reached the configured R threshold.
- The last N bars closed below `VWAP - ATR buffer`.

For V4:

- `confirmed_vwap_exit_bars: 3`
- `confirmed_vwap_exit_atr_buffer: 0.15`
- `disable_confirmed_vwap_exit_after_r: 1.0`

This keeps VWAP as a risk control for failed entries without letting one noisy close kill a developing runner.

## Second Chance Result

V4 keeps the same VWAP/EMA/MACD/RVOL entry and confirmed-exit logic, but allows `max_entries_per_ticker_per_day: 2`.

Focused known-runner result:

- V2 baseline: 8.46%, 6.87% max drawdown, 23 trades
- V3 structural exit: 12.94%, 5.50% max drawdown, 23 trades
- V4 second chance: 35.28%, 8.33% max drawdown, 43 trades

Broad 180-day volatile-stock result:

- V4 second chance: 4.53%, 6.14% max drawdown, 176 trades
- Lance V4 reclaim: 4.18%, 3.50% max drawdown, 97 trades
- V7 green-day reclaim: 2.04%, 2.63% max drawdown, 53 trades

## Verdict

V4 is the best runner-capture candidate, but not yet the safest paper default. It improves the exact problem the user identified, but it increases trade count and drawdown.

Next production-grade risk step:

- Add a per-ticker session loss guard.
- Halt a ticker after configurable realized loss or repeated failed entries.
- Keep max two entries for runner mode only.
- Keep position risk fixed; do not use later peak movement to justify wider risk after the fact.
