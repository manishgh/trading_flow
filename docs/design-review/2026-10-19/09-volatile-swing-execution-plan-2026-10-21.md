# Volatile swing: checked findings and bounded execution plan

Date: 21 October 2026.
Status: approved accounting repairs implemented; all ten technical portfolios executed once and independently audited. Eight portfolios have supported monthly results; both Group 5 books remain unavailable for SIVE. News source collection and the next concrete design are complete; news economics and new strategy registration have not run. Final client reporting checks are recorded in the verification receipt. Sol 6.1 High agents performed implementation and independent reviews; the parent reviewed the changes and results. This document authorizes nothing by itself.

## Outcome we are working toward

Explain why the current strategies accept, reject, miss or lose on Volatile stocks; make the normal backtest runner report genuine open holdings correctly; then test one specific improvement with the existing news tools. Report monthly by exact strategy and cohort. A strategy that can identify recoverable selling versus lasting repricing is still a research hypothesis.

Use existing components and shared decision/risk rules. Keep execution through the saved-plan API, durable worker and normal runner. No new research runner, optimizer, stock lists, hidden defaults, deployment or trading activation.

Data flow: saved plan -> frozen prepared inputs -> history eligibility -> completed-bar strategy decision -> shared risk/admission -> simulated fills and pending orders -> cash/positions -> source-session valuation -> immutable result -> API -> Web/Android.

## Findings before this repair

| Check | Verified finding | Consequence |
|---|---|---|
| Previous real run | Ten jobs completed; three portfolios had complete economics, seven had unavailable monthly results. Two complete VCP portfolios generated zero candidates. | Incomplete books are not losses; inactive books are not a capital-allocation failure. |
| Current strategies | Connors RSI2 and Minervini VCP did not use news or drawdown qualification for admission in this run. | News collection being enabled does not mean its information affected a trade. |
| Warm-up | The profile requires 260 completed daily bars before the window because existing AllowLateWarmup defaults to false. The runner and tests already support becoming eligible later. | Reuse that capability; preserve the required history. |
| End of study | Daily candidate simulation demands a hypothetical exit before actual portfolio admission. An unfinished hypothesis can fail its entire ticker batch and book. | A candidate is not an admitted position. Preserve unknown/pending/open states instead of inventing a sale. |
| Accounting | Artifact and monthly presentation checks currently assume admitted trades are closed and ending equity is starting capital plus closed profit. | Removing the exception alone would publish misleading results. Ledger, reconciliation and clients must change together. |
| Existing event execution | SIP event replay requires a precommitted terminal liquidation and rejects unresolved end exposure/orders. | It is not an unchanged substitute for open-position reporting. Reuse its reconciliation patterns and the generic valuator. |
| Recovery control | The exact prior broad-recovery/SMA5 artifact survives. It had mixed results; it was not established as a winner. Its terminal execution policy differs from the latest comparison. | Preserve the original identity, data and results. Do not relabel a new execution treatment as an identical reproduction. |
| News capture | The October 19 summaries contain 122,969 article versions: 85,400 headline-only and 37,569 body-supported. Original responses and pagination receipts are retained. | Ordinary news history exists; original historical receipt/revision proof and evaluated economic classification are different questions. |
| News evaluation | The existing news-review-25.xlsx is unchanged from the blank export. The inspected closeout receipt contains zero labels and 25 unreviewed items. The packet checks categories only. | Reuse the packet. Category review cannot establish adverse direction or materiality accuracy. |
| News gating | Existing evidence-only and adverse-event gates exist, but require independently evaluated classification, company/sector/global coverage and bound historical identities. The adverse switch covers negative/adverse events broadly. | A narrow negative-guidance experiment is not represented by that broad switch; do not silently filter the evidence or weaken the gate. |

Evidence: [latest run](../../../outputs/volatile-swing-20261021/monthly-results.md), [independent quant review](../../../outputs/volatile-swing-20261021/independent-quant-review.md), [October 18 completion](../../research/drawdown-qualification-completion-2026-10-18.md).

