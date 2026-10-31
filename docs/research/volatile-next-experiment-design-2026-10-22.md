# Volatile swing: next bounded experiment design

Date: 22 October 2026. Status: approved for implementation. The user approved this
concrete proposal and design on October 22. This authorizes the two reusable
recovery registrations, their ten-attempt technical comparison, and the generic
news evidence binding across server, Web and Android. News economics remain
conditional on the evidence requirements and separate frozen comparison below;
approval does not create missing human labels or historical receipt evidence.
The ten earlier baseline attempts remain immutable. Sol 6.1 High agents implement
the approved scope; the parent and independent engineering/quant reviewers verify it.

## Decision and evidence

Propose one price-only comparison: the retained broad recovery entry with its
SMA5 exit disabled versus enabled, under the same repaired native execution policy.
This isolates an exit change already supported by the shared engine. Do not tune
VCP volume/geometry, RSI2, risk or slots to the inspected rises. A later news
comparison uses the enabled-SMA5 recipe and existing evaluated-event admission;
it cannot run merely because additional articles were collected.

The completed diagnosis is
`outputs/volatile-swing-followup-20261021/strategy-diagnosis.md`. Its VCP first
failures are ordered rules: volume failures do not count otherwise valid patterns.
Connors' illustrated rising intervals did not satisfy its deep-pullback setup or
required warm-up. Recorded overlap rejections do not support more capital/slots.
SMA5 exits can lose from entry; their recovery condition is not a profit guarantee.
The corrected missed-move display now recognizes actual open entry fills; the
published older diagnostic remains unchanged. None of these findings identifies a
news cause or proves a profitable adjustment.

The recovery lineage and prior contrasts are in
`news-paired-diagnostic-audit-2026-10-21.md` and the preserved cycle03 evidence.
Parent identity is `technical.family03.mr_a0b1@1.0.0`, content SHA256
`c151cb4becae9f19b0aeeab7517c884401f14d716fbc404e78bf8dfa4b8396bb`.
Its `completed-bar-precommitted-terminal-owned-v5` policy differs from the repaired
native cutoff behavior. Original April 7–October 6 results and the old SOXL-containing
fifth cohort remain historical evidence; they cannot be relabeled as new controls.
Prior exit effects were mixed and repeatedly viewed, so this is a controlled
development comparison under repaired execution, not a new untouched discovery.

## Price-only treatments and fixed inputs

Proposed reusable Research identities are
`technical.recovery-exit-control@2.0.0` and
`technical.recovery-sma5-exit@2.0.0`. Names/version are proposal choices; actual
canonical hashes are computed and reviewed before any run, never supplied here.

Both resolve the parent's exact broad-recovery settings: daily long; stock above
SMA200 and lagged SPY/SMA200 regime; stretch in the preceding five bars through
RSI2<10 OR three down closes OR lower Bollinger stretch; close above the preceding
high; RSI2-ascending rank; structural swing/stretch-low stop plus $0.01 buffer;
2.5R target; five-bar maximum; minimum one bar before technical exit; no trailing
stop, sentiment, sector-return or drawdown gate. Preserve every other resolved
parent field. A machine-readable diff must show the single intended difference:

| Treatment | `exit_on_close_above_sma5` |
|---|---|
| Technical exit control | `false` |
| Technical early exit | `true` |

Bind both to one explicitly versioned shared admission profile describing the
actual repaired native execution/cutoff policy. Preserve completed-bar causality,
next executable fills and ordinary portfolio admission; retain real open exposure
at cutoff. Do not silently reuse a historical profile/hash if its policy differs.
No new execution algorithm is proposed. Technical news collection settings cannot
affect these recipes because neither consumes a news rule.

Use the existing shared swing profile and saved-plan workflow. Freeze before
outcomes: April 21, 2026 00:00 New York to October 21 00:00 exclusive; identical
prepared price/calendar snapshot across arms; 260 genuine available daily bars with
later eligibility; 10 bps adverse slippage and existing fees; independent $10,000
books, 1% sizing budget, 25% notional cap, four slots, 5% participation and unchanged
same-ticker overlap rule. No price/volume/stop/exit parameter grid or cost retuning.

| Cohort | Exact symbols |
|---|---|
| 1 | ASTS, BKSY, BXDC, CRCL, CRDO |
| 2 | DASH, DIOD, FPS, MIAX, MRVL |
| 3 | MVST, MXL, NBIS, NVTS, OUST |
| 4 | PATH, PGY, POET, RDW, RGTI |
| 5 | RKLB, SIVE, SMCI, STM |

