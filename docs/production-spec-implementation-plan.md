# Production Spec Implementation Plan — TradingFlow → Addendum v1.0

**Status: PLAN (checked in before execution). Execution starts only on explicit go.**
**Binding requirement sources (AUD-01, both in-repo):**
- `docs/spec/automated_trading_production_spec_v1.md` (Addendum — wins on conflict)
- `docs/spec/automated_us_equities_trading_research_design.md` (Base Spec)

**Rules of engagement (user-mandated):** no assumptions — every claim below is verified
against the codebase or marked OPEN; each phase is production-ready when it lands (no
stubs, no "future work" on the money path); every phase ends with build+tests green,
commit, push, and a status-ledger update in this file; behavior-preservation of the
existing evaluation engine is proven by byte-identical honest-rescore artifacts.

---

## 1. Decisions record (answered 2026-07-21)

| # | Decision | Answer | Consequence |
|---|---|---|---|
| D1 | Base Spec availability | Provided (was in Downloads) | Both specs copied to `docs/spec/`; full requirement set in scope |
| D2 | Alpaca SIP (Algo Trader Plus) | **User has the subscription** | DATA-01 implemented as written; the hardcoded IEX stream URL is a defect to fix in S0 |
| D3 | Strategy scope | **Swing-first with our evidence-backed strategies** as the SWG families; DAY = infrastructure only (PDT, flatten rules), no day strategies until evidence justifies | Deviation recorded in `docs/deviations.md` per spec §0.1; our V4 trend (ELIGIBLE) maps to the SWG-B family class, catalyst-drift maps to SWG-A |
| D4 | Production host | **Undecided — abstract it** | Host-agnostic supervision contract (startup gating, config-driven kill-file path, health endpoint); host chosen at S11 |

## 2. Verified current-state gap matrix

Evidence gathered 2026-07-21 by direct code inspection (not assumed):

| Domain | Status | Verified evidence |
|---|---|---|
| ACC | MISSING | Zero hits in `src/` for PDT, buying power, `shortable`, settled funds, account-ID assert |
| DATA | PARTIAL | WS market-data + trade-update streamers exist (`TradingFlow.Alpaca`). **Defect:** `AlpacaStreamClient.cs:22` hardcodes `wss://…/v2/iex` while REST `AlpacaOptions` defaults `MarketDataFeed="sip"` → live mixed-feed (violates DATA-01/02/03). No staleness detection, halt/LULD, symbol budget, clock-drift check |
| CAL | MOSTLY MISSING | IANA tz with Windows fallback exists (`SignalGenerator.Sessions.cs:494`). No calendar API, early-close, corporate actions, earnings calendar |
| FVZ | PARTIAL | Elite `/export?auth=` in use (`FinvizClient.cs:40`); `RateLimiterFactory` exists. No contract validator, raw archiving, versioned presets |
| NWS | PARTIAL | Catalyst one-shot lifecycle + key-dedupe built (`CatalystEligibilityService`); FinBERT/VADER sentiment exists. No 2-stage classifier, ground truth, latency tracking, contradiction handling |
| EXE | PARTIAL | `client_order_id` + **bracket orders** (broker-side stop+target, `AlpacaBrokerClient.cs:63`), OCO GTC exits, SQLite order repo. Submit precedes persistence (`MobileAutomationService.cs:265`) — **no write-ahead intent**. `LiveRunner.cs:150` has one-directional orphan-order *adoption*, not the spec's diff + RECONCILE_MISMATCH + manual-ack loop |
| RSK | SIZING ONLY | `RiskEngine.cs` = ATR sizing + position cap. Zero kill switches, loss limits, lockouts |
| SWG | STRONGEST | Doctrine archetypes ≈ SWG families with better epistemics (honest rescore, promotion gate, one-brain). Missing: gap-stress sizing, earnings exit, HOLDING_OVERNIGHT, overnight news monitor |
| PER | PARTIAL | SQLite (`TradingFlowDbContext`) + run artifacts + catalyst raw caches. No run_id/config_hash/code_version on rows, no Finviz raw archive, no 3-layer formalization |
| OPS | MISSING + DEFECT | **Secrets committed in `launchSettings.json` (Alpaca + eToro keys)** — OPS-04 violation. No supervised startup gating, metrics, alerting, runbook |
| TST | PARTIAL | 240+ tests; `PseudoBroker`; byte-identical honest-rescore replay (TST-02 in spirit, backtest path). **No CI** (`.github/workflows` absent), no property/chaos/kill-switch tests |
| CFG | PARTIAL | Disciplined YAML strategy configs. No Appendix-A registry, config hash, profile enforcement |

