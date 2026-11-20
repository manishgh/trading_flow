# Design: Alpaca Paid OPRA Options Sentiment Context — 20 November 2026

Status: **approved by the user on 20 November 2026.**

Verified on the operator's paid Alpaca account (`2026-11-20`):
- `GET https://data.alpaca.markets/v1beta1/options/snapshots/{symbol}?feed=opra` returns live OPRA bid/ask quotes (`bp`, `ap`), daily/minute bar volume, `impliedVolatility`, and full Greeks (`delta`, `gamma`, `rho`, `theta`, `vega`).
- `GET /v2/options/contracts?status=inactive` and `GET /v1beta1/options/bars` return historical expired OPRA contracts and bars (`Feb 2024+`).

## 1. Scope & Operating Boundary

Use Alpaca's paid **OPRA option chain** strictly as **read-only equity sentiment & volatility context** for trading US stocks (TradingFlow never trades option contracts themselves, preserving `docs/operating-boundaries.md`). All 10 existing `v1` strategy catalog hashes remain byte-for-byte unchanged.

### Four Trader Metrics Computed per Symbol (`OptionsSentimentCalculator`)
Filtering to contracts within `[0.80 * Spot, 1.20 * Spot]` and `2–60` calendar days to expiry whose bid-ask spread is `<= 35%` of mid-price (`mid > 0` and `(ask - bid) / mid <= 0.35`):

1. **Front Implied Move % (`FrontImpliedMovePct`):** `(ATM_Call_Mid + ATM_Put_Mid) / Spot * 100` on the nearest liquid expiry (`5–14` calendar DTE, falling back to nearest `2–30` DTE when no weekly exists).
2. **25-Delta Put–Call Skew (`Skew25Delta`):** `IV(Put, delta ~ -0.25) - IV(Call, delta ~ +0.25)` on the nearest liquid `20–45` DTE expiry (requiring `|abs(delta) - 0.25| <= 0.12`).
   - `> +0.06` (`+6` vol pts): `put_skew_hedging` (heavy institutional downside protection — caution on mean-reversion longs).
   - `< -0.04` (`-4` vol pts): `call_skew_squeeze_risk` (aggressive call demand — caution on breakdown/parabolic shorts).
3. **IV Term Structure (`TermStructureSpread`):** `ATM_IV(7–21 DTE) - ATM_IV(30–60 DTE)`.
   - `> 0`: `term_backwardation_stress` (near-term event/macro stress exceeding 1–2 month baseline).
4. **Liquid Call/Put Volume & Daily Implied/ADR Ratio (`CallPutVolumeRatio`, `ImpliedToRealizedAdrRatio`):** total daily volume of liquid calls vs puts, plus `(FrontImpliedMovePct / sqrt(max(1, TradingDaysToFrontExpiry))) / Adr20Pct` when `Adr20Pct` is supplied.

## 2. Architecture & Files

1. **Domain & Pure Engine (`TradingFlow.Domain` + `TradingFlow.Engine`):**
   - `src/TradingFlow.Domain/Market/OptionsSentimentSnapshot.cs`: `OptionContractQuote`, `OptionsSentimentSnapshot`, and `OptionsSentimentRegime` constants.
   - `src/TradingFlow.Engine/Abstractions/IOptionsSentimentSource.cs`: async lookup interface `GetSnapshotAsync(string symbol, decimal? spotPriceHint, decimal? adr20Pct = null, CancellationToken cancellationToken = default)`.
   - `src/TradingFlow.Engine/Market/OptionsSentimentCalculator.cs`: pure deterministic calculator over `IReadOnlyList<OptionContractQuote>` (parses OCC option symbol expiry/type/strike when needed, filters wide/zero quotes, computes ATM straddle implied move, 25-delta skew, term structure, volume ratio, and trader summary badge).
   - Optional config-driven Paper/Desk vetoes (`[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` on `EntryRules` & `TradeSignal`, wired through `SimpleYamlReader`, `RunConfigWriter`, `SignalGenerator`, `BasicStrategyEvaluator`, `LiveRunner`, and `StockStrategyEvidenceLoader` when configured — unset on all `v1` strategies so hashes stay identical):
     - `max_long_put_skew` (`decimal?`): refuses long entry (`options_put_skew_above_maximum`, fail-closed when configured and options snapshot/skew is unavailable) if `Skew25Delta > max_long_put_skew`.
     - `min_short_put_call_skew` (`decimal?`): refuses short entry (`options_call_skew_squeeze_risk`, fail-closed when configured and options snapshot/skew is unavailable) if `Skew25Delta < min_short_put_call_skew` (e.g. `-0.04`).
