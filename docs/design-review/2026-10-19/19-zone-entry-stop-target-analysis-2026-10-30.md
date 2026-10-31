# Proposal: test a stop below the observed two-candle bounce

Status: approved by the user October 30, 2026 for the two-candle long stop only, implementation and the same eight stocks on October 27–28, compared with D. RVOL, reward/risk and tolerance changes are not part of this approved experiment. The preceding analysis made no strategy or data changes. The approved implementation now passes 418 focused tests; plan 67 completed all eight requested portfolios and passed independent quant review. No threshold changes, new market downloads, cleanup or trading are authorized.

## Why the four versions still lose

We corrected cost arithmetic and allowed a target when the next zone was absent. That did not establish that the selected entries have a profitable directional edge. A stock moving substantially during a day does not mean the later admitted trade can capture that move.

The independent review examined all 92 trades from the four versions, including all 13 risk-derived targets. Those 13 are all longs and include repeated stock/session opportunities across variants; they are not 13 independent statistical observations.

- All 13 risk-derived targets remained untouched even over the full remaining regular session, including after stops and beyond the mandatory 15:55 exit. This is hindsight diagnosis only, not attainable profit or permission to hold longer. Nine actual exits were the session deadline, four were stops.
- Targets required 5.58–14.52 times the completed five-minute ATR (median 8.71), or 1.46%–11.21% beyond actual entry. ATR here measures recent five-minute true range, not a forecast or a daily movement ceiling.
- Remaining time to the mandatory exit was 80–375 minutes, median 350. Late-day entry alone cannot explain the failures.
- The most favorable remaining-session move covered a median 24.3% of the required target distance; even the best covered only 58.6%.
- The median stop distance was 3.40 ATR. Median source-zone width was 0.93 ATR and median confirmation-close distance beyond its top was 1.76 ATR. These are separate sample medians and are not meant to sum. The target is far away partly because entry has already moved away from support, not merely because the box is wide.
- Eleven of the 13 fallback entries followed a candle already closed above the source zone. This can be a valid continuation or retest, but the literal condition does not establish a new move out of support or continuing demand.
- Version D had 25 trades: 10 net winners, 14 gross losers and one gross winner turned negative by explicit fees. Modeled spread/slippage are already included in gross trade prices. Costs matter, but are not the sole reason D loses.
- Eight of D's 18 known-zone targets offered below 0.5R and thirteen below 1R. Positive net reward is not evidence of adequate expectancy. At a constant 0.5R payoff versus 1R loss, a strategy needs over two-thirds winners before extra unmodeled costs; actual trades have varying payoffs.
- D's MXL portfolio had four stop-outs. Changing target arithmetic cannot turn those adverse entries into favorable moves. Earlier positions also consume the one available slot and can displace later opportunities.

## Concrete examples

All examples below are version D. Prices are rounded for explanation. The final column is the highest remaining regular-session price, including hypothetical time after an actual stop; it is not a simulated exit.

| Stock/session | Actual entry | Stop | Net-2R target | Highest later RTH price |
|---|---:|---:|---:|---:|
| MU October 27 | 922.8622 | 901.87 | 969.52 | 935.87 |
| MU October 28 | 973.9888 | 963.32 | 1001.33 | 990.00 |
| MXL October 28 | 76.8338 | 74.85 | 81.35 | 77.03 |
| POET October 28 | 5.8087 | 5.49 | 6.46 | 5.85 |

For MU October 27, the trade needed another $46.66 from its fill to its target; the highest remaining price was only $13.01 above the fill. For POET, the target needed an 11.21% increase, while the subsequent high was only about 0.71% above entry.

The 2R calculation was correct under the chosen model. It expresses how much profit would compensate for estimated stop loss and costs. It does not estimate the probability or distance of a future move.

## Alternatives assessed and not recommended now

1. Adding only the missing lower overlap bound, previous.High >= zone.Bottom, changes none of the 63 admitted longs. It is a semantic weakness in the literal rule, not the demonstrated explanation for these losses.
2. Requiring the previous candle's close inside the support box would retain only three of D's 21 admitted longs: one winner and two losers. It would remove nine winning and nine losing longs, including all fallback trades. Those are classifications of the existing trades, NOT a new portfolio result: freed slots can create different later trades. Do not advertise this filter as an improvement.
3. Lowering 2R after seeing which historical highs were reachable would fit the target to these inspected outcomes. No such change is proposed.
4. A new history-based target-probability filter would introduce additional window, volatility and acceptance choices. Same-time historical movement can be useful later diagnostics, but adding an unvalidated probability model now would overcomplicate this checkpoint.
5. Moving stops closer merely to force a desired reward/risk ratio is not a meaningful invalidation rule. A stop must correspond to the trade premise failing. The proposed change below tests a different, explicit premise and can widen as well as narrow a stop.

## One next change to test

Keep D as the immutable control. Add one separate research recipe using the **low of the two candles forming the long bounce** as its stop basis.

Definitions, all known at the completed confirmation:
- p is the preceding completed same-session five-minute candle.
- c is the current completed five-minute confirmation candle.
- ATR is the existing completed five-minute Wilder ATR14.
- tick is the existing instrument's legal price increment.

Current long stop:
    floorToTick(sourceZone.Bottom - existingStopAtrMultiple * ATR)

Proposed long stop:
    floorToTick(min(p.Low, c.Low) - existingStopAtrMultiple * ATR)

The existing multiplier remains 0.15. Do not optimize it in this comparison. Do not take the tighter of old and new stops, clamp the result to the old zone, move the stop after entry, or use any future candle to choose it. A nonpositive or otherwise invalid stop is rejected through the existing geometry guard.