Exact recovery parent:
- Identity: technical.family03.mr_a0b1@1.0.0.
- SHA-256: c151cb4becae9f19b0aeeab7517c884401f14d716fbc404e78bf8dfa4b8396bb.
- Original file: C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/swing-family-cycle03-20261009/strategies/mr_a0b1.yaml; adjacent artifact.json supplies lifecycle identity.
- Daily entry with stock and SPY above SMA200 after recent RSI2 stretch, three down closes or lower-band stretch, followed by a close above the prior high; swing-low stop, 2.5R target, five-bar maximum, SMA5 recovery exit.
- Original dates were April 7–October 6, with a precommitted terminal execution policy. They are not the April 21–October 20 daily v3 comparison. The original fifth cohort also included SOXL; no new comparison may include it.
- This parent is absent from current Backtest catalog choices. Its external artifact is historical evidence, not normal saved-plan selection authority. A later approved derived treatment needs Research catalog registration, a source definition under configs/backtest/strategies and its matching admission profile; existing API/client choices then expose it.

## Execution sequence and owners

Use Sol 6.1 High for implementation. Run independent code preparation in parallel only across nonoverlapping files; builds, heavyweight tests and real portfolios run sequentially. Parent checks every diff, evidence claim and result; independent engineering and quant reviews remain separate.

| Stage | Owner | Work and completion condition |
|---|---|---|
| 1. Fix eligibility and end-of-window accounting | Sol engineer, parent review | Implement the approved design below through existing modules. Focused accounting/causality/risk/artifact tests pass. No evidence gap is converted to a zero return. |
| 2. Audit news and prior recovery evidence, in parallel with Stage 1 | Sol research engineer, parent review | Reconcile retained monthly recovery evidence; inventory exact event coverage/versions and pilot evaluation status. Query configured providers only for specifically identified missing records. Produce precise supported/unknown reasons and the next exact news experiment proposal. |
| 3. Integrate API, Web and Android after contracts settle | Sol client engineer, parent review | The same server result and valuation basis appear in both clients, with visible open holdings, pending actions and missing-data reasons. No UI-side P&L calculation. |
| 4. Verify and rerun the original ten books | Sol engineer operates normal workflow; independent quant checks | Freeze the repaired implementation/profile and source versions; run one cohort/strategy portfolio at a time through the existing saved-plan API; publish all months including unavailable ones and compare with preserved original evidence. |
| 5. Decide the next strategy experiment | Parent plus independent quant | Present a specific recovery/news treatment with exact rule, evidence, times, costs and run ceiling for approval. No new economics or parameter search are silently included in this checkpoint. |

Stages 1–4 are the concrete implementation and bounded rerun proposed for approval now. Stage 5 delivers the next design; it does not authorize an unspecified news rule.

## Stage 1: smallest reusable implementation

### Eligibility

Enable the existing later-warm-up behavior in the normal swing research profile. Keep 260 genuine completed daily observations and any greater declared factor history. First eligibility must use the completed/available prefix, never total future rows.

Publish each symbol's first eligible decision time or "still warming at study end," together with observed/required bars. A verified later start that never reaches warm-up remains in the selected cohort and contributes no admitted orders. It must not fail otherwise valid cohort accounting merely for being too young.

Short data alone does not establish a new listing or complete coverage. Preserve unexplained missing data as unavailable. In particular, query SIVE asset identity/history through the existing provider adapter before concluding why its archived bar responses are empty. Do not substitute a ticker, edit a wishlist, infer delisting or backdate current metadata. If unresolved, its book remains explicitly unavailable.

### Open and pending trades at the cutoff

Change the existing candidate contract to represent "no executable exit yet" and "exit decided, awaiting a later executable slice." Do not create sentinel exit prices/times, an artificial last-bar sale or a completed trade.

Continue ordinary chronological admission with SharedOrderRiskPlanner. Rejected candidates never create fills, exposure or portfolio accounting failures merely because their hypothetical exit is beyond the study.

Extend existing coordinator completion to capture the state at the fixed cutoff: actual fills, remaining quantities, working orders, pending exit intentions and cash reservations. Window end alone is a valid open lifecycle. Missing required execution evidence remains a failure. Handle long and short contracts consistently even though this study is long-only.

