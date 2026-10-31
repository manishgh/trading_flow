# Proposal: volume by entry pattern and targets beyond known zones

Status: approved by the user October 29, 2026 for implementation and all four versions on the same eight singleton-stock portfolios for October 27–28, with independent quant review and all core data preserved. A longer evaluation interval remains a separate later proposal. Prepared October 29, 2026.

## Purpose and evidence

Test whether requiring unusual volume on every support bounce, and requiring another pre-existing box above every long entry, unnecessarily exclude useful moves. This is a research hypothesis, not a promise to reproduce Gemini's MU profit.

The independent MU October 27 trace established:

- At 15:25 CET, the $911.06–$915.4599 support bounce and $917 target geometry pass, but RVOL is 202,761 / 352,108 = 0.57585, below 1.2.
- At 15:35 CET, RVOL 1.39716 and the same support bounce pass; no eligible higher target exists above the estimated $924.9822 ask.
- The allowed two-share MU position makes the first target too close to cover $2 ticket fees, even before the other modeled costs. At the later signal the unchanged structural stop is far below entry, so a 2R target is distant. Neither observation establishes a profitable trade.
- Removing both restrictions can also admit an EARLIER bounce at 14:40 CET on support $902.50–$910.83. It would be evaluated first and could occupy the one-position slot. We cannot select the later attractive candle by hand.

Evidence: outputs/zone-strategy-20261029/no-rr-eight-stocks/mu-oct27-rejection-review.md and its complete reconstructed trace. Noncandidate snapshots were reconstructed from retained candles; both preserved October 28 MU snapshots matched exactly. The new work must retain rejected-bar evidence directly.

## Recommended smallest complete strategy

Use a separate immutable research recipe. Retain the existing long-support-bounce and short-breakdown patterns. Change volume admission for long bounces and target handling only. Do not add a long breakout/continuation family, change stops, rewrite zones or add VWAP in this first proposal.

### 1. Data, clocks and zone definitions remain fixed

- Use complete five-minute regular-session SIP bars and the existing exchange calendar, not fixed CET times. Resample complete regular-session bars to 15 minutes for zone construction.
- Indicators use the existing Wilder ATR14 implementation: 15-minute ATR for zone merging/flips and five-minute ATR for the existing stop buffer. Preserve required warm-up and source coverage checks; no synthetic missing candles.
- Keep close pivots with k=3 and the existing non-strict tie handling. A pivot becomes available only after its three right-hand candles close. Keep body-to-wick bounds, oldest same-role bottom-distance merging at 0.35 ATR, symmetric flips at 0.2 ATR, and five-prior-session retention.
- A confirmation uses only the zone version known before its five-minute candle began. Never backdate a newly confirmed pivot or a role flip.
- Keep at least two eligible zones, each with at least two merged confirmed pivots. Label the count as confirmed pivots, not independent price touches.
- Keep the existing deterministic zone/alternative order. Never reorder alternatives using their later outcome or opening price.

### 2. Exact entries and volume

Let p be the preceding completed same-session five-minute candle and c the current completed candle. Let [B,T] be a qualifying zone frozen before c starts.

Long support bounce:

1. The zone is SUPPORT.
2. p.Low <= T + $0.15 (retain the existing tolerance for this comparison).
3. c.Close > T and c.Close > c.Open.
4. Volume evidence must be available and valid, but RVOL >= 1.2 is NOT required for a long bounce in the proposed treatment. Record the value and its denominator.

Short breakdown, unchanged:

1. p.Close >= B, c.Close < B, and c.Close < c.Open.
2. Retain the existing role-agnostic breakdown predicate; do not silently add a new source-role condition.
3. Require RVOL >= 1.2 using the same baseline as before.

For both patterns, RVOL is current completed five-minute volume divided by the median volume at the same regular-session clock slot over the preceding 20 trading sessions. Missing/invalid baseline evidence remains a named rejection. This proposal changes the role of volume for bounces, not its definition. It makes no claim that volume identifies institutional buyers.