Do not replace symbols or interpret a current wishlist as old membership. Preserve
warm-up reasons and unexplained gaps. The latest SIVE evidence leaves cohort 5
economics unavailable; an unresolved name must not be dropped to complete its book.
Retain each attempt even if it fails during preparation.

## Exact reusable changes requiring approval

Price comparison needs two reusable source artifacts under
`configs/backtest/strategies`, Research entries in `configs/strategy-catalog.json`,
the reviewed admission profile and explicit original-parent lineage. Preserve
canonical/current/archived definitions. These are selectable product research
recipes, not a custom run configuration or an output-folder executable. Registration
is a material addition under AGENTS and needs explicit design approval. If the
user's no-new-configuration constraint excludes even this reusable registration,
continue the existing Connors/VCP diagnosis; do not bypass the catalog through CLI.

`StrategyArtifactCatalog`, `ConfigCatalogService`, `RunConfigWriter` and ordinary
BacktestPlan already expose registered daily Research artifacts. Price-only
selection needs no new endpoint, arbitrary overrides or client P&L logic. Current
shared `MonthlyRunResult`, portfolio state and eligibility responses are reused.
The existing API/Web/Android choices should show descriptive recipe names and exact
version/hash, original lineage, costs and open-at-cutoff policy before saving.

The later news comparison additionally needs one generic evidence-selection bridge:

1. Extend the shared backtest request/plan contract with an optional immutable
   decision-context reference: server-resolved ID plus expected manifest SHA256.
   Add an explicit unavailable reason to evidence choices. Clients never submit
   arbitrary filesystem roots, classifier pass booleans or unverified event rows.
2. Extend existing configuration/catalog services to resolve that reference from
   approved research evidence, verify its manifest and full dependency hashes,
   then populate existing `engine.evidence_manifest_path` and
   `engine.evidence_research_root`. A required-evidence recipe with no valid binding
   is rejected before economic admission; saving an explicitly incomplete proposal
   must not start a zero-event portfolio.
3. Extend the existing frozen-context provider/input resolver to expose and freeze
   its verified dependency graph: manifest, typed sources, context documents and
   referenced price bundles. `BacktestRunInputFiles.Resolve` currently captures
   strategies and SIP/calendar inputs but does not enumerate this context graph.
   Include it in ordinary durable request capture, tamper/restart checks, prepared
   comparison identity and result provenance. Never mistake context evidence for
   executable configuration. Recheck exact bytes at job start and fail on changes.
4. Add the same evidence summary to shared plan/job-details transport: manifest
   hash, taxonomy/classifier/evaluation identity, availability basis, coverage scopes,
   lookback/freshness and unknown reasons. Web and Android render those server
   facts beside recipe identity and monthly results; they implement no classifier,
   coverage decision or return calculation. Existing unbound price plans retain
   their behavior. Test both clients and shared serialization/direct consumers.

No provider ownership migration, new research runner, second database, optimizer,
background polling, ad-hoc strategy toggle or paper/live routing is proposed.
Offline evidence preparation belongs in reusable existing evidence services/CLI;
if a real source-to-context mapping is unsupported, present that exact additional
adapter design for review rather than writing a bespoke output script.

## Later technical/news arms and evidence contract

The fixed early-exit recipe above is the price parent for all news arms. Proposed
shared settings are explicit existing defaults: five-day event lookback and 24-hour
maximum coverage age. These are availability assumptions for review, not thresholds
selected from profits. No category filtering or technical gate is added implicitly.

| Arm | Existing qualification fields |
|---|---|
| A: derived price-only | No qualification block |
| B: required-evidence control | `mode: gate`, `require_event_context: true`, `event_lookback_days: 5`, `event_max_coverage_age_hours: 24`, `reject_adverse_events: false` |
| C: broad adverse-event treatment | Same B, with `reject_adverse_events: true` |

C rejects observed adverse flags OR negative direction across the complete accepted
event set; it is not negative-guidance-only. Do not delete inconvenient categories
to imitate a narrow rule. Existing qualifier first requires eligible drawdown
observations as well as bound event evidence. Therefore B-minus-A is the complete
required-evidence admission effect, not raw article coverage; C-minus-B is the rule
contrast. Record each missing component and any later ordinary capital reallocation.

Use the existing `FrozenManifestDecisionContextProvider`, request assembler and
decision kernel in the normal runner. Evidence must satisfy current validators:
stable security/adjustment identity bound to price prefixes; dated sector mapping;
matching independently evaluated taxonomy/classifier; exact provider/article/revision
hashes; publication/update/actual receipt/classification/known-at clocks; independently
bounded company, sector and global coverage; latest available revision ordering.
Unknown required inputs reject at the original trigger, not a later retry. Empty
events establish no adverse event only when all coverage checks pass.

