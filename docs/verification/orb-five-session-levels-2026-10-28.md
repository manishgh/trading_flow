# Prior-five-session levels before ORB entries

User-approved read-only assessment, 28 October 2026. This checks the retained ORB 1.2.0 trades before proposing an entry-rule change. It adds no strategy, configuration, runner, API or UI behavior.

## Completed work

- [x] Freeze the level definition and signal-time comparison before grouping outcomes.
- [x] Obtain independent Sol 6.1 High quant review of the method.
- [x] Read the retained eight-stock October 1–26 results and their existing candle/calendar inputs.
- [x] Classify all 74 trades using the five immediately preceding complete exchange sessions.
- [x] Reconcile each stock's attributed trade net to its published monthly realized net.
- [x] Independently compare source dates, prices, arithmetic, trade outcomes and retained input hashes.
- [x] Preserve all source data, original results and analysis attempts; no downloads, execution or cleanup.

## Definition and findings

Each prior regular session contributes its high and low. These ten raw historical prices are possible obstacles, not demonstrated support/resistance strength or confirmed pivot bands. For a long, choose the closest price strictly above the completed signal close; for a short, choose the closest strictly below. Compare that distance with three times the signal-close-to-structural-stop distance. The future entry fill is excluded from this qualification calculation. Equal-target levels and absent opposing levels remain separate.

All 370 prior-session references across 121 unique stock/session pairs have complete expected five-minute bars. The classifications are 35 levels strictly before planned 3R, one exactly at 3R, 20 finite levels beyond 3R and 18 with no opposing level in the window; none are unknown. Actual target exits in those groups are respectively eight, zero, four and one.

The 35 obstacle trades contributed losses in CRDO, MU, MXL and TSM, but profits in NVDA, PATH, POET and SMCI. A blanket veto would exclude eight of the thirteen existing target winners. The finite-beyond-3R group also contains losing contributions in four of its six represented stocks. This sample therefore does not establish that rejecting entries near prior daily extrema improves the strategy consistently. Keep the strategy unchanged on this evidence.

The full report presents October separately by stock and level category. Those dollar amounts attribute existing filled-trade outcomes; they are not returns from a filtered portfolio. A real filter would change admission, rearming, later opportunities, sizing and the daily trade cap, requiring a separately approved shared-runner experiment. No such experiment was run here.

## Evidence and checks

- [Frozen method](../../outputs/orb-weekly-levels-20261028/method.md)
- [Stock-by-stock report and examples](../../outputs/orb-weekly-levels-20261028/analysis-report.md)
- [Exact trade/source evidence](../../outputs/orb-weekly-levels-20261028/evidence.json)
- [Independent method review](../../outputs/orb-weekly-levels-20261028/independent-quant-method-review.md)
- [Independent final review](../../outputs/orb-weekly-levels-20261028/independent-quant-final-review.md)
- [Independent source recomputation](../../outputs/orb-weekly-levels-20261028/independent-quant-recomputation-verified-v2.json)
- [Independent fieldwise comparison: 2,543 checks, zero failures](../../outputs/orb-weekly-levels-20261028/independent-quant-evidence-comparison-verified-v2.json)

Only read-only arithmetic and Markdown/JSON evidence were produced; no executable files were added. Compilation and runtime tests were not needed for this prose/analysis checkpoint. Scoped whitespace checks and report-link existence checks passed. The independent final reviewer accepted the descriptive report, with all 59 recorded source hashes matching. Prior stopped arithmetic checks remain recorded, including separate display rounding and annotated deadline-reason differences; the selected verified evidence reconciles the exact terminal ledger and monthly amounts.

This remains an 18-session development sample with the original OHLC execution, historical borrow, universe and receipt/revision limitations. No paper/live activation, promotion, core-data deletion or intermediate cleanup follows from this analysis.
