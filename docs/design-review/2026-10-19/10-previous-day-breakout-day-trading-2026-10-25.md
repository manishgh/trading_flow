# Previous-day high/low breakout — separate day-trading strategy

Status: the user authorized a separate day-trading strategy on 25 October 2026. The confirmed requirements below are fixed. The exact stop/target amendment below is approved and its shared settings/evaluator are implemented and tested. The user subsequently approved all remaining entry, mirrored short, expiry, session-close and diagnostic execution proposals and requested implementation followed by a wishlist backtest. Shared signal, persisted candidate and chronological replay integration are implemented and have received focused source review. Focused builds/tests, rendered Web details and Android compilation have passed. Normal saved plan 17 has completed all five requested portfolios; exact economic-outcome review and cleanup approval are in progress. See docs/verification/previous-day-breakout-2026-10-25.md and outputs/previous-day-breakout-20261025/monthly-results.md.

## Confirmed requirements

- Separate strategy identity and day-trading horizon; preserve the existing swing recipes and published results.
- Five-minute breakout of the previous day's high for longs or low for shorts, followed by a confirmation candle.
- Select the entry method from the observed candle pattern: a deeper pullback, retest of the level, or shallower pullback beyond the level.
- Long initial stop: breakout candle low. Short initial stop: breakout candle high, the mirrored rule discussed with the user.
- Initial target: 3 times original entry-to-stop price risk (3R). Under the user amendment below, confirmed strong continuation may extend the target; every target must remain at least 3R from entry. Freeze R from the actual entry and original stop, never recalculate it from a tightened stop.
- First stop adjustment: near 2.5R favorable movement, move protection just beyond entry in the profitable direction. After that, adjust the stop and extend the target only on confirmed strong continuation; do not trail automatically after every candle.
- Same-day positions only. Cancel outstanding entries and close positions within the regular trading session; no planned overnight exposure.
- Reuse shared components and ordinary saved plans. Preserve original candles, news, receipts, wishlists, settings and all published outcomes.

The user approved mirrored short entries, including a deep-pullback short above the previous-day low, in the final full-strategy approval.

## Proposed exact candle rules

Use the preceding completed regular exchange session, not the preceding calendar date, for the previous-day high (PDH) and low (PDL). Derive both from completed, consistently adjusted regular-session prices. Keep holidays, early closes and New York daylight-saving changes in the existing exchange calendar. Do not include premarket/postmarket prices by default.

Let B be the completed breakout candle, C the immediately following completed five-minute candle and tick the instrument's valid price increment. If the price increment or required session coverage is unavailable, report the missing evidence rather than inventing it. The one-tick retest zone below is a proposed convention, not an optimized or proven threshold.

| Rule | Long proposal | Short proposal |
|---|---|---|
| Breakout | Previous completed close <= PDH and B.close > PDH | Previous completed close >= PDL and B.close < PDL |
| Confirmation | C.close > B.close | C.close < B.close |
| Deep pullback | B.low < C.low < PDH - tick; buy limit at C.low | PDL + tick < C.high < B.high; sell limit at C.high |
| Retest | abs(C.low - PDH) <= tick; buy limit at PDH, provided PDH > B.low | abs(C.high - PDL) <= tick; sell limit at PDL, provided PDL < B.high |
| Shallow pullback | C.low > PDH + tick; buy limit at C.low, provided C.low > B.low | C.high < PDL - tick; sell limit at C.high, provided C.high < B.high |
| Initial stop | Exactly B.low | Exactly B.high |
| Initial target / minimum target distance | Actual entry + 3 x (actual entry - B.low); continuation may extend farther upward | Actual entry - 3 x (B.high - actual entry); continuation may extend farther downward |

The approved rules place an order at the confirmation candle's extreme, with valid order-increment rounding. Price rounding must use the valid instrument increment, preserve the structural stop, and reject nonpositive risk. Reserve risk before routing; reconcile each actual fill, fees and remaining quantity through the existing risk/order accounting. Do not increase quantity opportunistically after a favorable fill.

The classification selects one entry method before seeing the next price path. Never submit three nested orders and then call the first fill the best pattern, or select a winning method after observing the outcome. No SMA, RSI, volume or news filter is added to this baseline without a separate decision. A five-minute strategy does not inherit the swing recipe's 260-day history requirement or SMA5 exit.

## Proposed order, stop and session timing

