# Fifteen-minute opening-range breakout: approved implementation

Status: approved on 27 October 2026, including the opposite opening-range stop. The latest instruction changes the limit to two trades per stock per day and the run to eight singleton portfolios for October 1–26 inclusive. Implementation, corrected saved-plan execution, independent reviews and reviewed cleanup are complete.

## What changes

The prior strategy traded yesterday's high/low, an immediate confirmation candle and one of three pullback limit entries. The proposed ORB trades today's first fifteen-minute range. Waiting fifteen minutes while still trading yesterday's levels would not implement this ORB definition.

There is no universal ORB stop/filter/exit specification. This document freezes one understandable baseline rather than claiming all its settings are mandatory ORB rules. QuantConnect's [published ORB implementation](https://www.quantconnect.com/forum/discussion/18444/opening-range-breakout-for-stocks-in-play/) illustrates opening-range levels, opening direction, relative opening volume and stop entries, but uses a five-minute range, fourteen-day average volume and an ATR-based stop. Our fifteen-minute range, existing twenty-session median volume measure, opposite-range stop and user-requested exits are explicit choices; its reported performance cannot be attributed to this proposal.

## Exact proposed trading rules

| Item | Proposed behavior |
|---|---|
| Session | Authoritative US regular exchange session, interpreted in America/New_York. No fixed Berlin-time conversion. |
| Opening range | On a normal session, combine the completed 09:30, 09:35 and 09:40 five-minute candles. Freeze their highest high, lowest low, first open and last close at 09:45. Never include a later candle. |
| Waiting | No entry during those first fifteen minutes. The order becomes active at the completed-range boundary, not at an earlier candle timestamp. |
| Volume | First-fifteen-minute volume must be greater than its median across the immediately preceding twenty exchange sessions: opening relative volume > 1.0. Reuse the existing volume evidence profile, requiring all twenty valid opening observations and a positive denominator. The threshold is a baseline choice, not an optimized or proven value. |
| Direction | Opening close above opening open permits a long; below permits a short; equal means no trade. This is a precise opening-direction filter, not a claim to measure every aspect of trend. |
| Entry | One buy stop-market order one tick above the opening-range high for a long, or one sell stop-market order one tick below its low for a short. No additional breakout/confirmation candle pair and no inherited pullback limit routes. |
| Initial protective stop | Exact opposite opening-range boundary: range low for a long, range high for a short. This replaces the former breakout-candle stop, as approved. |
| Profit target | Initial target 3R from actual entry, where original R is the actual entry-to-initial-stop distance. Preserve tick-valid outward target rounding. |
| Protection | Preserve the requested completed-close 2.4R activation; stop past entry by estimated round-trip costs plus one tick. Preserve two fresh qualifying continuation candles before tightening the stop and extending the target by one original R. These are custom trade-management choices, not universal ORB requirements. |
| Frequency | At most two filled parent entries per stock per session, configured in the ordinary strategy recipe. Partial fills are one trade; only one parent/position may be active at once. After the first trade resolves, require a newly completed close inside the frozen opening range before arming another breakout; do not repeatedly re-enter while price stays outside. |
| Expiry and exit | Pending quantity expires at the existing deadline, five minutes before the authoritative close. Cancel entry remainder before liquidating filled exposure. Remain intraday; handle early closes through the exchange calendar. |
| Yesterday's high/low | Keep as separately labeled context; they do not define ORB entry or veto a trade. No unapproved distance-to-resistance filter. |
| Portfolio | Reuse normal saved-plan capital and risk settings. For the approved run, NVDA, SMCI, MXL, MU, TSM, POET, PATH and CRDO each form a separate one-stock cohort with $10,000. Retain nominal 1% entry risk and 25% position notional ceiling through normal plan choices. At most one position per stock overlaps. Two trades per stock per day means at most sixteen daily entries across the eight books, not sixteen trades over the full month. |

No SMA5, new VWAP filter, news filter, minimum range-width filter or additional optimization grid is included. Each would be a separate change to assess. The existing stricter requirement for complete previous/current-session evidence remains in place; therefore previous-day data can still affect availability even though its levels are contextual.

Flow: complete opening range → freeze opening volume and direction → qualify stock → place one direction-specific stop entry → fill/protect → manage 3R and continuation → cancel pending quantity and close before the session ends.

Example: opening range $99–$100, bullish opening body and volume above its historical median. With a $0.01 tick, the buy trigger is $100.01 and the initial stop is $99. If the actual modeled fill is $100.02, original R is $1.02 and the initial target is $103.08, before outward tick normalization. A gap above the trigger fills at the modeled executable price, not automatically at $100.01.

## Reuse and smallest code change

Register a new immutable ORB research identity in the existing strategy catalog. Preserve the previous-day strategy and every historical result; show the new ORB name in normal selections rather than relabeling old records.

Reuse the shared history reader, exchange calendar, `IndicatorEngine`, strategy decision kernel, risk/capital accounting, deterministic simulator, continuation protection, journal publication, saved-plan worker and monthly reporting. No custom runner, run-specific script, provider collector or extra application is needed.

The existing intraday policy explicitly implements a two-candle confirmation and one-bar limit entry. A true stop entry needs a small typed entry mode and a new versioned execution contract through the same portfolio executor. It cannot be implemented truthfully by merely changing yesterday's levels to opening levels or renaming the old policy.

Affected areas:

