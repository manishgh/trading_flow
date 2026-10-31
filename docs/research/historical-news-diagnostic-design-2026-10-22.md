# Historical company-news comparison — proposal for review

Status: approved for implementation and the bounded comparison on October 22, 2026. The user explicitly authorized existing-component implementation, Sol 6.1 High agents, independent quant review, monthly strategy/cohort reporting and preservation of core data. Engineering/source/frozen-plan checks below still precede economic attempts. The strict historical news comparison remains unchanged and unexecuted.

## The question this can answer

Does the existing recovery strategy perform differently when it avoids entries following a recently published, provider-linked company story?

This first comparison uses the presence of news, without claiming to know whether a story is adverse, material, accurate, relevant to the issuer, or the cause of a price move. It does not require inventing a passed human classification evaluation. It is a different question from the previously proposed adverse-event comparison; company, sector and global classification remain separate work.

The existing `VetoFreshNewsHours` rule supplies this economic idea. Its current transport is insufficient for this historical experiment: the normal catalyst path requires actual receipt/classification clocks, attaches one latest event and has a three-day attachment limit. We will not backdate those fields or feed a five-day diagnostic through that attachment limit.

## What is known and what would be assumed

Actual news receipts are retained as October 19/21/22 timestamps. The human pilot is still blank. Existing catalog collection/normalization passes do not establish classifier accuracy or original historical revisions. The old large-cap event study measures fixed-horizon event returns, not this strategy's portfolios, so its results cannot substitute here.

Reuse `HistoricalEvidenceResolver` and its existing `captured-provider-version-next-session-24h.v1` policy. For each exact retained revision, assumed availability is the first captured exchange-session open strictly after provider update time plus 24 hours. Keep this time in a separate diagnostic field. Actual receipt, archive and assessment times remain unchanged. A later revision, its symbols and any metadata never become available at an earlier revision's time.

Example: a revision updated Monday at 14:00 UTC passes its 24-hour delay Tuesday at 14:00 UTC. If the captured calendar's Tuesday open is 13:30 UTC, assumed availability is Wednesday's open. The existing completed-bar strategy still decides and fills according to its normal later-bar rules; the assumption does not move an execution earlier.

Freshness is measured from original provider publication time: `0 <= decision - published <= 120 hours`. The retained revision must also have reached its assumed availability before the decision. An update does not reset the story's publication age. Thus a month-old story updated today is not a fresh publication in this experiment. The five-day window is fixed before outcomes; no threshold search is proposed.

At each decision, first select the latest revision whose assumed availability has arrived, separately for each provider/article identity. Then apply that revision's symbol membership and original publication age. A newer eligible revision replaces earlier membership, including removing a stock association; do not retain superseded membership or union later metadata into earlier decisions. Exact repeated revisions collapse by content identity, and each article counts at most once. Search all selected fresh articles; do not use only the latest attached catalyst.

Both news treatments use the same evidence-availability check: complete query evidence plus unambiguous revision identity, timestamps and symbol association. Missing/inconsistent clocks, conflicting same-time revisions or unsupported identity produce unknown status and reject the original trigger in both treatments. Future revisions cannot retroactively change an earlier check. The retained evidence must allow the affected symbols and time range of an ambiguity to be determined; otherwise freeze that scope as unavailable before execution. Do not postpone an earlier trigger until evidence becomes available. This common check keeps the third-minus-second comparison limited to the presence rule and its subsequent capital/overlap effects.

## Three separately reported portfolios

Each treatment uses the reviewed SMA5-exit recovery recipe, the same 24 selected stocks excluding SOXL, the same five groups and independent $10,000 per strategy/group. Preserve April 21–October 21, 2026 New York boundaries, the prepared prices/calendar, costs, sizing, stops, exits, overlap rule, four slots and later warm-up eligibility. Group 5 stays unavailable if SIVE remains unresolved; do not replace or silently remove it.

| Treatment | Additional entry condition |
|---|---|
| Technical only | None; price reference under the same frozen diagnostic package |
| Technical with news evidence checked | Required provider-query window is complete and revisions/identity are unambiguous |
| Technical avoiding recent company news | Same evidence check, plus no selected company-linked article published in the last 120 hours |

The second versus first measures the effect of withholding entries when query or revision evidence is incomplete. The third versus second measures the presence rule plus its ordinary later effects on capital/overlap admission. These are not the previous strict A/B/C adverse-event treatments. Unknown query coverage must never be treated as no news.

“Queries checked” means exact requested symbols/window, successful response sequence, exhausted cursor, no truncating cap, intact payload/normalization hashes and documented provider query timestamp semantics. It does not mean all news published in the world, nor complete historical revision coverage. Label this distinction in every report. The publication window needs the preceding 120 hours, starting April 16 at 04:00 UTC. Existing missing-three-symbol collection starts April 21, so that earlier interval needs evidence or explicit unknown status. Audit the older known-symbol archive and October extension before claiming a complete joined window. The capped, unused news caches from the technical runs are not sufficient query-absence proof.