1. Calculate previous-session levels before they are used. Observe B, then C, only after each has completed.
2. After C closes, classify the pattern and activate its one pending limit order. No execution is allowed inside B or C using their completed information.
3. Keep the entry active for the next five-minute bar only, then cancel any unfilled remainder. This expiry is a proposed starting rule, not a selected optimum. Cancel at the session deadline if that comes first.
4. Allow wicks through the previous-day level. A completed close back inside the previous day's range cancels any remaining pending order. A fill earlier in that bar remains a real fill; the later close cannot erase it.
5. A filled position retains the original stop as its immutable risk reference and starts with a 3R target. Current stop and target then follow the user amendment below. An additional close-back-inside exit is not included; it would be a separate rule to approve. The initial stop protects the position until the first adjustment.
6. First move protection just beyond entry when favorable movement reaches the agreed threshold near 2.5R. Subsequent stop tightening and target extensions require the agreed strong-continuation condition. The former every-completed-candle trailing proposal is superseded. Exact triggers and update distances were approved in the amendment below; never loosen a stop.
7. Re-arm only on a new completed crossing of the previous-day boundary that occurs after the prior order/position is resolved. Do not retain a crossing from an active trade, repeatedly reuse the same breakout/confirmation or substitute a later favorable confirmation.
8. Proposed same-day deadline: reuse the existing session close-buffer setting at one five-minute interval. Cancel entries and submit liquidation at the beginning of the final regular-session five-minute bar (normally 15:55 New York; 12:55 on a 13:00 early close). Process already-active protective events at that time before closing the remaining position. No new entries in the final bar.

The final candle's closing price cannot be observed and then retroactively used as an executable fill at that close. Missing prices, halts, rejected exits or incomplete fills must remain failed/unresolved liquidation, not fabricated flat exposure. The research-only implementation does not activate broker trading. Existing live protection and promotion gates remain closed.

Use the existing conservative stop-before-target policy when OHLC does not establish event order, and clearly label that assumption. Do not infer a guaranteed limit fill from an intrabar touch, use a new trailing stop earlier in its source candle, or ignore gap losses. Historical short borrow, buying power, short proceeds, fees and cover-side costs require explicit treatment; present-day shortable flags are not historical evidence. Without historical borrow evidence, any approved short diagnostic must be labeled as an assumption, not executable historical performance.

## User amendment: protect entry first, then follow strong continuation

This amendment supersedes the original fixed-target and every-candle trailing proposals. Its exact approved definitions below are implemented as a shared evaluator; this is not evidence of improved trading results.

- Let E be actual entry and R the absolute distance between E and the original breakout-candle stop. Keep this original R unchanged throughout the trade.
- The first adjustment happens when price is almost 2.5R in profit. Move a long stop just above E; mirror a short stop just below E. The approved trigger is a completed close at 2.4R; the offset is estimated round-trip costs plus one tick.
- Subsequent stop adjustments require strong continuation of the trend. At those decisions the target also extends in the profitable direction. Without that condition, keep current orders unchanged. Stops only tighten, targets only extend, and every target remains at least 3 original R from entry.
- Same-day exit still applies. A target of at least 3R is an order-placement rule, not a promise of a 3R realized gain: protective stops, session exits and execution costs can produce smaller gains or losses.
- "Just beyond entry" is not automatically net breakeven. Define whether the offset is one valid tick or covers estimated entry/exit costs and slippage. Preserve actual fees and fills in results; do not claim guaranteed zero loss.
- Evaluate continuation using information already available at the decision. An existing target fill cannot be undone to extend the target afterward. Completed-candle decisions become effective only at subsequent executable events; an intrabar threshold requires ordered evidence or an explicitly declared conservative candle policy. Do not infer threshold-before-stop order from one candle's high and low.

### Exact stop and target definitions approved on 25 October 2026

The user approved the following proposal in chat:

| Detail | Approved behavior |
|---|---|
| First adjustment | A completed five-minute candle closes at 2.4 original R or better. A wick alone is insufficient. |
| Protective offset | Entry plus estimated round-trip costs per share and one valid tick for longs; mirror below entry for shorts. Round outward to the valid price increment. |
| Strong continuation | Two fresh consecutive completed candles make higher highs, higher lows and higher closes; reverse all comparisons for shorts. Compare the first with the activation/last observed candle. A failed comparison or missing interval restarts the count. |
| Adjustment | Only if it tightens the stop, use the latest qualifying candle's low/high and extend the current target by 1 original R. Both updates are one decision. |
| Repeat | Require two fresh candles before another adjustment; never reuse the same candles. |
| Risk reference | Freeze the actual entry and original structural stop. Initial target is 3R and no target is below 3 original R. |

Process existing orders first. A stopped or target-filled position cannot subsequently receive an update based on that candle. Changes decided at a completed candle become eligible only after that decision time. Skip any proposed stop that is already at or beyond the observed close, rather than pretending to place valid protection. Candles that started before entry cannot establish the first completed post-entry trigger or a continuation sequence.

### Bounded implementation checkpoint

The initial approval covered the common stop/target evaluator. The later explicit approval covers the complete described entry/short/expiry/execution/session design. The user also confirmed Volatile excluding SOXL, April21-October21 2026, automatic groups of five, separate10000USD per group. Use ordinary saved plans and publish monthly strategy/cohort results.