**Reusable strengths (do NOT rebuild):** one-brain evaluation (`StrategyDecisionBrain`/`BasicStrategyEvaluator`), regime gate (`RegimeGateService`), catalyst one-shot lifecycle, promotion gate + honest rescore, execution realism model, bracket-order broker client, SQLite persistence layer, rate limiter, IANA tz handling.

## 3. Architecture placement map

New code lands inside the existing solution — no new frameworks, no rewrite:

| Domain | Lands in |
|---|---|
| CFG registry + profiles | `src/TradingFlow.Engine/Configuration/` (`ProductionParameterRegistry`, `ProductionProfile`) |
| PER schema + archives | `src/TradingFlow.Data/` (`TradingFlowDbContext` migrations, `RawArchive/` writers) |
| EXE state machine, gate chain, reconcile | `src/TradingFlow.Engine/Execution/` + `src/TradingFlow.Data/Orders/` |
| RSK limits + kill switches | `src/TradingFlow.Engine/Risk/` (`RiskLimitLedger`, `KillSwitchService`) |
| ACC PDT/buying power | `src/TradingFlow.Engine/Accounts/` (new folder) |
| DATA staleness/halt/feed | `src/TradingFlow.Alpaca/` (streams) + `src/TradingFlow.Engine/Market/` (state) |
| CAL calendar/corp-actions/earnings | `src/TradingFlow.Alpaca/` (calendar client) + `src/TradingFlow.Engine/Sessions/` |
| FVZ validator/presets | `src/TradingFlow.Finviz/` + `configs/finviz-presets/` |
| NWS classifier | `src/TradingFlow.Engine/Catalysts/` + thin `ILlmScorer` adapter (Anthropic API; use the claude-api skill when implementing) |
| OPS startup/metrics | `src/TradingFlow.Web/` health+metrics endpoints; supervision contract in `src/TradingFlow.Engine/Ops/` |
| Reject codes | `src/TradingFlow.Domain/` shared enum (single source, DAY-04 superset) |

**Core architectural rule — fail-closed by construction:** the EXE-04 pre-trade gate
chain is the *only* path to order submission once S3 lands. The chain refuses to pass
if any of its 12 gate slots is unregistered. Later phases *register* gates (ACC in S5,
halt/staleness in S6, calendar in S7); until registered, that slot blocks entries with
`REJECT_DEGRADED_DATA`. Incompleteness therefore blocks trading rather than silently
passing — every intermediate state is production-safe, which is what makes phased
delivery compatible with "production-ready always."

**Behavior-preservation protocol:** any change touching `TradingFlow.Engine` evaluation
code re-runs the honest-rescore configs; result JSON must be byte-identical for
unchanged strategies (established practice in this repo).

---

## 4. Phases

Order: governance → config → persistence → order path → risk → account → data →
calendar → finviz → swing → news → ops → audit. Money-path safety before feature
expansion. Each phase: build clean (warnings=errors), full test suite green, commit +
push, ledger update here.