The premise is specific: if price breaks below both candles that supported this particular bounce, this entry failed even if the older support box remains intact. This differs from waiting for the entire old box to fail. A normal later retest can therefore stop the position earlier; that is a material risk of the proposal.

The observed geometry narrows 11 of the 13 fallback stops and widens two. Median stop distance changes from 3.40 to 2.55 ATR; median per-trade reduction is 26.8%. These numbers are only a comparison of proposed stop levels at already-admitted observations. They are NOT backtest performance, new target fills, proof that trades survive, or a forecast.

Keep unchanged:
- every long/short entry predicate, long-volume mode and short RVOL requirement;
- zone construction, causal availability, alternative ordering and warm-up;
- short stop basis (source-zone top plus the existing ATR buffer);
- existing known-target positive-cost floor and no-zone net-2R target solver;
- same 1% sizing budget, 25% position-notional limit, one position, two daily entries, $10,000 independent portfolios;
- spread, slippage, fees, participation, execution expiry, 15:50 entry cutoff and 15:55 liquidation;
- no VWAP/news/trailing/partial-profit changes.

Signal predicates and source ordering remain unchanged, but geometry rejection, fee-covered target acceptance and the first admitted alternative may change as mechanical consequences. Later portfolio availability can therefore change; do not promise identical trades. Known-zone target prices remain fixed for the selected alternative, while fallback target prices can change.

The shared risk planner must recompute quantity from the new stop, and the existing target solver must recompute fallback targets from that quantity, stop and costs before any future execution bar is read. Those are necessary consequences of changing the stop, not additional tuning. Actual prices can gap; planned risk is not a guaranteed realized loss. Do not simply move targets on the old trade ledger and claim a rerun.

## Smallest reusable implementation, only after approval

Reuse the recipe/catalog, existing zone pattern geometry and evidence, shared sizing/full-cost target solver, chronological portfolio execution, saved plans, prepared history and monthly reporting. Add one validated stop-basis recipe selection with the old basis as its unchanged default, omitting the optional field from old serialized recipes to preserve their hashes. Reuse one calculation in signal generation, trace and downstream validation rather than creating another strategy evaluator or a run-specific runner.

The API and existing Web/Android result presentation must state the stop basis and show previous low, confirmation low, ATR, raw/rounded stop, sizing and target origin. Preserve source-zone evidence even when the stop refers to the two bars. No new screen or operator-wide setting is needed. No APK/device work is included.

Tests must verify both stop bases, long-only scope, shorts unchanged, outward rounding, cases that widen as well as narrow the stop, invalid stop rejection, exact old strategy identities, future-bar prefix invariance, recomputed quantity/target/costs, gaps and conservative intrabar ordering, and matching API/client evidence. Independent engineering and quant reviews remain separate.

## Comparison and decision

After approval, run the new single-change recipe through the normal eight-stock singleton saved plans and compare it with preserved control D. October 27–28 remains a regression/explanation window, not validation; keep all costs and limits. Replaying the control is required only when source/code/settings cannot prove identity with its preserved result.

Before a broader economic run, inventory existing common history read-only and present explicit dates for approval. Freeze the rule before inspecting any untouched period. Use multiple stock/month cohorts and, where available, a genuinely uninspected chronological period. Do not search multipliers or select a version by MU alone. No data download is part of this proposal.

Report monthly by stock and strategy: net dollars/return, drawdown with sampling basis, winners/losers, costs, stop frequency, actual target hits, time exits, position-slot changes and actual fill-cap overshoots. Retain losing/no-trade outcomes. Maximum favorable movement after an exit remains outcome analysis, never an entry input. A narrower target alone is not success; net expectancy and drawdown after costs must improve consistently beyond these two days.

No profitable parameter change is established by the present analysis. If the new stop simply increases whipsaws, reject the hypothesis rather than adding filters until this sample turns green.

## Evidence, references and checklist

- outputs/zone-strategy-20261029/volume-target-four-versions/entry-stop-target-engineering-review.md
- outputs/zone-strategy-20261029/volume-target-four-versions/entry-stop-target-quant-review.md
- outputs/zone-strategy-20261029/volume-target-four-versions/entry-stop-target-quant-evidence.json
- outputs/zone-strategy-20261029/volume-target-four-versions/entry-stop-target-root-spotchecks.json
- outputs/zone-strategy-20261029/volume-target-four-versions/entry-stop-target-version-counts.json
- outputs/zone-strategy-20261029/volume-target-four-versions/target-ratio-counts.json

CME's guidance supports choosing a logical invalidation level before position sizing; it does not validate this specific two-bar rule: https://www.cmegroup.com/education/courses/trade-and-risk-management/proper-position-size
SEC guidance explains market-price uncertainty and limit/stop execution constraints: https://www.investor.gov/introduction-investing/investing-basics/how-stock-markets-work/types-orders

- [x] Inspect existing completed runs, entry conditions, stop/target geometry and source candles.
- [x] Quantify remaining-session favorable movement and compare it with all 13 fallback targets.
- [x] Independently spot-check MU/POET highs from retained raw SIP data.
- [x] Complete independent quant and engineering review of this final proposal. Both accepted it as an unproved, single-change research hypothesis. Corrected confirmation-close versus actual-entry distance wording and made control replay conditional.
- [x] User approves the new stop-basis experiment and exact run scope.
- [x] Implement, verify and run through existing components after approval. 418 focused tests passed; all eight plan 67 jobs completed using nine reused input queries and zero new candle downloads. Independent quant reconciled all 26 trades and 52 fills. Comparison: outputs/zone-strategy-20261029/two-candle-stop/results.md. Two stocks improved, MU unchanged, five worsened; this inspected window does not support the stop change as a general improvement.

All core data and every prior outcome remain preserved. Implementation and the eight-stock October 27–28 comparison are now authorized by the user; broader dates or other rule changes require separate approval.

