# Swing assessment and corrected exit timing - 8 October 2026

The user requested method assessment, missed-gain/loss analysis, drawdown analysis
and fixes before further strategy changes. Work remains isolated from Web, broker,
operational database and wishlists. Independent quant and requirements reviews are
read-only. No gain-factor preference response has arrived; account risk, stops,
targets, entry filters and hold caps are unchanged.

## Findings and corrections

The first method assessment established signal-rule alignment and completed-entry
information timing. It did not establish economic suitability for capturing every
multi-week rally. Connors is a rebound strategy with inherited risk overlays; VCP
is a quantitative technical proxy, lacking full fundamental/RS selection.

Deep review found a genuine timeout-exit defect overlooked by the earlier audit:
max_hold_bars/max_hold was chosen after observing current OHLC, but the portfolio
market order then used that same slice's earlier open. Four published trades used
the bar-holding cap. Entries and SMA5 next-open exits were independently checked
against their actual priced source slice and remain causal under the declared
model. Old results remain published with a superseded-timeout-timing receipt.

All non-price-triggered completed-slice exits now use a later eligible open, long
and short. Missing later liquidation evidence causes an explicit failed work item;
complete batch economics are suppressed. Exhausted data is never manufactured as
a retrospective closed trade. New immutable strategies v10/v6 and profile
causal-market-exits-v3 bind this repaired timing; economic YAML rules are unchanged.

Other corrections: missed moves count actual unified completed fills rather than
hypotheses, with compatible receipt/source-slice boundaries; VCP rejection reports
separate geometry/reference/dry-up explanations without relaxing acceptance;
closed-trade drawdown no longer produces a 'controlled drawdown' recommendation;
OHLC research requires a declared timestamp convention, exact daily boundaries
and excludes later exit-day extrema for next-open technical/time exits. Protective
exit-day extrema remain bounds, not reconstructed intrabar excursions.

PortfolioCloseValuation reconstructs cash, fill fees and marked positions from
recorded ledger fills and aligned completed closes; missing/conflicting marks fail.
An explicit expected grid detects entirely missing valuation dates. This metric
is close-to-close drawdown, not intraday/peak-to-trough quote drawdown.

57 focused tests pass: mirrored timeout/exhaustion, actual peak-session entries,
valuation fees/open losses/missing-grid/future perturbation, VCP diagnostics,
research timestamp mapping, canonical identity and kernel/catalog consumers.
Broader affected-consumer checks and final quant result review follow execution.

## Frozen paired rerun

New outputs: outputs/swing-study-exit-timing-fix-2026-10-08. Actual recapture matches
all 28 original file-byte hashes and all 26 canonical series hashes, independently
verified before outcomes. Window remains 7 April-6 October 2026; original 25-stock
roster, five boundaries, coverage-only exclusions and 4/4/5/5/4 executed subsets
remain. Native SIP/all snapshots do not establish original revision receipts or
historical membership. Same $10k per batch/strategy, 1% risk, 25% notional cap,
four slots, fees and 10bps slippage. Inputs are deleted after final review; results,
exact artifacts/configs, comparison receipts and audit outputs are retained.

The daily model still starts protection after the entry slice; this explicitly
limits real-risk/entry-day claims. Changing that execution model requires separate
open-event/intrabar evidence, not an assertion based on daily OHLC ordering.
No promotion, risk increase or live order is authorized by these results.

Final corrected results and attribution follow.

## Final assessment

We used published signal cores with existing overlays, not literal published
backtests. I kept portfolio risk/fees/exit overlays fixed to isolate method alignment
and causal execution. Their economic benefit had not been established by the first
assessment. The earlier assessment was incomplete as an explanation of missed
profits, and its timeout-path review missed a genuine look-ahead defect.