### S0 — Governance bootstrap + defect burn-down (S)
IDs: AUD-01, OPS-04 (secrets), TST-07 (minimal CI), DATA-01 (feed fix), §0.1 deviations.
1. ✅ Specs copied to `docs/spec/` (done with this plan's check-in).
2. `docs/deviations.md`: D3 (swing-first, day infra-only) and D4 (host abstracted) with justification; template for future SHOULD-deviations.
3. **Secrets remediation:** strip all keys from `launchSettings.json` (git history note recorded); load via env vars / .NET user-secrets; `.gitignore` guard. **USER ACTION: rotate the exposed Alpaca + eToro + Finviz keys** — they are in git history regardless of scrub.
4. CI bootstrap: `.github/workflows/ci.yml` — `dotnet build` (warnings=errors) + `dotnet test` + secret scan (gitleaks) on every push/PR. Every later phase inherits this gate.
5. Feed fix: `AlpacaStreamClient` stream URL derived from `AlpacaOptions.MarketDataFeed` (`/v2/sip` with the user's subscription); startup logs the feed; refuse `iex` unless `allow_iex_fallback=true`.
Accept: CI green on a pushed commit; `git grep` finds no key material; SIP WS authenticates in a manual smoke run.

### S1 — CFG: configuration governance (S–M)
IDs: CFG-01..04, ACC-13 (URL derivation), Appendix A.
1. `ProductionParameterRegistry`: every Appendix-A parameter as typed metadata (name, type, default, min, max, unit, spec ref) — code is the single source; a generator test asserts the registry ⊇ Appendix-A table parsed from `docs/spec/` (bidirectional CFG-04 check).
2. Startup loader: validate ranges, reject out-of-range, compute `config_hash` (SHA-256 over canonical serialization), log it; config immutable after load (CFG-02).
3. Profiles `paper`/`live` (CFG-03): profile selects Alpaca base URLs (trading + stream) — never independently configurable (ACC-13); live profile enforces locked-v1 params (`allow_extended_hours=false` etc.) and refuses non-defaults.
Accept: unit tests for range validation, hash stability, locked-param refusal, profile URL derivation; CFG-04 test green.

### S2 — PER: persistence foundation (M)
IDs: PER-01..06, FVZ-03, NWS-01 (storage side), PER dependencies for all later journaling.
1. Migrations on `TradingFlowDbContext`: `runs` (run_id, config_hash, code_version=git SHA, profile, started_at); `order_intents` (write-ahead, EXE-01); `order_events` (state transitions, EXE-02); `gate_evaluations` (EXE-04); `risk_events` + `kill_switch_events` (RSK); `reconciliations` (EXE-08); `candidates` (BS §16 shape); `catalyst_results` (component-level, NWS-10/PER-04). All rows carry run_id + schema_version.
2. `RawArchiveWriter`: byte-exact archive of Finviz CSVs, news payloads, broker responses before parsing (PER-02), filesystem layout `data/raw/{source}/{yyyy-MM-dd}/`, retention config.
3. PER-06 audit: repo already uses `decimal` on the money path (verified `RiskEngine`, `PersistedOrder`); sweep for `double`/`float` money leaks; fix any found.
4. SQLite durability: WAL mode + synchronous=FULL for the order-intent write path (PER-05); backup job + documented restore procedure (drill scheduled in S12). SQLite is the relational operational store — compliant with PER-01; recorded in deviations only if a server DB is later required.
Accept: migration tests; write-ahead fsync verified by test (intent row exists if process killed between persist and submit — simulated); raw archive round-trip test.

### S3 — EXE: order-path hardening (L) ← highest-value safety phase
IDs: EXE-01..13, DAY-04 (reject enum), parts of TST-03.
1. Reject-code enum in `TradingFlow.Domain` — Base-Spec §10 set + DAY-04 additions; one enum, used by gates, journal, and tests (CI sync-check vs spec in S12).
2. Write-ahead intent: `client_order_id` format `{strategy}-{side}-{symbol}-{yyyymmdd}-{seq}-{uuid8}`; persist INTENT (fsync) **before** `SubmitOrderAsync`; retries reuse the persisted ID (EXE-01). Refactor `MobileAutomationService` entry path and `LiveRunner` submission path onto one shared `OrderSubmissionService` (one-brain discipline applies to execution too).
3. `OrderStateMachine` (EXE-02): allowed transitions enforced; every transition → `order_events` with broker + local timestamps; orphan timeout `order_orphan_timeout_s` → RECONCILE_MISMATCH.
4. Fill-source discipline (EXE-03): `AlpacaTradeUpdateStreamer` primary; REST polling cross-check every `order_poll_interval_s`; divergence > 1 cycle → alert + block entries.
5. Full reconciliation loop (EXE-08): startup + every `reconcile_interval_s`, diff broker positions/orders vs ledger; RECONCILE_MISMATCH blocks new entries globally; auto-resolve only broker-has-fills-we-missed; `ops ack` CLI command clears others. (Extends, does not replace, the existing orphan-adoption at `LiveRunner.cs:150`.)
6. Protective-order invariant (EXE-09): after every fill and every reconcile pass, verify a broker-resting stop exists for each open position; if absent → auto-place backstop (`backstop_atr_mult`) + alert + RECONCILE_MISMATCH. Bracket entry orders already satisfy this at entry; this closes the gap for legs canceled/expired out-of-band.
7. Position-conflict rule (EXE-10): one open position per symbol across strategies, checked in the gate chain against the internal strategy-tagged ledger.
8. Gate-chain framework (EXE-04): ordered 12-slot short-circuiting chain; each gate returns pass/fail + reject code; ALL evaluations journaled to `gate_evaluations` (pass included). Slots for not-yet-built gates block (see §3 rule). Wire existing checks (spread, quote fetch, sizing) into their slots.
9. Sizing floors + caps (EXE-05/06/07) in `RiskEngine`: min stop distance `max(min_stop_spread_mult×spread, min_stop_atr_frac×ATR_ref)` used for sizing when the natural stop is tighter; caps: notional %, `max_pct_adv` of 20-session median volume, day-entry 5-min dollar-volume participation, buying power (gate registers fully in S5); binding cap journaled; zero/sign guards → `REJECT_SETUP_INVALID`.
10. Graceful shutdown (EXE-13): SIGTERM/host-stop → stop signals → cancel non-protective orders → verify EXE-09 → flush → exit; `shutdown_flatten` per day/swing.
Accept: `PseudoBroker` scripted scenarios (TST-03 subset): duplicate submit suppressed by intent reuse; partial fill; reject; replace failure resolution; kill-between-persist-and-submit recovery; naked-position backstop placement. Honest-rescore byte-identical (no evaluation change).

### S4 — RSK: risk shell + kill switches (M)
IDs: RSK-01..06, TST-05.
1. `RiskLimitLedger`: per-trade risk pcts, daily/weekly realized+unrealized loss, consecutive-loss counter, max positions day/swing, gross/net exposure (ACC-07 caps), equity marks ≤60s with staleness haircut (RSK-06).
2. Daily-loss breach (RSK-02): cancel non-protective, flatten day positions, block all entries until next session; no same-day override in live.
3. `KillSwitchService` (RSK-03): each trigger a named, individually testable unit — DATA_STALE, news-stream-down (blocks catalyst-dependent only), order-stream-down, reject-storm, unexpected-position, clock-drift, market-wide-halt (SPY sentinel), account-mismatch, manual. Fired switch → journaled, entries blocked per its scope.
4. Manual kill (RSK-04): CLI command + kill-file (path from config — host-agnostic per D4), polled ≤2s; re-arm requires operator ack journaled who/when/reason (RSK-05); no auto-re-arm in live.
Accept: TST-05 — one automated trigger test per switch; breach-behavior integration test on PseudoBroker.

### S5 — ACC: account & regulatory (M)
IDs: ACC-01..13, SWG-15 (per-fill day-trade classification), TST-01 (PDT part).
1. `PdtTracker`: internal rolling 5-business-day counter derived from fills, classified per-fill (same-session open+close = day trade, SWG-15); cross-check broker `daytrade_count`/`pattern_day_trader` at startup + `pdt_recheck_interval_s`; stricter value governs; disagreement alerts (ACC-02).
2. Pre-order PDT gate (ACC-03): equity < `pdt_equity_floor` AND prospective 4th day trade → `REJECT_PDT_LIMIT`; fail-closed on missing data; exits never blocked (ACC-04) — gate applies to entries only.
3. Buying-power model (ACC-06/07/10 accounting): day vs overnight computed separately; swing checked against overnight BP at entry; self-imposed exposure caps enforced ahead of broker limits; margin-interest estimate journaled daily for swing (ACC-08).
4. Shortability gate (ACC-09) — v1 is long-only (`allow_swing_shorts=false`) but the gate exists and blocks any short leg; asset `tradable`/`active` recheck within `asset_check_max_age_s` (ACC-11).
5. Account identity assert at startup (ACC-12): mismatch → refuse to arm. `account_mode=cash` path (ACC-05): settled-funds tracker, fail-closed on unknown settlement state.
Accept: property-based PDT tests (rolling windows, per-fill classification, boundary equity); gate-chain slots 7 registered; fail-closed tests (missing account data blocks).

### S6 — DATA: market-data hardening (M–L)
IDs: DATA-01..15, TST-04 (automatable subset).
1. Startup SIP assert (DATA-01): authenticate `wss://…/v2/sip`; failure → refuse to arm. `allow_iex_fallback` dev mode: degraded marking, dependent gates disabled-not-fed, orders hard-blocked (DATA-04).
2. Feed tagging (DATA-02/03): `data_feed` recorded on bars/quotes/snapshots/features; RVOL same-feed startup self-test vs historical cache; mismatch → RVOL unavailable (fail closed).
3. Reconnect policy (DATA-06): exponential backoff + jitter; on reconnect re-auth, re-subscribe, REST-backfill the gap, mark gap-spanning features invalid.
4. Staleness (DATA-07/08): global socket-silence detector (RTH/ext thresholds) → DATA_STALE switch; per-symbol quote-age check with REST refresh before managing positions.
5. Symbol budget (DATA-09): priority-ordered subscription manager (positions > pending > SETUP_VALID > WATCHING > RANKED), degradation to minute-bars then snapshot polling inside the REST budget.
6. Halt/LULD (DATA-10/11): trading-status subscription; per-symbol `TRADING|HALTED|PAUSED|UNKNOWN`; non-TRADING (incl. UNKNOWN) blocks entries; halt-with-position → cancel non-protective, freeze, journal, re-evaluate on resume.
7. Central REST limiter (DATA-12): route all REST through `RateLimiterFactory`-backed per-endpoint-class budgets; 429 → backoff + alert counter.
8. Cache guarantee (DATA-13): nightly refresh of 1-min (≥ rvol_lookback+5 sessions) + daily (≥60) bars, adjusted, feed-tagged; miss → feature unavailable, gates fail closed.
9. Latency + clock (DATA-14/15): recv−source latency p99 monitor; NTP drift check → clock-drift kill switch.
Accept: socket-kill/reconnect/backfill integration test; staleness and halt gate tests; feed self-test test.

### S7 — CAL: calendar, sessions, corporate actions (M)
IDs: CAL-01..07.
1. Alpaca calendar client + `TradingCalendarService`: all schedule times become offsets from official open/close (CAL-01); early-close recomputation, close-model skip, EOD flatten deadline (CAL-02); non-trading days = maintenance only (CAL-03).
2. DST self-test at startup after transitions (CAL-05); CAL-04 sweep: no hard-coded UTC offsets (tz handling already IANA-correct — verified).
3. Corporate-actions nightly pipeline (CAL-06): pull actions; adjust `previous_regular_session_close` for gap math; missing/ambiguous → `REJECT_CORPORATE_ACTION` + exclusion; re-adjust caches; migrate open-position symbols after reconcile.
4. Earnings calendar (CAL-07): nightly ingest from Finviz `Earnings Date` column; unknown date → swing entries blocked `REJECT_UNKNOWN_EARNINGS_DATE` (fail closed). Feeds SWG-14 and (dormant) DAY-02.
Accept: early-close simulation test; split-adjustment test; unknown-earnings fail-closed test; calendar gate slot 2 registered.

### S8 — FVZ: Finviz ingestion hardening (S–M)
IDs: FVZ-01..07.
1. Contract validator (FVZ-01): column names order-insensitive, per-column type checks, row-count sanity; violation → discovery HALTED for that preset, last-known-good marked `stale_discovery`, alert.
2. Request policy (FVZ-02): min-interval + timeout + 2 retries + fixed UA; token from secret store only. FVZ-06 calibration procedure implemented as a logged CLI routine (run over ≥5 sessions; production interval = 2× lowest zero-throttle interval).
3. Versioned presets (FVZ-04): one file per preset under `configs/finviz-presets/` with filter map + expected columns + version string journaled on candidates.
4. Derived-not-trusted (FVZ-05): downstream consumption restricted to symbol/exchange/sector/earnings-date/preset-membership; all trading numbers recomputed from Alpaca.
5. Sector→ETF map (FVZ-07): version-controlled table; unmapped → null + re-normalized confirmation score.
Accept: malformed-CSV fixture tests; raw archive verified (S2 writer); preset version journaling test.

### S9 — SWG: swing module completion (M) — extends our doctrine work
IDs: SWG-01..16, ACC-08 hook, DAY-03.
1. Register existing strategies as spec families: V4 trend → SWG-B class, catalyst-drift → SWG-A class; parameter deltas vs SWG-06/07 recorded in `docs/deviations.md` with honest-rescore evidence (D3). `swing_enabled_strategies` config governs enablement; day↔swing conversion prohibited (DAY-03).
2. Gap-stress sizing (SWG-08): second constraint `shares×(gap_stress_atr_mult×ATR14) ≤ swing_gap_risk_budget_pct×equity`; smaller size governs; portfolio gap budget + per-sector cap (SWG-09) via FVZ-07 map; overnight BP check at entry (SWG-10/ACC-06).
3. `HOLDING_OVERNIGHT` state (SWG-02): entered at each close with EOD mark, unrealized P&L, overnight risk snapshot journaled.
4. Swing order mechanics (SWG-11/12): DAY entry orders, window-end cancel, `min_fill_ratio` handling with MIN_FILL_ABORT; primary protection = broker-resting **GTC stop** placed on fill, adjustments via replace-not-cancel (EXE-12); this is EXE-09 for swing.
5. Overnight monitor (SWG-13): news watch 16:00–20:00 / 04:00–09:30 for held symbols; contrary catalyst ≥ materiality threshold → EXIT_AT_OPEN_REVIEW, exit 09:35–10:00 next session unless operator override (journaled).
6. Earnings-proximity exit (SWG-14): never hold into own earnings; forced exit with reason EARNINGS_PROXIMITY, buffer sessions config; depends on CAL-07.
7. Research labels (SWG-16): post-signal 1/3/5/10-session returns, overnight-gap distribution, ±1R probability, stop-gap frequency, cost drag → research journal.
Accept: gap-stress + sector-cap unit tests; GTC-stop invariant test; earnings-exit test; honest-rescore byte-identical for unchanged evaluation logic; MIN_FILL_ABORT scenario on PseudoBroker.

### S10 — NWS: catalyst engine upgrade (L, gated)
IDs: NWS-01..13. Prereq: S2 (storage), S4 (budgets/alerts). The existing one-shot lifecycle is kept; this adds classification quality on top.
1. `first_seen_at` semantics everywhere (NWS-02); ingest-latency distribution daily, p50 alert + stored on CatalystResults (NWS-03).
2. Dedup pipeline (NWS-04): 4 ordered stages — provider ID → canonical URL → normalized-headline hash → fuzzy similarity within 72h/symbol. **OPEN Q2:** similarity method — spec default is embedding cosine (needs an embedding provider); Jaccard fallback is config-supported. Decide before S10 starts.
3. Stage-1 deterministic classifier (NWS-05): versioned keyword/regex rule set + source-reliability table; standalone conservative CatalystResult.
4. Stage-2 LLM scorer (NWS-05): pinned model from config, temperature 0, strict JSON-schema validation, prompt versioned+hashed in repo, response cache on (article, prompt_version, model). Implement with the claude-api skill. Fail-closed (NWS-06): failure → Stage-1-only + `REJECT_AMBIGUOUS_DIRECTION` for dependent entries; budgets + cost cap (NWS-07); p95 pipeline latency (NWS-08).
5. v1 weights without surprise (NWS-09): 30/25/20/15/10; `estimates_source=none` reserved path; component-level persistence (NWS-10, via S2 schema).
6. Ground truth (NWS-11): ≥500 labeled articles, 100 double-labeled, instructions in-repo. **USER ACTION: labeling is human work** — I build the labeling harness (sampling, storage, adjudication flow); you label. Acceptance gate (NWS-12) blocks catalyst-dependent paper trading until accuracies pass.
7. Contradiction handling (NWS-13): opposite-direction non-duplicates within window → AMBIGUOUS, cooloff block.
Accept: dedup-stage unit tests; schema-validation + fail-closed tests; NWS-12 measured on held-out labels; budget-exhaustion → Stage-1-only test.

### S11 — OPS: operations & runtime (M)
IDs: OPS-01..08, D4 resolution.
1. Startup orchestrator (OPS-01): strict sequence config → clock → calendar → broker auth+ACC-12 → reconcile → streams+DATA-01 → self-tests (DATA-03, CAL-05) → arm; any failure → exit nonzero, no partial arming. Host-agnostic (generic host); **decide host here (D4)** and add the thin supervision wrapper (systemd unit or Windows service definition).
2. Environment separation (OPS-02): separate config trees + separate SQLite DBs per profile; live requires `TRADING_LIVE_CONFIRM=YES-I-UNDERSTAND` + banner.
3. Observability (OPS-05): metrics endpoint (stream ages, REST budget, gate counts by reject code, order latencies, reconcile status, risk vs limits, PDT counter, catalyst latency/budget, kill-switch states); alert channels page/notify — **OPEN Q3: delivery channel** (mobile push exists in this repo; email/other?); weekly synthetic alert test.
4. Structured JSON logging with correlation IDs, no secrets (OPS-06); runbook `docs/runbook.md` covering startup/shutdown/kill+re-arm/reconcile/stream-outage/broker-outage/DR (OPS-07); deploy discipline + running-SHA on health endpoint (OPS-03); host NTP/UTC (OPS-08).
Accept: startup-gate failure tests (each step failing prevents arming); synthetic alert fires; runbook commands exist and work.

### S12 — TST completion + AUD adherence audit (M)
IDs: TST-01..08, AUD-01..04, CFG-04 CI, DAY-04 sync.
1. Complete CI gates (TST-07): property tests (sizing/PDT), replay determinism (TST-02 — extend the byte-identical rescore harness to the candidate→gate→simulated-order pipeline over archived raw data), secret scan, CFG-04 bidirectional check, reject-enum sync vs spec.
2. Chaos drill scripts (TST-04): socket kills, SIGKILL+restart+reconcile, 5xx storm, clock skew — scripted against paper profile, scheduled monthly, results journaled; pass = EXE-09 never violated, no duplicate orders, reconcile converges.
3. Restore drill procedure (TST-08) documented + first run executed.
4. **Full AUD-02/03 audit**: every requirement ID in both specs → status/evidence/gap/severity/fix table → `docs/audit/adherence_report_<date>.md`; remediate all BLOCKERs; iterate until zero BLOCKER, then re-run. This is the campaign's exit gate.

---

## 5. Requirement-ID coverage map (nothing missed)

| IDs | Phase |
|---|---|
| CFG-01..04 | S1 (CI check S12) |
| PER-01..06 | S2 (restore drill S12) |
| EXE-01..13 | S3 (EXE-12 replace semantics also S9) |
| RSK-01..06 | S4 |
| ACC-01..13 | S5 (ACC-13 in S1) |
| DATA-01..15 | S6 (DATA-01 feed fix starts S0) |
| CAL-01..07 | S7 |
| FVZ-01..07 | S8 (FVZ-03 archive in S2) |
| SWG-01..16 | S9 (SWG-15 in S5) |
| NWS-01..13 | S10 (NWS-01 storage in S2) |
| DAY-01..04 | S3 (enum), S5 (PDT), S7 (calendar/flatten), rest dormant per D3 — deviation recorded |
| OPS-01..08 | S11 (OPS-04 secrets in S0) |
| TST-01..08 | S3/S4/S5 (as they land) + S12 completion |
| AUD-01..04 | S0 (files) + S12 (audit) |
| Base Spec §4–19 | Universe/§4 + presets/§5 → S8; schedule/§6 → S7; catalyst/§8 → S10; confirmation/§9 → S6/S8; ranking+rejects/§10 → S3; state machine/§15 + candidate/§16 → S2/S3; research/§17 → existing honest-rescore + S9/S12; phases/§18 → Appendix-B mapping below |

**Appendix-B phase position:** swing module is effectively at spec Phase 2 (paper) on
the strategy side but Phase 0 on the operations side. This plan closes the operations
gap; the existing pending work "Paper-validate V4 (30 sessions, zero EXE-09/reconcile
violations)" becomes the Appendix-B Phase-2→3 evidence gate. Existing task #20
(Archetype B live wiring) is absorbed by S3+S9+S10.

## 6. Open questions / user actions (blocking only where marked)

| # | Item | Needed by | Status |
|---|---|---|---|
| U1 | **Rotate exposed Alpaca + eToro + Finviz keys** (they live in git history) | S0 | USER ACTION |
| Q2 | Dedup similarity method: embedding provider or Jaccard fallback | S10 start | OPEN |
| Q3 | Alert delivery channel for page/notify (mobile push exists; email?) | S11 start | OPEN |
| Q4 | eToro path: spec is Alpaca-only — remove eToro client or leave dormant out of the production path | S3 start | OPEN |
| U5 | NWS-11 ground-truth labeling (~500 articles) — harness built by me, labels by you | S10 gate | USER ACTION |
| Q6 | Anthropic API key + budget for Stage-2 scorer (defaults: 500 articles/day, $10/day cap) | S10 start | OPEN |
| D4 | Host choice (Linux VPS vs Windows service) | S11 | DEFERRED by design |

**Q4 resolution (2026-07-21):** retain the eToro client source code, but keep it
dormant. Paper/live production composition MUST NOT register eToro services, load
eToro credentials, call eToro startup checks, or route orders through eToro. S3 adds
an automated composition test for this boundary.

## 7. Status ledger (updated at every check-in)

| Phase | Status | Commit | Notes |
|---|---|---|---|
| Plan + specs checked in | ✅ 2026-07-21 | (this commit) | Execution awaiting go |
| S0 | ⬜ | | |
| S1 | ⬜ | | |
| S2 | ⬜ | | |
| S3 | ⬜ | | |
| S4 | ⬜ | | |
| S5 | ⬜ | | |
| S6 | ⬜ | | |
| S7 | ⬜ | | |
| S8 | ⬜ | | |
| S9 | ⬜ | | |
| S10 | ⬜ | | |
| S11 | ⬜ | | |
| S12 (audit = exit gate) | ⬜ | | |

Sizes: S=small (≤1 session), M=medium (1–2), L=large (2+). Recommended execution:
S0→S1→S2→S3 form the safety core and should run consecutively; S4–S8 can interleave;
S9–S10 build on all of it; S11–S12 close out. Every phase commits + pushes on green.