Parent's completed normal-CLI collection is recorded in
`outputs/volatile-swing-followup-20261021/news-collection-audit.json`; use its exact
scope, page termination, source hashes and content counts rather than recomputing
or predicting them here. Actual receipts are October 21 22:13–22:15 UTC. Completion
of those company queries does not certify historical sector/global coverage,
original revisions or classification. A REST download made now does not establish an earlier
receipt or classifier completion. Provider-update diagnostic reconstruction is
separate from this strict context contract and is not currently wired into normal
API news input. Do not backdate clocks or switch to generic sentiment to force C.
Any conservative historical economic mode requires its own reviewed reusable
availability adapter and clearly distinct provenance; it is outside this proposal.

## Human review: only the existing 25-story pilot now

Reuse the retained `news-review-25.xlsx`, bound by `pilot-selection.json` to the
original assessment packet. Preserve the original; the human completes a copy.
Requested human work is only Review sheet Category column B, rows 7–31: choose one
of the 13 guide categories for as many stories as practical. Blank is unreviewed;
`unknown` is an actual judgment. Optional notes are column I; reviewer/completion
fields are B4/F4. Do not edit source identities/text/context/category guide or use
formulas. Judge exact archived text without later prices, revised website articles,
other reviewers or model answers. `news-category-pilot import` checks those bindings
and retains actual import time; no missing label is synthesized.

Deliver reviewed/unreviewed counts, category disagreements with frozen predictions,
and abstention/text-support examples. This is development feedback on categories,
not a held-out economic accuracy pass. The workbook has no direction, affected
target, adverse or materiality judgments. It cannot validate the C rule or satisfy
the existing full independent evaluation protocol. No new 500-story assignment is
included. If no already accepted evaluation and provenance become available, B/C
remain unexecuted; a separate realistic human-evaluation design is required before
news economics. Code-generated labels cannot substitute for independent humans.

## Preregistration and monthly/cohort decision criteria

Freeze exact resolved artifacts/profile, source and calendar hashes, cohorts,
boundaries, costs, masks, budget and these criteria before the first additional
economic attempt. Publish the freeze through the existing evidence/plan records.
Keep all monthly rows separately by strategy/version/hash and cohort, including
partial April/October, zero activity and unavailable results. No pooled or
accumulated-period return or selected top-N result replaces the complete table.

Operational acceptance requires exact order/fill/cash/quantity/fee/mark
reconciliation, causality, matching source snapshots and unchanged non-treatment
settings; unknown or incomplete controls withhold their comparisons. Reconcile
history eligibility, overlap/risk/slot reasons, pending/open exposure and monthly
realized/unrealized changes. A missing denominator or inactive arm is not a perfect
score. Report independent accepted opportunity counts and symbol/month concentration;
correlated months/cohorts are not independent trials. Candidate forward highs or
gross labels are not simulated profits of rejected opportunities.

Proposed conservative development screen, not a profitability/promotion gate:

- Use full May–September matched cohort/month cells as primary; publish partial
  months separately. The maximum is 25 full-month pairs per price contrast; missing
  cohort 5 currently reduces usable pairs rather than becoming zero.
- Compare monthly net total-equity return in percentage points and monthly
  close-to-close drawdown. A candidate is descriptively preferable only if no
  matched full-month cell has lower return or greater drawdown, and at least one
  cell strictly improves. Publish trade counts/exposure/costs alongside the result.
- If the treatment is weakly worse in every matched full-month cell with at least
  one strict deterioration, reject that change on this development evidence. Mixed
  tradeoffs or insufficient activity/evidence remain `RETAIN_RESEARCH`, not a tuned
  replacement or winner. Do not loosen this criterion after reading results.
- Apply the same paired table to C-versus-B only after valid evidence exists;
  publish B-versus-A separately. Do not count risk reduction from unknown evidence
  as classification skill or claim foregone losses from rejected price paths.

An independent quant review recomputes every published monthly row and difference.
No result here alone can advance to paper/live or satisfy the existing research
program's independence, concentration, execution and human-promotion requirements.

## Later validation and untouched evidence

Proposed prospective price-only validation is November 2, 2026–February 1, 2027
exclusive, followed by a separate untouched holdout April 1–October 1, 2027 exclusive,
all New York dates. Freeze the calendar, exact recipes and outcome embargo before
November 2; if registration occurs later, choose a new future validation boundary
before viewing it, never relabel elapsed observations. February–March is embargo:
purge validation samples whose outcome intervals overlap partition boundaries.
Retain every actual fill, cash movement and open-position mark in the economic
ledger; do not delete positions or force liquidation for sample separation. If any
declared outcome/position interval has not cleared, defer the later partition.
No tuning during either partition, and holdout is opened once after its fixed end.

