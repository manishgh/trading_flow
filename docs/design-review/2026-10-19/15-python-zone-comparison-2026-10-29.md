# Python zone entry/exit comparison

29 October 2026. The user requested trying the supplied Python function, then explicitly selected **existing causal 15-minute zones** and **candle-based diagnostic execution without the two-second quote gate**. This authorizes the comparison described here, not replacement of the original strategy or activation of trading. The original quote-based result and all core data remain preserved.

## Scope and assumptions

Create a separate immutable research strategy, `technical.zone-python-comparison` version `1.0.0`, displayed as **Python zone comparison**. Use the normal catalog, saved-plan API, existing intraday candle simulator, common sizing/costs and monthly reporting. No custom run executable or handwritten run configuration.

Repeat CRDO October 27–28, 2026 with its separate $10,000 portfolio, one open position, two filled parents per session, existing 1% account risk and 25% maximum position notional. Do not assume 100 shares can be funded or add the profits of overlapping positions. Preserve all original/provider data, warm-up history, earlier outcomes and intermediates pending independent review.

The Python snippet omits the zone builder, indicator definitions and execution loop. This is a comparison of its signal and stop/target formulas under explicit shared-component assumptions, not a claim to reproduce its reported profit:

- Existing causal 15-minute RTH zones, five prior sessions carried, unchanged merge/flip mathematics.
- Python `touches >= 2` is mapped to the existing **confirmed pivot count >= 2**. This is not a count of separate price visits. Record that distinction in the result description.
- Completed five-minute ATR14 and RTH session VWAP from existing indicators.
- Short RVOL uses the existing previous-20-session same-clock median. Python does not define its denominator. Longs have no RVOL eligibility gate; missing long RVOL must not suppress a signal.
- Current and previous five-minute bars must belong to the current regular session. No premarket/overnight entry or stale previous-day confirmation is introduced.
- Missing/nonpositive ATR or VWAP cannot create executable orders; do not inherit the snippet's zero-ATR division fallback.

## Literal entry rules

Select known zones with pivot count >= 2 and require at least two such zones. Sort by bottom, preserving deterministic creation order on ties. Iterate that list; evaluate long then short for each zone, returning the first qualifying order exactly as the supplied loop does.

| Rule | Long bounce | Short breakdown |
|---|---|---|
| Role | Support required | No role or prior flip requirement |
| Previous bar | Low <= zone top + **$0.15** | Close >= zone bottom |
| Current completed bar | Close > zone top and close > open | Close < zone bottom and close < open |
| VWAP | Close >= VWAP - 0.5 ATR | Close <= VWAP |
| Distance | Absolute close–VWAP distance <= 2 ATR | Same |
| Relative volume | No gate | >= 1.2 |
| Stop | Zone bottom - 0.15 ATR | Zone top + 0.15 ATR |
| Target | First higher eligible zone bottom above close | Last lower eligible zone top below close, retaining bottom-sorted ordering |

Target zones come from the same filtered known snapshot; no target-role filter. Preserve the long condition's literal one-sided previous-low test: do not silently add a previous-high overlap requirement. No added retest, fresh-touch consumption, EMA or minimum-R gate. The two uses of 0.15 have different units and separate named recipe parameters.

Freeze the source zone, target zone, ATR, previous/current bars, VWAP, optional RVOL and raw stop/target at confirmation. Round stop outward and target toward entry to the supported exchange tick using the common increment resolver. Preserve raw and executable levels in evidence. These are fixed zone prices: partial fills change quantity, not target location. This variant has no 3R target rebasing or continuation trailing.

## Session and execution

Use the exchange calendar and New York clock. Stop accepting completed confirmations after **15:30 New York**, inclusive at 15:30, equivalent to 20:30 Berlin on these dates. First entry is the next five-minute opening after completed confirmation; no retroactive same-close fill. The opening at the cutoff is allowed; no later partial-parent fill is allowed. Cancel unfilled remainder at the cutoff and keep the existing **15:55 New York** liquidation deadline separate. Early-close sessions use close-relative cutoffs.

Reuse the existing candle simulator's lagged opening liquidity, participation, costs, adverse same-bar stop/target ordering, position accounting and same-day liquidation. Do not check SIP quote freshness in this explicitly selected diagnostic; do not silently fall back from a quote execution run. Report candle-derived execution assumptions prominently. Preserve actual adverse gap fills and reduce invalid exposure causally if a fill crosses the structural stop or passes the fixed target; never delete those fills afterward.

Flow: existing archived candles -> completed indicators and causal 15m zones -> Python-rule 5m confirmation -> typed fixed-price intent -> existing risk sizing and candle execution -> existing per-strategy/cohort monthly result and trade evidence.

## Smallest reusable changes

- Domain zone policy and optional evidence fields: separate Python comparison parameters, fixed target, target-zone version, ATR, previous bar and entry cutoff. Preserve original zone policy validation.
- Engine: a small separate Python-rule evaluator, dispatch from the existing zone coordinator and corresponding pure kernel verification. Existing zone construction is reused.
- Normal parser/writer/catalog: freeze the research recipe and identity; no run-specific fields or CRDO constants.
- Backtest candidate adapter: retain typed zone evidence and fixed prices; separate entry cutoff from liquidation. Do not fabricate ORB ranges or previous-session high/low values merely to satisfy an unrelated contract.
- Shared candle executor: validate zone intents separately, support fixed-price protection and inclusive opening cutoff, reuse all matching/accounting. Retain existing ORB and original quote-zone behavior.
- Normal profile resolver and reporting: explicit candle execution selection, raw SIP candle provenance, shared API/Web/Android presentation for target and assumptions. No new screen, APK/device work or provider is required.

## Completion checklist

- [x] Read-only component/design review identifies existing 3R/ORB assumptions and reuse points.
- [x] User selects existing zones and candle diagnostic.
- [x] Record formulas, assumptions, source preservation and normal workflow scope.
- [x] Implement engine, recipe and typed evidence using Sol 6.1 High agents.
- [x] Integrate existing candle execution, profile routing and shared presentation.
- [x] Verify exact Python asymmetries and order, no long-volume gate, causal zone/target snapshots, fixed levels, partial/gap fills, cutoff equality, same-day close and old-policy regressions.
- [x] Independent senior engineer and quant review; resolve findings before accepting results.
- [x] Run CRDO through the normal saved-plan workflow using existing data.
- [x] Publish October partial-month and per-trade results separately from the original strategy; retain all results and core data.

Work is limited to this comparison and its necessary shared-component integration. It does not include broader strategy optimization, new zone discovery, new data downloads unless verified required, production deployment or trade promotion.

Completed evidence: outputs/zone-strategy-20261029/python-comparison/results.md and outputs/zone-strategy-20261029/python-comparison/quant-result-review.md. Plan 42 / job fcdd3647-6b06-5df3-5014-c31c9ae6c2d7 completed using cached candles; no cleanup or trading enabled.
