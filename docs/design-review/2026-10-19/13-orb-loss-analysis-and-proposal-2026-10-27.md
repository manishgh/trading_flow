# ORB losses, missed moves, and proposed next comparison

Status updated 28 October: stages 1 and 2 implementation, eight-stock comparisons and independent reviews are complete; see `docs/verification/orb-order-rounding-2026-10-28.md` and `docs/verification/orb-breakout-candle-entry-2026-10-28.md`. Stage 2's successful retry is compared against both retained controls. Stage 3 remains a proposal requiring separate approval. No strategy, configuration, core data, or published result was changed during the original 27 October analysis.

## Evidence reviewed

Only the corrected `outputs/orb-october-20261027/rerun-1` publication is used: `technical.opening-range-breakout@1.0.0`, October 1–26, 2026, 18 New York sessions. Eight separate $10,000 singleton portfolios, 43 completed trades, 86 fills. The superseded first attempt is not mixed into these numbers.

Two independent Sol 6.1 High agents reviewed (1) entries, stops, costs and excursions and (2) qualification, missed moves and raw provider evidence. Their reports and machine-readable evidence are under `outputs/orb-october-20261027/analysis/`. No alternative strategy was simulated by the analysis calculations.

- [x] Reconcile actual trade economics and original 3R targets.
- [x] Compare held price movement with initial risk and prior completed daily ranges.
- [x] Audit all 144 stock-days and explain unfilled/excluded opportunities.
- [x] Verify source hashes and provider opening candles.
- [x] Obtain independent critique of the proposed next steps.
- [x] Obtain user approval for stage 1 implementation/experiment.
- [x] Implement and independently review the approved stage 1 scope.
- [x] Run the approved stage 1 comparison through the normal saved-plan workflow.
- [x] Obtain separate approval for stage 2.
- [x] Implement stage 2, review it independently and compare the successful eight-stock run with both controls.
- [ ] Obtain separate approval before stage 3.

## Why seven portfolios lost

The entries usually did not develop enough favorable movement relative to the distance to the opposite opening-range stop. One R means the distance from actual entry to the original protective stop. A 3R order is a target, not an assurance that the market can reach it before the same-day exit.

| Observation across actual trades | Finding |
|---|---:|
| Median initial stop distance / entry price | 2.43% |
| Median original 3R target distance / entry price | 7.38% |
| Original target distance above prior-20 mean full-session high-low range | 39 of 43 |
| Median favorable movement while held | 0.40R |
| Trades reaching at least 1R / 2R / 2.4R / original 3R while held | 5 / 2 / 1 / 0 |
| Session-deadline exits / stop exits | 30 / 13 |

The historical range comparison uses completed regular sessions, not ATR, and is not a bound on a future trend day. It explains how demanding the targets were; it does not validate a filter that would simply exclude 39 trades.

The median entry source interval began only ten minutes after the opening range ended. Fourteen entries occurred in the first eligible interval. Late entry is therefore not the main explanation. Seven stocks were already negative before explicit fees and remained negative in fixed-fill arithmetic removing recorded slippage. Costs worsen the result, but do not explain it away. This accounting decomposition is not a zero-cost backtest.

**CRDO, October 2:** long entry $225.46524, opposite-range stop $211.91, original target $266.14. That demanded another 18.04% rise after entry. The best held price was $231.59, only 0.45R. Its prior-20 mean entire regular-session range was $9.65, versus $40.67 needed from entry to target.

**MU, October 7:** the trade reached about 2.63R on fully observable held bars. Its advanced protective stop realized about $84.36 net. The original 3R target was first touched roughly 50 minutes after exit. This was the only such later target touch among the 43 trades. It merits a protection-policy review, but cannot explain the other 42 trades or justify removing protection generally.

The excursion counts remain unchanged when ambiguous entry/stop-exit bar highs and lows are included as generous bounds. Thus intrabar ordering did not hide a 3R touch in these actual holdings.

## What the strategy missed

The strategy admits above-usual opening volume, then fixes direction from the first 15-minute body's color. A green opening permits only longs for that day; red permits only shorts. This is a weak, fixed description of direction, not evidence that continuation will persist.

Of 144 stock-days, 73 failed the opening-volume filter, 18 passed volume but failed exact-cent opening-price checks, and 53 qualified. All 53 qualified days received their first order at 09:45. Thirteen had no fill: eight broke only the forbidden direction and five broke neither direction. The existing independent audit found no missing eligible order among 3,922 candidate opportunities.

Examples, using New York times:

- MXL October 8: green opening, $101.11–$105.00 range. The long trigger never traded; price later fell to $93.21. The strategy prohibited that short.
- CRDO October 20: red opening, $161–$169 range. Its short trigger never traded; price later rose to $177.19. The strategy prohibited that long.
- POET October 15: volume qualified, but opening extremes $5.645 and $5.4823 failed the exact-cent gate. Price later reached $6.10.

These are observed exclusions, not reconstructed profitable trades. Both opening boundaries broke on 45 of the 144 days; choosing the better direction after seeing the day would be look-ahead bias.

All 144 opening groups matched the archived provider responses. Fractional extrema were already in the provider data; they were not introduced by CSV conversion. Requests used `adjustment=all`, but these records alone cannot establish whether a particular fractional value came from adjustment or underlying trade prices.

## Recommended sequence

### 1. Correct submitted order prices; retain the original control

