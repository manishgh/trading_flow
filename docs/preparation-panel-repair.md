# Preparation panel repair — 28 September 2026

Repaired the embedded preparation flow and verified it in the existing signed-in browser.

The account had no saved preparation definitions. The old panel exposed empty controls, restored drafts poorly, and did not refresh queued collection results automatically. Its lazy JavaScript URL also lacked a content hash, so the existing browser continued loading stale controls after deployment.

Changes:

- Versioned lazy script loading with a retry path after loading failure.
- Explicit first-use setup, meaningful disabled controls, paused-save guidance and named discovery sources.
- Duplicate-source prevention, per-account draft storage, clean-versus-edited draft restoration and recovery of missing saved workflows.
- Automatic history refresh every ten seconds while the panel is visible. Edited reviews and uncertain continuation keys remain pinned across newer results and retries.
- Server-projected current collection windows using the same authoritative calendar check as manual collection. Admission rechecks time after calendar access.
- Clear queued, closed-window, missing-history and unavailable-calendar explanations.

Operational configuration saved through the normal UI:

- Name: **Desk preparation**.
- State: **paused**; automatic collection was not enabled.
- Sources: union of `volatile`, `Macro Trade Suggestions`, and `long`.
- Default configurable schedule: 45 minutes after exchange close; morning starts 90 minutes before open and refreshes every 15 minutes until 15 minutes before open.
- Verified next slots: September 28 20:45 UTC / 22:45 Berlin nightly; September 29 12:00 UTC / 14:00 Berlin first morning.
- Current manual collection is outside its configured window. No collection was forced, no time was fabricated, and no orders were placed.

Validation: 46 preparation backend tests and 31 preparation/Desk browser tests passed. Includes real paused-save/reopen API coverage, calendar boundaries, failed loading/retry, automatic completion, edited-review preservation and delayed/lost-response handoff identity. Affected projects compiled; JavaScript syntax and git whitespace checks passed. Independent requirements and code/design reviews passed.

This is a component checkpoint, not release certification. Mobile, ML, broker execution and the separate natural-session acceptance result were not newly certified. The isolated natural-session worker remains unchanged. Existing secrets, databases and candles were preserved; the paused workflow was saved by the application's ordinary API, not by direct database editing. No GitHub synchronization.