2. **Alpaca OPRA Adapter (`TradingFlow.Alpaca`):**
   - `src/TradingFlow.Alpaca/AlpacaOptionsSnapshotClient.cs`: implements `IOptionsSentimentSource` using `GET /v1beta1/options/snapshots/{symbol}?feed=opra&expiration_date_gte=...&expiration_date_lte=...&strike_price_gte=...&strike_price_lte=...&limit=1000` (up to 3 pages via `next_page_token`, 5-minute in-memory cache per symbol, raw receipt archiving via `IRawArchiveWriter` when configured, structured warning logging on HTTP/parse error). If `spotPriceHint` is null, fetches latest stock trade/bar or derives midpoint parity from near-money contracts.
3. **Desk API & Thin UI (`TradingFlow.Web`):**
   - `src/TradingFlow.Web/DeskOptionsEndpoints.cs` (`GET /api/v1/desk/options-sentiment`): accepts `symbol` (optional selected stock) and returns `SPY`, `QQQ`, and the selected stock's `OptionsSentimentSnapshot` (reading spot hint from `DeskReadService` / Alpaca). Registered in `Program.cs` and mapped under `/api/v1` (authenticated).
   - `src/TradingFlow.Web/Pages/Shared/_OptionsSentimentPanel.cshtml` + `src/TradingFlow.Web/wwwroot/js/desk-options.js` included on `src/TradingFlow.Web/Pages/Desk.cshtml`: renders a compact **OPRA Options Pulse** bar (`SPY` & `QQQ` implied weekly move, `25Δ` skew, term structure) plus the **Selected Stock Options Context** readout when inspecting a symbol on the Desk (`window.tradingFlowFetch`, `pagehide` abort cleanup).

## 3. Verification Log
- **Build & Syntax Checks (20 Nov 2026):**
  - `dotnet build TradingFlow.sln -c Debug --no-restore -m:1 /nr:false -p:UseSharedCompilation=false -tl:off`: `0 Warning(s), 0 Error(s)` across all 18 projects.
  - `node --check src/TradingFlow.Web/wwwroot/js/desk-options.js` and `watch-desk.js`: exit `0`.
  - `git -c core.whitespace=cr-at-eol diff --check`: exit `0`.
- **Round 1 Independent Review Findings (`#159` `code-reviewer`, `#160` `plan-reviewer`) & Round 2 Fixes:**
  1. **Dense-chain (`SPY`/`QQQ`/mega-cap) OPRA pagination truncation & bounded memory (`AlpacaOptionsSnapshotClient`):** added targeted `20–45` DTE skew (`[0.80, 1.20]`, up to 2 pages) and `30–60` DTE tight ATM (`[0.95, 1.05]`, 1 page) follow-ups only when the primary 1–3 page fetch truncates with a remaining `next_page_token`, deduplicated by `ContractSymbol` with truncation warning logs; replaced unbounded per-symbol locks with a fixed 64-stripe `SemaphoreSlim[]` array and expired-entry cache sweep past 512 entries.
  2. **Shared-brain parity & Desk concurrency (`MobileAutomationService`, `StockStrategyEvidenceLoader`):** wired `IOptionsSentimentSource?` into `MobileAutomationService` (`MobileAutomationService.EntryPreparation.cs`) with fail-closed skew handling and warning logs; updated `StockStrategyEvidenceLoader.LoadOptionsSkewAsync` to `Parallel.ForEachAsync` (`MaxDegreeOfParallelism = 4`) with structured failure warnings.
  3. **Pre-flight refusal & direction validation (`WorkflowRunConfiguration`, `SignalStudyOutcomes`, `SimpleYamlReader`):** rejected live-only OPRA skew vetoes upfront in `WorkflowRunConfiguration.ResolveEffectiveProfileAsync` (before plan save / trial admission) and `SignalStudyOutcomes.RequireSupported`, and rejected direction-mismatched skew YAML keys in `SimpleYamlReader.ValidateOptionsSkewVetoes`.
  4. **Share-class OCC symbology, V1 error envelope, & script order (`OptionsSentimentCalculator`, `DeskOptionsEndpoints`, `desk-options.js`, `Desk.cshtml`):** supported `BRK.B`/`BF-B` <-> `BRKB`/`BFB` via `NormalizeOccRoot` while excluding foreign underlying roots and adjusted deliverables (`NVDA1`), returned `ApiErrorResponse` on invalid symbols, and removed `defer` on `desk-options.js`.
- **Automated Tests (Round 2):** `78/78` focused offline tests passed (`OptionsSentimentTests` 12/12 including dense-chain `SPY` follow-up + `ProductionCompositionTests` + `BacktestPlan` + `SignalStudy` + `StrategyArtifactCatalogTests`), confirming all 10 existing `v1` swing strategy catalog SHA-256 hashes remain byte-for-byte unchanged.
- **Independent Reviewer Sign-Off (Round 2):**
  - `plan-reviewer` (`#164`): **ACCEPT**
  - `code-reviewer` (`#165`): **ACCEPT**
