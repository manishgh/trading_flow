# Reuse saved candle history across requested date ranges

## Approval and purpose

On 26 October 2026 the user approved fixing the common history reader so runs reuse existing five-minute and daily candles, preserving core data, avoiding custom scripts and unnecessary downloads, then running a three-month backtest with independent quant review. This implements the previously explained correction: select requested dates from compatible existing history instead of treating every date range as another download.

The existing implementation indexes prepared sources by an exact request hash, including start and end. This prevents a June–August request from reusing April–October data. Older provider responses and committed price files remain preserved; they are not deleted or physically consolidated by this change.

## Smallest shared change

1. The common cached provider first tries an exact saved source, then a verified containing source with matching symbol, timeframe, provider, feed, adjustment, revision and parser contracts. Selection is deterministic. Explicit refresh still collects a new immutable version.
2. A contained selection references the original source manifest and price file. Its admitted date range and selected row count are recorded separately from the original provider request identity. Reading a slice does not create another OHLCV file or rewrite receipt times.
3. Prepared backtests keep their exact admitted boundaries and immutable source binding. Requests outside that admission still fail; recovery cannot replace a corrupt or missing pinned source with a download.
4. Ordinary cached candle and calendar reads use the same reuse helpers. Day-plan normalization uses an authoritative cached preceding exchange session when available instead of unnecessarily fetching another year of calendar dates.
5. Existing API/Web/Android cache selections and preparation evidence remain the interface. No additional operator setting, service, custom run configuration or research driver is introduced.

Conceptual flow: requested stock/timeframe/dates → compatible verified saved history → exact date slice → existing frozen comparison → existing portfolio runner → monthly results.

Primary affected files are the existing `CachedMarketDataProvider` partials, `PreparedMarketDataVersion`, prepared source verification/readers as needed, and `WorkflowRunConfiguration`. Keep helpers small and purpose-commented. Independently review both engineering and quantitative implications.

## Data protection and limits

- Preserve original responses, source manifests, candle files, wishlists, credentials and every prior result. No archive migration or destructive consolidation is authorized.
- A source with complete HTTP pagination may still omit a provider-generated bar. Range reuse does not invent that bar or change the strategy, signal eligibility, fill assumptions or missing-execution handling.
- Closed historical queries can reuse verified history without a timer alone forcing a replacement download. This does not assert that the saved adjusted prices are the latest revision or establish original historical receipt times.
- Unregistered old files without sufficient source evidence are not silently mixed with a different feed or price basis. No run-specific path is hardcoded.

## Requested run

Use the normal saved-plan workflow, previous-day breakout v1 unchanged, June 1 through September 1, 2026 New York (end exclusive), the Volatile wishlist excluding SOXL, original groups of up to five and separate $10,000 capital. Keep the existing 1% account risk, 25% position notional ceiling and four simultaneous positions. The user confirmed SIVE stays in this run; its wishlist entry remains unchanged. Report June, July and August separately for each cohort. Explicitly report unavailable evidence rather than replacing it with zero returns. Trading remains paused.

## Completion checklist

- [x] Record user approval, existing-reader limitation and bounded design.
- [x] Independent quant plan review: source basis, exact slice, preceding session and immutable parent checks.
- [x] Implement common containing-range reuse for five-minute/daily candles and calendars.
- [x] Integrate ordinary cached reads and day-plan normalization.
- [x] Independent engineering and quant source review.
- [x] Build and pass focused range-boundary, provenance, corruption, retention, row-budget, refresh and zero-provider-call tests, plus affected workflow/intraday checks.
- [x] Run the normal three-month cohort plan and verify reused/downloaded counts and original source hashes.
- [x] Independently audit fills, monthly accounting and missing-source limitations; publish results.
- [x] Remove only exact run-owned generated copies after independent quant approval; recheck core and result hashes.

Build/test jobs run sequentially. Initial code/verification work is expected to dominate runtime; the prior five-book cached backtest took approximately three minutes. No runtime estimate is a guarantee of complete market coverage or profitable results.


Runtime verification also caught plan preview using profile-relative data roots. The existing writer's runtime-root mapping is now shared with preview; the corrected preview made zero external provider calls. The initial calendar-only response remains preserved. See outputs/common-history-reuse-20261026/final-workflow-check.json and verification/runtime-root-reuse.trx (41 passed). The main history/intraday suite passed 141 tests; these suites overlap.


Completed plan 21: five jobs, three available monthly rows (Group 4), twelve unavailable rows under unchanged missing-evidence handling. Independent audit reconciled 442 fills, 215 trades, 836 admissions and 930 hypotheses with zero failures. Reviewed cleanup removed 1,561 generated files (257,598,343 bytes). Post-cleanup SHA checks passed for all 3,128 baseline core files and 159 published artifacts across fifteen old/new books; all 134 prepared versions and the pinned snapshot remain intact. Full monthly results and exact gap times: outputs/common-history-reuse-20261026/monthly-results.md. No trading activation or strategy changes.

