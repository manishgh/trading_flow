# Volatile: existing swing strategies, six months, independent cohorts

Status: user requested this exact research execution on 21 October 2026. The user explicitly approved both workflow repairs and the run. Both approved repairs are implemented and verified. The actual saved plan (ID 2) completed all ten backtests through the normal API, sequentially, with trading/feeds paused. Three books have complete native-daily economics; seven explicitly withhold monthly returns. Independent quant review reconciled all ten result/monthly hashes, 78 published artifacts and all 70 monthly rows. Final runner evidence and monthly report are under `outputs/volatile-swing-20261021/`.

## Frozen research choices

Use the current operator database `C:/project/trading_flow/data/tradingflow.db`, wishlist `volatile`, ID `220939C3-1E91-4C4E-9BCA-5D61F9837EBD`. The read-only capture found 25 active members, including SOXL. Exclude SOXL from this plan only; do not modify the wishlist. Membership is observed today and does not prove historical membership.

Run the two current research catalog strategies unchanged: `swing.minervini-vcp` version `10.0.0` and `swing.connors-rsi2` version `6.0.0`, both on `causal-market-exits-v3`. Capture their complete canonical content hashes through the existing catalog API. Do not select archived versions, optimize parameters or promote a strategy.

Use six calendar months: 21 April 2026 00:00 New York through 21 October 2026 00:00 New York (UTC `2026-04-21T04:00:00Z` through `2026-10-21T04:00:00Z`, end exclusive). This omits today's incomplete session. April and October are partial reporting months. The existing 400-day warmup precedes the evaluation window; required benchmark overfetch remains separate.

| Cohort | Stocks | Starting capital for EACH strategy |
|---|---|---|
| Group 1 | ASTS, BKSY, BXDC, CRCL, CRDO | $10,000 |
| Group 2 | DASH, DIOD, FPS, MIAX, MRVL | $10,000 |
| Group 3 | MVST, MXL, NBIS, NVTS, OUST | $10,000 |
| Group 4 | PATH, PGY, POET, RDW, RGTI | $10,000 |
| Group 5 | RKLB, SIVE, SMCI, STM | $10,000 |

This yields ten independent portfolios. The final cohort has four stocks because excluding SOXL leaves 24. Five-stock grouping is separate from position/risk limits. Preserve the existing swing profile: SIP daily/hourly data, 1% account-risk budget, 25% maximum position notional, four concurrent positions, 5% bar participation, $1 each buy/sell plus configured regulatory fees, and existing slippage. Preserve existing strategy/news settings; do not silently add drawdown suitability or news protection. The reviewer found neither selected strategy enables drawdown qualification or a news veto.

Use the shared prepared input selection and existing durable workflow. Reuse eligible versions; capture missing data through configured Alpaca adapters, retain originals/receipts/committed versions, and fail explicitly on unavailable/invalid records. Historical candle coverage has now been queried and archived for every original selected symbol. SIVE returned no bars; asset-identity metadata was not queried, so its current identity/listing status is not asserted. Do not rename or replace stocks silently. No credentials were printed. The configured Alpaca SIP candle collection succeeded; original receipts and pagination are retained without exposing credentials.

The independent quant reviewer confirmed grouping, date conversion and unchanged strategy controls. Retrieval of 400 calendar days does not prove 260 genuine completed pre-window daily observations: after provider checks, missing history/new listings/unknown symbols must be reported exactly. The current runner suppresses book economics when any strategy/ticker batch fails. Existing v3 strategies also have no forced study-end liquidation; an entry without a later causal exit can leave economics incomplete and monthly values unavailable. Do not silently extend the study, substitute symbols, fabricate bars or add terminal exits to obtain a number.

Report monthly rows separately for every canonical strategy/version/hash and cohort, using each month's opening equity. Include closed-trade count, costs and drawdown with their valuation basis and partial-month labels. The existing native daily profile reports realized closed-trade equity; its drawdown omits unrealized/intraday moves. Do not present pooled or accumulated-period returns. Preserve complete final outcomes and immutable parameters, including failures, and clean only private run copies. Independent quant review must inspect actual source/config/data identities and reconcile results; this remains diagnostic research on today's chosen list.

## Two small workflow repairs proposed for approval

