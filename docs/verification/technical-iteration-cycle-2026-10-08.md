# Technical indicator development cycle - 8 October 2026

User explicitly requested research/iteration beyond named examples and monthly,
strategy-specific reporting only. Original immutable audit totals are retained
internally; no accumulated or pooled returns appear in the new report.

Seven precise preregistered recipes,35independent strategy/batch runs,22eligible
stocks in unchanged4/4/5/5/4subsets,7April-6October2026. This is already-viewed
development data, not an untouched holdout. OOS/walk-forward are disabled. Risk1%,
capital10000,25%notionalcap,fourslots,10bpsslippage,fees and no-news are fixed.

Recipes: RSI(2)<5control; only-threshold RSI(2)<10; previous-day RSI(2)<5 then close
above prior high; previous-day lower20/2Bollinger close then prior-high reclaim;
atleast3downcloses endingyesterday then prior-high reclaim; old quality-reclaim
entry/exits normalized to daily/10bps/SPY200/1%risk/260warmup/no-news; EMA20/50
trend-STATE continuation with specified long-hold/EMA20/trailing exits. All fields
and hashes are frozen in outputs/technical-cycle-20261008/trial-registry.json.
Unused stretch OR arms disabled; lookback1; repeated qualifying states allowed
and same-symbol overlap blocked. EMA is state eligibility, not a new crossover.
All family comparisons except RSI-threshold are complete hypotheses, not single
indicator ablations. No claiming exact historical legacy strategy replication.

Precommitted final-session-open liquidation is opt-in, fixed before outcomes and
validated as the last authoritative complete native daily source session. Entries
at/after it are rejected. Boundary exits use only sourceOpen, prior completed
liquidity for capacity, and no final H/L/C/RVOL/ATR/volume. Ordinary technical
exits landing at boundary are normalized to the same policy. Missing calendar,
source slice, prior liquidity or remaining liquidation quantity fails completeness.
Old None/v3 strict policy remains unchanged. Daily opening-point execution still
is not real quote calibration or entry-day protection. Profilev4/manifest hashes
make the new study boundary explicit; it grants no paper/live authority.

Monthly percentages use source NY session dates, prior-month closing marked equity
as next month opening, and127calendar source valuations. April7-30 andOctober1-6
are partial. One strategy per run, actual ledger fills/fees, exact independent-vs-
unified trade equality and final cash reconciliation required before export.
Existing correctedv10/v6 reports were separately exported to70monthly rows.

Research sources: Alvarez ABCs mean-reversion framework;
https://alvarezquanttrading.com/blog/the-abcs-of-creating-a-mean-reversion-strategy-part1/
Bollinger authored methods https://www.bollingerbands.com/tradestation-methods
Reversal cost paper https://repub.eur.nl/pub/25718/AnotherLook_2011.pdf
AQR trend research https://www.aqr.com/insights/research/journal-article/a-century-of-evidence-on-trend-following-investing
These motivate hypotheses; the recipes are not claims of exact replication or
proven edge. Every losing, null and failed outcome is retained, with no parameters
changed during the cycle. Data is isolated and owned inputs deleted after checks.

Independent quant/requirements approved scope and terminal design; initial70
focused checks passed after final implementation, with earlier74/35checks overlap.
Final affected tests, monthly reports and result review follow execution.
