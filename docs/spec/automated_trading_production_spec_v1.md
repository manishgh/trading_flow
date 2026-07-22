# Automated US Equities Trading System — Production Specification Addendum v1.0

**Status:** Binding engineering specification (companion to `automated_us_equities_trading_research_design.md`, hereafter "Base Spec")
**Date:** 20 July 2026
**Scope:** Day trading (intraday) AND swing trading (multi-session) on US-listed equities via Finviz Elite + Alpaca
**Purpose:** Close every gap in the Base Spec, define the swing module, define production operations, and provide a machine-auditable requirement set so Claude Code can verify that the implemented system adheres to this design.

> Research framing carries over from the Base Spec: nothing here asserts profitability. This document defines correctness, safety, and completeness of the *system*, not the edge of any strategy.

---

## 0. Document conventions

### 0.1 Requirement language (RFC 2119)

- **MUST / MUST NOT** — mandatory. Non-compliance is a defect.
- **SHOULD / SHOULD NOT** — strong default; deviation requires a written justification recorded in the repo (`docs/deviations.md`).
- **MAY** — optional.

### 0.2 Requirement identifiers

Every requirement has a stable ID: `<DOMAIN>-<NN>` (e.g., `RSK-04`). IDs are never reused or renumbered. Claude Code audits reference these IDs. Domains:

| Prefix | Domain |
|---|---|
| ACC | Account, regulatory, and broker constraints |
| DATA | Market data platform |
| CAL | Calendar, sessions, time |
| FVZ | Finviz ingestion |
| NWS | News ingestion and catalyst engine |
| EXE | Order management and execution |
| RSK | Risk engine and kill switches |
| SWG | Swing trading module |
| DAY | Day trading module deltas (beyond Base Spec) |
| PER | Persistence and research store |
| OPS | Operations, deployment, environments |
| TST | Testing and CI gates |
| AUD | Adherence audit protocol |
| CFG | Configuration governance |

### 0.3 Configuration principle — nothing implicit

**CFG-01 (MUST):** Every numeric threshold, window, weight, timeout, and limit in this document MUST exist as a named configuration parameter with: name, type, default, allowed range, and unit. The canonical registry is Appendix A. Hard-coding any of these values inline in business logic is a defect.

**CFG-02 (MUST):** Configuration MUST be loaded once at startup, validated against Appendix A ranges, hashed, and the hash recorded in every research-journal row produced during that run (`config_hash`). A config change mid-run MUST require a restart.

**CFG-03 (MUST):** Two configuration profiles MUST exist: `paper` and `live`. The active profile MUST be logged at startup and stored with every order record.

**CFG-04 (MUST NOT):** No parameter may exist in code without appearing in Appendix A. The audit (AUD-03) checks this bidirectionally.

### 0.4 Relationship to Base Spec

Where this addendum and the Base Spec conflict, this addendum wins. Base Spec sections referenced as `[BS §n]`.

---

## 1. ACC — Account, regulatory, and broker constraints

### 1.1 Pattern Day Trader (PDT)

**ACC-01 (MUST):** The system MUST model the PDT rule: a margin account with equity below USD 25,000 is limited to 3 day trades within any 5 rolling business days. A "day trade" is opening and closing (in whole or part) a position in the same symbol within the same trading day, in a margin account.

**ACC-02 (MUST):** The risk engine MUST maintain its own rolling day-trade counter (`pdt_internal_count`) derived from fill records, AND cross-check the broker's values (`daytrade_count`, `pattern_day_trader` from the Alpaca account endpoint) at startup and at least every `pdt_recheck_interval_s` (default 300 s). If the two counters disagree, the stricter value governs and an alert fires.

**ACC-03 (MUST):** Pre-order gate: if account equity < `pdt_equity_floor` (default 25,500 USD — includes a 500 USD buffer) AND the prospective order could become the 4th day trade in the rolling window, the order MUST be blocked with reject code `REJECT_PDT_LIMIT`. Blocking is fail-closed: if equity or counter data is unavailable, the order is blocked.

**ACC-04 (MUST):** Closing an existing position is never blocked by ACC-03. Risk exits always execute; PDT status is recorded on the exit record for research.

**ACC-05 (MUST):** `account_mode` config (`margin` | `cash`, default `margin`). In `cash` mode: PDT does not apply, but the system MUST track settled vs unsettled funds under T+1 settlement and MUST block any purchase that would use unsettled proceeds in a way that could produce a good-faith violation. Fail closed when settlement state is unknown.

### 1.2 Buying power and overnight exposure

**ACC-06 (MUST):** Day-trade buying power and overnight (Reg T) buying power MUST be modeled separately. Intraday positions are checked against day-trade buying power; any position designated `swing` (see §7) is checked against overnight buying power at entry time, not at hold time.

**ACC-07 (MUST):** Default self-imposed caps (stricter than broker limits): `max_gross_exposure_intraday_pct` default 100% of equity; `max_gross_exposure_overnight_pct` default 75% of equity. The engine MUST enforce its own caps even when the broker would permit more leverage.

**ACC-08 (MUST):** Margin interest accrual on overnight debit balances MUST be estimated daily and written to the research journal as a cost line item for swing trades.

### 1.3 Short selling

**ACC-09 (MUST):** Short entries are permitted only when the Alpaca asset record shows `shortable = true` AND `easy_to_borrow = true` at decision time. Otherwise `REJECT_NO_SHORT_AVAILABILITY`.

**ACC-10 (MUST):** Overnight short positions (swing shorts) MUST record estimated borrow cost daily. `allow_swing_shorts` config default `false` (v1 swing is long-only).

**ACC-11 (MUST):** The asset's `tradable`, `status = active`, and `fractionable` flags MUST be re-verified within `asset_check_max_age_s` (default 3600 s) before any entry order.

### 1.4 Broker/account identity

**ACC-12 (MUST):** At startup the system MUST fetch the account ID and compare it to `expected_account_id` in config. Mismatch → refuse to start (`SYSTEM_KILL_SWITCH: ACCOUNT_MISMATCH`).

**ACC-13 (MUST):** Paper and live API credentials MUST never be loadable in the same process. The trading base URL MUST be derived from the profile (CFG-03), never independently configured, so a paper profile cannot point at the live endpoint.

---

## 2. DATA — Market data platform

### 2.1 Feed requirements

**DATA-01 (MUST):** Production operation (paper Phase 2 onward and all live phases) REQUIRES the Alpaca consolidated SIP feed (Algo Trader Plus subscription or equivalent). At startup the system MUST authenticate against `wss://stream.data.alpaca.markets/v2/sip`; authentication failure → refuse to start. Rationale: the free plan is IEX-only, limited to 30 trade/quote symbol subscriptions, 200 REST calls/min, and blocks recent-SIP REST queries — all incompatible with the Base Spec's spread gates, quote-age gates, halt detection, and 30–150-candidate monitoring.

**DATA-02 (MUST):** `data_feed` MUST be recorded on every bar, quote, trade, snapshot, and derived feature row. Mixed-feed computation of any single feature is a defect.