Configured precommitted terminal exits continue unchanged. If a treatment explicitly requires liquidation, failure to satisfy it stays visible; the change must not reinterpret that requirement as permission to keep a position. The immediate rerun uses the existing no-forced-liquidation definitions.

Risk sizing, structural stops, rank, overlap/slot rules, fees, slippage, participation, entry/exit decisions and executable-bar ordering remain fixed. Existing closed-trade fixtures must retain identical fills and admission results. This repairs end-of-study representation; it does not claim that native daily fills prove quote-level execution or immediate first-entry protection.

### Valuation and publication

Reuse PortfolioCloseValuation.Analyze and MonthlyStrategyPerformance.Calculate with actual admitted simulated fills, real aligned daily closes and the independent exchange-session calendar.

Normalize native daily records onto their source-session ledger before valuation. Their existing timestamps are completed-bar availability stamps, not observed intraday fill times; MonthlyBacktestPresentation.SourceSessionDate already maps them back one day before converting to New York. Centralize/reuse that policy, preserve original timestamps and execution ordering, and apply that session's simulated fills before its close valuation. Do not misassign a June 30 fill to July 1, or a Friday fill to Saturday, and do not invent observed intraday precision.

For partial fills/exits, maintain weighted actual entry cost per admitted candidate, realize gross profit only on the actually exited quantity, and mark the remaining signed quantity against its remaining entry basis. Charge every actual entry/exit fee once, at its source session, as a realized cash expense. Cumulative realized net profit plus unrealized profit must equal equity minus starting capital (absent external flows). Monthly realized profit is its change during that month; summing fully closed trades is insufficient. Preserve old reports' different fee/closed-trade accounting labels.

Report:
- cash and actual fees;
- closed/realized profit separately from unrealized profit;
- equity = cash + marked position value;
- open quantity, mark/time and remaining order quantity/status;
- monthly total equity return, month-opening denominator and close-to-close drawdown;
- closed-trade count separately from admitted orders, fills and open positions.

Do not use reserved cash as a loss or count a pending order as a position. Do not forward-fill unexplained missing valuation prices. Publish earlier supported months only when their complete ledger/mark prefix independently reconciles; withhold the affected and dependent later months explicitly.

Update artifact integrity checks to reconcile orders, fills, quantities, cash and equity. Existing immutable results keep their original realized accounting labels. New results identify their code/profile/source versions and valuation basis.

## Stage 2: use the news tools without pretending evaluation is finished

1. Reuse archived Alpaca/Benzinga responses, prepared candles, exchange calendars, evidence catalog and existing classification/workbook commands. Use Finviz for current screening and optional enrichment; today's Finviz result is not historical membership.
2. Audit the original 25-symbol roster and the proposed 24-stock selection after SOXL exclusion, at the proposed decision times. Output which company, sector and global intervals are supported, the exact publication/update/receipt/classification times, and missing identity/mapping/evaluation evidence.
3. If a particular provider dataset is absent locally, query it through the configured adapter; record symbols, dates, feed, HTTP result, pagination and returned coverage. Preserve original and prepared versions. Do not redownload the archive indiscriminately.
4. Reconcile the existing recovery-control monthly/trade evidence, including rejected candidates, holding time, deployed risk, exits, loss concentration and execution limitations. Keep original strategy, cohort and date identities. Do not present the SOXL-containing old cohort as a new stock-only comparison.
5. Reuse C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/strategy-validation-closeout-20261019/news-review-25.xlsx if the user supplies labels. No new 500-story assignment is part of this stage. Partial category review is development feedback only; code-generated predictions are not human ground truth.
6. Produce a precise next experiment definition. If required evidence remains unproved, state the exact affected candidates/sessions and continue the price-only diagnosis. Missing news is unknown, not "no adverse event."

The retained news classifier already predicts economic categories, including operational disruption and negative guidance. Its existing tests verify program behavior, not real-world accuracy. No completed independent accuracy evaluation was located within the inspected October 19 closeout/pilot/assessment artifacts, relevant research/verification documents and current October 21 output bundle. This is a bounded inspection, not a claim about every file anywhere.

Future comparison structure:
- A: explicitly versioned derived price-only recovery control under the same reviewed execution policy as B/C; the immutable older parent remains reference evidence.
- B: the same control restricted to decisions with the exact required news evidence.
- C: the same B plus one explicitly defined news rule.