- `TradingFlow.Domain/Strategies`: OR rules and strategy definition; normal horizon/admission checks.
- `TradingFlow.Engine/Strategies`: OR evaluator, shared typed intraday order intent, decision-kernel selection.
- `TradingFlow.Engine/Configuration/SimpleYamlReader.cs` and `TradingFlow.Web/Services/RunConfigWriter.cs`: parse, validate and freeze ordinary strategy parameters without custom run configuration.
- `TradingFlow.Web/Services/BacktestExecutionProfileResolver.cs` and `BacktestSessionContextResolver.cs`: derive the required preceding twenty sessions through the existing calendar/history workflow.
- `TradingFlow.Backtesting/BacktestRunner.Intraday.Candidates.cs`: use the frozen opening volume snapshot and opening range; retain previous-day context separately.
- `TradingFlow.Domain/Backtesting/ExecutionReplayPolicy.cs` and `TradingFlow.Backtesting/IntradayPortfolioExecution.cs`: versioned stop-entry behavior through the existing executor.
- Normal strategy catalog, job evidence contracts and `TradingFlow.Contracts/Presentation/IntradayDecisionPresentation.cs`: display actual opening levels, volume ratio/sample count, opening direction, trigger and actual fills.

API, Web and Android should expose the new identity, rules and evidence through their existing catalog/result flows. No separate ORB screen, custom cohort configuration or APK distribution is included. Interactive strategy-parameter editing is outside this bounded change; ordinary recipe parsing and normal saved-plan selections remain the supported configuration flow.

## Execution details the implementation must preserve

- Size and reserve the submitted order from information known before routing. A stop trigger is not a guaranteed fill price. Record actual gap/slippage outcomes and apply existing causal risk handling; never retroactively resize an order to conceal a gap breach.
- Keep the opposite-range stop fixed. Initialize actual risk and management from actual fills, preserving the existing partial-fill accounting convention. Planned 1% risk is not a guarantee against gaps or slippage.
- Once a stop order triggers, its unfilled remainder stays a market order; it does not require another breakout. Protect filled quantity and cancel entry remainder when the position exits or the session deadline arrives.
- Preserve adverse five-minute OHLC ordering where intrabar sequence is unknown. Retain trigger/fill/exit phases; never infer a favorable exit before entry or use a future candle close to activate an earlier order.
- Retain declared participation limits. Opening execution cannot use future final-bar volume as if it were known at the open.
- Missing required range, volume-baseline or execution data is not a zero-volume/no-trade observation. Keep missing-evidence reporting explicit; no synthetic candles or silently substituted older baseline sessions.

## Verification and later comparison

Use isolated synthetic fixtures for range boundaries, long/short direction, doji, holidays/DST/early closes, frozen opening RVOL, twenty-session median and missing samples. Test stop-trigger gaps, persistent triggered remainder, partial-fill protection, two trades per session with causal rearming, adverse same-bar handling, actual-fill accounting, session expiry, and unchanged previous-day limit behavior. Test normal configuration/catalog/API and shared Web/Android evidence projection. Compile and test sequentially.

After implementation and independent engineering/quant reviews, use only the existing normal saved-plan workflow: eight singleton cohorts NVDA, SMCI, MXL, MU, TSM, POET, PATH and CRDO, separate $10,000 portfolios, October 1–26, 2026 inclusive (New York sessions; October 27 exclusive endpoint). Reuse existing candles first and obtain missing coverage through the existing provider components, as expressly authorized. Include twenty preceding exchange sessions for opening-volume qualification. Do not change wishlists or fabricate missing candles.

Publish monthly results separately by strategy and cohort, including rejected setups and missing evidence. Keep all core history, news, wishlists, original responses and published results. Delete only exact generated copies after independent quant approval through the existing cleanup component.

Scope is one bounded strategy/evaluator and one additional entry mode with normal integration, not a system redesign. Code integration and causal fill tests dominate the work; a cached five-book execution previously took minutes. No precise duration or profitable result is promised before implementation is verified.

## Review checklist

- [x] Inspect existing strategy, shared indicators, history and intraday entry constraints.
- [x] Independent Sol 6.1 High quant and engineering design reviews; direct stop-entry addenda supersede the earlier close/next-bar draft discussed during review.
- [x] Present exact proposed rules, preservation boundaries and API/Web/Android implications.
- [x] User approval, including opposite-range stop, direct stop entry, opening direction/volume and the amended two-trade daily limit.
- [x] Implement through shared components and register immutable strategy/policy identities.
- [x] Independent code/design and quant reviews; 189 focused tests passed, including normal prepared-runner/artifact/monthly integration and historical-null timestamp rearming.
- [x] Run the normal saved-plan comparison; correct historical-null rearming and rerun with zero downloads. Publish monthly results with independent accounting, execution and missed-opportunity review.
- [x] Perform only reviewer-approved generated-copy cleanup; final hashes preserve 3,128 original core files, 63 selected source files and 72 corrected published files, with wishlists unchanged.

## Completed execution evidence

Corrected results and review: `outputs/orb-october-20261027/rerun-1/monthly-results.md` and `independent-quant-actual-review.md` in the same directory. All eight singleton portfolios completed; 43 trades, 86 fills, 63 eligible orders including all ten re-entry orders. Independent review inspected all 3,922 candidate opportunities; no missed eligible admissions or unexplained refusals remain. All 189 focused checks passed. The original attempt is explicitly superseded in `outputs/orb-october-20261027/result-status.json`; its outcomes, initial approval and later withdrawal evidence remain preserved. Temporary copies were removed only through the existing cleanup component after exact independent approval. No paper/live trading was enabled.