VWAP remains recorded where available but is not an entry gate. No new trailing, trend, news, minimum-range, rolling-volume or choppy-zone filter is introduced.

### 3. Stop, sizing and estimated costs

Keep the existing structural stop: for a long, source-zone bottom minus 0.15 times completed five-minute ATR14; for a short, zone top plus that buffer. Round outward to the instrument's legal tick. Do not tighten the stop afterward merely to make a ratio pass.

Keep separate $10,000 starting portfolios, the existing planner's 1% current-equity risk budget, 25% current-equity position-notional cap, whole-share sizing, one open position and at most two filled parent entries per stock/day. Preserve the shared planner's current adverse-price and fixed-fee sizing calculation; it does not include every regulatory fee or quantity-dependent impact component. There is no forced 100-share size or minimum trade quota. If zero shares fit, reject.

The full-cost loss estimate L below is a target-calculation denominator at that unchanged sized quantity. It is not a new sizing rule. Publish both the existing planner's budget use and the full-cost estimate; do not silently resize C/D or claim the 1% budget guarantees a maximum realized loss or a full-cost L <= 1%. Any shared sizing refinement would require a separate reviewed change.

Keep the existing diagnostic costs: estimated full spread 0.10%, adverse per-side slippage 0.10%, fixed ticket and configured regulatory fees, existing participation/capacity and limit-fill constraints. Reuse the shared cost and sizing components. The new target estimator must include all applicable modeled costs, not the old supplied Python's ticket-only ratio approximation. Existing recipes and their accounting remain unchanged.

### 4. Target selection: known zone first, risk-based target only if absent

First use the existing deterministic next-zone selection from the frozen eligible set: long target is the selected higher box's bottom; short target is the selected lower box's top. Preserve existing ordering and role eligibility for this paired comparison, even where that differs from Gemini's new opposite-role-only proposal.

After sizing, estimate net reward at that target with the shared fill/cost assumptions.

- If a zone target exists and estimated net reward is positive, use it. There is no 1.5R or 2R minimum for these zone-target trades. Record the estimated ratio.
- If that target cannot cover modeled costs, reject THIS source alternative with `target_does_not_cover_costs`, then continue the existing frozen source-alternative order. Reject the whole group only if no alternative passes. Do NOT skip that source's nearby box or replace it with a farther box or fallback to force that alternative to trade.
- Only if there is no eligible next-zone target, calculate a fixed target aiming for 2 times estimated net stop risk after modeled costs. This is an explicit initial research setting, not a proven optimal payoff or a prediction of how far price will travel.

The positive-net-reward check is a NEW proposed admission floor. With positive risk it is mathematically RR > 0. It is not already authorized by the previous removal of 1.5R, and it is not being presented as a mandatory safety rule. Approval of this proposal would approve that specific change.

Definitions for quantity N, direction d (+1 long, -1 short), estimated entry fill E, estimated stop fill S, and prospective target order price P:

    L = N * d * (E - S) + entry_fee + stop_exit_fee
    W(P) = N * d * (estimated_target_fill(P) - E) - entry_fee - target_exit_fee(P)

Require L > 0. Use the nearest favourable legal target tick P for which W(P) >= 2L when no zone target exists. Fee schedules and target-fill price constraints must be the same shared components used in simulation. Reject if no finite positive legal price is possible (including an impossible short target). Do not double-charge spread on a quote-side fill, or fill a limit worse than its limit merely to subtract costs.

Pin prospective fill assumptions explicitly. Let M be the completed confirmation close and a be (half the configured full spread + configured slippage + configured maximum-participation impact) / 10,000. Use E = M * (1 + d*a), S = rounded structural stop * (1 - d*a), and estimated_target_fill(P) = P: assume no favourable limit-price improvement. Apply the shared deterministic price rounding and configured side-specific fixed/SEC/TAF fees at N and these hypothetical prices. The current saved profile has impactBpsAtMaxParticipation = 0; thus a = (5 + 10 + 0) / 10,000. A nonzero configured impact uses that existing maximum as the disclosed conservative planning assumption, never an unknown future candle's volume. Actual fills retain the unchanged simulator's participation, impact and gap rules and can differ from estimates.