Compare B minus A to identify the complete required-evidence admission effect, and C minus B to identify the rule effect. The current qualifier first requires an eligible drawdown observation, then event identity/classifier/coverage checks; report the contribution and unknown counts of each mask component instead of calling B minus A raw news coverage. Keep omitted opportunities visible and do not invent replacement candidates. Separately describe how freed capital affects later ordinary portfolio admission. Freeze policy, cohort, cost and decision clocks before reading outcomes.

The prior recovery parent's execution policy and the newly repaired study-end reporting differ. Any later derived treatment must retain parent lineage and declare its actual policy/identity. A narrow category rule requires explicit implementation design because the current broad adverse-event switch is not category-selective.

## Stage 3: API and client changes

Reuse the existing saved-plan/start/job-details endpoints and MonthlyRunResult. Add explicit terminal-position/pending-order summaries to the shared result contract; retain nullable unavailable values and ValuationBasis.

Affected source areas:
- Domain BacktestTrade/BacktestResult and native BacktestRunner simulation/diagnostics.
- ChronologicalBacktestExecutionCoordinator; existing portfolio valuation/monthly functions.
- BacktestArtifactIntegrity, artifact writer/projector, MonthlyRunResultWriter and MonthlyBacktestPresentation.
- Contracts/V1/BacktestPlanContracts.cs and JobContracts.cs and their direct consumers.
- Web _LabResults.cshtml/run workflow result display; Android BacktestsPage and shared transport mapping.
- Existing swing profile and affected tests.

No independent client risk logic, new business-rule endpoint, second database or new operator toggle is needed for this fixed cutoff behavior. Explain "open at test end" and "waiting for execution" in ordinary words. Display valuation basis next to returns.

## Stage 4: fixed replay and verification

Frozen window: April 21, 2026 00:00 New York through October 21 00:00 New York, end exclusive; source sessions end October 20. April/October partial. Current wishlist membership remains retrospective diagnostic selection.

| Cohort | Symbols |
|---|---|
| 1 | ASTS, BKSY, BXDC, CRCL, CRDO |
| 2 | DASH, DIOD, FPS, MIAX, MRVL |
| 3 | MVST, MXL, NBIS, NVTS, OUST |
| 4 | PATH, PGY, POET, RDW, RGTI |
| 5 | RKLB, SIVE, SMCI, STM |

Run the same Connors RSI2 6.0.0 and Minervini VCP 10.0.0 definitions; each strategy/cohort owns $10,000. Preserve 1% risk, 25% position cap, four positions, 5% participation, configured fees and 10bps adverse slippage. Keep SOXL excluded only from this plan. No replacement stocks and no alteration to strategy rules.

Attempt each of the ten portfolio treatments once: a hard ceiling of ten economic job attempts, including jobs that fail during preparation. Do not retry automatically. If an additional attempt is needed, report the concrete failure and propose an amendment before starting it; publish any still-unrun portfolios explicitly. No optimizer or changed treatment is included.

Required checks before real execution:
- Exact warm-up boundary, later eligibility, delayed known-at time, never-warms and unexplained-data-gap cases.
- Unclosed long/short hypotheses, including rejected overlap/risk/slot candidates.
- Admitted open positions, partial fills, partial exits, reservations, pending exits and cutoff independence from later data.
- Missing marks, month boundaries, New York source dates and open-position monthly reconciliation; June 30/July 1, August 31/September 1, Friday/Saturday and the exact study cutoff.
- Partial entry and partial exit across a month boundary: weighted basis, actual fees once, realized/unrealized separation and equity reconciliation.
- Existing closed fills/fees/risk/admission remain identical; changed reporting bases are labeled.
- Artifact identity/tampering and fill/cash/position/equity reconciliation.
- API contract round trip; Web result display; Android compilation and client result mapping. Phone installation is not required for this checkpoint.
- Saved-plan settings/hash, idempotency, cancellation drain and isolation where affected.
- Independent senior-engineering review before running; independent quant audit of actual data identities, eligibility, fills, marks and all monthly rows afterward.

