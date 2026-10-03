# UI Desk Handoff: Desk, Orders Screen and Follow-up

Date: 2026-10-03. Audience: the next agent (local Claude Code, ChatGPT/Codex or a
developer) picking up the web UI work in `C:\project\trading_flow`. Read this file,
then `AGENTS.md`, then `docs/ui-desk-universe-plan.md` (the design and verification
record with research sources) before changing anything.

## 1. Where the code is

| What | Value |
|---|---|
| Repository | `https://github.com/manishgh/trading_flow` |
| Implementation | on `main` at `cbc8116`, also on branch `claude/sharp-johnson-qd88ey` |
| Base before this work | `93bd5a5` (Record successful main publication) |
| This handoff file | on `main` and `claude/sharp-johnson-qd88ey`, in docs-only commits on top of `cbc8116` |

The three implementation commits, oldest first:

| Commit | Scope |
|---|---|
| `0720173` | Desk: every wishlist by default, one wishlist, or a Finviz screener as the universe; portfolio cell; preparation band; symbol-set live streams |
| `c41f3f4` | Orders screen as a subset of the desk: journal plus the desk's quote, position and reviewed ticket in a rail |
| `cbc8116` | Ticket prices from strategy rules (nothing fixed), Change / Change %, phone universe picker, 8 more Finviz signals, Save as preset, Finviz `f=` fix |

All other GitHub branches (`unified-swing-product`, `codex/architecture-optimization`)
are fully contained in `main`; nothing on GitHub is missing from it.

## 2. Your local checkout: reconcile first, never reset

The user said they merged changes into their local `main` before this UI work
started. No such merge reached GitHub: GitHub `main` went from `93bd5a5` to
`cbc8116` only through the three commits above. Earlier records
(`docs/integration/market-predictor-handoff.md`) also say the original checkout
was on `unified-swing-product` with substantial uncommitted work and a running Web
process. So the local checkout may hold work that is on no remote.

Before any change:

```powershell
git status
git fetch origin
git rev-list --left-right --count main...origin/main   # local-only / remote-only commits
git log --oneline origin/main..main                     # commits only on your machine
git log --oneline main..origin/main                     # 0720173, c41f3f4, cbc8116 and the handoff doc commits
git stash list
```

Rules, from `AGENTS.md` and the user:

- Never reset, stash-drop, or force-checkout over local work. Preserve
  `appsettings.local.json`, `tradingflow.db`, paper state and cached candles.
- Stop `TradingFlow.Web`, `TradingFlow.WarmupService` and other project processes
  before building.
- If local `main` has commits not on GitHub, first back them up to a side branch
  (`git push origin main:local-main-merged`, only if the user agrees to the push),
  then merge `origin/main` into local `main` with a merge commit. Expect conflicts
  in `Pages/TradeDesk.cshtml(.cs)`, `Pages/Orders.cshtml(.cs)`,
  `Pages/Shared/_DeskOrderTicket.cshtml`, `Pages/Shared/DeskPartialModels.cs`,
  `Program.cs` and `wwwroot/js/trading-flow-desk.js`. Keep both sides' behaviour;
  ask the user when both changed the same logic.
- Do not sync GitHub unless the user asks.

## 3. What was built

### Desk (`/TradeDesk`)

- **Universe** (`Services/Wishlists/DeskUniverse.cs`): resolved before any quote is
  read. `Wishlists` (default **All wishlists**, one row per symbol naming its lists,
  plus held positions on no list) or one wishlist; `Screener` (saved presets or the
  Finviz signal catalogue, or a custom URL/query) where the hits are the rows.
- **Finviz catalogue** (`TradingFlow.Finviz/FinvizSignalCatalog.cs`): 33 built-in
  signals, each confirmed against a live Finviz page title for that exact `s=` code.
  Input form `signal:<code>`. **Save as preset** on the custom query saves a screen
  by name (Finviz cannot list browser-saved screens).
- **Portfolio**: strip cell over every open position (value, cost, P/L, %); grid
  splits Position (shares @ avg, cost, value) from P/L; rail shows the position.
