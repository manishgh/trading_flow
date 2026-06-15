# Strategy Forensics - 2026-06-13

## Executive Read

The latest proven-stock run confirms that our issue is not only stock selection. The engine can identify momentum, but several models exit too early or enter pullback/chop instead of the clean opening drive. The strongest intraday model is the simple 1-minute Ross-style stack; the strongest swing model is the long reversal reclaim.

## Ranked Models

### Intraday

| Rank | Strategy | Evidence | Why It Wins | Main Risk |
|---:|---|---|---|---|
| 1 | TOP1 Intraday Runner - Ross VWAP EMA Volume MACD | Latest UI run: 10.53%, 8.69% DD, 48 accepted trades. Known-runner CLI run: 8.20%, 7.10% DD. | Enters early on 1m when price is above VWAP, EMA10, EMA20, EMA10 > EMA20, MACD positive, and cumulative RVOL is high. This is closest to the actual behavior of violent runners. | Too many exits on VWAP/stop events. It captures runners but still gets shaken out when the first pullback is normal rather than fatal. |
| 2 | TOP2 Intraday Safer - Green-Day VWAP Reclaim | 180d run: 2.04%, 2.63% DD. Known-runner run: 3.25%, 2.40% DD. | Requires the stock to be green versus prior close and session open, avoiding some dead/choppy names. | Misses straight-line opening-drive moves and gives up profit versus runner mode. |

### Swing

| Rank | Strategy | Evidence | Why It Wins | Main Risk |
|---:|---|---|---|---|
| 1 | TOP1 Swing Long - Reversal Reclaim Bull Quality | 180d swing comparison: 11.95%, 3.85% DD, 10 trades, 60% win rate. | Buys quality reclaim/pullback structure and lets the position work. Payoff ratio was strong: average win about 2.27x average loss. | Small sample. Losses in MSFT/MU show it still needs market/sector context. |
| 2 | TOP2 Swing Short - Overbought Rollover | 180d swing comparison: 3.66%, 0% DD, 2 trades, 100% win rate. | Captures short-side rollover after extended advances. | Very small sample. Keep as candidate, not standalone truth. |

## Common Win Pattern

Winning intraday trades share these traits:

- Entry occurs early enough to catch the first real expansion, not after the runner is already exhausted.
- Price is above VWAP and short EMAs at entry.
- MACD histogram is positive or not bearish.
- RVOL is high on a cumulative same-time-slot basis, which means real participation versus prior sessions.
- Big winners come from payoff asymmetry, not high win rate. The Ross 1m stack won fewer than half its trades but had large winners.

Winning swing trades share these traits:

- They enter after a reclaim/pullback rather than at a random high.
- They use wider time horizons, so ordinary intraday VWAP/EMA noise does not force an exit.
- SMA10/SMA20 structure is more useful as a swing exit than VWAP.

## Common Loss Pattern

Losses are mostly not caused by a lack of indicators. They are caused by the wrong exit behavior for the selected stock type:

- `technical_exit_below_vwap` is too sensitive for violent runners. A stock can dip below VWAP briefly and still continue higher.
- `technical_exit_below_ema20` similarly exits too early when the stock is in a fast, high-volatility pullback.
- Stop losses are large because entries sometimes occur after extension; if the stock mean-reverts even briefly, the stop takes a full hit.
- Strict bull-flag detection performed badly on the runner basket. It fired late or on the wrong structure, then lost every trade in the old Ross Gap-Go Bull Flag V2 run.

## Latest UI Run Exit Evidence

From `data/backtest/results/web/portfolio/backtest-lab-20260613163055.json`:

- TOP1 Intraday Runner: `technical_exit_below_vwap` fired 12 times, trailing stop 13, stop loss 11, take profit 6.
- Breitstein Intraday VWAP Trap Reclaim: `technical_exit_below_vwap` fired 853 times, stop loss 144, take profit 50. This model is not behaving like a runner system; it is entering churn and getting flushed out by VWAP.
- V4/V7 do not use VWAP exit directly; their losses mostly come from EMA20 and MACD technical exits.

## Is It Us?

Partly, yes. The evidence points more to execution/exit design than raw indicator calculation.

What looks correct:

- RVOL is not just last-bar volume. It is cumulative current-session volume at the same exchange-time slot versus up to 63 prior comparable sessions.
- VWAP/EMA/MACD indicators are available and are being used consistently across backtest and paper strategy evaluation.
- The same strategy engine is used for paper/backtest signal logic.

What looks suspect:

- Runner exits are too reactive. `exit_on_close_below_vwap: true` is suitable for a weak VWAP scalp, not for a known high-momentum runner where VWAP retests are normal.
- Technical exits fire before the trade has enough room/time to develop.
- The system does not yet have a runner-specific state machine: opening drive, first pullback, reclaim, continuation, exhaustion.
- The persisted UI result can use summary retention; analyzer memory may show richer live detail than the projected JSON.

## Next Model Direction

Do not add more indicators first. Build the next intraday candidate as a runner-mode variant of TOP1:

- Keep 1m entry and the VWAP/EMA/MACD/RVOL stack.
- Disable immediate VWAP exit or require confirmed failure after a minimum hold and structural break.
- Use a wider initial stop for the opening drive, then trail only after at least 1R.
- Treat VWAP loss as a warning, not an automatic exit, when price remains above EMA20 or reclaims quickly.
- Add a per-ticker session guard: stop trading a ticker after configurable realized loss or repeated failed entries.

This is the clearest path to catching the proven runner basket without turning every volatile name into a loss machine.
