# Code, design and maths review of `ui/redesign-v2` — 21 November 2026

Reviewer: Claude (Claude Code), at the user's request. Branch `ui/redesign-v2` at `31ddb98`,
compared with `main` at `d4b4688` (37 files, +5,186 / −217 lines). Companion to
[`claude-ui-review-handoff.md`](claude-ui-review-handoff.md), which covers screen layout.

How this was checked: every finding below was read in the branch source, and the
number-related ones were compared with the running preview on `http://127.0.0.1:10089`.
Nothing was built, tested or changed; no strategy, catalog, engine or broker code was
touched. Strategies are out of scope at the user's request; findings in shared engine
maths are reported as **existing on main** and are not requests to change strategies now.

**Overall verdict: not gold.** The branch is careful about look-ahead (completed bars only,
known-at times, exchange calendars) and about never pooling monthly returns, and its
front-end request handling is mostly safe. But it adds a second and third way of loading
market data outside the shared candle pipeline, copies business rules (regime thresholds,
gate wording, risk maths, ratio bands) into endpoints and JavaScript, and ships one ratio
whose maths is wrong. Severity: **High** = shows wrong numbers or breaks a project rule;
**Medium** = will cause drift, load or maintenance cost; **Low** = tidy-up.

---

## A. Maths

| # | Sev | Finding | Evidence | Why it matters | Fix |
|---|---|---|---|---|---|
| M1 | High | **Implied move ÷ ADR ratio ignores time to expiry.** The Catalysts card divides the whole straddle move to the front expiry (for example ±3.9% over 5 trading days) by a one-day average range. | `CatalystOptionsAdrService.ComputeImpliedMoveToAdrRatio` (branch) = `move / adr`. `main` already has `OptionsSentimentCalculator.ComputeImpliedToRealizedAdrRatio`, which divides the move by √(trading days) first. | A 5-day move is about √5 ≈ 2.2× a 1-day move, so almost every symbol lands in the "▲ >2.0× extreme" band by construction (SPY: 3.9 ÷ ~1.3 ≈ 3.0). The test at `Ui2RedesignTests.cs:422` asserts the unscaled value, so it protects the error. | Delete the branch copy; reuse the main calculator's scaled ratio (send `TradingDaysToFrontExpiry` already in the card). Re-derive the 1.2 / 2.0 bands from data, and keep them on the server. |
| M2 | High | **"Day %" on a closed market is not the day's change.** MU shows "Prev close $594.51 · +2.71%" on Saturday 21 Nov, while the chart shows the 20 Nov daily close as $594.51. | `formatRowDayChange` in `watch-desk.js` = `mid / previousClose − 1`; mid was the Friday after-hours quote. | The column labelled "Day %" shows the after-hours move against Friday's own close, not Friday's change against Thursday. | Outside regular hours, show "After hours +2.71%" against the latest close, and show the completed session's change separately. |
| M3 | Medium | **"Volatility" pills are price ranges, not volatility.** `Vol 1W 29.4%` = (highest high − lowest low over 5 sessions) ÷ latest close. | `DailyObservationService.ComputeVolatility` (branch). | Readers will take "volatility" as the standard deviation of returns. A trending stock gets a large "volatility" with calm daily moves. | Rename to "Range 1D / 1W / 1M", or compute annualised close-to-close volatility if volatility is wanted. |
| M4 | Medium | **ADR% divides by the latest close, not each day's own price.** `ADR% = mean(High − Low over 20 sessions) ÷ latest close`. | `SignalGenerator.ComputeAverageRangeAndDollarVolume` (**existing on main**), reused by the branch for the Catalysts card. | After a fall (MU ≈ $900 → $594 in November) ranges measured at higher prices are divided by a lower close, so ADR% is overstated; after a rally it is understated. The common definition is `mean(High ÷ Low − 1)` per day. This also feeds strategy admission (`min_average_range_pct`), so changing it changes strategy results. | Report only for now (strategies are out of scope). Record it as a known measurement choice; decide separately whether a new strategy version should switch to the per-day form. |
| M5 | Medium | **Straddle "expected move" includes intrinsic value when no strike sits at the price.** | `OptionsSentimentCalculator.FindStraddleInWindow` (**existing on main**): `straddleMid / spot`, nearest strike, no adjustment. | Small on MU ($5 strikes on $610). Large on cheap stocks: with $1 strikes on a $6.27 stock, up to $0.50 of intrinsic value = up to 8 percentage points of "expected move" that is not expected movement. | Subtract intrinsic value `|spot − strike|` from the in-the-money leg, or interpolate between the two strikes around the price. |
| M6 | Medium | **Put-vs-call "fear" is shown as an absolute number with fixed thresholds.** SPY normally carries a positive 25-delta skew of several volatility points, so "put hedging" is close to permanent for indexes. MU showed +40.7 points, which is unusually high and worth a check against the raw quotes. | `PutSkewHedgingThreshold = 0.06`, `CallSkewSqueezeThreshold = −0.04` in `OptionsSentimentCalculator` (main); wings accepted anywhere in delta 0.13–0.37, spread up to 35% of mid. | A fixed threshold mixes "normal for this symbol" with "unusual today". Wide accepted delta and spread ranges let mismatched or stale wings set the number. | Compare each symbol's skew with its own recent history (percentile), tighten the wing delta band, and show the chosen strikes and deltas in the tooltip so odd values can be checked. |
| M7 | Low | **Trading days to expiry count weekdays only.** | `CountWeekdaysExclusiveInclusive` (main). | Thanksgiving (26 Nov 2026) week counts one day too many; the scaled ratio in M1 shifts slightly. | Use the exchange calendar already loaded elsewhere. |
| M8 | Medium | **Risk strip repeats server sizing maths in JavaScript.** Stop distance, dollar risk and reward-to-risk are recomputed from the form fields. | `refreshRiskStrip` in `order-workflow.js` (branch). | The server plan (`DeskTicketPlanService`) is the authority; the browser version ignores slippage, fees and gap risk and treats every entry as a limit at the typed price. Two formulas will drift. Short tickets show "—" silently. | Have `/api/v1/orders/plan` (or the preview call) return these four figures and display them as sent. |
| M9 | Low | **"Support/resistance" for moving averages only means above/below.** | `renderSmaPill`: `price ≥ SMA ? 'support' : 'resistance'`. | The wording claims more than the calculation shows; combined with red styling it reads as a warning. | Label "SMA20 above / below" or show the distance in % and ATR. |
| M10 | Low | **Chart "Entry" line is the ask price at the moment the stock was clicked.** | `syncChartTradePlan` sends `limitPrice: askPrice`; its cache key excludes the price, so the line is not refreshed as the quote moves. | For breakout strategies the entry is a trigger level, not the current ask; and the line goes stale. | Return the strategy's own entry trigger from the server; refresh when the quote crosses a set change. |