1. **Exclude a stock from this comparison without editing its wishlist.** Today `UniverseSourceResolver` captures all 25 Volatile members, automatic groups include every member, and manual groups reject omitted members. Add optional `ExcludedSymbols` to the existing saved-plan request and response. Validate exclusions against the captured source, filter only the selected run membership, retain the original source/hash and explicit exclusion in the immutable saved plan, then reuse normal grouping/admission. Web and Android show the same optional `Exclude stocks` field and the resulting groups. Existing choices are unchanged when empty. Reject unknown exclusions, empty selection and groups containing an excluded stock. Do not create another wishlist or alter source evidence.
2. **Run historical backtests while trading stays paused.** No TradingFlow Web/ngrok host is currently running. The existing paused-host worker only accepts stock-assessment jobs, so it cannot execute these backtests. Add `backtest` to its existing paused-mode allowed job types; retain the same worker, resource limits, leases and cancellation. Paper/live, broker dispatch/recovery and market feeds remain paused. Add no new scheduler, execution driver or host setting. The run uses the latest checkout with the explicitly configured existing operator data root and configured host credentials; no secret file copying or operator-source code overwrite.

Affected boundaries: existing `BacktestPlanContracts`, `BacktestPlanService`, Web backtest workflow script/view, Android Backtests page/shared transport, and `Program.cs` paused allowed job types. Data changes are the ordinary saved plan, source observations, ten durable jobs, protected provider/prepared data and final results. No wishlist mutation, schema migration, strategy change, deployment or trading authorization is proposed.

Focused verification: plan with 25 source members and exclusion SOXL yields the five groups above/ten books; saved/reopened plan retains exclusion and original source; invalid/mixed manual choices fail without partial jobs. API/Web/Android serialize/show the same choices. Paused-mode worker executes a synthetic backtest while paper/order handlers stay unavailable. Run existing affected tests, browser workflow and Android compiler checks. Obtain independent engineering review for repair and quant review for the real research run. Compile and heavyweight checks stay sequential.

Expected work: a bounded contract/selection fix plus one paused-worker allow-list correction, then collection and ten real backtests. No reliable run-duration prediction is available before one representative real book completes; report measured preparation and execution time before forecasting the remainder.

## Checklist

- [x] Read-only operator wishlist and configured credential presence checked; no secrets displayed.
- [x] Existing strategies/profile and independent quant source review inspected.
- [x] Confirm current source includes SOXL and existing plan cannot exclude it.
- [x] Confirm no active host and paused-mode backtest worker restriction.
- [x] User approved both concrete workflow repairs and the run: “Approve both fixes and run”.
- [x] Implement and verify API/Web/Android exclusion plus paused backtest execution: 53 focused .NET cases, 12 browser scenarios and Android Release (zero warnings/errors). Independent code/design reviews found no remaining issue. A discovered cancellation-polling race was repaired and its stronger drain check passed.
- [x] Capture immutable source, exact strategies/settings, cohorts and shared data request. Saved plan 2 and accepted jobs are retained under `outputs/volatile-swing-20261021/`; independent quant review confirmed actual source/hash, dates, exact groups, capital and profile hash. Original Volatile retains SOXL.
- [x] Prepare provider-backed evidence and measure first representative book. Independent audit checked 54 prepared versions, 620 HTTP-200 original responses and all 153,389 returned candle rows. Source preparation took 6m29s; all ten sequential jobs finished in 10m25s. Incomplete 21 October daily bars are filtered before evaluation.
- [x] Complete ten portfolios and preserve all outcomes. Three complete books: Connors Group 4 (eight closed trades), Minervini Groups 3 and 4 (no trades). Seven books have unavailable monthly economics, with exact history/SIVE/unfinished-candidate reasons retained. No stock substitutions or changes to the two strategies.
- [x] Independent quant reconciles strategy/cohort monthly results and execution/data limitations. All 21 available rows reconcile; 49 null rows remain correctly unavailable. The eight Connors Group 4 trades reconcile to source opens, 10bps slippage, 16 fills, costs and risk/capital limits. Completed-daily availability stamps explain source-session month attribution. Five unfinished candidate hypotheses, not proved admitted positions, explain the three Connors boundary-affected books.
- [x] Publish monthly report and clean only private intermediates. See `outputs/volatile-swing-20261021/monthly-results.md`, `outcome-evidence.json`, `independent-quant-review.md` and audit JSON files. All ten result-owner directories have no private run-input spools; the task-owned encrypted API session was deleted. Original provider/prepared data, operator wishlists and all final outcomes remain retained. Web/ngrok remain available with trading/feeds paused.
