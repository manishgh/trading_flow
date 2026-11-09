# Core strategies on the volatile wishlist - 9 November 2026

Diagnostic backtests of the kept core strategies through the ordinary saved-plan workflow
(build `2dfabc1`, site `127.0.0.1:5088`). Not a promotion: no pass/fail rules were frozen
before these runs, so no version is retired on these numbers.

## Set-up (identical for every plan)

- Stocks: the `volatile` wishlist without SOXL (an ETF), 24 stocks. Current membership, so
  results carry hindsight in the stock list.
- Groups: random order, seed 20261109, 5 stocks per group:
  G1 DIOD FPS RDW RGTI SIVE; G2 BKSY BXDC MRVL OUST PATH; G3 MIAX MXL NBIS PGY STM;
  G4 ASTS CRCL MVST POET SMCI; G5 CRDO DASH NVTS RKLB.
- Each strategy and group is its own $10,000 portfolio: 1% account risk, 25% position cap,
  at most 4 positions, profile `swing-backtest-profile.yaml` (configuration SHA-256 9f807488...).
- Daily eligibility judged from completed earlier sessions only:
  - Connors: previous close at least $5, 20-session average volume at least 1M shares, 20 sessions of history.
  - ORB: that day's open at least $5, 14-session average volume at least 1M shares, ATR(14) above $0.50.

| Plan | Strategies | Window (UTC) |
|---|---|---|
| 73 `connors-volatile-6m-20261109` | Connors 7.0.0, 9.0.0, Connors short 2.0.0 | 21 Apr 04:00 - 21 Oct 04:00 2026 |
| 82 `connors-volatile-6m-g1-without-sive-20261109` | same, G1 without SIVE | same |
| 77 / 79 `orb-1-2-0 / orb-2-0-0-volatile-oct-20261109` | ORB 1.2.0 / 2.0.0 | 1 Oct 04:00 - 1 Nov 04:00 2026 |
| 84 / 86 `...-g1-without-sive-20261109` | ORB 1.2.0 / 2.0.0, G1 without SIVE | same |

## Exclusion

SIVE: Alpaca returned no daily bars on SIP or IEX for 1 Jun 2025 - 8 Nov 2026 (HTTP 200, one
page, 0 bars). Alpaca's asset `SIVE` is "Silver Verde May Mining", OTC, inactive, not tradable.
Plan 73 G1 therefore published no economics; G1 was re-run without SIVE (plan 82). 1 of 24
stocks (4%), below the 5% escalation threshold.

## Connors monthly returns (% of month-opening equity, marked to market)

April (from 21 Apr) and October (to 20 Oct) are partial months. G1 is plan 82.

Connors 7.0.0 (risk-managed, gaps kept)

| Month | G1 | G2 | G3 | G4 | G5 | Closed trades |
|---|---|---|---|---|---|---|
| 2026-04 (partial) | 0.00 | 0.56 | -0.02 | 0.00 | 0.00 | 0 |
| 2026-05 | 0.00 | 0.03 | 0.87 | 0.00 | 0.83 | 3 |
| 2026-06 | 0.16 | 0.00 | 0.61 | 0.40 | 1.13 | 5 |
| 2026-07 | 0.00 | 0.48 | 0.35 | -0.10 | -0.24 | 5 |
| 2026-08 | -0.64 | -0.72 | -0.28 | 0.00 | -0.12 | 5 |
| 2026-09 | 0.14 | 1.07 | 0.48 | 0.00 | 0.05 | 6 |
| 2026-10 (partial) | 0.00 | -0.62 | -0.81 | -1.20 | 0.00 | 3 |

Connors 9.0.0 (published rules, no stop, sized by position cap)

| Month | G1 | G2 | G3 | G4 | G5 | Closed trades |
|---|---|---|---|---|---|---|
| 2026-04 (partial) | 0.00 | 3.47 | -0.06 | 0.00 | 0.00 | 0 |
| 2026-05 | 0.00 | 0.22 | 3.73 | 0.00 | 4.24 | 3 |
| 2026-06 | 1.59 | 0.00 | 2.34 | 3.17 | 9.83 | 5 |
| 2026-07 | 0.00 | 3.35 | 4.53 | -0.82 | 1.18 | 7 |
| 2026-08 | -2.79 | -3.95 | -0.36 | 0.00 | 0.00 | 1 |
| 2026-09 | 0.01 | 3.45 | 3.03 | 0.00 | 0.15 | 7 |
| 2026-10 (partial) | 0.00 | -2.67 | -2.40 | -3.52 | 0.00 | 0 |

Connors short 2.0.0 (published rules, no stop)

| Month | G1 | G2 | G3 | G4 | G5 | Closed trades |
|---|---|---|---|---|---|---|
| 2026-04 (partial) | 0.00 | 0.00 | 0.75 | 1.27 | 0.80 | 3 |
| 2026-05 | -0.24 | -2.23 | 4.12 | 0.73 | 0.00 | 3 |
| 2026-06 | 0.00 | -0.45 | -0.66 | 0.00 | 0.00 | 2 |
| 2026-07 | 0.00 | 0.22 | 0.73 | 2.00 | 0.37 | 5 |
| 2026-08 | 0.00 | -4.09 | 0.00 | -2.52 | -0.33 | 4 |
| 2026-09 | 0.00 | 0.00 | 0.00 | 0.00 | -1.07 | 2 |
| 2026-10 (partial) | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0 |

Trade counts are small (0-7 closed trades a month across all five groups), so these months
cannot separate skill from chance.

## ORB October 2026

| Version | G1 (no SIVE) | G2 | G3 | G4 | G5 |
|---|---|---|---|---|---|
| 1.2.0 return | n/a (22 trades) | n/a (38) | n/a (63) | n/a (35) | -6.80% (37 trades) |
| 2.0.0 return | n/a (15 trades) | n/a (19) | n/a (43) | n/a (23) | -5.32% (19 trades) |

"n/a": the run withheld economics because a stock's regular-session five-minute candle
record has a gap (G1 DIOD, G2 BKSY from 19 Oct 15:10 UTC and BXDC from 21 Oct 16:10 UTC,
G3 MIAX, G4 MVST). Checked for BKSY 19 Oct 15:10-15:15 UTC: Alpaca SIP has no 5-minute bar,
but 51 trades exist (35 odd-lot `I`, 14 `F+I`, 1 `4+I`, 1 average-price `B`; 601 shares).
None of these trade types sets a bar's price, so the missing candle is correct provider
behaviour, not lost data. The replay treats any missing regular-session candle as missing
evidence; resolving that needs an engine decision, not more data.

## Snapback not run

Snapback 2.0.0 and snapback short 2.0.0 require the frozen company-news package
`historical-news:12629c5c...`. 2,744 of its 3,015 news revisions live in
`C:\Users\manis\Documents\Codex\2026-10-03\c\outputs\data-gap-collection-20261019`, deleted on
31 October 2026, so the server cannot verify the package and refuses the plan. The remaining
271 revisions are in `data/research/evidence/historical-news-source-packages/diagnostic-20261022-verified`.

Results are retained under `C:\project\trading_flow\data\backtest\results\.durable-attempts\backtest`.
