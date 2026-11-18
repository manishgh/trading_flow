# Reading results saved before the data folder moved

Status: approved by the user on 18 November 2026 ("approved your last message"); implemented, independently reviewed
and accepted. Implementation record at the end of this document.

## What was observed

The operator data folder moved from the repository's default `C:\project\trading_flow\data` to
`V:\trading_flow\data` (the `TradingFlow:DataRoot` setting). Records written before the move still name the old folder:

- In `tradingflow.db`, the `Jobs` table stores absolute paths. The 8 completed backtests (Connors 9.2.0 checks, ETF
  trend, sector comparisons, rotation; 12–13 November) have `ResultReference` under `C:\project\trading_flow\data`, and
  14 jobs have such a `ConfigPath`.
- All 305 generated backtest settings files in `V:\trading_flow\data\runtime\configs\backtest` embed
  `C:/project/trading_flow/data/...` for `results_root`, `raw_root`, `normalized_root` and the evidence catalog. These
  files are each run's frozen parameters and must not be rewritten.
- The result files themselves are intact on `V:`, and each already has its publication marker written on 12–13
  November. Nothing was lost; it is only looked for in the wrong place. `C:\project\trading_flow\data` now holds only an
  older database copy and a `research` folder.

Two effects, both measured on 18 November:

1. **Log flood.** Every second the site rechecks each completed backtest's publication marker. The old path does
   not exist, so each check logs a warning with a full stack trace ("result marker is not available yet ... the next
   refresh will retry"). After the restart at 13:40 UTC each of the 8 jobs logged 210 such warnings in about
   3.5 minutes. The previous `site.log` reached 3.8 GB in five days, two-thirds of it this warning. The wording is also
   wrong: a completed run's missing file never appears by retrying.
2. **Results not viewable.** The Audit page for these runs (checked: `connors-9-2-0-check-202511_450c5160395e7f24eb4e`
   and `etf-trend-dev-2017-2023_9903699c70d122d66676`) says "Published workflow evidence could not be verified",
   because it reads the settings file and result from `C:` and then rejects paths outside the current data folder.

## Proposed change

**Part 1: read a pre-move path from the current data folder.** Add one method to the existing `ProjectPaths`:
a stored path that lies inside the repository's default data folder (`<repository>\data`) is read from the same
relative location under the configured data folder, when the two differ. Every other path is returned unchanged. It
is a plain path mapping: no file probing and no guessing between locations. The existing confinement and hash checks
still run on the mapped path. Apply it only where completed, published results are read:

- the publication-marker check in the backtest, optimization and paper job services;
- the research-ledger verification of a backtest result;
- `AuditEquityReader` and `AuditWorkflowReader` (settings path, its `results_root`, and the result path).

Unchanged: database values, settings files, published results and their hashes. New runs already write under `V:`.
Not covered: re-running an old settings file would still read market data from its frozen `C:` roots; that is a
separate decision if a replay is ever wanted.

**Part 2: warn once, not every second.** The three services each carry an identical marker method. Replace them with
one shared helper that remembers, per job, the last failure. It logs one warning when a failure first appears or its
reason changes, naming the job, the exact path and that retrying cannot recreate the file, and one information line
if a later check succeeds. The cheap existence check continues each refresh, so a repaired path is picked up without a
restart. Memory is one short entry per failing job. This also protects against a future cause such as the `V:` drive
not being mounted, which would otherwise make every completed job write a warning every second.

## Example

Stored: `C:\project\trading_flow\data\backtest\results\.durable-attempts\backtest\6bd8...\3dec...\result.json`
Read from: `V:\trading_flow\data\backtest\results\.durable-attempts\backtest\6bd8...\3dec...\result.json`
A path such as `C:\project\trading_flow\data2\x` or `D:\other\x` is not mapped.

## Files, checks and scope

Files: `ProjectPaths.cs`, `BacktestJobService.cs`, `OptimizationJobService.cs`, `PaperJobService.cs`,
`AuditEquityReader.cs`, `AuditWorkflowReader.cs`, one small shared marker helper, and focused tests. No database
schema change, no data rewrite or deletion, no strategy, order or research behaviour change.

Tests: mapping cases (inside, outside, look-alike prefix, already under the data folder, data folder equal to the
default); the helper logs once, again on a changed reason, and once on recovery; audit readers open a result whose
settings and result paths name the former folder; refresh of a relocated completed job writes its marker check without
warnings. Then build, separate A (plan/execution) and B (code/design) review, Release install to the local site after
an account and queued-work check, and confirm on the running site that the warning stops and the Audit page shows
these 8 runs.

Not part of this change: the older database copy at `C:\project\trading_flow\data\tradingflow.db` (512 MB plus a
150 MB write-ahead file, last written 14 November 00:42). It is core data; removing it needs its own explicit approval.

## Implementation record, 18 November 2026

- `ProjectPaths.LocateStored` maps a fully qualified path inside `<repository>\data` to the same relative path under
  `DataRoot`. It returns the path unchanged when it is relative, already under `DataRoot` (so a data folder nested in
  `<repository>\data` is never mapped twice), outside the default folder, or when both folders are the same.
  `ProjectPaths.RelativeInside` is the single "is this path inside that folder" rule, now also used by
  `AuditEquityReader.Confine` and `ConfineConfig` (it also rejects `../` written with `/`, which is stricter).
- `DurableResultMarkers` replaces the three identical marker methods in the backtest, optimization and paper job
  services. The failure reason is the exception type, its error code and the located path, never the message (a failed
  marker write names a new random temporary file each attempt). An add-or-swap loop makes exactly one of two concurrent
  failures log. `DurableJobResultPublication.PublishIfCompletedAsync` takes the located reference explicitly; the stored
  job row is unchanged. Research-ledger verification reads the located path.
- `AuditEquityReader.ReadStoredResultAsync` is shared by both Audit readers (settings path, its `results_root`, result).
- Not changed: the Backtests page badge (`BacktestResultAvailability.Observe`) still checks only the stored location,
  so the eight pre-move runs still show "Result unavailable" there. The design did not list it; both reviewers advised
  raising it with the user as a separate decision.

**Tests.** New `StoredDataPathTests` (mapping inside/outside/look-alike/already current/nested/trailing separator/letter
case/unmoved; warning once, again on a changed reason, once on recovery; a failed marker write with a new temporary file
each attempt logs once; non-completed jobs skipped), a former-folder Audit read in `AuditEquityReaderTests`, and a real
`BacktestJobService` refresh with a former-folder result in `BacktestJobServiceTests`. Debug build of the test project:
0 warnings, 0 errors. Focused and direct-consumer tests (stored paths, audit readers, the three job services, durable
job processing, data-root resolver, result availability, production composition, Audit page, chart endpoints, research
screens): **160/160 passed** after all review fixes.

**Independent review.** Reviewer A (plan/execution) first asked for a reason key that ignores the temporary-file name
and a test through a real job service; reviewer B (code/design) found the same reason-key defect, a double mapping when
the data folder sits inside `<repository>\data`, a possible double warning under concurrency, a misleading recovery
message, the duplicated folder check and two missing purpose comments. All were fixed except two accepted points:
entries for jobs deleted while failing stay in memory until restart (one short entry per job), and no test asserts DI
injection separately (the service test exercises the mapping inside a real job service). Both re-reviewed the code
without re-running tests and accepted.