The existing 24-stock retrospective roster can supply diagnostic future monitoring,
not a promotion-grade PIT universe. The binding program additionally requires at
least 60 independent formation periods, 12 untouched holdout formations and 30 distinct
holdout issuers, plus costs/benchmarks/concentration and other gates. This roster
cannot satisfy 30 distinct issuers. Preserve those requirements rather than calling
six calendar months sufficient validation. News needs a separate future freeze
after its evaluation/coverage is available; it cannot inherit a period already
viewed during price development or claim classifier availability before evaluation.

## Attempt/resources and checkbox plan

Next technical proposal: two arms by five cohorts, hard ceiling 10 job attempts,
including preparation failures; no automatic retry. This is additional to the ten
completed baseline attempts. Later news proposal: three arms by five cohorts,
hard ceiling 15 attempts under a separate reviewed freeze. It is not started or
reserved automatically. Reuse an earlier A only if every input/policy/decision
byte and declared cutoff match; otherwise count its replay within the 15. No grid,
extra 20 bps search or successive improvement loop is included. Any additional
attempt/material change needs a recorded reviewed amendment.

Use one heavy verification/economic process at a time. Cached ten-book execution
is provisionally 5–15 minutes plus preparation/provider gaps and review; 15 news
books provisionally scale from the measured normal runner after evidence exists.
Catalog/profile work is approximately one focused engineering checkpoint; generic
evidence binding, contracts and both clients require a separate 1–2-day checkpoint
estimate, refined after source review. Human evaluation/strict historical evidence
has no credible completion estimate. No new collection estimate belongs here.

- [x] Ten baseline attempts completed; price and news limitations diagnosed.
- [x] Concrete proposal prepared without implementation or economic execution.
- [x] Parent and independent design review; clarify that validation-sample separation never deletes actual positions or economic records.
- [x] User approved the reusable-registration scope on October 22: "next concrete proposal and design is approved."
- [x] User approved the exact price pair/profile/identities and ten-attempt budget; no new generic rule.
- [x] Register reusable Research artifacts; verify single-field economic diff,
  profile identity, closed/open execution parity and Web/Android selection.
- [x] Freeze normal saved plan 8 and sources; run exactly 10 technical attempts once (October 22, 09:39–09:43 UTC).
- [x] Independent quant review; publish all 70 monthly rows and the unchanged `RETAIN_RESEARCH` disposition in `outputs/recovery-exit-comparison-20261022/monthly-results.md`.
- [ ] Import any volunteered 25-story category review; record limitations explicitly.
- [x] Review and implement generic API evidence binding and the complete dependency/provenance graph, with Web/Android consumers and focused checks.
- [ ] Establish actual classifier evaluation and identity/scope/time contracts.
- [ ] Separately approve/freeze A/B/C and at most 15 attempts, or leave unavailable.
- [ ] Freeze future validation/embargo/holdout before their outcomes are viewed.
- [x] Preserve core data and every result; independently inventory each exact terminal run and its disposable/protected files.
- [ ] Remove economic input spools only after exact-run approval. Approval is withheld for all ten: 46 unique normalized news files retain actual receipt/assessment clocks inside those folders. The current component deletes the whole folder, so it cannot safely perform this cleanup yet. Two redundant progress captures alone were approved and removed; see `cleanup-status.json` and `quant-cleanup-approvals.json`.

Implementation is now authorized by the user's October 22 approval. Complete the
approved work without repeated approval requests. Material additions, a different
economic treatment or extra attempts still require an explicit reviewed amendment.

Implementation and run evidence are retained under
`outputs/recovery-exit-comparison-20261022`. The component checkpoint passed 123
focused .NET tests, nine isolated browser checks and an Android Release build.
The two registered recipes have shared native execution policy and differ only in
the SMA5 exit boolean; saved costs, full identities and cutoff behavior are visible
in both clients. The real comparison reused all 54 prepared queries and collected
no new market data. Eight books have economic rows; both cohort-5 books retain
unavailable returns because SIVE has no required candles.

Conditional work remains distinct: no completed human review was supplied, no
server profile currently binds a strict decision-context archive, and the future
validation periods have not occurred. The integration does not manufacture those
inputs. No news-enabled economic job, extra price attempt, future scheduler or
paper/live activation was started.
