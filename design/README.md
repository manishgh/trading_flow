# TradingFlow UI — Design Handoff

Target repo: `c:\project\trading_flow`
Extract this folder as `c:\project\trading_flow\design`.

## What this is

Five screen designs for the TradingFlow operator UI, built as HTML prototypes:

| Prototype | Replaces / extends | Razor page today |
| --- | --- | --- |
| `prototypes/Trading Desk.dc.html` | Trade Desk, two layout options | `Pages/TradeDesk.cshtml` |
| `prototypes/Trading Desk Mobile.dc.html` | Phone desk (new) | `src/TradingFlow.Mobile` |
| `prototypes/Earnings.dc.html` | Earnings calendar | `Pages/Earnings.cshtml` |
| `prototypes/Backtest Lab.dc.html` | Quant Workstation + job view | `Pages/Backtests.cshtml`, `Pages/Job.cshtml` |
| `prototypes/Desk Operations.dc.html` | Positions, Orders, Wishlists, Health | `Pages/RunningTrades.cshtml`, `Pages/Orders.cshtml`, `Pages/Wishlists.cshtml`, `Pages/Paper.cshtml` |

## About the design files

**These are design references, not production code.** They are self-contained HTML
prototypes that show intended look and behaviour. They run on a small client-side
runtime with mock data and are not meant to be dropped into the app.

The task is to **recreate these designs inside TradingFlow's existing environment** —
ASP.NET Core Razor Pages, `wwwroot/css/site.css` tokens, and the vanilla-JS modules in
`wwwroot/js` — using the patterns already there:

- Server-rendered tables and forms; `hidden` attribute for show/hide (already forced to
  `display:none` in `site.css`).
- Sort and filter state carried in the query string so a view survives reload and can be
  shared (see `TradeDeskModel.AriaSortFor` / `NextDirectionFor`).
- Live patching by id from the existing JS modules (`trading-flow-desk.js`,
  `running-trades.js`, `orders.js`, `earnings.js`), not client-side re-rendering.
- `asp-page` / `asp-route-*` for every navigation.

Every screen here is already backed by real server models. Where a value does not exist
yet it is called out in `api-gaps.md` — read that before starting.

## Fidelity

**High fidelity.** Exact colours, type sizes, weights, tracking, spacing, borders and
row heights are specified per screen. Recreate pixel-for-pixel.

One deliberate change from the current app: these screens are on the **Modernist**
visual system (light ground, single red accent, Archivo, zero corner radius, 2px section
rules), not the current dark `site.css` scheme. See `tokens/README-tokens.md` — it maps
every Modernist token onto the existing `site.css` custom-property names so the migration
is a token swap rather than a rewrite, and so the light/dark/CVD scheme switch keeps
working.

## Read in this order

0. `screenshots/README.md` — twelve captures of the screens, with a note on what to look
   for in each. Fastest way to see what you are building.
1. `tokens/README-tokens.md` — colours, type, spacing, and the gain/loss rule.
2. `api-gaps.md` — what the backend does not expose yet. Some screens are blocked on it.
3. `screens/01-trading-desk.md`
4. `screens/02-earnings.md`
5. `screens/03-backtest-lab.md`
6. `screens/04-operations.md`
7. `screens/05-mobile-desk.md`

## Non-negotiables carried over from the repo

These are stated in `docs/operating-boundaries.md` and must survive the redesign:

- **Fail closed.** A missing or unreadable value renders as unknown, never as a guess. A
  locked environment renders **no** control at all rather than a disabled one the client
  could re-enable (`RunningTrades.cshtml` already does this).
- **Eligibility is authoritative.** Market Predictor output is read-only evidence. The new
  side-by-side treatment must not imply the model can authorise an entry.
- **Server revalidates.** Every ticket is re-checked for quote, spread, session, account,
  exposure and duplicates at submit. The client checklist is a preview of that, not a
  substitute.
- **Advisory surfaces cannot route.** The earnings monitor states this on screen.
- **Direction is never colour alone.** Every gain/loss carries a sign and an arrow glyph
  as well as a colour, so the CVD scheme stays readable.

## Assets

- Font: **Archivo** 400/500/600/700/800, Google Fonts. Self-host under
  `wwwroot/fonts` for an offline-capable local UI.
- Icons: **Lucide** (https://lucide.dev), inlined as SVG on `currentColor`,
  `stroke-width="2"`, round caps and joins. Icons used are listed per screen.
- No images or photography anywhere in these screens.
