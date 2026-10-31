# Company-assessment streaming and verification reuse — 25 October 2026

Approved scope: [design 09](../design-review/2026-10-19/09-company-assessment-verification-reuse-2026-10-24.md). Implementation uses the existing assessment, source verifier, immutable publisher, saved-plan workflow and runner. No strategy, public contract, configuration setting or historical-availability assumption changed.

## Completed implementation

- [x] Stream packet publication, hashing, comparisons and reports while preserving serialized content.
- [x] Retain one factory-created verified provider and exact source-file leases per preparation or worker attempt. Verify afresh for a new attempt.
- [x] Reject missing original dependencies, mismatched identities, fabricated guards and disposed scopes.
- [x] Independent engineering and quant source reviews completed. Execution acceptance remains conditional below.

Review records and test receipts are in `outputs/company-assessment-reuse-20261025`. Final engineering review accepted the source and focused regressions; final quant review confirmed native-order runner parity and cancellation protection. Both retain the full resource gate before any actual retry.

## Verification and limits

Component-level verification, not a release or strategy-promotion checkpoint:

- Affected C# build passed with zero warnings and errors.
- Final focused suite: **249 passed, zero failed**, with two opt-in memory profiles skipped in that invocation (`tests/focused-final.trx`). Earlier receipts remain for audit.
- Corrected small fixture and frozen numeric-shape checks: two passed (`tests/memory-fixture-smoke.trx`). An earlier synthetic model-family mistake was corrected in the fixture; production provenance checks were retained.
- Large-body/high-fanout profile passed: 403 issuer rows, two versions, 326,195,473-byte packet; sampled peak working set 1,344,933,888 bytes, sampled peak private memory 1,297,408,000 bytes, retained reader heap 528,051,968 bytes. It exercised publication, reports, complete read verification, twenty sequential scope consumers, and a fresh worker scope (`tests/memory-stress-corrected.trx`).
- Diagnostic cancellation test passed: original files remain write-locked while the cancelled child drains; the scope rejects use and releases its locks after the worker exits.
- Runner parity initially asserted the wrong order projection. Completed trades were present; `AcceptedOrders` was empty. This runner records native orders and executions in `PortfolioState.AdmittedOrders` and `PortfolioState.Fills`. The corrected public-runner test passed: both paths have completed trades, native admitted orders/fills and rejections; complete portfolio state, trades, diagnostics, provenance and candidate transitions match. This proves runner plumbing parity, not news profitability.

The full-shaped profile preserves 2,951 article versions, 18,016 issuer assessments, original joint field sizes and issuer duplication, including 3,263 rows for the original selected 24 symbols. Synthetic wording, cue distribution and issuer-name lengths are approximations. This is a resource test, not twenty economic portfolios or twenty concurrent workers.

**Full memory verification is incomplete.** The test was deliberately stopped at 16:12:26 UTC, before packet publication, because C: had only 1,860,009,984 bytes free. The packet was projected near that capacity before remaining structure and reports. No out-of-memory result was observed in this attempt. The TRX reports an aborted host because it was deliberately stopped; `full-memory-stopped-disk-capacity.json` records the actual reason. The sampled observations do not establish a full-run peak or reader capacity.

Only the stopped test's exact GUID temporary directory was removed: 128 synthetic files, 27,913,393 bytes. Its receipt, failure information and measurements remain (`synthetic-cleanup.json`, `host-memory-observations.jsonl`). Core data and published results were outside that directory.

## Next execution boundary

- [x] Corrected complete runner parity passes.
- [ ] Adequate disk space is available; repeat the isolated full-shaped profile and assess memory with FinBERT/worker headroom.
- [ ] Independent engineering and quant reviewers accept the complete measurements.
- [ ] Consume the one approved actual assessment retry, verify its full population and frozen provenance, then review the ordinary saved plan.
- [ ] Start the existing twenty portfolios and publish monthly results separately by strategy and cohort.

No actual assessment retry has been consumed and no new economic jobs have been started. The user has been asked to free space on C: or provide another storage path. Do not replace this with deletion of core market/news data or old results, shrink the assessment population, or claim the smaller profile proves the full comparison fits.

## Reviewed cleanup and restored Web service

The existing reviewed-cleanup component completed all 15 previously approved jobs. Independent post-cleanup verification confirmed all 52,449 approved temporary input files were removed (115,929,208 logical bytes), with all 15 published result hashes and 15 monthly artifact hashes unchanged. Exact source/review/ownership pins also matched. No core market history, news, wishlists or published results were deleted. Evidence: `approved-cleanup-execution.json`, `postcleanup-verification.json` and `postcleanup-finding.txt`.

The Web service was restarted with trading paused. At 16:37:44 UTC, authenticated API and public ngrok login both returned HTTP 200; the API still listed the same 45 completed jobs. Evidence: `web-restart.json` and `web-verification.json`.

After cleanup, C: had 2,081,349,632 bytes free (about 1.94 GiB), still insufficient headroom for the full test and retained publication. The pending request is for additional disk space, preferably at least 10 GiB, or another storage path. The full memory gate, actual assessment retry and twenty new backtests remain pending.