Build this target during admission from completed evidence, capped quantity and frozen estimated costs. Freeze source zone, stop, target, quantity and estimated ratio before the future execution bar is read. Actual later fill prices and realized costs may differ from estimates; do not claim exact realized 2R. No repricing the target after seeing a favourable high, after a loss, or to maintain a retrospective ratio.

### 5. Execution and exits remain fixed

- Signal only at completed candle close; entry uses the existing later executable opening, never the signal close.
- Keep the existing order-expiry, insufficient-capacity, invalid-fill and adverse-gap handling. An invalidated entry must retain any modeled fills/costs the shared engine actually incurred; it cannot be erased retrospectively.
- At most one position and two filled entries per day across both patterns. Earlier entries can prevent later ones.
- Entry cutoff is exchange close minus ten minutes, inclusive (15:50 New York on a normal session). Liquidate at exchange close minus five minutes (15:55). Early-close sessions use their calendar close.
- Freeze the stop and target. Use the existing protective ordering and conservative same-bar resolution. No trailing, partial profit-taking, target extension or overnight position in this version.
- Keep diagnostic results explicitly distinct from historical quote execution or live fills. No broker trading or promotion is authorized.

## What this proposal deliberately does not import from Gemini

Do not change ATR to a simple moving average, RVOL to the last 20 bars' mean, pivot ties, box merging, flip thresholds, flip-count expiry, opposite-role targets or VWAP filtering at the same time. Do not invent a manual $928 target or a $914.50 fill. These would be separate experiments requiring review, not incidental cleanup.

If this first paired experiment shows the problem is broad zone construction or stops, present that evidence before proposing a new entry family or narrower stop. Missing a profitable chart move does not alone establish an implementation bug.

## Implementation scope after approval

Reuse the existing configuration parser/catalog, zone evaluator, candidate decision kernel, risk planner, chronological portfolio simulator, prepared history reader and saved-plan workflow. No per-run executable, custom ticker configuration, alternate backtester or new service.

Small reusable recipe selections are needed for long-bounce volume treatment and target policy. Keep the existing old-recipe defaults unchanged. Add only policy parameters needed to express the four comparison cells below, including a declared fallback multiple. Do not scatter per-stock thresholds or defaults across API/client code.

Expected touch points:

- Domain zone rules and shared signal/evidence contracts: distinguish zone-derived versus risk-derived targets without inventing a fake target zone.
- Shared pattern evaluation: retain a valid pattern when no next target exists under the new target policy; apply volume by pattern.
- Shared risk/admission planning: size first, estimate costs, then resolve the permitted target without reading future bars.
- Existing simulator validation/protection: consume the same frozen target metadata; do not create another execution algorithm.
- Saved-plan API and common Web/Android presentation: show volume mode, target origin, fallback multiple, stop basis and cost assumptions. Existing strategies remain selectable and immutable. No APK/device work in this proposal.
- Existing result/audit artifacts: persist bounded per-evaluation rejection evidence, not just aggregate reasons. Include candle/zone clocks, bounds/roles/pivot counts, each predicate, volume denominator, target absence/cost rejection, and sizing outcome. Reuse artifact manifests/pagination and avoid embedding whole history in every row.

Flow: completed bars -> existing causal zones -> pattern and pattern-specific volume -> risk-sized shares -> known-zone or risk-based target -> later execution -> existing monthly and trade reports.

This is a focused change across existing components, not an architecture redesign. Build/test work is sequential; independent code/design and quant review are separate. A precise runtime estimate depends on the trace-contract changes and existing cached coverage; do not promise a completion time or initiate data collection from this proposal.

