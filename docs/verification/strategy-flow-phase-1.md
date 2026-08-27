# Strategy Flow Phase 1 Verification

**Status:** complete; requirements and code/design re-reviews approved

**Date:** 2026-08-27

## Scope

Phase 1 establishes an explicit immutable strategy catalog and one persisted
authorization ledger. It separates artifact existence from permission to execute,
requires exact strategy identity and explicit execution mode, and admits each new
broker entry at the final order-submission boundary.

## Implemented Boundaries

- `configs/strategy-catalog.json` classifies all 21 retained YAML files exactly
  once: 6 research and 15 archived.
- Canonical strategy identity is `(strategy_id, semantic_version,
  content_sha256)` over the fully resolved strategy and admission profile.
- Paper experiment artifacts are immutable and content-addressed. Artifact
  publication precedes grant creation; catalog visibility requires both stores.
- Experiment registration uses a stable operation ID and UTC request timestamp.
  Artifact-first retry is idempotent; a different operation cannot create a second
  active grant for the same exact identity.
- `SqliteStrategyPromotionRegistry` is the single ledger for paper experiment,
  paper shadow, validated, suspension, supersession, revocation, and durable entry
  admissions.
- A prior entry-admission token is idempotently reusable after authorization is
  withdrawn; a new intent fails closed. Protective exits do not require entry
  authorization.
- Backtest, paper experiment, paper shadow, and live modes are explicit. A missing
  or malformed paper mode is rejected by Web and mobile APIs.
- Shadow authorization remains eligible only while its exact paper-experiment grant
  exists; validated authorization requires both experiment and shadow prerequisites.
- Derived immutable artifacts participate in experiment, shadow, and validated
  catalogs by exact identity and preserve their artifact-store path.
- Generated run YAML and strategy snapshots are immutable provenance. Directory
  names and file placement never grant execution authority.
- The operational Web host cannot write human paper-shadow or validated promotion
  decisions.

## Verification

```powershell
dotnet build TradingFlow.sln --no-restore --nologo
dotnet test src\TradingFlow.Tests\TradingFlow.Tests.csproj --no-build --nologo
```

Results:

- Solution build: passed, 0 warnings, 0 errors, including `net10.0-android`.
- Focused lifecycle tests: 66 passed, 0 failed, 0 skipped after review corrections.
- Final prerequisite/API correction gate: 26 passed, 0 failed, 0 skipped.
- Paper handler/experiment-artifact gate: 14 passed, 0 failed, 0 skipped.
- YAML/artifact round-trip regression tests: 36 passed, 0 failed, 0 skipped.
- Full suite: 1,149 passed, 0 failed, 0 skipped.
- `git diff --check`: passed.

Focused regression coverage includes:

- exact-identity catalog classification and archived identity stability;
- global family/version conflict detection across research and archive bootstrap;
- canonical YAML round-trip for every executable research strategy;
- experiment artifact/grant separation and persisted visibility after reopen;
- artifact-first saga failure/retry and distinct duplicate-operation rejection;
- revocation, suspension/resume, supersession, and duplicate-grant rejection;
- experiment-prerequisite revocation invalidating dependent shadow/live new entries;
- validated-grant creation rejected after its experiment prerequisite is revoked;
- prior-admission replay versus post-revocation new-entry rejection;
- final order-boundary authorization and unblocked protective-stop submission;
- SQLite migration rollback after an injected failure and clean retry;
- spoofed `configs/strategies` directory rejection;
- concurrent same-name run publication with distinct complete snapshots;
- missing paper mode rejection and no fallback to research strategy files;
- authorized identity preservation after live session-policy transformation;
- isolated UI-test roots that reject operational data-root overrides;
- stable mobile request errors for missing strategy/config fields.
- stable mobile HTTP `400`/`409` codes for publication and unavailable-strategy failures.

## Browser Verification

- Signed in through the real local Identity flow using the isolated UI-test account.
- Opened `/`, `/TradeDesk`, `/RunningTrades`, `/Orders`, `/Earnings`, `/News`,
  `/Wishlists`, `/Backtests`, and `/Paper`; every route rendered successfully.
- Confirmed the initial paper-shadow catalog is empty and fails closed.
- Registered the first immutable ATR-compression paper experiment through the Paper
  UI. The artifact was published below `.tmp/ui-tests`, its SQLite grant committed,
  the exact strategy became selectable, and the paper start action enabled.
- Enabled the bounded parameter editor, changed the minimum volume ratio, assigned a
  new semantic version, and registered the derived artifact through the real Paper
  POST handler. The browser redirected to the new content-addressed artifact; the
  unchanged source and both derived versions remained separately selectable.
- Corrected native numeric-step validation and optional GET-binding contracts found
  during browser execution. The final Paper GET had no validation errors and no
  invalid controls.
- The browser run used no data/cache/backup overrides. UI-test composition wrote only
  below `.tmp/ui-tests`; no operational authorization database was created or changed.

## Independent Review

- Plan/requirements reviewer: approved the corrected actual diff.
- Code/design reviewer: approved the corrected actual diff.
- All reported critical, high, medium, and low findings were resolved before commit.
