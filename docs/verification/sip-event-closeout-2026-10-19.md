# Execution sources and news category pilot — 19 October 2026

This is a component checkpoint. It is not a strategy promotion or a new portfolio
performance run. Trading was not enabled. Existing published research results,
including failed runs, retain their original policies and outcomes.

## Implementation and focused verification

The shared C# execution book has an explicit nanosecond event path and an additive
single-opportunity coordinator. They use actual quotes/trades instead of requiring
an OHLC candle for every execution interval. Commands cannot consume the event
that created them. Stops require an eligible print inside a strictly earlier fresh
quote and fill on a later event. Parent exposure, partial fills, full-parent native
protection, route expiry, OCO, fees and late fill reports have focused tests.
Replacing an entry cannot extend its committed route expiry.

The existing ordinary portfolio runner retains its published candle-based policy.
The new event coordinator is not automatically selected by that runner. A new
portfolio experiment still needs the event adapter connected to its frozen policy,
an explicit supported mapping from raw quote/trade conditions to eligibility, and
verified price/quantity units. Unknown condition codes are retained in the source
inventory; they are not granted stop or fill eligibility.

The final affected projects built successfully. The focused component command
passed **201 tests, zero failed, zero skipped**. It covers the event book,
single-opportunity coordinator, archives/readers, existing execution/OCO consumers,
historical evidence workflow, economic news rules and Excel importer. The full
application suite and a new economic portfolio experiment were deliberately not
run at this component checkpoint.

The result is `closeout-focused-news-pilot-v1.trx` under the closeout output's
`test-results`. From the repository root, the focused command was:

```powershell
$filter = 'FullyQualifiedName~EconomicNewsRuleClassifierTests|FullyQualifiedName~NewsCategoryPilotReviewTests|FullyQualifiedName~DeterministicExecutionEventBookTests|FullyQualifiedName~SipExecutionArchiveTests|FullyQualifiedName~SipExecutionArchiveReaderTests|FullyQualifiedName~EventBacktestExecutionReplayTests|FullyQualifiedName~NewsAssessmentWorkflowTests|FullyQualifiedName~HistoricalEvidenceWorkflowTests|FullyQualifiedName~DeterministicExecutionSimulatorTests|FullyQualifiedName~OwnedOco|FullyQualifiedName~FineExecutionReplayTests'
& 'C:\Program Files\dotnet\dotnet.exe' test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore -m:1 --disable-build-servers --filter $filter --logger 'trx;LogFileName=closeout-focused-news-pilot-v1.trx' --results-directory 'C:\Users\manis\Documents\Codex\2026-10-03\c\outputs\strategy-validation-closeout-20261019\test-results' -- xUnit.MaxParallelThreads=1 xUnit.ParallelizeTestCollections=false
```

The implementation design and repairs have independent source-hash-bound review
receipts. The output root is:

`C:\Users\manis\Documents\Codex\2026-10-03\c\outputs\strategy-validation-closeout-20261019`.

## Completed physical source audit

Both collectors, the C# paired reader, the independent prefix checker and the
final physical-file reconciliation completed successfully. The final artifact is
`source-closeout-reconciliation.json`, SHA256
`ec20146435b00b6c0b0b16a7eb6d16a77b792a6f76db83260ba999741672a9cb`.
All 747 required pairs and 1,494 full-session requests are present. Their 28,306
pages contain **165,076,283 quotes and 110,441,012 trades**, with 2,936,310,272
compressed bytes. Every compressed payload was rehashed after the full reader
pass, and measurement totals/histogram populations were reconciled to their
pinned source identities. The 747 additional pre-open archives contain 61,583
quotes in 747 pages, with 675,346 compressed bytes; their raw and compressed bytes,
receipts, nanosecond bounds and unique request membership were independently
checked. No requested pair or prefix remains unfinished.

These counts describe the declared regular-session windows and their thirty-second
prefixes. They do not claim overnight/auction coverage, executable fills, original
live receipt timestamps or a completed strategy performance rerun. The remaining
execution integration and historical-feature limitations below still apply.

## Source collection scope

`execution-horizon-freeze.json` binds all twenty control journals and **420
triggered candidates**, including candidates without a completed order intent.
It specifies **747 symbol/session pairs** and **1,494 SIP quote/trade requests**.
The universe is the original five cohorts, with at most five stocks per cohort;
SOXL remains excluded. Twenty symbols have candidate windows; MVST has no frozen
trigger requiring a quote/trade window. No stock was substituted.