## Frozen validation plan, also subject to approval

First use October 27–28 as an explanation/regression dataset only. It is already inspected and must not be described as unseen validation. Use the same eight singleton cohorts, limits and costs through ordinary saved plans.

| Cell | Long-bounce volume | Target policy | Purpose |
|---|---|---|---|
| A | RVOL >= 1.2 | Existing next-zone only; no ratio floor | Existing no-RR control |
| B | Diagnostic only | Same as A | Isolate volume admission |
| C | RVOL >= 1.2 | New zone/cost-covering/no-zone-2R package | Assess target package |
| D | Diagnostic only | Same as C | Combined proposed strategy |

Reuse completed A only when source/code/settings identities prove it is identical; otherwise run all four through the normal workflow. C/D change both target availability and the explicit cost-covering floor; their effect cannot be attributed to either subchange separately. Do not add extra grids or tune thresholds after viewing MU.

Then inventory retained history read-only and propose a fixed multi-month evaluation interval with complete common coverage for all eight stocks. Document which periods have already influenced prior research. Split chronologically; freeze the recipe before evaluating a genuinely uninspected interval. If none exists, label retrospective results exploratory and reserve future sessions for the untouched check. Data downloads require the normal authorized workflow; no dates or new downloads are silently assumed here.

Report monthly separately by strategy and stock/cohort: net profit/return, trades, wins/losses, drawdown with its sampling basis, fees, target origin, rejected counts and both-side pattern breakdown. Report fixed-horizon post-signal excursion only as outcome analysis, never as an entry input. Include all losses/no-trades and earlier-slot consequences.

Do not choose a winner by MU profit, total trade count or pooled return. Require consistent out-of-sample net expectancy and drawdown behaviour across months/cohorts, disclosure of uncertainty and turnover/cost sensitivity before any further promotion proposal. This design itself grants no trading permission.

Tests must cover prefix invariance (appending future bars changes no earlier decision), exact snapshot clocks, both volume modes, missing evidence, target absence versus nearby unprofitable target, whole-share fee effects, legal tick rounding, symmetric short risk targets, gap/limit semantics, two-entry chronology and API/shared-client evidence. Independent quant review must reconcile the actual admitted trades, not only the favourable hand-picked signals.

## Checklist

- [x] Read the supplied Gemini specification and audited MU trace.
- [x] Prepare a bounded complete proposal using existing components.
- [x] Record independent quant and engineering review of this final draft. Both reviewers accepted the clarified proposal with no remaining essential design finding; this is not user implementation/run approval.
- [x] User approves the concrete rules and four-version October 27–28 validation scope. No additional dates or trading are authorized.
- [x] Implement and test the approved reusable changes. Initial 391 tests passed; 35 focused follow-up tests passed after the result-evidence reader correction.
- [x] Run the approved comparison and independently audit every result. All 32 portfolios completed; independent quant accepted all outcomes. Evidence: outputs/zone-strategy-20261029/volume-target-four-versions/results.md and independent-quant-results-review.md.

Preserve all core candles/news/quotes, databases, wishlists, earlier results and new research outcomes. No cleanup is part of this proposal.

Final reviews: `outputs/zone-strategy-20261029/no-rr-eight-stocks/volume-target-proposal-quant-review.md` and `volume-target-proposal-engineering-review.md`. Resolved findings explicitly pin prospective fill/impact assumptions, continue to the next source alternative after a cost rejection, and distinguish the unchanged sizing budget from the full-cost target denominator.

## Supporting references

TradingView describes same-time relative volume as comparing corresponding historical clock offsets; this supports retaining time-of-day normalization, not a claim that our median or 1.2 threshold is optimal: https://www.tradingview.com/support/solutions/43000705489-relative-volume-at-time/

SEC order guidance distinguishes market execution uncertainty, limit-price constraints and stop triggers: https://www.investor.gov/introduction-investing/investing-basics/how-stock-markets-work/types-orders