## Smallest reusable implementation, after approval

Reuse the evidence catalog, raw collector/normalizer, historical resolver, immutable artifact store, existing evidence selector and durable dependency capture. Add only the missing adapter from these sources into the normal strategy decision path. No standalone economic runner, second database, output-folder source code or hand-built run configuration.

1. Extend the existing evidence packaging workflow with a versioned diagnostic news manifest: exact revision/source hashes, query coverage, real receipt clocks, policy identity/hash and assumed availability. Use ordinary collector requests only for proven gaps. Keep every source and prior version. Compute story membership per revision; do not reuse the old event study's across-revision union.
2. Add a small typed diagnostic news-presence context to the shared decision request, separate from strict classified-event evidence. It carries the decision time, policy identity, provider-query status, eligible story count and exact consumed revisions. Validate it only for explicit Research backtests with the matching versioned admission policy. Paper/live, strict context and unbound requests reject it. Do not reinterpret `FirstReceivedAtUtc`, `DecisionKnownAtUtc` or evaluation `Passed` flags.
3. Extend the existing news-entry evaluation to consume that presence context for the two declared conditions. Reuse the same recovery signal, order planning, sizing, portfolio, fills and monthly reporting. Register descriptive reusable Research recipes through the strategy catalog, with exact parent lineage and frozen hashes. No runtime sentiment threshold or category toggle is added.
4. Extend the current server evidence reference/summary and dependency capture to recognize this explicit diagnostic manifest kind. Web and Android show “Historical news diagnostic — provider update + 24h, then next session” with actual receipt range, query gaps, policy/hash and a clear statement that original availability and classifier accuracy are unproved. Keep current strict selection semantics. Clients do not compute eligibility or returns.

Conceptual data flow:

```text
Retained provider pages and query receipts + captured exchange calendar
  -> existing catalog and historical resolver
  -> immutable diagnostic revisions/query coverage with separate assumed times
  -> saved-plan evidence selection and durable hash capture
  -> shared recovery decision + coverage/presence condition
  -> existing portfolio execution and monthly results
  -> independent quant review, then exact-file intermediate cleanup
```

The diagnostic fields and policy are server-owned, immutable evidence, not editable timing knobs. No new operator configuration file is required. Existing core archives, wishlists, Finviz lists and results are never modified or deleted.

## Checks and bounded execution

Before economics, test calendar holidays/exact-open boundaries; no use before assumed availability; actual clocks unchanged; later revision metadata cannot affect earlier decisions; a superseding eligible revision removes obsolete symbol membership; all five days are searched; publication age is not reset; duplicates do not inflate counts; incomplete/capped queries and ambiguous revisions cause identical evidence rejections in both news treatments; incompatible policies fail; paper/live and strict paths reject diagnostic context; API/Web/Android and durable restart hashes agree.

Engineering and independent quant reviews must accept the exact adapter, source coverage and frozen plan before Start. Freeze three treatments by five groups, a maximum of 15 attempts including failures, no automatic retries or tuning. Reuse an earlier price book only if all frozen inputs/policy/decision bytes match; otherwise its replay consumes this budget. Do not start empty or unbound treatments merely to obtain a result table.

Publish every monthly row by exact strategy and cohort, including partial April/October, zero activity and unavailable cells; include counts, fees, exposure, accepted/rejected entries and consumed news identities. Use the prior fixed full May–September paired return/drawdown screen; mixed effects remain research. No accumulated/pooled return or hypothetical profit from rejected trades. Historical-data and current-universe limitations remain even if the diagnostic improves results.

Expected scope: one generic evidence/decision adapter plus affected contracts/clients and focused tests, approximately 1–2 engineering days after approval. Source gaps may add provider time; there is no promise they can be resolved. The latest ten cached price books ran in about 4 minutes 20 seconds; 15 books would provisionally take 5–15 minutes after preparation, with independent review taking longer. This is a planning estimate, not a guaranteed runtime.

- [x] Inspect existing components and actual evidence; distinguish strict comparison from diagnostic study.
- [x] User requested this diagnostic proposal for review.
- [x] Independent Sol 6.1 High quant and engineering/design reviews; revision selection and common unknown-evidence treatment clarified.
- [x] User approves this exact diagnostic design and bounded comparison (October 22, 2026).
- [x] Implement and verify the reusable adapter and API/Web/Android integration (177 affected tests; final 27 packaging and 14 client checks; Android build; independent engineering review).
- [ ] Freeze available sources and the saved plan; execute at most 15 attempts.
- [ ] Independently reconcile monthly results and retain all outcomes/core evidence.

Nothing in this proposal authorizes trading or replaces the separate evaluation of adverse news classifications.

Review evidence: [final quant review](../../outputs/news-comparison-cleanup-20261022/independent-quant-diagnostic-design-review-final.md) and [engineering review](../../outputs/news-comparison-cleanup-20261022/independent-engineering-review.md). The earlier review is retained alongside these records.