Each window covers the entry session through its sixth subsequent session, using
full regular trading hours and the captured exchange calendar, truncated at the
study's 6 October endpoint. This is maximum possible holding-period coverage,
not windows chosen from realized exits or winners. A separate frozen request set
collects the thirty seconds immediately before each session opens. Thirty seconds
is a declared freshness scenario, not a measured broker latency.

`execution-measurement-index.json` and `source-closeout-reconciliation.json` are
completion artifacts only after every required paired source has been consumed
and checked. `boundary-source-audit.json` separately checks every pre-open source.
Do not infer completion from the number of files or a successful individual HTTP
response. Per-page compressed/raw hashes, timestamps, row counts and complete
pagination are checked. Failed/interrupted collection logs remain audit records.

DIOD's 21 May 2026, 16:00–16:05 UTC interval returned **90 trades and 50 quotes**;
BKSY's 26 August 2026, 16:15–16:20 UTC interval returned **71 trades and 118 quotes**.
Both still returned zero one-minute/five-minute candles for those intervals.
These are missing-candle intervals with actual market observations. No substitute
candle was invented. The new event path can represent the
observations; declaring a particular print executable still requires its condition
and quote checks.

Read-only paper-account collection returned 23 relevant orders, including twelve
filled orders and 33 fill activities. All twelve quantities and weighted average
prices reconcile within 0.000001. Three market orders submitted during regular
hours took 0.426613, 1.216805 and 4.944004 seconds from broker submission timestamp
to last fill. Resting limits and outside-hours submissions are separate. These
durations include queuing/partial fills and do not measure client network or fill
report latency. Paper fills do not establish live market impact or queue priority.

## News: code first, small human pilot

`EconomicNewsRuleClassifier` implements thirteen versioned economic categories,
records supporting text and abstains on conflicting/insufficient evidence.
Direction is attached to the matched event. Stock metadata alone does not establish
which company is affected. Materiality remains unknown rather than being guessed.
Headline-only, summary-supported and body-supported results remain distinct.

`news-economic-rules-v1.json` freezes predictions for the original 500-item packet
at **19 October 2026, 14:19:31 UTC**, before delivering the blind pilot workbook.
It binds the source packet and classifier assembly hashes. No human labels were
generated, no accuracy was measured, and no historical classifier time was claimed.
The original display-topic baseline remains a separate immutable artifact.

The user-facing **news-review-25.xlsx** asks for only the Category column on 25
development stories. Unknown and partial completion are accepted. Source context,
publication dates, links and a category guide are included; AI answers and the
untouched held-out stories are excluded. Import checks bind the workbook's source
cells, article revisions, links and supporting sheets to the original packet.
Formulas are rejected. Importing the untouched workbook correctly produced zero
labels and twenty-five unreviewed items.

This pilot supplies development feedback and can expose classifier mistakes. It
does not satisfy the full human labeling/promotion protocol, and code cannot
independently certify the accuracy of labels it generated itself. The user is not
being asked to complete the original 500-item review now.

Alpaca's returned 122,969 news revisions include 85,400 without body or summary,
despite requesting available content. The classifier can use factual headlines
and abstain where necessary; it cannot reconstruct absent article bodies. One
revision whose update time precedes its creation time remains quarantined.

## Historical facts that today's download does not establish

The actual historical manifest contains 122,969 news versions, 42 current-vintage
earnings records, 21 current Finviz sector observations, 24 current asset records
and 104 corporate actions. Actual capture times are retained.

- An original delivered news revision and the app's original receipt time require
  a saved delivery/revision log. Today's REST response does not supply that log.
- The earnings date/consensus known before an old entry requires a dated schedule
  or estimate snapshot. Returned current-vintage earnings results do not supply it.
- A sector assignment effective before an old entry requires dated membership
  evidence. Current Finviz sectors and an issuer's SEC industry code do not supply
  old Finviz membership.
- Current asset identifiers and the returned corporate actions do not by themselves
  certify a complete historical share-class master or every adjustment factor.

The resolver rejects free-text or boolean claims of original delivery. It provides
an explicitly separate conservative provider-update diagnostic mode and an official
dated-archive fact mode. Those modes cannot relabel a current capture as an old
receipt or authorize trading. No accepted SEC filing archive was supplied to this
checkpoint. These limitations affect strict point-in-time feature claims; ordinary
downloadable candle history is present and should not be described as missing.

No operator database, wishlist or live trading state was used as a test sink.
Synthetic test fixtures are scoped and deleted by their tests. Real requested
provider archives and immutable research evidence are retained.