Report monthly, separately by strategy/version and cohort: total-equity return, realized/unrealized amounts, close drawdown, costs, exposure, closed/open counts and reasons for rejected/unavailable results. No pooled or accumulated-period performance. Reconcile changed numbers to eligibility, admitted open exposure and accounting differences; do not attribute them to better signals.

April–October has already been inspected repeatedly. These reruns are development and implementation evidence, not untouched validation or authorization to trade.

## Resources, retention and stop conditions

Engineering estimate: roughly 1–2 working days for the lifecycle/contracts/client changes, focused verification and review, revised after the first tested checkpoint. This is a cross-layer accounting repair, not a one-line toggle.

The previous ten jobs took 10m25s including 6m29s preparation; subsequent books took about 18–30s each. A cached native-daily rerun is provisionally 5–15 minutes, excluding provider gaps and review. News coverage/evaluation has no honest completion estimate until the source audit is done. No new SIP history collection is silently included.

Stop project runtime before source changes; identify and stop only owned TradingFlow processes. Restart Web/ngrok afterward with trading and feeds paused. No live/paper orders. Keep heavyweight processes sequential.

Preserve originals, prepared history, news, wishlists, Finviz lists, credentials and every published outcome, including failures. Retain run-owned temporary copies until an independent quant reviewer explicitly approves cleanup of the exact run; then delete only those verified owned copies through the reusable cleanup component. Keep executable code in normal source, never outputs. Do not migrate old reports to make them look like new results.

## Completion checklist

- [x] Source and retained-evidence checks by two Sol 6.1 High agents.
- [x] Existing capabilities and specific gaps identified; no code/data mutation during investigation.
- [x] Concrete scope, data flow, API/client impact, tests and runtime estimate written.
- [x] Parent and independent plan review finalized; source-session valuation, partial-exit accounting, attempt ceiling and evidence-mask findings incorporated.
- [x] User approved Stages 1–4: "run the fixes" and "Backtest the cohort". Later instruction also authorizes existing-component technical/news comparisons and improvement work; this does not fabricate missing news evidence or permit a custom runner/configuration.
- [x] Eligibility and open/pending lifecycle implemented and reviewed.
- [x] Artifact/ledger/monthly reconciliation implemented and reviewed.
- [x] API/Web/Android integration checked: server records round-trip, real Web rendering/month submission verified, Android Release compiled. No phone installation claimed.
- [x] News/prior-results audit completed with exact unknowns. See `docs/research/news-paired-diagnostic-audit-2026-10-21.md`; it identifies the catalog/adapter gap, original receipt limits, unevaluated classification and retained monthly recovery evidence. Current SIVE asset response and primary listing-date checks are retained in `outputs/volatile-swing-followup-20261021`; they do not establish missing historical trades or historical receipt times.
- [x] Focused verification passes on final code: 266 original focused checks, seven matrix cases, six browser scenarios, then seven actual-entry and 15 assessment/projection checks for the final reporting repairs. Counts overlap and are not summed.
- [x] Ten normal saved-plan portfolios rerun and independently audited: exactly ten attempts, 56 supported monthly rows and 14 explicitly unavailable SIVE rows.
- [x] Monthly report and next exact strategy experiment delivered: `outputs/volatile-swing-followup-20261021/monthly-results.md` and `docs/research/volatile-next-experiment-design-2026-10-22.md`. New recipe registration and news execution remain proposals.
- [x] Temporary copies cleaned only after ten exact-run quant approvals; core evidence preserved; final Web checked through ngrok with trading/feeds paused. See `docs/verification/volatile-swing-followup-2026-10-22.md`.

Approval boundary: AGENTS.md, "Before adding anything to the implementation, present the concrete design to the user and obtain explicit approval." The earlier request to approve designs remains in force. This document makes the next approval concrete; there will be no repeated approval request for work within the approved scope.

Execution instruction, 21 October: use Sol 6.1 High workers and parent review; use normal components and shared configuration selections only. Preserve all final outcomes. Cleanup needs a saved quant-review approval bound to the exact result/workspace; success, failure and cancellation do not themselves authorize cleanup. Technical-plus-news execution must use a supported registered strategy and defensible availability policy; absent evaluation or coverage stays visible rather than becoming a fabricated no-news pass.