- **Change / Change %**: against the previous session close from Alpaca's batched
  `/v2/stocks/snapshots` (`AlpacaQuoteService.GetPreviousClosesAsync`, cached per
  symbol per New York day); recomputed by `trading-flow-desk.js` on every live quote;
  optional sortable `Chg %` column.
- **Preparation** (`Services/DeskPreparationService.cs`): reads the warmup service
  (`WarmupService:BaseUrl`, default `http://127.0.0.1:53120`) with a 2 s budget; shows
  Ready / Queued / Stale (>24 h) / Failed / Not prepared per symbol; Prepare queues
  the ones that need it. "Preparation" was interpreted as the Warmup feature; the
  user has not contradicted that.
- **Live streams**: `GET /api/v1/desk/quotes/stream?tickers=` and
  `/api/v1/desk/activity/stream?tickers=` (authenticated, symbols validated, max
  300). The wishlist-id streams used by Android are unchanged.
- **Phone**: at the list level the universe switch, list/screener choice, strategy,
  search, screener band and preparation band show as 44 px targets.

### Orders (`/Orders`)

- Journal kept (live polling by `orders.js`, filters, sort, cancel) plus the desk's
  account cells, a **New order** symbol box, a live Last column, and a rail with the
  symbol's quote, lists, position, recent orders, a strategy picker and the same
  two-stage reviewed ticket (`_DeskOrderTicket`). Replace opens the ticket prefilled
  (`ticker`, `side`, `limitPrice`). `/OrderTicket` still exists but nothing links to it.

### Ticket prices: nothing fixed (user requirement)

`Services/DeskTicketPlanService.cs` derives the opening limit, stop and target with
the engine's own code: `SignalGenerator.CreateTradeSignal` (setup timeframe),
`StrategyInitialStopResolver` (execution timeframe) and the `StrategyOrderPlanner`
target rule (R multiple, or VWAP mode), on live state from
`StreamingMarketStateProcessor` warmed to the paper profile's `IndicatorWarmupBars`.
Anything it cannot derive stays **empty with the reason under the field**. Symbols
the processor does not already track are never queried (a query would register a
pipeline that only explicit eviction removes). The 1R preset sizes to equity ×
`AccountRiskBudgetPct`, disabled when either is unknown. Do not reintroduce any
fallback percentage. The quantity still opens at 1 share; the user has not ruled on it.

### Fix worth knowing

Saved presets, pasted Finviz URLs and `f=` strings reached Finviz as a bare filter
list (`/export?cap_smallover,...`) without `f=`. `FinvizClient` now sends a bare list
as `f=`; pasted URLs keep their `s=` signal. This also affects the Wishlists screen
and screener verification. It is reasoned from code and unit-tested, not yet
confirmed against live Finviz.

### Small behaviour changes outside the new screens

- Wishlist activity streams and single-wishlist desk snapshots now only return
  signals for active symbols in the list.
- Two existing CSS load-order bugs fixed: the phone summary strip and the phone
  floating ticket button no longer show on desktop.

## 4. Verification status

Tier: component. Run in a Linux cloud container (no broker, no Alpaca, Finviz or
warmup service; network to those hosts was blocked). Nothing was confirmed, and no
order was sent anywhere.

| Check | Result |
|---|---|
| Solution build | 0 warnings, 0 errors |
| Full .NET suite | 1,655 passed, 39 failed |
| The 39 failures | identical set on the unchanged base `93bd5a5` in the same container: `SharedNewsPublicationConsumerTests` (24), `EvidenceDatasetCollectionRequestTests` (8), `SharedNewsImportCommandTests` (3), `EvidenceCollectionRequestAdapterTests` (2), `ConfigCatalogServiceTests` (1), `DurableFileJobRequestTests` (1). Likely Linux-specific; not investigated; may pass on Windows |
| Playwright UI suite | 91 of 91, on a fresh UI data root and again on the persisted one |

