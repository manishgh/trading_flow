# Why entries fail, and the next proposed comparison

Date: 30 October 2026. Status: user-approved implementation complete; 453 focused checks pass and independent engineering/quant reviews accepted. Economic comparison dates remain to be selected and reviewed separately.

The user requested research and independent quant review before changing anything, then approved this proposal with “lets implement it.” That approval covers the bounded implementation below, focused offline verification and normal API/Web/Android integration. It does not authorize trading, core-data changes or an unreviewed economic comparison period. Sol 6.1 High quant and engineering reviewers inspected the retained evidence and existing components.

## What is missing

The current rules identify candle shapes but have not established that those shapes predict a profitable move after entry. Price volatility creates opportunities in hindsight; the useful question is whether information available at the entry distinguishes continuation from reversal after costs. Correct sequencing and accounting are necessary, but do not answer that question.

Version D is `technical.zone-bounce-volume-risk-target@1.0.0`; E is `technical.zone-two-candle-stop@1.0.0`. D's long trigger needs an old support box, previous low at or below its top plus $0.15, a green confirmation candle, and a close above the box. It need not show a fresh touch, reclaim or breakout. E changed the long stop, not that premise. The short trigger is a local downward crossing with RVOL at least 1.2, without a retest or VWAP direction requirement. Neither version has a long breakout-continuation pattern. Long volume is descriptive, although valid historical volume evidence is required.

The independent reviewer examined all 51 actual D/E trades, including winners, plus selected rejected observations. These are overlapping trades on two inspected days, not 51 independent tests. See [review](../../../outputs/zone-strategy-20261029/two-candle-stop/entry-quality-quant-review.md) and [source evidence](../../../outputs/zone-strategy-20261029/two-candle-stop/entry-quality-quant-evidence.json).

| Observation, New York time | What happened | What it establishes |
|---|---|---|
| PATH Oct 27, 10:10 E long | Support $13.75–13.78; previous low $13.93 only qualifies through the $0.15 tolerance. Confirmation $14.025 is 3.29 five-minute ATR above the box; RVOL 0.755. Entry $14.046038, stopped 10:20, net -$29.87. | The named bounce did not require a touch of the actual box. Breaking the prior high did not save this trade. |
| POET Oct 28, 09:40 E long | Green confirmation closes $5.715, exactly the preceding close, below its $5.78 high; RVOL 0.463. Stopped 09:50, net -$31.44. D's wider stop survived to a target. | Green candle color is not necessarily progress; a tighter stop can convert an eventual winner into a loss. |
| MXL Oct 27, 12:15 / 12:40 shorts | RVOL 1.742 / 1.509 passes; price is 3.28 / 3.52 ATR above session VWAP. Both stop. | Volume measures activity, not selling direction. But a below-VWAP MXL short on Oct 28 also loses. |
| MXL Oct 27, 10:25 completed rise | Candle opens $69.78 and closes $71.59, RVOL 1.899; prior low exceeds every qualifying support-touch threshold. | A rising breakout can be outside this bounce-only long definition. No missing candle is implicated. |
| MU Oct 27, 10:25 / 10:35 | Candidates exist, but the 09:40 position still owns the only slot. | These are occupied-position rejections, not omitted eligible fills. More slots are not proposed. |

On E's actual longs, an above-VWAP filter would retain five winners and eight losers and reject three winners and six losers. A prior-high-break filter also retains five winners and eight losers. Deleting historical rows would change later portfolio admissions, so these counts are not counterfactual profits. A previous-close-inside-zone proposal was already examined: it removes nine of D's ten winning longs. No simple exclusion has been demonstrated to solve the problem.

The suggested threshold relaxations do not answer these findings. Lowering short RVOL from 1.2 to 1.0 admits additional shorts but cannot establish direction, and current longs already have no volume threshold gate. D/E no longer impose the earlier 1.5 reward/risk floor, so lowering that absent floor cannot fix these runs. Increasing the touch tolerance from $0.15 to $0.50 would admit candles farther from the actual box; PATH already illustrates the problem at $0.15. None is included in the proposed comparison.

