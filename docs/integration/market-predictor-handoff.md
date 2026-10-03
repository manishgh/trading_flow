# Market Predictor integration handoff

Date: 2026-09-27. Status: revised consumer integration implemented and focused
verification passed, including the Android build.

## Implemented

- The production deserializer reads `contract`. Validate accepts exactly
  `market_predictor.prediction`; missing, null, wrong-type, unknown and retired-only
  contract fields become incompatible. No default contract is fabricated for an
  unavailable result. Malformed batch responses remain isolated from later batches.
- Predictor result and Web/Android projection properties are named Contract. The
  operations page uses the current contract name. Published `/v1` endpoint URLs
  remain as specified by the producer; no endpoint migration was inferred.
- The HTTP transport and evidence/serialization records remain separated into
  `MarketPredictorHttpClient.cs` and `MarketPredictorEvidence.cs`.
- The copied golden fixture is
  `src/TradingFlow.Tests/Fixtures/Contracts/swing_prediction_response.json`, with
  source path and SHA-256 recorded beside it. SHA-256 is
  `b62f577dbd9ca1a09c9959d5da84df0db9040d8e6b3c17d26eabfcb4f369f1cf`.
  Tests use actual HTTP deserialization and Validate through both request paths
  for T000, T059, T060 and MISSING. Abstentions retain available swing evidence,
  null scores and distinct Errors. The copied bytes match the published hash and
  the local producer fixture; a missing source in an existing checkout fails.
- positive_setup is supportive, low_probability opposed, and
  ranked_candidate/neutral/abstain neutral. Actions and retired names do not
  classify a signal. A swing leg cannot override a neutral final signal.
- A shared reason formatter distinguishes inputs incomplete at the decision from
  not in the point-in-time universe in the web desk, evidence detail and Android
  symbol detail. Raw codes survive the Web-to-Android JSON projection, including
  nullable evidence. Unknown reasons retain their text.
- Predictor weights, contributions and veto penalties remain zero. Tests verify
  operational score and ordering stay independent of predictor direction and
  availability. No order/admission logic was changed by this integration.

Both September 27 contract entries are acknowledged in
`market-predictor-notes.md`, with the later unversioned name superseding the initial
name and earlier deferment. Pending training metadata remains unimplemented.
The earlier draft under `.test-tmp/predictor-api-deferred` is historical only;
active implementation and fixtures are the files listed above.

## Verification

Tier: component checkpoint, limited to changed evidence consumers, with additional
focused retirement checks to answer the user's swing-only status question.
Normal nullable/analyzer and warnings-as-errors settings remain enabled.
Heavyweight checks run sequentially.

From `C:\project\trading_flow`:

```powershell
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter 'FullyQualifiedName~MarketPredictorHttpClientTests|FullyQualifiedName~UniverseRankServiceTests|FullyQualifiedName~PredictorEvidencePresentationTests|FullyQualifiedName~RetiredStrategyConfigurationTests|FullyQualifiedName~ReserveAsync_NonSwingRiskHorizonPersistsNothing|FullyQualifiedName~RecoverPendingAsync_NonSwingEntryExpiresWithoutBrokerPost|FullyQualifiedName~DispatchAsync_RetiredHorizonPriorAttemptRequiresBrokerEvidence' --verbosity minimal --logger 'trx;LogFileName=predictor-integration.trx' --results-directory .test-tmp/predictor-integration -m:1 --disable-build-servers -- xUnit.MaxParallelThreads=1 xUnit.ParallelizeTestCollections=false
```

Result: 138 passed, 0 failed, 0 skipped. Tests, Web, Contracts and their project
dependencies compiled successfully. Report:
`.test-tmp/predictor-integration/predictor-integration.trx`.

The HTTP tests verify exact request fields and published swing endpoint, contract
rejection, all four fixture outcomes, source parity, malformed batch isolation and
Web-to-Android JSON. Local evidence tests cover scored/absent evidence, combined
and unknown reasons, both abstentions and final-signal precedence.

```powershell
dotnet build src/TradingFlow.Mobile/TradingFlow.Mobile.csproj --no-restore --verbosity minimal -m:1 --disable-build-servers
```

Android build: succeeded, 0 warnings, 0 errors (1m59s). The updated client compiled and packaged; no device or emulator session was started.

`git diff --check`: passed. Reference scan found no stale numbered predictor
contract identifier or ContractVersion property in active predictor consumers.
Independent plan and code/design reviews found no actionable defects.

## Swing-only status

Current scans found no intraday/day-trading references in the checked Web, Mobile,
backtesting configuration/engine and active config sources. The passing retirement
checks reject 48 retired strategy settings, retain 1h/15m/5m swing execution
confirmation, refuse non-swing reservations and prevent retired-horizon entries
from reaching broker submission. Prior ambiguous attempts still require broker
evidence, preserving reconciliation. Tests use isolated temporary databases and
mock brokers; no actual order was sent.

## Verification limits and preservation

No live predictor/provider/broker connection, browser session, Android device or
emulator was exercised. Python producer tests were deliberately unrun under the
read-only producer boundary; consumer parity uses the published served fixture.
The full release suite and strategy/model replay were unnecessary for this scoped
consumer change. This does not close unrelated product workflow checkpoints.

No TradingFlow runtime was active before edits. No application service was started.
No GitHub sync occurred. Secrets, local appsettings, database/paper state and candle
caches remain untouched. Existing scratch files were preserved; build/test outputs
are inside TradingFlow. Market Predictor was read only.
