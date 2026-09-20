# Swing Research Implementation Status

**Decision:** `RETAIN_RESEARCH`

**Trading horizon:** swing only

## Verified Foundation

- Immutable raw-before-parse market/news evidence and cataloged dataset handles.
- Point-in-time security identity, listing, universe, exchange-session, and corporate
  action contracts.
- Completed-bar decisions, later-bar confirmation, next executable-bar fills, and
  pre-fill initial-stop construction.
- Deterministic chronological backtest coordination and execution simulation.
- Spread, slippage, fees, quote side, participation, partial/non-fill, and impact
  evidence in the execution journal.
- Development, validation, and one-use chronological holdout lifecycle controls.
- Exact strategy identity and promotion authorization firewall.
- Shared decision, risk, and execution path for backtest and paper modes.
- Daily-primary swing admission enforced before market observation or broker mutation.

## Current Catalog

`configs/strategy-catalog.json` contains two research artifacts and thirteen archived
swing artifacts. None is authorized for a new paper-shadow or live run. Archived
artifacts remain parseable evidence but are hidden from normal operation.

## Removed Scope

All same-session strategy families, research analyzers, profiles, YAML artifacts,
Web/API/mobile exposure, and historical day-trading plans have been removed.
Sub-daily market plumbing remains only for declared swing confirmation and execution.

## Evidence Still Required

- A promotion-grade, point-in-time universe with complete membership and delisting
  history for the selected research period.
- Frozen cross-sectional momentum baseline results across sufficient independent
  formation periods.
- One-use holdout results with benchmark, concentration, and parameter-sensitivity
  reports.
- Evidence that any VCP, catalyst, or sub-daily confirmation overlay improves the
  baseline net of costs.
- A completed paper-shadow window with no strategy mutation.

Until those items are present, `RETAIN_RESEARCH` is the only valid integrated
decision.

## Verification Boundary

Engineering verification demonstrates deterministic and safe behavior; it does not
prove alpha. Strategy promotion remains blocked even when build, unit, integration,
API, and UI tests are green.