- [x] Record the approved exact stop/target definitions.
- [x] Add optional shared settings and YAML roundtrip without changing existing strategy identities.
- [x] Implement and verify the shared long/short completed-candle protection evaluator.
- [x] Reject use through existing runners that cannot yet apply paired stop/target changes; do not silently ignore the new policy.
- [x] Independent engineering and quant review of this checkpoint.
- [x] Integrate the common stop/target evaluator into the approved day-trading order lifecycle.
- [x] Complete and verify explanatory updates in API/Web/Android (Web rendered checks and Android compilation; no real-device test).

The earlier architect/quant reviews covered the initial design. Focused implementation reviews have additionally checked the shared signal/candidate boundary and replay behavior. The final verification checklist below remains the completion gate; source review alone does not establish a passing build or an audited economic result.

### Approved first research fill policy

Use five-minute OHLC diagnostic execution first, without claiming quote/trade-calibrated fills. Do not silently substitute it for a SIP-event policy. The existing native simulator uses next-bar-open entries and begins protection on a later bar, while the generic limit simulator triggers on a simple touch; neither is sufficient unchanged.

- A pending order already active before a bar may fill at a marketable opening price under the frozen adverse-slippage/cost policy, bounded by its limit. If it is not marketable at the opening, require penetration of at least one valid price increment through the limit during the bar for a modeled intrabar limit fill. A mere touch is not enough. This is a declared fill assumption, not proof of queue priority or actual liquidity.
- Reuse the existing configurable participation cap and quantity/fill accounting. Model only the allowed partial quantity; protect the filled quantity immediately and cancel the remainder on expiry, protective exit or the session deadline. Whole-bar volume is retrospective execution evidence, not information available for the earlier sizing decision. Its use in modeled participation must be labeled rather than presented as an executable live decision.
- Protection begins with the actual modeled fill, including its entry bar. When OHLC cannot establish whether entry preceded an adverse stop touch, retain the qualifying modeled entry and adverse stop outcome. When entry, stop and target all lie in the same ambiguous bar, retain the adverse sequence; do not discard the trade. Any later separately approved event replay must use event ordering instead.
- Pre-placement invalid-risk rejection must not erase an already-active order that fills through its structural stop on a gap. Record the fill and immediate protective liquidation at the policy's available executable price/cost, with no invented 3R target for nonpositive actual risk and no assertion that the stop capped the loss.
- Apply scheduled liquidation at the final bar's opening using only state already available. Never use that final bar's full volume or closing price to decide a fill claimed to occur at its opening. If evidence required by the declared close-fill policy is unavailable, report unresolved exposure instead of manufacturing a fill.

Keep this policy versioned, deterministic and shared through the existing execution abstractions. No universal simulator replacement or run-specific fill script is proposed. A final implementation review must confirm each assumption is represented in retained run evidence and that unfilled/partial/ambiguous events are accounted for consistently.

## Reuse and smallest implementation boundary

Flow: existing wishlist/Finviz source -> previous-session price context -> completed breakout -> completed confirmation -> one pattern-selected pending order -> shared sizing and portfolio admission -> fill/protection/trail -> scheduled session exit -> existing monthly results and audit.

| Existing component | Reuse or required bounded extension |
|---|---|
| StrategyDefinition, SimpleYamlReader, strategy catalog | Separate research identity and explicit strategy-derived day horizon; bounded breakout/entry/stop/trail fields only where current fields cannot express the approved rule. Canonical identity includes all material settings. |
| SignalGenerator, StrategyDecisionKernel/Brain, candidate journal | Add previous-session level context and day-breakout state through the shared decision path. Keep B and C identities/times separately. |
| CompletedBarExecutionPlanner | Reuse completed-bar ordering. Its current stop context is the confirmation index; preserve the breakout reference explicitly instead. A pending pullback order also needs activation, expiry and pattern reason, not just a next-bar index. |
| StrategyInitialStopResolver, StrategyOrderPlanner, SharedOrderRiskPlanner | Add exact breakout-candle stop selection for both directions. Reuse long/short risk sizing and R targets; preserve stop price and reduce quantity rather than tightening the stop. |
| Existing deterministic order book, chronological portfolio and session calendar | Reuse order triggers, cost/fill events and session schedules. Extend the common portfolio admission/accounting for shorts and day deadlines where absent; do not create a custom cohort runner. |
| Existing detailed daily replay policies | Preserve their identifiers and semantics. They currently explicitly reject intraday setup, shorts, confirmation and trailing. Register a distinct intraday execution policy through existing replay infrastructure; never relabel this strategy as daily long to bypass validation. |
| API/Web/Android selection and result displays | Obtain the horizon and supported modes from the selected catalog artifact, not hardcoded swing strings. Show previous-session levels, entry method, B/C times, frozen stop, current stop, target, expiry and session-exit reason through shared contracts. Do not create a duplicate strategy screen or trading activation flow. |