Checked and fine: completed-bar checks (`bar end ≤ now` and `KnownAtUtc ≤ now`) in the
regime header and ADR; exact 20-session calendar matching in `ComputeAdr20Pct`; the
monthly matrix copies stored `MonthlyRunResult` values and never adds months or
strategies together; `RMultiple` comes from the shared `BacktestDerivedMetrics`.

## B. Candle and news data paths

| # | Sev | Finding | Evidence | Why it matters | Fix |
|---|---|---|---|---|---|
| P1 | High | **Two new market-data paths bypass the candle pipeline.** The regime header and the Catalysts ADR cards call `runtimeFactory.CreateProvider(...)` and read bars straight from Alpaca REST. | `WorkspacePulseEndpoints.BuildPulseAsync` (SPY/QQQ, 170 days of `1d`, falling back to `1h`), `CatalystOptionsAdrService.TryLoadDailyBarsAndSchedulesAsync` (65 days of `1d`). The existing Desk path, `StockStrategyEvidenceLoader`, uses `CandlePipelineEngine` with `ICandleStore`. | Breaks "one brain": the header can disagree with Desk checks for the same moment, the bars are not stored in `data/candles`, the warm stream state is ignored, and each path has its own calendar window (15 vs 65 days) and completion rule. | Put one benchmark-bar reader behind `ICandleStore` / the candle pipeline (or reuse `StockStrategyEvidenceLoader`'s benchmark load) and have both features call it. |
| P2 | High | **Every cache miss writes provider receipts to disk from a page header.** `PaperRuntimeFactory` builds providers with `FileSystemRawArchiveWriter` under `DataRoot/raw`; `AlpacaMarketDataProvider` archives each REST response. | The header is on every signed-in page and polls every 60 s (`regime-pulse.js`, `REFRESH_MS = 60000`); server cache 60 s. | Roughly one calendar + two bar requests per minute while any page is open, all archived. Raw provider receipts are core data that may not be deleted without dataset-specific approval, so this growth is permanent. | Read from the candle store instead (P1). If a live fetch is still needed, refresh daily-bar facts once per session close, not per minute. |
| P3 | Medium | **Regime header uses hard-coded rules, not each strategy's rules.** SPY 50-day / 20-bar rising check, VIX 18 / 22 / 0.5× are constants in the endpoint; QQQ reuses SPY's rule. | `CanonicalLongSpyRule`, `CanonicalShortSpyRule`, and a second copy of the same numbers inside `EvaluateBenchmark` (`new RegimeRules(symbol, …, 50, SmaRisingLookbackBars: 20)`). | AGENTS.md requires config-driven behaviour. Today all ten swing configs in `configs/backtest/strategies/` happen to use these same numbers, so the header is currently right; it goes wrong the first time one config changes. It also shows QQQ as a gate for every stock, while the configs apply QQQ only to Nasdaq-listed stocks (`nasdaq_listing_benchmark: QQQ`), and it shows a gate to strategies with no regime block (opening-range breakout, ETF strategies). | Derive the header from the selected strategy's `Regime` block (or show "for strategy X"), with numbers coming from the server response. |
| P4 | Medium | **Top sectors read from a CSV by guessing.** Newest folder chosen by name sort; last 4 KB of `eligible.csv`; split on `,`; first column assumed to be date and second the ticker; all errors swallowed. | `TryReadLatestTopSectors` / `ReadTailLines`. | A renamed folder, quoted field, column reorder or longer final day silently shows wrong or no sectors. A reader with manifest hash checking already exists: `PublishedResearchScreen.Read` in `TradingFlow.Engine/Universe`. | Use the published research-screen manifest/reader; pick by publication time; show the as-of date and mark it stale past one session. |
| P5 | Low | **News pipeline not changed.** The branch only restyles News/Earnings. The preview correctly showed "News refresh has not run on this host" because feeds are disabled on port 10089. | `News.cshtml`, `earnings.js` diffs. | No action on the branch; verify feeds on the operator site separately as AGENTS.md "Data Policy" requires. | — |

## C. Duplication and reuse

| # | Sev | Duplicate | Where | Fix |
|---|---|---|---|---|
| R1 | Medium | Plan-ID extraction from `RequestJson` written twice, with two case-insensitive property helpers, then the plan ID filtered a third time. | `SqliteJobRepository.TryExtractPlanId` + `TryGetPropertyCaseInsensitive`; `BacktestJobService.TryReadPlanId` + `TryGetJsonProperty`; `SavedPlanMonthlyMatrixBuilder.Build` (`job.Cohort?.PlanId == plan.PlanId`). | Store `PlanId` as an indexed column (migration) or filter with SQLite `json_extract` in the query; keep one parser. |
| R2 | Medium | Calendar loader written twice (15-day vs 65-day windows) and `IsCompletedBar` written twice. | `WorkspacePulseEndpoints.TryLoadSchedulesAsync`, `CatalystOptionsAdrService.TryLoadAdrSchedulesAsync`; `IsCompletedBar` in both files. | One shared helper in the engine's market/regime area, used with P1. |
| R3 | Medium | Same exception filter `catch (Exception ex) when (ex is not OperationCanceledException || …)` six times, none logged. | Branch server code (6 occurrences; 0 logger calls in all added server code). | One helper that logs a structured warning with symbol, source and reason, then returns the unavailable result. |
| R4 | Medium | Gate wording with numbers copied into JavaScript: `'VIX above long-entry limit (>22)'`, `'Benchmark close below 50-day SMA'`, `'Market regime gate is OFF (SPY)'`. | `checkLabels` in `watch-desk.js`. | Server returns the plain-English sentence built from the check's actual inputs ("20-day ADR 1.82% — needs ≥ 2.50%", as the plan promised). One source for words and numbers. |
| R5 | Medium | Implied-move/ADR ratio implemented twice (one wrong, M1); ratio bands 1.2 / 2.0 in JavaScript. | `CatalystOptionsAdrService` vs `OptionsSentimentCalculator`; `formatRatioPill` in `catalysts-opra.js`. | Single server calculation and classification; browser only displays. |
| R6 | Low | Options card markup and four tooltip texts copied three times in Razor plus once in JavaScript; options request handling (abort + sequence guard) copied between two scripts. | `_OptionsSentimentPanel.cshtml` (×2), `Desk.cshtml`, `catalysts-opra.js`; `desk-options.js` vs `catalysts-opra.js`. | One partial for the card; one small shared fetch helper. |
| R7 | Low | Two static in-process caches with their own expiry and size rules. | `WorkspacePulseEndpoints.cachedPulse` (+ `SemaphoreSlim`), `CatalystOptionsAdrService.AdrCache`. | Register both as DI services using `IMemoryCache`; testable and resettable. |

## D. Structure and maintainability

| # | Sev | Finding | Fix |
|---|---|---|---|
| S1 | Medium | Business logic in the web layer: `WorkspacePulseEndpoints.cs` (447 lines) evaluates regime gates, VIX sizing, label text and CSV parsing inside an endpoint class; `CatalystOptionsAdrService` is a static class taking six collaborators per call. AGENTS.md: "UI calls APIs/services; services orchestrate; engine computes." | Thin endpoint → injected service → engine calculators. |
| S2 | Medium | Large front-end files grew further: `watch-desk.js` 1,160 lines, `run-workflow.js` 1,139 lines. Matrix rendering, drawer, presets and form validation all live in `run-workflow.js`. | Split into modules per feature (matrix/drawer, presets, scanner, chart levels). |
| S3 | Medium | Stylesheet override chain: `ui2-foundation.css` (536 lines) loads last and wins through higher specificity (47 `.workspace-app` selectors, `html body.workspace-app …`). Global `button { min-height: 40px }`. | Do the promised merge into `tokens.css` / `components.css` / `workspaces.css` and remove the old rules instead of overriding them; scope the 40 px rule. |
| S4 | Low | Checkpoint and version names in code: CSS comments `/* UI-5: … */`, test names `Ui5ContractsCover…`, storage keys `scannerDensityV2`, `tradingflow.stock-chart.v3` (the v3 bump silently discards saved chart settings). The user prefers descriptive names; only the public API is versioned. | Name by purpose (`scannerDensity`, `stockChartPreferences`) and migrate the old saved value instead of discarding it. |
| S5 | Low | Interface default method `IJobRepository.GetJobsForPlanAsync` falls back to loading every job. | Remove the default so each repository must implement the filtered query. |
| S6 | Low | `_Layout.cshtml` joined lines: `};    var systemPages` and `}        @RenderBody()`. | Split. |

## E. Race conditions and load

| # | Sev | Finding | Evidence | Fix |
|---|---|---|---|---|
| C1 | Medium | No single-flight on ADR cache misses: two Catalysts requests for the same symbols at once both call Alpaca (and both archive, P2). | `CatalystOptionsAdrService.LoadAsync` checks then fetches without a lock. | Per-key lazy task in the cache (`IMemoryCache.GetOrCreateAsync` with a shared `Lazy<Task>`). |
| C2 | Low | Header pulse rebuild holds one global lock for up to 6 s; every page's pulse request waits behind it. A cancelled first caller cancels the shared build and the next caller starts again. | `CacheGate.WaitAsync` + `BuildPulseAsync(…, cancellationToken)`. | Build on a background refresh (hosted service, once per session close) and serve the last value. |
| C3 | Low | Cache size check and insert are separate steps, so the 256-entry limit can be passed under concurrency. | `AdrCache.Count < MaxCacheEntries` then `AdrCache[symbol] = …`. | Resolved by `IMemoryCache` with a size limit (R7). |
| C4 | Low | Matrix request reads every backtest job's `RequestJson` and parses it in memory on each request, then loads full snapshots (including all completed trades) for the plan's jobs. | `GetJobsForPlanAsync`, `GetSnapshotsForPlanAsync`. | Indexed `PlanId` (R1); load trade samples on drawer open, not with the matrix. |
| — | OK | Front-end request races are handled: chart trade plan (`tradePlanSeq` + `AbortController`), options panels, regime strip and matrix fetch all drop stale responses. | `watch-desk.js:870–931`, `desk-options.js`, `catalysts-opra.js`, `regime-pulse.js`, `hideSavedPlanMatrix`. | — |

## F. Behaviour gaps found while reviewing

| # | Sev | Finding | Fix |
|---|---|---|---|
| B1 | Medium | The month drawer shows trades only from the 25 newest trades of the whole job, filtered by the clicked month. Older months will show no trades even when they had some. | Request the clicked month's trades from the server (paged). |
| B2 | Medium | Ticket plan, chart levels and risk strip are long-only; half the listed swing strategies are short and get "unavailable". | Design short-side plans (stop above entry, target below) on the server before showing levels for short strategies. |
| B3 | Medium | "May–Oct 2026" window preset hard-coded in `Backtests.cshtml` with fixed timestamps; `GroupSize = 1000` hard-coded in `run-workflow.js`. The plan said the profile would supply these. Fixed one-click windows also make it easy to keep re-testing the same period, which the operating boundaries treat as overfitting risk. | Presets and defaults come from the backtest profile / research boundary config; show which period is reserved as untouched test data. |

## F2. Configuration verbosity (existing on main; strategy files, so report only)

The branch adds no configuration files. The strategy configs it displays are repetitive:
`ema21-trend-pullback.bt-v1.yaml` (81 lines) and `bollinger-squeeze-breakout.bt-v1.yaml`
(79 lines) share 57 identical non-comment lines; the 10-line `regime:` block is copied
into all ten swing configs with only the long/short rule changing. Each copy can drift, and
the header (P3) had to hard-code a "canonical" copy because there is no single named one.
When strategy work resumes, consider named shared blocks (for example
`regime_profile: spy-50d-vix-18-22-long`) resolved before hashing, so the content hash
still covers the fully expanded settings. Not a request to change strategies now.

## G. Tests

| # | Sev | Finding | Fix |
|---|---|---|---|
| T1 | Medium | 6 of the 12 tests in `Ui2RedesignTests.cs` read source files through the `WebFile(...)` helper and check that they contain text (58 literal `Assert.Contains`/`DoesNotContain` calls in the file), for example `Assert.Contains("Results.NotFound()", endpoints)`. They fail on harmless rewording and pass when behaviour is broken. | Replace with endpoint tests (`WebApplicationFactory`) and DOM tests for the scripts. |
| T2 | Medium | The ratio test locks in the M1 error (`7.00 / 3.31`). | Rewrite after M1 with a known 5-day vs 1-day case. |
| T3 | Low | Missing tests: concurrent ADR requests (C1); pulse with strategy-specific regime settings (P3); stale sector CSV (P4); after-hours "Day %" (M2); straddle with off-price strikes (M5). | Add with the fixes. |

## H. Review dimensions to add next time

The user asked which areas might have been missed. These were included above or are worth a
standing check on every UI checkpoint:

1. **Data cost and retention** — provider calls per minute per open page, and whether each call writes permanent receipts (P2).
2. **Single source of truth** — any number, threshold or wording that exists in both C# and JavaScript (R4, R5, M8).
3. **Observability** — every "unavailable" result logs why, with symbol and source (R3).
4. **Market-hours meaning** — what each figure means pre-market, after hours, weekends and holidays (M2, M7).
5. **Short-side support** — every long-only feature states it and has a plan for shorts (B2).
6. **Research-window governance** — UI shortcuts must not pre-select reserved test periods (B3).
7. **Saved-preference migration** — changing a storage key must carry users' settings forward (S4).
8. **Cheap-stock and edge cases** — maths checked at $1–$10 prices, wide strikes, thin options (M5, M6).

## I. Suggested order (each still needs user design approval and the two AGENTS.md reviews)

1. M1 + R5 + T2 (wrong ratio), M2 (Day % label), P2 (stop per-minute archived fetches).
2. P1 + P3 + R2 (one benchmark-bar path through the candle store, strategy-driven regime).
3. R4 + M8 (server sends human wording and risk figures), B1, R1/C4.
4. S1–S3 structure, T1 tests, then the Low items.
5. Separately, outside this branch: decide on M4–M7 for the shared engine maths when strategy work resumes.