## Earlier work must inform this decision

This review is not an audit of every claimed 100+ iteration. The inspected earlier evidence shows different failure mechanisms:

- Swing VCP generated no candidates in four complete cohorts: many bars failed trend, volume or contraction rules. Connors RSI2 bought deep pullbacks, while its SMA5 recovery exit could still be below entry. Those are specific premises and exit effects, not proof that all swing strategies fail. See `outputs/volatile-swing-followup-20261021/strategy-diagnosis.md`.
- Completed ORB breakout plus candle stop was already tested on October 1–26: 74 trades, 50 stops, 13 original-3R targets; only two of eight stock portfolios positive. It is not a new solution to recommend again.
- The original intraday zone evaluator already has actual overlap/rejection and armed breakout/retest state. Its CRDO October 27–28 quote-based test had one qualifying signal and zero fills: the available prior quote was 7.534 seconds old against a two-second limit. That did not test the profitability of filled retests. It also does not validate a different candle execution model.
- D/E stop and target changes adjusted payoff geometry but did not establish entry advantage. Larger promised reward cannot supply a missing future price move.

## What published research supports—and does not

Zarattini, Barbon and Aziz study opening-range trades across US stocks and condition selection on unusual opening activity. Their relative volume uses opening volume against prior opening volumes; it is not our individual signal-bar ratio. Their universe, exits, leverage and cost model also differ. The useful research direction is to assess which stock-days suit an entry, rather than assume a historically volatile wishlist makes every day suitable. It does not prove a new threshold or our retest strategy profitable. [Author/institution paper](https://www.alexandria.unisg.ch/server/api/core/bitstreams/3c2989c4-688d-4d78-8a71-f02690990d51/content).

Gao et al. find first-half-hour to last-half-hour momentum in ETFs, stronger in particular volume, volatility and macro-news conditions. This supports investigating context and time of day. It does not validate five-minute single-stock bounces or imply that positive headlines should always trigger buys. [Journal article abstract](https://profiles.wustl.edu/en/publications/market-intraday-momentum/).

Bailey et al. explain why selecting among many backtests on finite history produces false discoveries, and why ordinary holdout procedures can be inadequate when the selection process is ignored. Repeatedly changing rules after seeing these same two days is development, not fresh confirmation. The paper does not establish that overfitting caused each observed loss. [Primary paper](https://carmamaths.org/resources/jon/backtest2.pdf).

Inference for TradingFlow: the missing evidence is a repeatable conditional advantage after costs, not another indicator by itself. Market/sector/news context may matter, but it has not been shown to separate our winners from losers. Adding all of it at once would obscure which premise helps. Existing historical news diagnostics also cannot establish original receipt/revision availability merely from a later download.

## One proposed entry comparison

**Hypothesis:** a resistance break followed by a real retest and reclaim is a more coherent long entry than a green candle near an old support box. This is a testable hypothesis, not a claimed improvement in returns. D remains the immutable control. Replace only its long entry package; retain its shorts and all economics. Do not import E's two-candle stop into this comparison.

Proposed long sequence:

1. Use the existing completed 15-minute zone calculator, its pivot delays, five-session history and existing upward flip rule: a resistance zone becomes support after a completed close exceeds its top by the existing 0.2 ATR15 buffer. Require the existing `CanArmRetest` condition: the source identity and resistance role existed before the 15-minute flip candle began. Preserve the existing merge-then-flip ordering at its close and freeze the resulting updated support version and flip time. A newly created source that flips at that same close cannot arm. It must meet D's existing minimum-pivot evidence requirement.
2. Wait for a later regular-session five-minute candle beginning at or after that flip became known. Its range must actually overlap the frozen box: low <= top and high >= bottom. Its completed close must be above the top. No fixed-dollar near-touch substitution and no retrospective entry into the breakout bar. Do not require green color, VWAP side or a new volume threshold as additional gates in this comparison.
3. Cancel an armed long after a completed five-minute close strictly below its frozen bottom, a replacement flip, a new session or after the last-entry cutoff. Bottom equality does not cancel. Preserve D's inclusive 15:50 cutoff: a confirmation completed at 15:50 may qualify; expire only when its decision time is later. Another attempt after consumption requires a new qualifying flip. Reuse the existing state transitions with this explicit cutoff contract.
4. Emit the causal pattern/geometry alternatives, with target evidence pending account-dependent sizing, through the existing candidate-group contract. Consume every armed long source included in that emitted group, even if another alternative is ultimately selected or all fail portfolio admission; sources not emitted remain armed subject to invalidation. Full-cost feasibility remains at chronological portfolio admission using current equity and final capped quantity. Do not estimate it early using future fills or substitute fixed shares. Among eligible long/short alternatives preserve ascending-source-bottom ordering and the existing stable long-before-short tie behavior: use each armed long's FROZEN bottom, and the unchanged short's current source bottom. This is deliberately not an experiment in nearest-zone selection.
5. Submit the accepted intent to the existing next-bar market simulation. Stop remains D's frozen source bottom minus 0.15 ATR5 for longs. Keep D's known-next-box target, otherwise its modeled net-2R fallback. The confirmation ATR is completed and frozen as today. Later fill costs and gap handling remain the common execution policy.

Target evidence must remain causal: use the eligible target-zone snapshot already known at the confirmation candle's start, never a later discovered ceiling. Keep the armed source geometry frozen even if later same-role merges expand its live box. Require the source ID still to be active with support role in that start snapshot; expiration/opposite role cancels it. Exclude that source ID from target alternatives. Preserve D's minimum eligible-zone count using the CURRENT eligible start snapshot. Sort that target snapshot using D's existing bottom order; select the first eligible overhead bottom beyond the modeled entry, with the existing cost checks/fallback behavior. Persist frozen source and target-snapshot versions separately. The current Python resolver uses an exact source ID/version index; it needs a bounded resolver for the frozen-source case rather than pretending the old version still indexes the live snapshot. This source-version reconciliation is an explicit integration change, not permission to use future zones.

```text
Known resistance box
  -> completed 15-minute upward flip
  -> freeze box and wait
  -> later 5-minute actual touch + completed close above box
  -> emit causal alternatives and consume their armed long sources
  -> existing sizing, target/cost checks and portfolio admission
  -> later executable entry

Close below box / expiration / opposite flip / deadline -> cancel
Every emitted long alternative -> consume; no retry after admission rejection
```

Unchanged: eight stocks NVDA/SMCI/MXL/MU/TSM/POET/PATH/CRDO, separate $10,000 books, current risk and 25% notional limits, one position, two filled entries/day, 15:50 New York last-entry and 15:55 exit, 0.10% estimated full spread, 0.10% per-side slippage, $1 per-ticket commission and existing regulatory fees/participation limits. Valid same-slot historical volume evidence remains required; longs remain volume-descriptive and shorts keep RVOL >=1.2. No trailing, news, sector, VWAP, tolerance or additional-entry changes are bundled in.

This replacement changes the sequence and eligible opportunity set, not merely one numeric filter. It can wait too long, miss a move with no retest, and reject winners. A 15-minute flip may arrive after the early MU move. It is not promised to capture that anecdote. The retained shorts may still lose; report long and short contributions separately rather than quietly dropping them.

## Reuse and bounded implementation scope, if approved

Reuse `IntradayZoneStateCalculator`, the armed/frozen state concepts in `IntradayZonePatternEvaluator`, the existing Python-zone candidate/trace contracts, full-cost target planner, capital admission, candle execution and saved-plan runner. Extract/reuse the state primitive rather than fork a strategy-specific runner. The original evaluator bundles VWAP/RVOL gates and confirmation-candle stops, so selecting it directly would change more than intended. Its existing validator is not a drop-in D validator: a separate immutable strategy identity and matching causal evidence validation are required.

No higher-low, order-flow or buying-pressure model is claimed. Quote eligibility checks are execution safeguards. A limit-entry redesign, new UI settings or a regime/news scoring system are outside this proposal. The normal strategy registry, API, Web/Android choice and evidence display must reflect the new identity through their shared contracts. No run-specific executable or custom cohort configuration.

The decision record must retain both the frozen armed source and the active source version in the immutable confirmation-start snapshot. Exclude the source ID only from target alternatives, not from the minimum eligible-zone count. The pure decision validator must check supplied arm/transition and start-snapshot evidence without consuming pattern state again; it cannot reuse the existing stateless Python recomputation as if both source versions were identical.

## How to avoid another uninformative iteration

October 27–28 remain explanatory development cases. Use them to verify the declared sequence and reconcile entries, not to select the best-looking thresholds.

Before an economic test, inventory existing date coverage and prior experiment usage without inspecting outcomes to choose dates. Freeze the recipe and select one contiguous comparison period for all eight stocks with adequate warm-up, subject to user review. If every available period has already informed choices, label it development; genuinely new evidence must come from later unseen sessions. A previously viewed month cannot become untouched just by giving the run a new name.

Compare D and this one proposed version through normal saved plans. Report each stock/month, long/short contribution, number of setups/fills, stops/targets/deadlines, entry distance from the frozen level, adverse/favorable movement after entry, and exact missed-entry reasons. Include winners the new sequence rejects, not just losses it avoids. Missing data is not zero return. No pooled return or result-driven symbol exclusions.

The hypothesis fails as an entry improvement if it merely produces fewer trades without better after-cost trade outcomes, retains similarly adverse paths, or helps only the already-inspected anecdotes. Sparse fills mean insufficient evidence, not success. Require independent quant reconciliation and broader stock/month consistency before considering further testing or promotion. Do not claim a statistical edge from two days; do not repeatedly tune until the same examples turn green. Any later change must state a new hypothesis and retain every prior result.

## Review checklist

- [x] Diagnose actual failed and winning entries and distinguish missed patterns from occupied positions.
- [x] Consult independent quant and engineering reviewers and primary research.
- [x] Identify existing components and prior failed experiments.
- [x] Independent reviewers check this complete proposed contract; emission/cost timing, setup consumption, source identity/version reconciliation and the inclusive cutoff are explicitly resolved.
- [x] User approves the implementation proposal and subsequently requests October 27–28 for the same eight stocks against D.
- [x] Implement and verify causal clocks, state, targets and client/API evidence; final results in `outputs/zone-strategy-20261030/retest-entry/verification.md`.
- [x] Verify all nine retained input versions against D; freeze October 27–28 as previously inspected development sessions and complete all eight portfolios through saved plan 70, with zero candle/calendar downloads.
- [x] Independently review all outcomes: no source, causal-entry, fill or accounting discrepancy; three stocks improve numerically and five worsen. Six long fills yield four stops and two MU deadline wins. No general entry improvement is established. See [results](../../../outputs/zone-strategy-20261030/retest-eight-stocks/results.md) and [quant review](../../../outputs/zone-strategy-20261030/retest-eight-stocks/independent-quant-results-review.md).

All core data, prior results and retained economic evidence are preserved. The original proposal and reviews involved no implementation or economic run; the subsequent approved implementation is recorded under `outputs/zone-strategy-20261030/retest-entry/`.

Final review records: [quant proposal review](../../../outputs/zone-strategy-20261029/two-candle-stop/entry-quality-proposal-quant-review.md), [engineering proposal review](../../../outputs/zone-strategy-20261029/two-candle-stop/entry-quality-proposal-engineering-review.md). Their acceptance concerns a coherent, causal research design; it is not evidence of profitable performance or approval to trade.