The cloud run used .NET SDK 10.0.112 with `global.json` lowered locally (not
committed; the repo pins 10.0.300), and a local Playwright config pointing at a
pre-installed Chromium (not committed). On Windows use the normal setup.

Commands (stop project processes first):

```powershell
dotnet build TradingFlow.slnx
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --filter "FullyQualifiedName~DeskUniverseTests|FullyQualifiedName~DeskTicketPlanTests|FullyQualifiedName~AlpacaPreviousCloseTests|FullyQualifiedName~AlpacaQuoteServiceTests|FullyQualifiedName~FinvizClientTests|FullyQualifiedName~ScreenerPresetQueryTests|FullyQualifiedName~UniverseRankServiceTests|FullyQualifiedName~Wishlist|FullyQualifiedName~Screener"
cd tests/ui; npm ci; npx playwright test
```

`tests/ui` starts its own app on port 53018 in `TRADINGFLOW_UI_TEST_MODE` with an
isolated data root under `.tmp/ui-tests`. One existing Wishlists test ("the management
table no longer renders one form per row") depends on that persisted data; delete
`.tmp/ui-tests` if it fails after manual seeding.

New test files: `DeskUniverseTests.cs`, `DeskTicketPlanTests.cs`,
`AlpacaPreviousCloseTests.cs` (plus additions to `AlpacaQuoteServiceTests.cs` and
`FinvizClientTests.cs`); UI specs `desk-universe.spec.js`, `orders-screen.spec.js`,
`desk-ticket-change-preset.spec.js`.

## 5. Not verified against live services: do this next

Needs the local Alpaca paper keys (`ALPACA_KEY_ID` / `ALPACA_SECRET_KEY` or
`Alpaca:KeyId` / `Alpaca:SecretKey`), `FINVIZ_API_KEY`, and the warmup service.
Paper only; do not place orders just to satisfy a check.

1. **Change %**: compare a few desk rows with the broker's previous close, before
   the open, in the session and after hours. The snapshot response shape was taken
   from Alpaca's published examples; the parser accepts a symbol-keyed body or a
   `snapshots` wrapper. If it differs, fix `AlpacaQuoteService.ParsePreviousCloses`.
2. **Ticket plan**: for a streamed, warmed symbol with a strategy selected, check the
   stop and target against the strategy's `exit_rules`; for an unstreamed symbol,
   confirm the "not streamed" note. Check 1R against equity × risk budget.
3. **Finviz**: run a catalogue signal, a pasted Elite URL with `f=` and `s=`, and a
   saved preset; confirm hit counts match the Finviz website. This confirms the
   `f=` fix.
4. **Preparation**: with the warmup service running, prepare a symbol and watch it
   move from Queued to Ready.
5. **Orders**: open a ticket from the journal, Replace a working order, cancel one.
6. **Phone**: 390 px list view; choose a wishlist and a screener.

Record results here, with the tier, commands and anything not run (`AGENTS.md`
Risk-Based Verification).

## 6. Open items

- Volume / RVOL column: indicator state is not kept for watching rows (see
  `WishlistDeskRow` remarks).
- Listing screens saved in the Finviz browser UI: no Finviz endpoint exists.
- Quantity opens at 1 share: ask the user whether it should be risk-sized.
- In Screener scope every desk load reads Finviz (and archives the raw CSV), as
  the pre-existing screener sync did; consider caching if Finviz rate limits bite.
- Android: no changes were made to `TradingFlow.Mobile`.

## 7. Working conventions

- Line endings are mixed per file. CRLF (wholly or mostly): `TradeDesk.cshtml`,
  `TradeDesk.cshtml.cs`, `Orders.cshtml.cs`, `trading-flow-desk.js`, and the UI specs
  `responsive-baseline`, `trade-desk-workstation` and `trading-capabilities`. The
  rest, including every file this work added, use LF. Preserve each file's style
  so diffs stay readable.
- Predictor output stays advisory; rows never submit orders; tickets keep server
  review before confirm; the live environment stays locked.
- Keep `docs/ui-desk-universe-plan.md` as the design record and this file as the
  current-state handoff; update both when behaviour changes.