This is the smallest next change and run I recommend approving first. Valid source prices should not be rejected merely because they contain more decimals than a submitted order permits. Alpaca documents precision constraints on submitted order prices in its [order documentation](https://docs.alpaca.markets/us/docs/orders-at-alpaca).

Preserve every source OHLC value. For order increment t, derive long trigger as ceil-to-tick(high + t), short trigger as floor-to-tick(low - t), long stop as floor-to-tick(low), and short stop as ceil-to-tick(high). This retains the existing full-tick breakout buffer. Recompute sizing from submitted levels and retain actual-fill original R and outward-rounded 3R targets. Record observed and submitted levels together.

Use a distinct immutable strategy version. Keep opening duration, volume rule, body direction, protection, same-day exit and two-fill limit unchanged. This removes the price-format barrier on 18 observed days; it does not promise 18 extra trades or higher returns.

Reuse the existing order-increment resolver, directional rounding patterns, shared risk planner, evaluator, strategy catalog and saved-plan runner. Likely implementation touches are `OpeningRangeBreakoutEvaluator`, its shared rules/validation, strategy registration and focused tests. Existing API/Web/Android selection and result displays must expose the exact strategy version and consistent submitted levels. No bespoke runner, per-run source code, new screen or new operator setting is proposed.

### 2. Separately compare a completed-breakout entry with a candle-based stop

This is a proposed next experiment after the corrected control is reviewed, not part of stage 1. Retain the 15-minute opening range, original volume and body-direction rules. Require a completed five-minute candle to close beyond the valid opening trigger. Enter at the next executable bar opening, with the stop below that completed breakout candle's low for a long, or above its high for a short. Use outward valid-price rounding and actual-fill 3R. The order is committed after the signal closes; use actual opening/gap prices and ordinary risk/notional limits. If a gap fills through the structural stop, retain the shared execution path's immediate risk-reduction behavior, including the resulting loss. Do not retrospectively cancel a committed order using its future fill price.

This deliberately changes both entry timing and stop placement. It tests whether a completed breakout provides better continuation evidence and a more relevant failure level. It is not a stop-only comparison, and a smaller stop is not guaranteed; narrower stops can produce more stop-outs and larger costs in R. No candle low/high may be used before the candle completes. Earliest entry is 09:50 New York, after the 09:45–09:50 candle completes. Commit only one pending entry for a signal and wait for its resolution; a further setup requires the existing fresh inside-range rearm, rather than repeatedly selecting already-outside candles. Preserve the same-day exit, two filled entries and existing protection policy. Opening liquidity must use already-known evidence, and protection must be effective in the entry interval with the existing adverse ordering.

The independent reviewer found that the current intraday entry contract supports next-bar limits and stop-market entries, not an unconditional next-bar-open market entry. Therefore this is **not configuration-only**. It needs a separately approved generic entry-mode/clock extension, reusing existing market-fill, liquidity, risk and protection machinery. Do not fake a market entry using an artificial limit or stop. Contract/API/Web/Android descriptions and result evidence would need the same change. This added scope is why stage 2 should be approved separately.

The reviewer also suggested a first-five-minute price range activated at 09:45. I am not recommending that hybrid in the first comparison: it adds another range definition while the user requested a 15-minute opening range. Neither alternative is validated by these results.

### 3. Defer independent changes until that comparison is understood

The eight opposite-only days justify a later test of first-break direction instead of opening-body direction. Before implementing, define cancellation, partial fills, and what happens when both boundaries are crossed in the same five-minute candle. Do not use hindsight to pick a direction.

Keep volume thresholds and continuation protection fixed initially. Do not add news or several indicators, lower the requested 3R target, increase risk, or tune a cutoff from these few outcomes. Published ORB implementations differ materially: [QuantConnect's own example](https://www.quantconnect.com/forum/discussion/18444/opening-range-breakout-for-stocks-in-play/) uses a five-minute range, broad relative-volume selection and ATR-based stops. Its results cannot validate this 15-minute, opposite-range-stop, eight-stock strategy.

## Verification and experiment limits

Retain all original results and core data. First compare the order-price change on the same eight stocks and October dates to isolate its mechanical effect. October is now development evidence. Freeze any further rules before examining additional periods, and reserve later untouched months before claiming an improvement.

Keep $10,000 per stock, nominal 1% risk, 25% notional cap, one overlapping position, maximum two filled trades/day, the same costs and same-day liquidation. Smaller stop distances must not bypass caps to force the nominal risk budget to be spent. Report actual initial dollar risk, costs/R, admission counts, fills, stops, deadlines, target touches, drawdown and realized monthly net results separately by strategy/version and stock. Show no-trade and losing outcomes too; suppressing nearly all participation is not evidence of success.

Focused tests must cover fractional prices, gap entries, causal clocks, risk sizing, original 3R construction, two-fill limits and same-day exits. Independent engineering review checks reuse and contracts; independent quant review checks trades, accounting and missed opportunities before any run-owned temporary copies are cleaned. No core source or published evidence is disposable.

The current analysis covers only 18 shared market sessions and 43 trades in related stocks. It diagnoses the failure mechanisms observed here; it cannot establish a profitable replacement or independent performance across market regimes.

## Supporting reports

- `outputs/orb-october-20261027/analysis/independent-loss-excursion-review.md`
- `outputs/orb-october-20261027/analysis/independent-loss-excursion-audit.json`
- `outputs/orb-october-20261027/analysis/missed-moves-selection-audit.md`
- `outputs/orb-october-20261027/analysis/missed-moves-selection-audit.json`
- `outputs/orb-october-20261027/analysis/independent-proposal-review.md`
- `outputs/orb-october-20261027/rerun-1/monthly-results.md`
