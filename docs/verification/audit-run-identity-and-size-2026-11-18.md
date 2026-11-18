# Audit page: completed backtests open again — 18 November 2026

Approved by the user on 18 November 2026 ("Approve it"). Display only: no data, database, strategy or order change.
Follows the correction recorded in `docs/design-review/2026-11-18/stored-data-paths-after-move.md`.

## Observed problem

The Audit page showed no curve or workflow evidence for any of the operator's 8 completed backtests ("The published
result could not be verified or exceeds the comparison limits"):

1. **Name check.** The page selects a job by its database run name (`etf-trend-dev-2017-2023_9903699c70d122d66676`)
   and required the published manifest and `result.json` to carry that same name. They carry the `run_name` written
   in the job's own generated settings file
   (`etf-trend-dev-2017-2023-9903699c70d122d66676-1e5b2ab38ce04bd9a1e5279ffa4bdc34-20261113163800492-ba46…`), which is
   also that file's name. All 8 differed.
2. **Size cap.** The page refused results over 10,000,000 bytes. Three are 12,965,167, 13,062,090 and 17,440,724
   bytes, because each stores the same ~5.7 MB portfolio record three times (`UnifiedPortfolio`,
   `StrategyResults[0].PortfolioState`, `PortfolioState`). Published results are not rewritten.

## Change (`AuditEquityReader.cs`; `AuditWorkflowReader.cs` follows the signature)

- The database name still selects the one job and labels its curves. The publication (manifest and result) must
  carry the `run_name` of that job's own settings file, which is still confined to the canonical or generated
  settings folders. A result must therefore belong to the exact settings that produced it.
- One limit, `MaxPublishedResultBytes = 64,000,000`, applies to the manifest's declared length and to the actual
  read; the SHA-256 check is unchanged.
- Added on review: a single shared gate lets one Audit request at a time hold a published result in memory (a page
  load reads up to five: workflow evidence plus four compared runs).

## Verification

- `dotnet test … --filter Audit|StoredDataPathTests|BacktestJobServiceTests`: **91/91**. New: a settings name that
  differs from the database name opens; a manifest or a result naming another run is refused; a 12 MB result opens;
  over 64 MB is refused at both checks; the moved-folder case uses the settings file's run name.
- Browser specs that open the Audit page (`audit-paging`, `responsive-baseline`, `trading-capabilities`): **51/51**
  (run before the review follow-ups, which are server-only and covered above).
- Reviewer A (plan/execution) and reviewer B (code/design) accepted. Applied from their notes: the shared read gate,
  removal of an unreachable blank-name check (the settings reader already refuses a missing `run_name`), and a
  swapped-name test. Not added: a unit test through the page's own `ReadAsync` (it needs a full backtest job); the
  live check below exercises that path on the real jobs.