Relevant inspected paths include `src/TradingFlow.Domain/Strategies/StrategyDefinition.cs`; `src/TradingFlow.Engine/Execution/CompletedBarExecutionPlanner.cs`; `src/TradingFlow.Engine/Strategies/StrategyDecisionKernel.cs`, `StrategyDecisionModels.cs`, `StrategyCandidateDecisionOrchestrator.cs`; `src/TradingFlow.Engine/Risk/StrategyInitialStopResolver.cs`, `StrategyOrderPlanner.cs`; `src/TradingFlow.Backtesting/BacktestRunner.FineExecution.cs`, `EventPortfolioExecution.cs`; and `src/TradingFlow.Web/TradingApiV1Endpoints.cs`, Web candidate/backtest consumers and their Mobile counterparts.

The generic simulator has limit/stop triggers, but that alone does not establish shared portfolio short accounting, correct pending-entry chronology or guaranteed session flattening. The implementation must demonstrate those exact behaviors instead of merely attaching a new YAML file.

The user has explicitly expanded the research scope to this day-trading strategy. Record that narrow authorization when updating operating-boundary documentation; do not weaken data-time, source-retention or paper/live promotion requirements. Preserve all existing swing behavior and frozen evidence. No broad compatibility layer, new service, database or framework is proposed.

## Implementation and verification checklist

- [x] Record the user's separate day-trading strategy request and confirmed stop/target/holding requirements.
- [x] Record the user amendment: first protection near 2.5R, then continuation-only stop/target adjustments with a 3R minimum target.
- [x] Independent Sol 6.1 High senior-engineering review of existing components and gaps.
- [x] Independent Sol 6.1 High quant review of pattern selection, information timing, short ambiguity and session exits.
- [x] User approved the pattern/entry rules, mirrored short interpretation, trail, diagnostic fill policy and final-bar liquidation timing.
- [x] Implement shared previous-session context, explicit breakout stop reference, pattern selection and pending-order state; keep functions small and purpose-commented.
- [x] Implement the bounded intraday replay/portfolio extension, short accounting and session cancel/exit lifecycle through existing components.
- [x] Update catalog, API, Web and Android consumers together for the day horizon and explanatory fields. Keep research lifecycle and execution authorization distinct.
- [x] Run focused deterministic fixtures for both directions and all three entry routes: holidays/DST/early closes, gapped opens, missing session data, no same-bar confirmation fill, invalid risk, pending expiry, re-entry, partial fills/non-fills, entry/stop/target touched within one replay bar, near-2.5R trigger/stop ordering, immutable original R, continuation-only tightening/target extension, no extension after an existing target fill, next-event updates, simultaneous protection/deadline events and unfillable session exits.
- [x] Run affected swing/native-ledger and API/client regression checks; independent engineering and quant review before the economic run. This was a component checkpoint, not a full release test.
- [x] Prepare a normal saved-plan preview using the chosen source/date range/capital. Freeze identical data and costs for any approved route comparisons; do not automatically add a parameter search or new cohort-specific code.
- [x] Report monthly outcomes by strategy/cohort, direction and entry method; nine supported rows and 26 explicitly unavailable. Preserve unsuccessful outcomes and originals; delete only exact generated copies after independent quant approval.

The shared signal, persisted candidate and stop/target/replay components are implemented. Web rendering and Android compilation passed; no real-device test was performed. The corrected execution build and 238 focused tests passed. Independent economic review rejected the first completed five-book attempt because 26 candidates exited before entry and short sizing could exceed the known-limit notional ceiling. The bounded fixes were independently reviewed, and the same saved plan is now being rerun with unchanged strategy, cohorts and core source versions. The corrected actual-run accounting audit passed: 802 fills, 394 trades, 1,611 admission caps and nine supported monthly rows reconcile; no reversed exits remain. All 26 affected original cases map to corrected causal trades. Independent approval preceded cleanup of 5,984 exact generated copies; all core data and both attempts' results remain preserved. Source gaps still prevent reporting 26 monthly returns. Profitability is not established. Three separately frozen entry-method comparisons and a predeclared combined policy are possible later research choices, not four automatically authorized new experiments. Data/storage availability for five-minute history must be measured before collection or execution; the previously observed C: disk shortage has not been waived.

## Execution evidence clarification

The session-deadline opening uses only its observed opening price and the immediately preceding completed five-minute bar volume for a lagged participation-cap estimate. It never uses the final bar future volume. This is explicitly an OHLC diagnostic liquidity assumption, not proof of opening liquidity. An unfilled remainder or missing preceding evidence leaves unresolved exposure and unavailable economics. Short borrowing availability is assumed for the diagnostic, never represented as historical broker eligibility.
