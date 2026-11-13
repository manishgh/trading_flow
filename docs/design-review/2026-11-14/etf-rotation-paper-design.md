# Paper trading the ETF sector rotation: design for approval (14 November 2026)

Status: proposal; nothing is built. The rotation `etf.rotation-12-1@1.0.0` ended its development test at RETAIN_RESEARCH
(`docs/research/etf-rotation-development-2026-11-13.md`): 93% of buy-and-hold's return with 64% of its worst fall. The
user chose to paper trade it as a slow core holding (14 November 2026). Paper trading here proves the live system makes
the same decisions and gets fills close to the backtest's. It cannot prove profit: 12 decisions a year are far too few.

## What it does, in plain words

Once a month, after the last trading session's close:
1. **Read prices:** read the 11 sector funds' completed daily bars from Alpaca (the same SIP feed the backtest data came from).
2. **Measure:** compute each fund's 12-1 month return and its 210-session average with the same functions the backtest used.
3. **Choose:** the target holdings are the top 3 by 12-1 return that also close above their average. Fewer than 3 qualifying means some cash.
4. **Compare:** compare the target with what this paper run holds. A fund to sell left the top 3 or fell below its average; a fund to buy is a new member.
5. **Order:** place market-on-open orders on the Alpaca paper account for the next session: sales first, then purchases. Each purchase is sized at marked equity divided by 3, within the run's own capital, keeping the 2.5% cash reserve.

Nothing happens on the other days. A month with no change places no orders.

## Existing parts reused

**Shared decision code**, the same functions the backtest calls (one brain):
- `DecisionSchedules`, `ExchangeSessionCalendar`, `TechnicalFactorCalculator` (`sma_distance`), `DailyScreenCommand.Return12Minus1`;
- the tier ranking, moved from the command-line project into the engine so the backtest list and paper trading share one ranking function;
- `MeasureAdmissionCapacity`, so paper uses the same slot and cash numbers.

**Existing broker and run machinery:**
- the paper order submission service with its idempotency keys;
- run tags on orders;
- the paper-experiment grant (`StrategyExperimentArtifactStore`), bound to the exact identity `etf.rotation-12-1@1.0.0` / `15dd1e26…`;
- the existing Paper page to start, see and cancel the run.

**Exchange calendar and Alpaca daily bars:** as already used by the warm-up and paper paths.

## New pieces (smallest change)

1. **A monthly rebalance service:** a hosted background service, the same pattern as the preparation scheduler. It wakes after the close of each session the calendar marks as a month's last, and runs the steps above for each active rotation paper run. It replaces the refusal of `decision_schedule` and `exit_when_off_list` on the paper path for this one strategy shape only. Daily paper runs keep refusing them.
2. **A small record per decision:** for each month-end, store the date, each fund's 12-1 return and distance from its average, the target, the orders sent and their fills. This feeds the Paper page and the comparison in point 3.
3. **A comparison check:** for every past month-end, recompute the decision from the frozen evidence catalog. It must equal the development backtest's holdings exactly; this is a test, not a run. In live use, each month's decision is also recomputed from the catalog once the bars are collected; any difference is flagged.

## Safety

**Account and orders:**
- **Paper only:** the paper account guard (`Alpaca:ExpectedAccountId`) stays; no live endpoint.
- **No duplicates:** one decision per month-end per run, with an idempotency key per order, so a restart can never send an order twice.
- **Missing data:** a fund with missing or late bars produces no decision for that month, with the reason shown. Nothing is guessed.

**Pausing:** the run can be paused or cancelled from the Paper page. Cancelling never sells by itself; the user chooses whether to close positions.

## What success looks like (from the success-definition draft)

- **Matching decisions:** every monthly decision equals the backtest's decision recomputed on the same data.
- **Fills:** close to the modelled open: proposal within 0.10% on average.
- **Duration:** no missed month and no unexplained order over 6 monthly decisions, after which the user decides whether real money is considered under the success definition.

## Verification

**Unit tests:**
- the month-end trigger;
- target selection against the backtest functions;
- sell-before-buy ordering and sizing;
- idempotency across a restart;
- refusal on missing bars.

**Comparison test:** every 2017-2023 month-end's paper decision equals the development backtest's holdings.

**Before the first real month-end:** a paper run on the Alpaca paper account, started by the user from the Paper page.

**Reviews:** an independent code review and the affected test slices, then the full test suite.

## Scope

- **Work:** about two days of building and tests.
- **Then:** paper runs on its own once a month. The next month-end is 30 November 2026, with orders at the 1 December open.