Connors' authored RSI2<5/price>SMA200/SMA5-recovery core is supported by
[his SMA5 description](https://tradingmarkets.com/recent/etf_trading_strategies_3x_etfs_and_the_200-day_ma-639612).
He also describes on-close execution and an RSI70 exit in
[his execution discussion](https://tradingmarkets.com/recent/how_to_enter_and_exit_a_position_on_the_close-677551).
Our next-open execution, SPY gate, ATR2 stop,2.5R target, shared260-bar warmup and
five completed POST-ENTRY bars before next-open timeout are explicit adaptations.
The entry slice is excluded from exit checking in the inherited daily model,
including protection; this is another difference from immediate on-close recovery
and real broker protection. VCP is a quantified technical proxy of broader SEPA,
not a full fundamental/relative-strength/discretionary implementation; see
[the publisher](https://www.mheducation.com/highered/mhp/product/trade-like-stock-market-wizard-how-achieve-super-performance-stocks-any-market.html).

## Corrected paired-input results

All five runs have complete economics/44successfulwork items. All28captured file
byte hashes and26canonical candle-series hashes match the previous capture.
Economic inputs, fees, risk, signals and dates are held fixed; timing and reporting
are repaired under v10/v6/profilev3. No holdout or optimization claim is made.

| Batch | Prior return (superseded) | Corrected return | Realized DD | Daily-close DD | Mean exposure at127closes | Trades |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | +1.6912% | +1.4789% | 0.7731% | 0.8376% | 0.6369% | 6 |
| 2 | +0.7553% | +0.7553% | 0.9664% | 0.9063% | 1.3536% | 7 |
| 3 | +3.1237% | +3.2499% | 0.3428% | 0.8445% | 0.9268% | 11 |
| 4 | +0.9513% | +0.9791% | 0.5782% | 0.8092% | 1.2452% | 8 |
| 5 | -0.4400% | -0.3172% | 0.7564% | 0.9320% | 0.3405% | 4 |

Each batch/strategy is a separate$10,000simulation; returns are not summed into a
whole-universe portfolio. VCP generates zero trades in every batch. Its zero return
is zero exposure, not evidence of drawdown-free profitable trading.

## Why gains were small

The corrected36Connors trades have26wins/10losses, mean initial notional$504.72 and
mean entry-to-stop distance23.05%. The risk planner sizes to the minimum of the
1%account-risk allowance and25%position-notional ceiling, including expected costs.
Wide ATR stops lead to few shares; increasing the25%cap alone does not remove that
risk constraint. Mean marked exposure across127closes is only0.34%-1.35%, with
most capital in cash. These are observed closes, not continuous intraday exposure.

31trades exited on SMA5 recovery,4on the holding cap,1on a stop. Across independent
ledgers, SMA5 exits contribute$945.4022net; holding caps-$232.4859; stop-$98.3027.
Assumed total fees$72.6110 reduce results. These are grouped trade diagnostics,
not a combined account return. Net-trade profit factor2.5706; before-fee trade
profit factor2.8444. Profit factor is a measured outcome, not a tunable gain knob.

Connors buys sufficiently oversold pullbacks above the long trend; it is not an
always-invested trend rider. In sourceApr7-May5(close availabilityApr8-May6), MXL
andSOXL each have20Connors rejection days for noRSI2oversold trigger and1regime-off.
ForVCP, MXL has8breakout-volume failures,12structure failures and1regime-off;
SOXL has20breakout-volume failures and1regime-off. Their hindsight high-to-start
moves are not executable strategy profits.

Across the study VCP's ordered first failures include1212price/SMA50 and672prior50
volume gates.109candidates reach a rejected pivot structure, with recorded depth,
low/reference-volume/dry-up/progression explanations. These are sequential first
failures; removing one gate does not establish that the remaining gates pass or
that a profitable entry results. No filter was loosened to manufacture trades.

## Gain factor and drawdown

No gain-factor clarification arrived, so no numerical preference change was made.
If it means a higher take-profit R multiple, all36actual exits preempt the target.
A price-path check finds no earlier2.5R target touch in the recorded protected
intervals. Raising that level cannot create an earlier target touch. This is a
narrow monotonic assessment under the current simulation, not an executed5Rtrial.
Capturing later rallies requires testing an exit/holding/entry hypothesis, rather
than merely increasing a target that never controls the exit.

Of31SMA5 exits,25have a complete20-session follow-up:14later closes are higher,
11lower,6horizons unavailable. These observations use later data only for post-run
assessment; they are not fed into decisions and are not executable alternative
returns. Stops, interim losses, costs and changed portfolio overlap can change an
actual longer-hold experiment. RDW demonstrates overlap consequences: its later
causal timeout replaces a Sep1receipt/62share entry with a Sep2receipt/70share
entry. The effect is not just four isolated exit-price substitutions.

Drawdown cannot reliably be eliminated while taking market risk. Reducing account
risk tends to reduce both gains and losses; tighter stops can increase stop-outs,
longer holds increase exposure to reversals, and higher quantity magnifies losses.
A stop is not a guaranteed execution price, especially across gaps; see
[the SEC bulletin](https://www.investor.gov/introduction-investing/general-resources/news-alerts/alerts-bulletins/investor-bulletins-15).
The reported daily-close DD does not prove intraday or entry-day risk is small.

Next controls should separately measure the sharedwarmup/SPYgate/holding-cap and
stop/target overlays against explicitly declared reference methods. Freeze one
variable per comparison, costs/execution/calendar and unviewed periods before
outcomes. Full SEPA needs real fundamental/relative-strength evidence; a broad
trend-following reference should be separately identified rather than silently
renaming a relaxed VCP. Do not increase live risk or select best hindsight peaks.
The next execution priority is separately tested entry-open/protection and intrabar
liquidity evidence; this daily prototype is not an execution-calibrated strategy.

## Verification, source and cleanup

Final146affected-component tests pass(0failures); earlier57overlap. Web/Razor build
passes with0warnings/errors. The independent quant reviewer checks5284decisions,
36entries/exits, all fourtimeout source/open relations,69manifest files, exact
identities/input hashes, full127calendar valuations and money/fees/quantities.
Requirements review approves the diagnostic scope and reporting denominators.
Backtest/optimization labels now explicitly say realized drawdown; numeric fields
remain unchanged. These source changes are on the research branch; the existing
Web/ngrok build and feed collectors were left running.

Original published studies are retained and marked superseded for timeout evidence.
New corrected summaries, ledgers, candidate audits, exact strategy sidecars/configs,
coverage/hash comparison, attribution, target assessment and127point equity curves
are retained. Captures and derived valuation-input copies are deleted after review;
no operational wishlist or Finviz source is changed. Original membership hashes
match before and after. No paper/live promotion, broker writes or order activation.
