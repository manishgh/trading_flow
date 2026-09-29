# Market Predictor integration handoff

Date: 2026-09-29. Branch: main. Status: verified integration merged and pushed.

The user explicitly requested merging both projects into main and pushing to their
configured GitHub remotes. Main fast-forwards to the existing unified-swing-product
history. The original TradingFlow checkout has substantial unrelated uncommitted work
and a running Web process, so the merge uses this separate worktree. No original source,
runtime, local settings, broker state or database is modified.

Publication receipt: after the user's renewed approval on September 29, main was
pushed successfully to `https://github.com/manishgh/trading_flow.git` through
`e3b6734`. Market Predictor main was also pushed through `8d6a9c6`. The earlier
automatic approval-review blockers are resolved. Runtime deployment is unchanged.

## Frozen merge scope

Carry the reviewed API v4 migration and only its required predictor dependencies onto
committed main: transport/evidence separation, exact contract_version validation,
producer golden fixture, available abstentions with nullable scores, shared reason
labels, display-only final-signal mapping, and Web/Android evidence projections.
The existing uncommitted order, authentication, preparation, persistence and broader
UI changes remain in the original checkout and are excluded from this commit.

The seven-file migration was reviewed against the dirty source snapshot and passed
84 freshly built tests plus an Android build. That receipt is not proof of this
isolated main checkout: re-run the affected tests/build after copying only the needed
dependencies. Read-only independent review must check the isolation boundary.

Exit gates: exact v4 and retired/malformed refusal on both request paths; five fixture
outcomes with producer hash/byte parity; distinct sector_peer_floor and preserved
nullable scores; advisory score/order isolation; fresh Web/Contracts/Android builds;
no unrelated modifications in the main diff. Atomic code history preserves the prior
main commit for rollback; do not modify runtime or data to roll back this source change.

Real endpoint acceptance, nightly production inputs, promoted models and live service
deployment remain separate from this source merge. No broker calls or release claim.

## Final verification

Tier: component. The independent plan and final isolation reviews found no remaining
actionable issues. All 84 focused tests passed with fresh compilation of Web, Contracts
and their dependencies. The Android build succeeded with zero warnings and errors.
The producer fixture remains byte-identical with SHA256
`96cbcd133b8e96253b034fabba10624be79b0d80550f043f3869905251bec522`.

Commands from this isolated main checkout (TEMP/TMP set to the chat's writable work):

```powershell
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --disable-build-servers --filter 'FullyQualifiedName~MarketPredictorHttpClientTests|FullyQualifiedName~UniverseRankServiceTests|FullyQualifiedName~PredictorEvidencePresentationTests' --logger 'trx;LogFileName=predictor-main.trx' --results-directory ../consumer-main-results -m:1
dotnet build src/TradingFlow.Mobile/TradingFlow.Mobile.csproj --no-restore --disable-build-servers -f net10.0-android -v minimal -m:1
```

No full suite or live endpoint acceptance was run for this bounded predictor source
integration. Original runtime and dirty source remain unchanged; its other changes
must be reconciled separately without resetting local work or data. Use the committed
main implementation, not the earlier dirty-snapshot patch, for further integration.
