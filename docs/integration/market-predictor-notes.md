# Market Predictor integration notes

## 2026-09-27 — acknowledgement of the 2026-09-27 contract entry

TradingFlow acknowledges `market_predictor.prediction.v3` and the published golden
fixture SHA-256 `e445eaac20a42c21a5d83900d3ec3f63c9354745102d9633635143f862956c95`
before implementing consumer support. The accepted contract is
`C:\project\market-predictor\docs\contracts\prediction_api.md`.

The consumer will accept only v3, use the current final-signal vocabulary, and
preserve distinct `live_inputs_incomplete` and `out_of_universe` abstentions as
available advisory evidence. Null training_data_end and null abstention scores
remain valid. No pending training-metadata changes are implemented.

Predictor output cannot qualify, veto, prioritize for execution, or authorize
orders. Market Predictor remains read-only; no producer jobs or GitHub sync run.

Questions/change requests: none. Verification results will be recorded in
`docs/integration/market-predictor-handoff.md` after implementation and review.

## 2026-09-27 — API integration deferred by user

This supersedes the adoption plan above. The user requested local structural
preparation while the producer updates its naming and handoff instructions.
Response-version enforcement and the producer golden-fixture integration are
therefore deferred. They are not part of the active consumer changes. Existing
wire behavior remains at its previous baseline until the revised contract is
published and acknowledged. The previous passing integration attempt is retained
only as an uncompiled local draft under `.test-tmp/predictor-api-deferred`.

Current work: separate transport from evidence models; centralize operator-facing
reason labels shared by web and Android; map current final signals for display;
verify advisory isolation. No pending producer changes are implemented.

Question for the next published handoff: confirm the final contract identifier,
fixture location/hash, and whether the version field itself changes. The user is
coordinating that update; no direct developer-to-developer message is sent.

### Local structural verification completed

57 focused transport, ranking/isolation and local evidence-presentation tests
passed. Web/Contracts compiled through the test build; Android build succeeded
with 0 warnings and 0 errors. Independent plan and final code/design reviews found
no actionable issue. See `market-predictor-handoff.md` for exact commands, scope
and deliberately deferred checks. API adoption remains deferred.

## 2026-09-27 — revised contract acknowledged; integration resumed

The user resumed integration after the producer published the later September 27
change-log entry. TradingFlow acknowledges both entries and adopts the later one:
read `contract`, accept only `market_predictor.prediction`, and use the fixture
`C:\project\market-predictor\tests\fixtures\contracts\swing_prediction_response.json`
with SHA-256 `b62f577dbd9ca1a09c9959d5da84df0db9040d8e6b3c17d26eabfcb4f369f1cf`.
This supersedes the earlier naming/adoption and deferment plans above. Historical
hash-pinned records remain unchanged.

Predictor result and web/mobile projection names will use Contract. No new numbered
class, identifier, schema or file name is introduced. The published URLs still
contain `/v1`; they are retained exactly as the endpoint table specifies. Any URL
renaming requires a producer change-log entry rather than an inferred migration.

Pending training metadata remains unimplemented. Predictor output remains advisory.
No other contract questions or change requests are open for this consumer update.
Verification is recorded in `market-predictor-handoff.md` after the final checks.

### Revised integration verified

The revised contract integration is complete for the five assigned consumer tasks.
Both production request paths accept the published fixture and preserve all four
outcomes. The fixture SHA-256 and local source parity passed. Predictor naming,
current signals and abstention displays are integrated in TradingFlow, web and
Android. No pending contract change was adopted.

Verification: 138 selected tests passed, 0 failed/skipped; Web/Contracts compiled
through the test build; Android build succeeded with 0 warnings and 0 errors.
Independent plan and code/design reviews found no actionable defect, and
`git diff --check` passed. The selection includes focused swing-only retirement
and non-swing execution-blocking regressions. Exact commands and limits are in
`market-predictor-handoff.md`. Live predictor, browser and device end-to-end tests
remain unverified; no provider/broker orders, producer jobs or GitHub sync ran.

## 2026-09-27 — producer correction observed; API work deferred

The latest producer handoff has been read. It now requires the public wire field contract_version and value market_predictor.prediction.v3, with fixture SHA-256 360206c2440e4fb39bac210a50b12c6d233e4f49a140fd356e5d283b034536cb. This differs from the earlier integration recorded above.

The user's latest scope explicitly excludes Market Predictor API integration. This entry acknowledges receipt, not adoption. The consumer parser and pinned fixture have therefore not been updated in this structural workflow checkpoint. GoldenFixture_MatchesPublishedHashAndLocalProducerCopy currently detects the mismatch and fails; that failure is retained as pending integration evidence. No pending-change feature is implemented and no producer file is edited. Predictions remain advisory.
