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

Final corrected results and attribution will be appended after execution.