**DATA-03 (MUST):** Same-time RVOL [BS §9.2]: numerator (today's volume-so-far) and denominator (median same-clock-time volume over `rvol_lookback_sessions`, default 20) MUST be computed from the same feed. A startup self-test MUST verify feed identity of the historical cache versus the live stream and refuse to compute RVOL on mismatch.

**DATA-04 (MUST):** IEX-degraded mode: if config `allow_iex_fallback` (default `false`) is enabled for development, then spread_bps, quote-age, depth, halt state, and same-time RVOL MUST be marked `reliability = "degraded"`, all reject rules that depend on them MUST be disabled rather than fed bad data, and **no orders may be submitted** (`degraded_mode_trading = false`, not configurable upward in `live` profile).

### 2.2 WebSocket operations

**DATA-05 (MUST):** Exactly one concurrent connection per Alpaca streaming endpoint (market data; trading/account updates). All symbol subscriptions multiplex over the single market-data connection.

**DATA-06 (MUST):** Reconnect policy: exponential backoff starting `ws_backoff_initial_ms` (default 500), factor 2, jitter ±20%, cap `ws_backoff_max_ms` (default 30,000). On reconnect: re-authenticate, re-subscribe the full current symbol set, then backfill the outage gap via REST (bars and, where needed, trades) before resuming feature computation. Features computed over an unfilled gap MUST be marked invalid.

**DATA-07 (MUST):** Staleness detection: during regular hours, if no message (including heartbeats) arrives on the market-data socket for `ws_stale_after_s` (default 10 s), the DATA_STALE condition fires: block new entries, mark open-position management as degraded, alert. During extended hours the threshold is `ws_stale_after_ext_s` (default 60 s).

**DATA-08 (MUST):** Per-symbol staleness: an open position whose latest quote is older than `quote_max_age_ms` (default 2,000 ms during RTH) MUST NOT be managed off that quote; the engine MUST refresh via REST snapshot before acting, and if still stale, escalate to DATA_STALE handling for that symbol.

**DATA-09 (MUST):** Subscription budget: the number of simultaneously subscribed trade/quote symbols MUST be capped at `ws_symbol_budget` (default 300) with a priority order: open positions > order-pending candidates > SETUP_VALID > WATCHING > RANKED. Lower-priority symbols degrade to minute-bar-only subscription, then to REST snapshot polling at `snapshot_poll_interval_s` (default 30 s), within REST rate budget (DATA-12).

### 2.3 Halts and LULD

**DATA-10 (MUST):** The system MUST subscribe to trading-status messages on the SIP stream and maintain per-symbol state: `TRADING | HALTED | PAUSED | UNKNOWN`. Entry orders MUST be blocked for any state other than `TRADING` (`REJECT_HALT_OR_LULD`). `UNKNOWN` blocks (fail closed).

**DATA-11 (MUST):** On a halt of a symbol with an open position: cancel all resting non-protective orders for that symbol, freeze management, alert, and on resumption re-evaluate against strategy invalidation rules before any action. This event MUST be journaled with the halt/resume timestamps.

### 2.4 REST budget, historical cache, integrity

**DATA-12 (MUST):** A central rate limiter MUST own the REST budget per endpoint class (config `rest_budget_per_min`, defaults: market-data 9,000, trading 180). All REST calls go through it; 429 responses trigger backoff and an alert counter.

**DATA-13 (MUST):** Historical cache: for every symbol in the eligible universe, the system MUST maintain ≥ `rvol_lookback_sessions` + 5 sessions of 1-minute bars and ≥ 60 sessions of daily bars, refreshed nightly, corporate-action adjusted (CAL-06), feed-tagged. Cache misses at decision time → feature marked unavailable, dependent gates fail closed.

**DATA-14 (MUST):** Every ingested record stores both the exchange/source timestamp and the local receive timestamp (nanosecond precision where provided). Derived latency (`recv - source`) is monitored; p99 above `data_latency_alert_ms` (default 1,500 ms) alerts.

**DATA-15 (MUST):** Host clock discipline: NTP-synchronized; measured drift beyond `clock_drift_max_ms` (default 250 ms) fires the clock-drift kill switch [BS §14].

---

## 3. CAL — Calendar, sessions, time

**CAL-01 (MUST):** All scheduling MUST be driven by the Alpaca trading-calendar API, fetched daily at `calendar_fetch_time` (default 03:30 ET) and cached. The static schedule table [BS §6] defines *offsets and windows relative to that day's official open/close*, not absolute times.

**CAL-02 (MUST):** Early-close days: all post-open windows MUST be recomputed from the actual close. The closing-session model (Base Spec 15:00–15:50 and SWG close-window logic) MUST be skipped entirely when the session is shorter than `min_session_hours_for_close_model` (default 6.0 h). Day-trade positions MUST be flattened no later than `eod_flatten_offset_min` (default 10 min) before the actual close.

**CAL-03 (MUST):** Non-trading days: the scheduler runs data-maintenance jobs only; no scans, no orders. Half-day and holiday determinations come only from CAL-01, never from a hard-coded list.

**CAL-04 (MUST):** All internal timestamps are stored in UTC with timezone-aware types; all market logic converts through the IANA `America/New_York` zone at evaluation time. Hard-coded UTC offsets anywhere are a defect [BS §6].

**CAL-05 (MUST):** DST-transition self-test: on the first run after any US DST change, a startup assertion verifies that "09:30 America/New_York" maps to the expected UTC instant from the calendar API.

**CAL-06 (MUST):** Corporate actions: nightly (after CAL-01 fetch) the system MUST pull corporate actions (splits, cash/stock dividends, symbol changes, mergers) for the universe. Effects:
1. `previous_regular_session_close` used in the live gap formula [BS §5.3] MUST be the adjusted close when an action is effective that morning.
2. If adjustment data is missing or ambiguous for a symbol with an action effective today → `REJECT_CORPORATE_ACTION` (new reject code, added to the [BS §10] enum) and exclusion from that day's candidate set.
3. Historical caches (DATA-13) MUST be re-adjusted the night an action lands.
4. Symbol changes MUST migrate open swing positions' internal records to the new symbol after broker reconciliation.

**CAL-07 (MUST):** Earnings calendar: the system MUST ingest, nightly, the upcoming earnings dates (source: Finviz export `Earnings Date` column; config `earnings_calendar_source`). This feeds the MVP event-anchored discovery (DAY-02) and the swing earnings-proximity exit (SWG-14). If a symbol's earnings date is unknown, swing entries in that symbol are blocked (`REJECT_UNKNOWN_EARNINGS_DATE`) — fail closed.

---

## 4. FVZ — Finviz ingestion

**FVZ-01 (MUST):** The Finviz Elite export endpoint MUST be treated as an unstable CSV contract, not a REST API. A data-contract validator runs on every pull: expected column names, order-insensitive; per-column type/parse checks; row-count sanity (`finviz_min_rows` default 1, `finviz_max_rows` default 5,000). Any violation → discovery HALTED for that preset, alert raised, last-known-good candidate list retained but marked `stale_discovery = true`. Silent degradation is a defect.

**FVZ-02 (MUST):** Request policy: minimum interval between any two Finviz requests `finviz_min_interval_s` (default 60 s; validated empirically per FVZ-06), request timeout `finviz_timeout_s` (default 20 s), max retries 2 with backoff, a fixed descriptive User-Agent, and the auth token stored only in the secret store (OPS-04).

**FVZ-03 (MUST):** Every pull is archived raw (byte-exact CSV) with pull timestamp, preset ID, and HTTP metadata before any parsing (PER-02). Parsing always operates on the archived copy.

**FVZ-04 (MUST):** Preset definitions [BS §5] MUST live in version-controlled config (one file per preset: filter map + sort + expected columns), referenced by `finviz_preset` name in the Candidate record [BS §16]. Editing a preset changes its version string, which is journaled.

**FVZ-05 (MUST):** Derived-not-trusted rule: fields that the Base Spec says to compute internally (premarket gap, premarket dollar volume, spread) MUST NOT be taken from the Finviz export even when present. Finviz fields are discovery hints only; every trading-relevant number is recomputed from Alpaca data. The only Finviz fields consumed downstream: symbol, exchange, sector/industry (for FVZ-07), earnings date (CAL-07), and preset membership.

**FVZ-06 (MUST):** Phase 0 rate-limit calibration procedure (because Finviz publishes no precise limits): starting at 120 s intervals, halve toward 30 s over ≥5 sessions while logging HTTP status, latency, and any throttle/captcha responses; the production `finviz_min_interval_s` is then set to 2× the lowest interval that produced zero throttle events across 3 consecutive sessions. The calibration log is retained as evidence.

**FVZ-07 (MUST):** Sector mapping: a version-controlled table maps Finviz sector → sector ETF (default: GICS-style sectors → SPDR XLB/XLC/XLE/XLF/XLI/XLK/XLP/XLRE/XLU/XLV/XLY). Unmapped sector → `sector_excess_return = null`, and the market-confirmation component that needs it is re-normalized over the remaining components rather than defaulted to zero.

---

## 5. NWS — News ingestion and catalyst engine

### 5.1 Ingestion

**NWS-01 (MUST):** News arrives via the Alpaca news WebSocket stream (primary) and REST backfill (recovery + historical). Every article stores: provider article ID, canonical URL, headline, summary/body, symbols, `created_at`, `updated_at`, first local ingest timestamp, and raw payload (PER-02).

**NWS-02 (MUST):** Single-wire honesty: the news source is Benzinga-via-Alpaca. All "freshness" and "first published" semantics MUST be defined and journaled as *first seen on this wire*, and the catalyst object field is named `first_seen_at`, not `first_published_at`, replacing the field in [BS §8.4].

**NWS-03 (MUST):** Latency measurement (Phase 0 onward): the distribution of `ingest_ts − created_at` is recorded per day; p50 above `news_latency_alert_s` (default 90 s) alerts, and the current day's p50 is stored on every CatalystResult so freshness scores can be interpreted later.

**NWS-04 (MUST):** Deduplication pipeline, in order: (1) exact provider article ID; (2) canonical-URL match; (3) normalized-headline hash (case/punctuation/whitespace-folded); (4) fuzzy similarity on headline+lead using the configured method `dedup_method` (default: cosine similarity on embeddings) with threshold `dedup_similarity_threshold` (default 0.92) over a `dedup_window_hours` (default 72 h) window per symbol. A match links `duplicate_of` and applies the staleness penalty [BS §8.3]; it never silently drops the record.

### 5.2 Catalyst classification mechanism (fills [BS §8], which defined scores but no mechanism)

**NWS-05 (MUST):** Two-stage classifier:
- **Stage 1 — deterministic rules:** category assignment [BS §8.1 taxonomy] via a version-controlled rule set (keyword/regex + structured cues such as "8-K", "guidance", "downgrade"), plus source-reliability scoring from a version-controlled source table. Stage 1 alone MUST be able to produce a conservative CatalystResult.
- **Stage 2 — LLM scorer:** materiality, novelty (in combination with NWS-04 output), directional clarity, direction, and a one-paragraph explanation. Requirements: fixed model ID from config `catalyst_llm_model`; temperature 0; strict JSON-schema output validated on receipt; the exact prompt stored in the repo with a version string; `prompt_version` + `prompt_hash` + `model_id` stored on every CatalystResult; response cache keyed on `(article_id, prompt_version, model_id)` so identical inputs always yield identical stored scores.

**NWS-06 (MUST):** Fail-closed fallback: if Stage 2 errors, times out (`catalyst_llm_timeout_s`, default 8 s), or returns schema-invalid output after 1 retry, the article gets a Stage-1-only CatalystResult with `llm_status = failed`, and any candidate whose trade decision depends on that article is blocked from automated entry (`REJECT_AMBIGUOUS_DIRECTION`). LLM failure never silently downgrades to a guessed score.

**NWS-07 (MUST):** Budget: `catalyst_llm_max_articles_per_day` (default 500) and `catalyst_llm_daily_cost_cap_usd` (default 10). Exceeding either → Stage-1-only mode + alert; trading continues only for candidates already scored.

**NWS-08 (MUST):** Latency budget: end-to-end article-ingest → CatalystResult p95 ≤ `catalyst_pipeline_p95_s` (default 15 s), measured continuously.

### 5.3 Score model corrections

**NWS-09 (MUST):** The **surprise-magnitude component is removed from v1** because no consensus-estimates source exists in the current stack [Base Spec gap]. The v1 catalyst score weights are: materiality 30, novelty 25, directional clarity 20, source reliability 15, freshness 10 (sum 100). This supersedes [BS §8.2]. A config flag `estimates_source` (default `none`) reserves the v2 path: when a licensed estimates source is configured, surprise is reinstated at weight 20 with the [BS §8.2] distribution, gated behind its own validation.

**NWS-10 (MUST):** Component persistence: every CatalystResult stores all raw component values, penalties applied, dedup evidence, and Stage-1/Stage-2 provenance — never only the composite (see PER-04).

### 5.4 Ground truth and acceptance gate

**NWS-11 (MUST):** Phase 0 deliverable: a labeled ground-truth set of ≥ 500 articles covering all taxonomy categories, labeled for category, direction, and coarse materiality (low/medium/high), with labeling instructions stored in-repo. At least 100 articles double-labeled; disagreements adjudicated and the adjudication recorded.

**NWS-12 (MUST):** Acceptance gate before any paper trading uses catalyst scores: on the held-out portion of NWS-11, Stage-1+2 must achieve category accuracy ≥ `catalyst_min_category_acc` (default 0.85) and direction accuracy on clear-direction articles ≥ `catalyst_min_direction_acc` (default 0.90). Failing the gate blocks Phase 2 for catalyst-dependent strategies; results are stored as evidence.

**NWS-13 (MUST):** Contradiction handling [BS §8.3]: two non-duplicate articles for the same symbol within `contradiction_window_min` (default 120 min) with opposite direction and both `materiality ≥ 0.5` → symbol marked `AMBIGUOUS`, automated entries blocked for `ambiguity_cooloff_min` (default 240 min) (`REJECT_AMBIGUOUS_DIRECTION`).

---

## 6. EXE — Order management and execution

### 6.1 Identity and idempotency

**EXE-01 (MUST):** Every order carries a client-generated `client_order_id` of the exact format `{strategy}-{side}-{symbol}-{yyyymmdd}-{seq}-{uuid8}` (e.g., `SWGA-B-ABCD-20260720-003-1f9c2a7e`). It is generated once, **persisted with intent (write-ahead) before submission**, and reused on any retry of the same logical order. Duplicate submission of a new ID for the same logical intent is a defect.

**EXE-02 (MUST):** Order state machine per order: `INTENT → SUBMITTED → ACKED → {PARTIALLY_FILLED → FILLED} | CANCEL_PENDING → CANCELED | REJECTED | EXPIRED`, with every transition journaled with broker timestamps and local timestamps. Terminal reconciliation: any order in a non-terminal local state with no broker record after `order_orphan_timeout_s` (default 30 s) → RECONCILE_MISMATCH handling (EXE-08).

**EXE-03 (MUST):** Trade-updates stream (account WebSocket) is the primary fill source; REST order polling every `order_poll_interval_s` (default 15 s) is the cross-check. Divergence between the two beyond one polling cycle → alert + block new entries until reconciled.

### 6.2 Pre-trade gate sequence

**EXE-04 (MUST):** The [BS §14] entry-control list is implemented as an **ordered, short-circuiting gate chain**, every gate producing a pass/fail with a reject code, all results journaled even on pass. Order: (1) system state (no kill switch, not degraded, not stale); (2) calendar window valid for the strategy; (3) candidate state = SETUP_VALID and revalidated within `setup_max_age_s` (default 20 s); (4) halt/LULD = TRADING; (5) quote age ≤ `quote_max_age_ms`; (6) spread ≤ strategy `max_spread_bps`; (7) ACC gates (PDT, buying power, shortability); (8) position-conflict check (EXE-10); (9) sizing valid (EXE-05/06); (10) exposure limits (RSK); (11) expected-slippage estimate ≤ `max_expected_slippage_bps`; (12) duplicate-order check against open orders. Any fail → no order, reject journaled.

### 6.3 Sizing

**EXE-05 (MUST):** Base formula [BS §14] `shares = floor(per_trade_risk_dollars / (entry − stop))` with a mandatory **minimum stop distance**: `stop_distance ≥ max(min_stop_spread_mult × current_spread, min_stop_atr_frac × ATR_ref)` where defaults are `min_stop_spread_mult = 4`, `min_stop_atr_frac = 0.25`, and `ATR_ref` is intraday-scaled daily ATR for day trades and daily ATR(14) for swing. If the strategy's natural stop is closer than the floor, the floor distance is used for sizing (not for the stop placement), shrinking size.

**EXE-06 (MUST):** Caps applied after EXE-05, all of: `max_notional_per_trade_pct` of equity (default 15%); participation caps — shares ≤ `max_pct_adv` (default 1%) of 20-session median daily volume AND, for day entries, notional ≤ `max_pct_recent_dollar_vol` (default 5%) of trailing 5-minute dollar volume; available buying power per ACC-06; portfolio exposure per RSK. The binding cap is journaled.

**EXE-07 (MUST):** Division-by-zero / sign guards: entry ≤ stop for longs (or ≥ for shorts), non-positive risk budget, or missing inputs → hard reject `REJECT_SETUP_INVALID`, never a fallback size.

### 6.4 Reconciliation and recovery

**EXE-08 (MUST):** On startup and every `reconcile_interval_s` (default 60 s): fetch broker positions and open orders; diff against the internal ledger. Any mismatch → state `RECONCILE_MISMATCH`: new entries blocked globally, protective handling of the mismatched symbol (EXE-09 backstops remain), alert with the full diff. Automatic resolution is permitted only for the case "broker has fills the ledger missed" (apply fills); every other case requires manual acknowledgment (`ops ack` command) to clear.

**EXE-09 (MUST):** Protective-order invariant: **every open position MUST have a broker-resting protective stop order at all times**, including during engine downtime. Day trades MAY be managed with engine-side logic, but a hard backstop stop (at the structural stop or `backstop_atr_mult` × ATR beyond it, default 1.5) MUST rest at the broker from fill confirmation until exit. Swing positions use broker-resting GTC stops as the primary stop (SWG-12). An open position detected without a resting protective order → immediate alert + auto-place backstop + RECONCILE_MISMATCH review.

**EXE-10 (MUST):** Position-conflict rule: at most one open position per symbol across all strategies (`allow_multi_strategy_same_symbol = false`, not raisable in v1), because broker positions net per account and per-strategy attribution would otherwise be unverifiable. The OMS maintains a strategy-tagged internal position ledger (via EXE-01 IDs) reconciled to the broker's net position.

**EXE-11 (MUST):** Extended-hours trading is controlled by `allow_extended_hours_trading` and defaults to `false` in every profile. When disabled, entries outside the provider-reported regular session MUST fail closed. When enabled, only explicit limit + time-in-force DAY entries are permitted outside regular hours; the adapter MUST set Alpaca `extended_hours = true` only for an order actually submitted during an extended session and MUST NOT convert another order type. Session classification MUST use Alpaca's trading calendar, including holidays and early closes. Overnight entries additionally require a current asset response with `status=active`, `tradable=true`, and `overnight_tradable=true`. Market-data ingestion and indicator warm-up are independent of this execution permission.

**EXE-12 (MUST):** Cancel/replace: modifications use the broker replace endpoint where available; a replace that fails MUST leave the resolved state journaled (original live, or canceled) after re-query — never assumed. Trailing stops MUST NOT be combined inside bracket/OCO structures unless a Phase-0 broker-behavior test demonstrates and documents the exact semantics [BS §14].

**EXE-13 (MUST):** Graceful shutdown: on SIGTERM the engine (1) stops accepting signals, (2) cancels non-protective open orders, (3) verifies EXE-09 backstops exist for all positions, (4) flushes journals, (5) exits. `shutdown_flatten` config (default: `true` for day positions, `false` for swing positions) controls whether positions are closed on shutdown.

---

## 7. RSK — Risk engine and kill switches

**RSK-01 (MUST):** Limits, all enforced pre-trade and monitored continuously, each a named config: per-trade risk `per_trade_risk_pct` (default 0.5% of equity, separate `per_trade_risk_pct_swing` default 0.5%); daily realized+unrealized loss `max_daily_loss_pct` (default 2%); weekly `max_weekly_loss_pct` (default 4%); consecutive losing day trades `max_consecutive_losses` (default 3 → no new day entries until next session); simultaneous positions `max_positions_day` (default 1 in v1), `max_positions_swing` (default 3 in v1); gross/net exposure per ACC-07.

**RSK-02 (MUST):** Daily-loss breach behavior: cancel all non-protective orders, flatten all **day** positions, block all new entries (day and swing) until next session, alert, require manual re-arm for same-day override (override MUST NOT exist in `live` profile).

**RSK-03 (MUST):** Kill-switch catalog [BS §14] with concrete triggers — each MUST be implemented, individually testable (TST-05), and journaled on fire: DATA_STALE (DATA-07/08); news-stream down > `news_stream_down_max_s` (default 120 s) → block catalyst-dependent entries only; order-update-stream down > 30 s → block all entries; ≥ `max_order_rejects` (default 3) broker rejections in 5 min → block entries; unexpected position (EXE-08) → RECONCILE_MISMATCH; clock drift (DATA-15); market-wide halt (DATA-10 on SPY as sentinel + status feed) → block entries, review positions; account mismatch (ACC-12) → refuse to run; manual emergency stop (RSK-04).

**RSK-04 (MUST):** Manual kill: a local command and a break-glass file flag (`/run/trading/KILL`) both trigger: cancel non-protective orders, optionally flatten per `manual_kill_flatten` (default true for day, prompt for swing), halt engine. Checked at least every 2 s.

**RSK-05 (MUST):** Re-arm discipline: any fired kill switch requires an explicit operator acknowledgment recorded in the journal (who/when/reason) before entries resume. Auto-re-arm is prohibited in `live`.

**RSK-06 (MUST):** All risk arithmetic uses equity marked from broker account data refreshed ≤ 60 s old; if unavailable, the last-known value minus a `equity_staleness_haircut_pct` (default 5%) haircut is used for limit checks (conservative), and entries beyond 5 min of staleness are blocked.

---

## 8. SWG — Swing trading module (new; extends the system beyond the intraday Base Spec)

### 8.1 Placement in the architecture

**SWG-01 (MUST):** Swing is a **separate strategy engine instance** sharing the discovery, catalyst, market-confirmation, risk, order-management, and journal infrastructure — consistent with [BS §3.3]: swing hypotheses are never merged into intraday scoring formulas, and each swing strategy gets its own rank list [BS §10].

**SWG-02 (MUST):** The candidate state machine [BS §15] is reused with one added state for swing: `HOLDING_OVERNIGHT` (between MANAGING and EXIT_PENDING), entered at each session close while the position remains open, journaled with the end-of-day mark, unrealized P&L, and overnight risk snapshot.

### 8.2 Universe and data

**SWG-03 (MUST):** Swing universe = Base Spec universe [BS §4] with identical v1 thresholds (price > $10, cap > $2B, avg vol > 1M). Daily bars (corporate-action adjusted, DATA-13) are the primary decision data; intraday data is used only for entry/exit execution quality.

**SWG-04 (MUST):** Swing discovery runs on the post-close scan (16:30 window per CAL-01 offsets) and pre-open review (08:45), using the same Finviz presets plus the earnings calendar (CAL-07). Swing candidates carry `horizon = "swing"` and never enter the day-trading ranking, and vice versa.

### 8.3 v1 swing strategy families (research hypotheses, parametrized — same epistemic status as [BS §11])

**SWG-05 (MUST):** Exactly two swing families exist in v1; each is validated independently [BS §17], and only one may be live-enabled at a time until both pass their own gates (`swing_enabled_strategies` config).

**SWG-06 — Family SWG-A, post-earnings drift continuation (long, v1):**
- Signal day T conditions (evaluated at T close): fresh earnings/guidance catalyst with catalyst_score ≥ `swga_min_catalyst` (default 70) and direction positive; gap and total day return positive; close in the top `swga_close_range_pct` (default 25%) of the day's range; same-time RVOL at close ≥ `swga_min_rvol` (default 2.0); market-confirmation score ≥ `swga_min_confirm` (default 60).
- Entry T+1 within window `swga_entry_window` (default 09:45–10:30 ET), only if the T+1 open gap versus T close is within ±`swga_max_entry_gap_atr` (default 1.0 × daily ATR) — otherwise skip (no chase).
- Initial stop: `min(T_low, entry − swga_stop_atr_mult × ATR14)` with `swga_stop_atr_mult` default 1.5, subject to EXE-05 floors.
- Exits: target `swga_target_r` (default 2.0 R) OR time exit after `swga_max_hold_sessions` (default 10) OR daily close below the `swga_trail_ma` (default 10-session EMA) after ≥ 1R open profit OR any RSK/CAL forced exit.

**SWG-07 — Family SWG-B, trend pullback (long, v1):**
- Context: 20-session MA rising over `swgb_ma_slope_lookback` (default 5) sessions; price above 50-session MA; no SWG-A-style fresh negative catalyst (catalyst engine consulted as a veto, not a driver).
- Setup: pullback touching the 20-session MA zone within `swgb_ma_zone_atr` (default 0.5 × ATR14) with contracting volume (`swgb_pullback_vol_ratio` default ≤ 0.8 vs 20-session average).
- Trigger: next session trades above prior session high; enter intraday on that break within 09:45–15:45, spread gate per EXE-04.
- Stop: below pullback low − `swgb_stop_atr_frac` (default 0.25) × ATR14. Exits: `swgb_target_r` (default 2.5 R), time exit `swgb_max_hold_sessions` (default 15), close below 20-session MA by > 0.5 × ATR.

### 8.4 Overnight risk (the defining difference from day trading)

**SWG-08 (MUST):** Gap-through-stop sizing: swing size MUST satisfy both the stop-based formula (EXE-05) **and** a gap-stress constraint: `shares × (gap_stress_atr_mult × ATR14) ≤ swing_gap_risk_budget_pct × equity`, defaults `gap_stress_atr_mult = 2.0`, `swing_gap_risk_budget_pct = 1.0%`. The smaller size governs. Rationale: overnight stops do not bound loss; sizing must assume the stop can be gapped through.

**SWG-09 (MUST):** Aggregate overnight limits: total swing notional ≤ ACC-07 overnight cap; sum of per-position gap-stress risk ≤ `swing_portfolio_gap_budget_pct` (default 3% of equity); at most `swing_max_per_sector` (default 2) positions per sector ETF mapping (FVZ-07).

**SWG-10 (MUST):** Day-trading buying power MUST NOT be used to justify swing size (ACC-06). The entry gate computes overnight buying power as if the position were held from that instant.

### 8.5 Swing order and stop mechanics

**SWG-11 (MUST):** Swing entries are DAY orders (limit or marketable-limit per strategy config `swg_entry_order_type`, default marketable limit at `entry_limit_offset_bps` default 10 bps through the touch). Unfilled entry orders are canceled at window end; partial fills ≥ `min_fill_ratio` (default 50%) keep the position with recomputed risk; below that, the remainder is canceled and the fill is exited same-day (journaled `EXIT: MIN_FILL_ABORT`).

**SWG-12 (MUST):** Primary protection is a **broker-resting GTC stop** (stop or stop-limit per `swg_stop_order_type`, default plain stop) placed immediately on fill and maintained through the hold — this is the EXE-09 invariant for swing. Stop adjustments (trailing per SWG-06/07) are replace operations (EXE-12), never cancel-then-place.

**SWG-13 (MUST):** Overnight/extended-hours events against a swing position: the engine monitors news 16:00–20:00 and 04:00–09:30 for held symbols. A fresh contrary catalyst with materiality ≥ `swg_news_exit_materiality` (default 0.7) → flag `EXIT_AT_OPEN_REVIEW`; the position is then exited in the next regular session within 09:35–10:00 unless the operator overrides (journaled). Automated extended-hours exits remain prohibited (EXE-11).

**SWG-14 (MUST):** Earnings-proximity exit: a swing position MUST NOT be held into the symbol's own next earnings release. Force exit no later than the close of the last session before the announcement window (`swg_earnings_buffer_sessions` default 1), exit reason `EARNINGS_PROXIMITY`. Combined with CAL-07 fail-closed rule for unknown dates.

**SWG-15 (MUST):** Swing exits count toward the PDT day-trade counter only when entry and exit occur in the same session (e.g., MIN_FILL_ABORT or same-day invalidation). The counter logic (ACC-02) MUST classify per-fill, not per-intent.

### 8.6 Swing research protocol

**SWG-16 (MUST):** Swing labels extend [BS §17.1]: post-signal returns at 1/3/5/10 sessions; overnight-gap distribution while held; probability of +1R before −1R at daily resolution; stop-gap frequency and magnitude; borrow/margin cost drag. Validation split, bias controls, and phase gates from [BS §17–18] apply unchanged; swing has its own Phase 0–3 progression independent of the day module.

---

## 9. DAY — Day trading module deltas (beyond Base Spec)

**DAY-01 (MUST):** Everything in [BS §5–11, §14, §19] remains binding for the day module, as amended by this addendum (feed, gates, sizing floors, PDT, calendar).

**DAY-02 (MUST):** MVP discovery is **event-anchored**: each evening the engine builds tomorrow's primary watch set from the earnings calendar (CAL-07) filtered to the universe — reporters after today's close and before tomorrow's open. Finviz screening continues in parallel as the capture path for [BS §8.5] cohorts 2–6 and as a cross-check that no calendar-known reporter was missed. Discrepancies (screened mover with earnings not on the calendar) are journaled as calendar-coverage defects.

**DAY-03 (MUST):** Day positions are always flattened by the CAL-02 end-of-day deadline; a day position can never convert to a swing position (`allow_day_to_swing_conversion = false`, not raisable in v1). Conversion would bypass SWG-08 sizing and SWG-14 earnings checks.

**DAY-04 (MUST):** The [BS §10] reject enum is extended system-wide with: `REJECT_PDT_LIMIT`, `REJECT_CORPORATE_ACTION`, `REJECT_UNKNOWN_EARNINGS_DATE`, `REJECT_DEGRADED_DATA`, `REJECT_RECONCILE_LOCK`, `REJECT_BUDGET_EXHAUSTED` (NWS-07), `REJECT_PARTICIPATION_CAP` (EXE-06).

---

## 10. PER — Persistence and research store

**PER-01 (MUST):** Three storage layers: (1) **append-only raw event log** — every inbound message (market data messages may be sampled per `raw_md_sampling` config, default: full for subscribed symbols) with receive timestamps; (2) **relational operational store** — candidates, catalyst results, gates, orders, fills, positions, risk events, reconciliations; (3) **research journal** — one row per decision (trade or reject) with full feature vector. All schemas versioned; every row carries `schema_version`, `config_hash` (CFG-02), `code_version` (git SHA), and `run_id`.

**PER-02 (MUST):** Raw-before-parse: Finviz CSVs (FVZ-03), news payloads (NWS-01), broker responses are archived byte-exact before interpretation. Reprocessing MUST be possible from raw archives alone.

**PER-03 (MUST):** Timestamps per [BS §13.2] plus: gate-evaluation timestamps (EXE-04), kill-switch fire/ack timestamps, reconciliation snapshots, overnight marks (SWG-02).

**PER-04 (MUST):** Component features, never only composites: every scored object persists all raw inputs and component values so any weight in [BS §8.2/9.4/10] and NWS-09 can be refit offline without re-collecting data.

**PER-05 (MUST):** Durability: operational store writes for order intents (EXE-01) are synchronous (fsync/committed) before the network call to the broker. Backups daily; restore procedure tested quarterly (TST-08). Retention: raw ≥ 24 months, operational and journal indefinitely.

**PER-06 (MUST):** All monetary values stored as integer micro-dollars or decimal types — binary floats for money are a defect. Prices/sizes preserve broker precision.

---

## 11. OPS — Operations, deployment, environments

**OPS-01 (MUST):** Process model: supervised long-running service (systemd or equivalent) with automatic restart, `Restart=on-failure`, start-limit backoff. Startup sequence, strictly ordered: load+validate config → clock check → calendar fetch → broker auth + ACC-12 → EXE-08 reconcile → data-stream connect + DATA-01 assert → self-tests (DATA-03, CAL-05) → then and only then arm trading. Any step failing → process exits nonzero, no partial arming.

**OPS-02 (MUST):** Environment separation: distinct hosts or at minimum distinct OS users + config trees for `paper` and `live`. `live` startup additionally requires the environment variable `TRADING_LIVE_CONFIRM=YES-I-UNDERSTAND` and logs a distinct banner. Paper and live MUST never share a database.

**OPS-03 (MUST):** Deploys to `live` only outside market hours (per CAL-01) unless an emergency fix, which requires the RSK-04 kill first. Every deploy records git SHA; the running SHA is exposed on the health endpoint.

**OPS-04 (MUST):** Secrets (Alpaca keys, Finviz token, LLM API key) live only in a secret store or root-protected environment files; never in the repo, config files under version control, logs, or journal rows. A CI secret-scan (TST-07) enforces this.

**OPS-05 (MUST):** Observability: a metrics endpoint exporting at minimum — stream connectivity + last-message age per feed, REST budget utilization, gate pass/reject counts by code, order latencies (submit→ack, ack→fill), reconcile status, open risk vs limits, PDT counter, catalyst pipeline latency + LLM budget burn, kill-switch states. Alert channels: `page` (immediate: kill switches, RECONCILE_MISMATCH, DATA_STALE with open positions, EXE-09 violation) and `notify` (contract breaches, latency, budget). Alert delivery MUST be tested weekly by a synthetic alert.

**OPS-06 (MUST):** Structured logging (JSON), correlation by `client_order_id` / candidate ID / article ID; log levels configurable; no secrets or full account numbers in logs.

**OPS-07 (MUST):** Runbook file in-repo covering: startup/shutdown, kill + re-arm (RSK-05), reconcile-mismatch resolution (EXE-08), stream-outage handling, broker-outage handling (positions protected by EXE-09 resting stops), and disaster recovery from backups (PER-05). The AUD audit verifies the runbook exists and matches implemented commands.

**OPS-08 (MUST):** Host time in UTC; timezone math only through the tz database (CAL-04). The host MUST run NTP with monitoring (DATA-15).

---

## 12. TST — Testing and CI gates

**TST-01 (MUST):** Unit coverage on all money-path math: sizing (EXE-05/06/07 including floors, caps, zero/negative guards), risk limits (RSK-01), PDT counting (ACC-02/03, SWG-15), gap adjustment (CAL-06), RVOL same-feed guard (DATA-03). Property-based tests for sizing (random inputs never produce size exceeding any cap or division errors).

**TST-02 (MUST):** Deterministic replay harness: the full pipeline (discovery → catalyst → confirmation → gates → simulated orders) MUST run against archived raw data (PER-02) and produce byte-identical journals for identical inputs + config. Replay determinism is a CI gate.

**TST-03 (MUST):** Simulated broker for integration tests: partial fills, rejects, replace failures, out-of-order trade updates, duplicate updates, reconnect gaps. EXE-01/02/03/08/09/12 behaviors each have at least one scripted scenario.

**TST-04 (MUST):** Chaos drills (paper profile, at least monthly, results journaled): market-data socket killed mid-position; order-update socket killed during a pending order; process SIGKILL with open position then restart-and-reconcile; broker 5xx storm; clock skew injection. Pass criterion: EXE-09 invariant never violated, no duplicate orders, reconcile converges.

**TST-05 (MUST):** Every kill switch (RSK-03) has an automated trigger test in the paper environment.

**TST-06 (MUST):** Backtest-integrity tests encode [BS §17.2] as executable checks where possible: e.g., assertion that no feature timestamp postdates its decision timestamp in any journal row (continuous, in production too); feed-tag uniformity per feature; first-seen (not updated) news timestamps in event studies.

**TST-07 (MUST):** CI gates on every merge: unit + property tests green; replay determinism (TST-02); secret scan; config↔Appendix-A bidirectional completeness check (CFG-04); reject-code enum synchronized between code and this spec (DAY-04).

**TST-08 (MUST):** Quarterly restore drill from backups into a scratch environment, verifying journal row counts and a sample of order chains.

---

## 13. AUD — Adherence audit protocol (for Claude Code)

**AUD-01 (MUST):** The repository MUST contain this file at `docs/spec/automated_trading_production_spec_v1.md` and the Base Spec alongside it. The audit treats both as the requirement source, with this file taking precedence (§0.4).

**AUD-02 (MUST):** Audit output format — one row per requirement ID (every ID in this document; none skipped):

| Field | Content |
|---|---|
| id | e.g., EXE-09 |
| status | COMPLIANT / PARTIAL / MISSING / NOT_APPLICABLE (N/A requires justification) |
| evidence | file:line references to implementing code, config entries, and tests |
| gap | precise description of what is absent or divergent, if not COMPLIANT |
| severity | BLOCKER (money/safety path: ACC, RSK, EXE-05..10, DATA-01..11, SWG-08..14) / MAJOR / MINOR |
| fix | concrete remediation step |

**AUD-03 (MUST):** The audit additionally performs: (a) CFG-04 bidirectional check — every Appendix-A parameter exists in config code and vice versa; (b) reject-code enum diff; (c) search for hard-coded thresholds, timezone offsets, URLs, and credentials; (d) verification that every kill switch has a test (TST-05); (e) verification that EXE-01 ID format is generated and persisted before submission in the actual call path.

**AUD-04:** Prompt to paste into Claude Code at the repo root:

```text
Read docs/spec/automated_trading_production_spec_v1.md and
docs/spec/automated_us_equities_trading_research_design.md in full.
Then audit this repository for adherence.

Rules:
1. Produce the AUD-02 table covering EVERY requirement ID in the
   addendum (ACC, DATA, CAL, FVZ, NWS, EXE, RSK, SWG, DAY, PER, OPS,
   TST, CFG, AUD). Do not skip or summarize IDs. Cite evidence as
   file:line. If you cannot find evidence, the status is MISSING —
   never assume compliance.
2. Perform the AUD-03 mechanical checks and report each result.
3. Then audit the Base Spec sections §4–§19 the same way for anything
   not superseded by the addendum.
4. Output: docs/audit/adherence_report_<date>.md containing the table,
   a BLOCKER summary at the top, and a remediation plan ordered by
   severity. Make no code changes during the audit run.
5. Treat fail-open behavior anywhere on the order path as a BLOCKER
   even if not tied to a specific ID.
```

---

## Appendix A — Configuration registry (canonical; CFG-01)

Units: s = seconds, ms = milliseconds, bps = basis points, pct = percent of account equity unless stated.

| Parameter | Type | Default | Range | Ref |
|---|---|---|---|---|
| account_mode | enum | margin | margin,cash | ACC-05 |
| pdt_equity_floor | usd | 25500 | ≥25000 | ACC-03 |
| pdt_recheck_interval_s | int | 300 | 60–900 | ACC-02 |
| max_gross_exposure_intraday_pct | pct | 100 | 10–200 | ACC-07 |
| max_gross_exposure_overnight_pct | pct | 75 | 10–100 | ACC-07 |
| allow_swing_shorts | bool | false | — | ACC-10 |
| asset_check_max_age_s | int | 3600 | 60–86400 | ACC-11 |
| expected_account_id | str | (required) | — | ACC-12 |
| allow_iex_fallback | bool | false | dev only | DATA-04 |
| rvol_lookback_sessions | int | 20 | 10–60 | DATA-03 |
| ws_backoff_initial_ms | int | 500 | 100–5000 | DATA-06 |
| ws_backoff_max_ms | int | 30000 | 5000–120000 | DATA-06 |
| ws_stale_after_s | int | 10 | 3–60 | DATA-07 |
| ws_stale_after_ext_s | int | 60 | 10–300 | DATA-07 |
| quote_max_age_ms | int | 2000 | 250–10000 | DATA-08 |
| ws_symbol_budget | int | 300 | 30–2000 | DATA-09 |
| snapshot_poll_interval_s | int | 30 | 5–300 | DATA-09 |
| rest_budget_per_min (md/trading) | int | 9000/180 | plan-bound | DATA-12 |
| data_latency_alert_ms | int | 1500 | 250–10000 | DATA-14 |
| clock_drift_max_ms | int | 250 | 50–1000 | DATA-15 |
| calendar_fetch_time | ET time | 03:30 | 00:00–06:00 | CAL-01 |
| min_session_hours_for_close_model | float | 6.0 | 3–6.5 | CAL-02 |
| eod_flatten_offset_min | int | 10 | 2–30 | CAL-02 |
| earnings_calendar_source | enum | finviz | finviz,other | CAL-07 |
| finviz_min_interval_s | int | 60 (per FVZ-06) | 30–600 | FVZ-02 |
| finviz_timeout_s | int | 20 | 5–60 | FVZ-02 |
| finviz_min_rows / finviz_max_rows | int | 1 / 5000 | — | FVZ-01 |
| dedup_method | enum | embedding_cosine | +jaccard | NWS-04 |
| dedup_similarity_threshold | float | 0.92 | 0.80–0.99 | NWS-04 |
| dedup_window_hours | int | 72 | 24–168 | NWS-04 |
| news_latency_alert_s | int | 90 | 10–600 | NWS-03 |
| catalyst_llm_model | str | (pinned ID, required) | — | NWS-05 |
| catalyst_llm_timeout_s | int | 8 | 2–30 | NWS-06 |
| catalyst_llm_max_articles_per_day | int | 500 | 50–5000 | NWS-07 |
| catalyst_llm_daily_cost_cap_usd | usd | 10 | 1–100 | NWS-07 |
| catalyst_pipeline_p95_s | int | 15 | 5–60 | NWS-08 |
| estimates_source | enum | none | none,licensed | NWS-09 |
| catalyst_min_category_acc | float | 0.85 | 0.7–1.0 | NWS-12 |
| catalyst_min_direction_acc | float | 0.90 | 0.8–1.0 | NWS-12 |
| contradiction_window_min | int | 120 | 30–480 | NWS-13 |
| ambiguity_cooloff_min | int | 240 | 60–1440 | NWS-13 |
| setup_max_age_s | int | 20 | 5–120 | EXE-04 |
| max_spread_bps | bps | per-strategy, default 20 | 2–100 | EXE-04 |
| max_expected_slippage_bps | bps | 15 | 2–100 | EXE-04 |
| min_stop_spread_mult | float | 4 | 2–10 | EXE-05 |
| min_stop_atr_frac | float | 0.25 | 0.1–1.0 | EXE-05 |
| max_notional_per_trade_pct | pct | 15 | 1–50 | EXE-06 |
| max_pct_adv | pct of ADV | 1 | 0.1–5 | EXE-06 |
| max_pct_recent_dollar_vol | pct | 5 | 1–20 | EXE-06 |
| reconcile_interval_s | int | 60 | 15–300 | EXE-08 |
| order_orphan_timeout_s | int | 30 | 10–120 | EXE-02 |
| order_poll_interval_s | int | 15 | 5–60 | EXE-03 |
| backstop_atr_mult | float | 1.5 | 1.0–3.0 | EXE-09 |
| allow_multi_strategy_same_symbol | bool | false (locked v1) | — | EXE-10 |
| allow_extended_hours_trading | bool | false | true/false | EXE-11 |
| shutdown_flatten (day/swing) | bool | true/false | — | EXE-13 |
| per_trade_risk_pct / _swing | pct | 0.5 / 0.5 | 0.1–2.0 | RSK-01 |
| max_daily_loss_pct | pct | 2 | 0.5–5 | RSK-01 |
| max_weekly_loss_pct | pct | 4 | 1–10 | RSK-01 |
| max_consecutive_losses | int | 3 | 2–10 | RSK-01 |
| max_positions_day / _swing | int | 1 / 3 | 1–20 | RSK-01 |
| news_stream_down_max_s | int | 120 | 30–600 | RSK-03 |
| max_order_rejects | int | 3 | 1–10 | RSK-03 |
| manual_kill_flatten | bool | true(day) | — | RSK-04 |
| equity_staleness_haircut_pct | pct | 5 | 1–20 | RSK-06 |
| swing_enabled_strategies | list | [SWG-A] | subset | SWG-05 |
| swga_min_catalyst | score | 70 | 50–95 | SWG-06 |
| swga_close_range_pct | pct of range | 25 | 10–50 | SWG-06 |
| swga_min_rvol | float | 2.0 | 1.2–5 | SWG-06 |
| swga_min_confirm | score | 60 | 40–90 | SWG-06 |
| swga_entry_window | ET window | 09:45–10:30 | RTH | SWG-06 |
| swga_max_entry_gap_atr | float | 1.0 | 0.25–2.0 | SWG-06 |
| swga_stop_atr_mult | float | 1.5 | 0.5–3.0 | SWG-06 |
| swga_target_r | float | 2.0 | 1.0–5.0 | SWG-06 |
| swga_max_hold_sessions | int | 10 | 2–20 | SWG-06 |
| swga_trail_ma | sessions | 10 (EMA) | 5–20 | SWG-06 |
| swgb_ma_slope_lookback | int | 5 | 3–10 | SWG-07 |
| swgb_ma_zone_atr | float | 0.5 | 0.1–1.0 | SWG-07 |
| swgb_pullback_vol_ratio | float | 0.8 | 0.5–1.0 | SWG-07 |
| swgb_stop_atr_frac | float | 0.25 | 0.1–1.0 | SWG-07 |
| swgb_target_r | float | 2.5 | 1.0–5.0 | SWG-07 |
| swgb_max_hold_sessions | int | 15 | 3–30 | SWG-07 |
| gap_stress_atr_mult | float | 2.0 | 1.0–4.0 | SWG-08 |
| swing_gap_risk_budget_pct | pct | 1.0 | 0.25–3.0 | SWG-08 |
| swing_portfolio_gap_budget_pct | pct | 3.0 | 1–10 | SWG-09 |
| swing_max_per_sector | int | 2 | 1–5 | SWG-09 |
| swg_entry_order_type | enum | marketable_limit | limit | SWG-11 |
| entry_limit_offset_bps | bps | 10 | 0–50 | SWG-11 |
| min_fill_ratio | pct of intended | 50 | 10–100 | SWG-11 |
| swg_stop_order_type | enum | stop | stop,stop_limit | SWG-12 |
| swg_news_exit_materiality | float | 0.7 | 0.5–1.0 | SWG-13 |
| swg_earnings_buffer_sessions | int | 1 | 1–3 | SWG-14 |
| allow_day_to_swing_conversion | bool | false (locked v1) | — | DAY-03 |
| raw_md_sampling | enum | full_subscribed | — | PER-01 |

Every parameter above is tunable only through config + restart (CFG-02); "locked v1" parameters are validated at load and refuse non-default values in the `live` profile.

---

## Appendix B — Phase gate summary (merged day + swing)

| Phase | Day module | Swing module | Gate to advance |
|---|---|---|---|
| 0 | Passive capture [BS §18] + FVZ-06 calibration + NWS-03 latency + NWS-11 labels + EXE-12 broker-behavior tests | Same capture; daily-bar cache build | Data-quality report; NWS-12 pass; contract validators green ≥ 10 sessions |
| 1 | Watchlists + reject lists, no orders | Swing signal lists, no orders | Selection metrics [BS §17.4] reviewed; TST-02 determinism green |
| 2 | Paper trading, 1 strategy | Paper trading, SWG-A only | TST-04/05 drills passed; ≥ 30 sessions paper with zero EXE-09/reconcile violations |
| 3 | Live, minimal risk, RSK defaults | Live SWG-A after day module stable ≥ 20 live sessions | Operator sign-off per module; separate evidence per strategy |
| 4 | [BS §18 Phase 4] expansions | SWG-B live; shorts (ACC-10) as its own phase | One dimension at a time, each with its own validation |

---

## Appendix C — Explicit non-goals (v1)

To leave nothing implied: v1 does NOT include options, futures, crypto, extended-hours execution, day↔swing conversion, multi-account support, sub-$2B market caps, short swing positions, intraday strategy stacking on one symbol, or any ML-fitted ranking weights (weights are fixed config until refit offline from PER-04 data through the [BS §17.3] split).

*End of addendum v1.0.*
