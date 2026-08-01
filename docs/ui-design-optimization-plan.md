# TradingFlow UI Accessibility And Trading-Desk Redesign Plan

Status: implemented through UI6; re-audited 2026-08-01

Audit date: 2026-07-22

Implementation evidence is maintained in `docs/ui-verification-matrix.md`. Automated completion
does not replace the physical-device TalkBack, Android Accessibility Scanner, or 200% font-scale
release gates documented there.

Applies to:

- `src/TradingFlow.Web` - ASP.NET Core Razor workstation and responsive web UI
- `src/TradingFlow.Mobile` - .NET MAUI Android application
- `market-predictor` API integration as read-only prediction intelligence

This document replaces the earlier UI optimization plan. The previous plan focused on visual tokens and code extraction but excluded workflow changes. The current audit found that responsive behavior, mobile scrolling, action meaning, order review, data freshness, and accessibility must be corrected together. Visual polish alone would leave unsafe and inaccessible interaction paths in place.

## 1. Product Boundary

TradingFlow is the user-facing trading workstation. It owns:

- watchlists and candidate triage
- technical strategy decisions
- alerts and activity
- risk and order validation
- paper/live environment state
- order preview, submission, reconciliation, and execution state

Market Predictor remains prediction-only. It owns:

- swing and intraday model inference
- model and data readiness
- opportunity and downside probabilities
- catalyst confirmation or conflict
- global-context impact
- SPY/QQQ comparison
- immutable prediction evidence

The UI must not blur these boundaries. A model opinion is not an executable trading instruction.

Every symbol detail view must present two separate blocks:

1. **Model intelligence** - what Market Predictor estimates and whether that estimate is ready.
2. **TradingFlow decision** - whether the configured strategy and risk controls permit an action.

Market Predictor must not acquire alert, position, portfolio, or order-entry responsibilities.

## 2. Goals

1. Make the web workstation efficient for cross-sectional monitoring and repeated desktop use.
2. Make the native app safe and comfortable for one-handed triage and position response.
3. Make responsive web usable at 320 CSS pixels without document-level horizontal scrolling.
4. Make all order-related actions explicit, reviewable, and environment-aware.
5. Make data source, market session, freshness, and stale state visible before an action.
6. Meet WCAG 2.2 AA for the web and equivalent Android accessibility expectations for the native app.
7. Preserve one engine: UI changes must not duplicate strategy, risk, or execution rules.
8. Keep the implementation testable, cloud-deployable, and independent of a large frontend framework.

## 3. Non-Goals

- No React, Blazor, SPA, or CSS-framework rewrite.
- No reimplementation of engine rules in Razor, JavaScript, XAML, or code-behind.
- No direct Market Predictor order or alert integration.
- No real-money execution enablement as part of the visual redesign.
- No large MVVM migration before the navigation and scrolling defects are resolved.
- No dark-mode work ahead of reflow, action safety, semantic accessibility, and data freshness.
- No compatibility layer for obsolete UI structures; the application is not yet in production.

## 4. Audit Baseline

### 4.1 Runtime measurements

The Razor application was audited against live local data on 2026-07-22.

| Surface | Viewport | Measured document width | Result |
|---|---:|---:|---|
| Trade Desk | 1440 x 900 | 1425 px | No horizontal document overflow |
| Trade Desk | 390 x 844 | 570 px | 180 px horizontal overflow |
| Trade Desk | 320 x 800 | 570 px | 250 px horizontal overflow |
| Running Trades | 390 x 844 | 506 px | Horizontal overflow |
| Paper | 390 x 844 | 506 px | Horizontal overflow |
| Wishlists | 390 x 844 | 506 px | Horizontal overflow; page exceeded 11,000 px height |
| Backtests | 390 x 844 | 709 px | Severe horizontal overflow |
| Warmup | 390 x 844 | 506 px | Horizontal overflow |

At 320 px, the top navigation alone measured 490 px wide and the Trade Desk table measured 528 px. No skip link or `aria-current="page"` marker was present.

Visible web controls are generally 36 px high; compact trade controls are 30-39 px high. These sizes are inappropriate for touch-first use.

### 4.2 Web findings

1. The top navigation does not collapse or scroll within its own bounded region.
2. Responsive rules stack grids but do not control intrinsic table or navigation width.
3. Trade Desk combines monitoring, wishlist administration, Finviz import, signals, news, and immediate order actions.
4. Buy and Sell post immediately with a hidden quantity of one.
5. Quote labels and order-button prices update through SSE without quote age, feed, session, or stale state.
6. The dead `Live` navigation item has `href="#"`.
7. There is no active-page state, skip link, or deliberate focus-visible system.
8. Inline page JavaScript replaces complete signal/news containers, which can disrupt focus and screen-reader position.
9. Page-specific CSS and JavaScript increase behavior and accessibility drift.

### 4.3 Native mobile findings

1. Major screens place fixed-height `CollectionView` controls inside a parent `ScrollView`.
2. Wishlist uses separate nested scrolling regions for stocks, signals, and news.
3. Fixed heights such as 420, 280, and 220 prevent content from adapting naturally to screen and font size.
4. The Android target hardcodes many controls to 44 units; Android recommends a 48 dp focusable target.
5. The project has no explicit MAUI `SemanticProperties` for ambiguous controls and dynamic state.
6. Buttons labelled `+` and `-` do not expose a descriptive operation or ticker context.
7. Buttons labelled `Trade` start automated paper strategy runs rather than opening an order ticket.
8. Paper is presented as a destination even though paper/live is an execution environment.
9. Five primary tab destinations plus a nested More group create a wide and inconsistent information architecture.
10. Status and live-update announcements are not designed for TalkBack.

### 4.4 Useful foundations to retain

- The desktop split between a watchlist table and contextual intelligence rail is directionally correct.
- Existing status text generally supplements color rather than relying on color alone.
- The native app has centralized resource dictionaries and a consistent minimum-control baseline.
- Running Trades already asks for confirmation before a paper position is closed.
- Razor pages already use a common layout and shared CSS entry point.
- Market Predictor already returns structured readiness, probability, downside, catalyst, context, and model metadata.

## 5. Design Principles

1. **Status before action.** Environment, market session, connection, quote age, and risk state appear before Buy or Sell.
2. **Progressive disclosure.** Lists support scanning; details, news, predictions, and order parameters open only after selecting a symbol.
3. **One scroll owner.** A screen must have one primary vertical scrolling surface.
4. **Desktop density, mobile focus.** Desktop compares many symbols; mobile handles one decision at a time.
5. **No direct trading from ambiguous list controls.** Buy, Sell, and Start Strategy open a reviewable workflow.
6. **Probability is not certainty.** Model scores always include horizon, readiness, downside, and evidence age.
7. **Color reinforces text.** Gain/loss, readiness, and action state always have textual or iconographic labels.
8. **Stable live updates.** Streaming values may update text, but must not move controls, steal focus, or replace focused containers.
9. **Touch-safe defaults.** Android interactive targets are at least 48 dp. Web controls used at mobile breakpoints are at least 44 CSS pixels.
10. **Simple visual system.** Restrained colors, small radii, tabular numbers, compact rows, no decorative dashboard cards, and no nested cards.

## 6. Reference Patterns

The redesign adopts established interaction patterns without copying a vendor's visual identity.

### TradingView

- Watchlist, alerts, news, and symbol details are contextual tools rather than separate full-screen forms on desktop.
- Watchlist alerts apply independently to symbols and explicitly support regular or extended sessions.
- Reference: https://www.tradingview.com/support/solutions/43000746464-getting-started-with-supercharts/
- Reference: https://www.tradingview.com/support/solutions/43000739708-watchlist-alerts-your-trading-edge/

### Interactive Brokers

- Selecting a watchlist row leads to quote details before order entry.
- The order ticket exposes quantity, order type, price, time in force, and preview before submission.
- Mobile submission can require a deliberate slider or explicit submit action after preview.
- Reference: https://www.ibkrguides.com/ipad/watchlist.htm
- Reference: https://www.ibkrguides.com/androidtablet/order-ticket.htm

### Accessibility standards

- WCAG 2.2 reflow: https://www.w3.org/TR/WCAG22/#reflow
- WCAG target size: https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum.html
- WCAG financial error prevention: https://www.w3.org/WAI/WCAG22/Understanding/error-prevention-legal-financial-data.html
- Android 48 dp guidance: https://developer.android.com/guide/topics/ui/accessibility/views/apps-views
- MAUI semantic accessibility: https://learn.microsoft.com/en-us/dotnet/maui/fundamentals/accessibility
- MAUI scrolling guidance: https://learn.microsoft.com/en-us/dotnet/maui/user-interface/controls/scrollview

## 7. Target Information Architecture

### 7.1 Web workstation

Primary navigation:

1. **Desk** - cross-sectional monitoring and symbol selection
2. **Positions** - open positions, protection, P/L, and close workflow
3. **Orders** - working, rejected, filled, and cancelled orders
4. **Research** - backtests, audit, and optimization
5. **Operations** - warmup, environment validation, and service health

Persistent operational strip:

- environment: `PAPER` or `LIVE`
- market session: premarket, regular, postmarket, closed, or weekend
- market time in New York
- quote connection and feed
- newest quote age
- model service readiness
- broker synchronization state
- risk/admission state

Desk layout at desktop width:

```text
Operational strip
Watchlist/filter toolbar
+--------------------------------------+--------------------------+
| Dense watchlist table                | Selected symbol detail   |
| sortable and keyboard navigable      | Overview / Model / News  |
| no direct submission from each row   | Chart / Strategy state   |
+--------------------------------------+--------------------------+
```

Desk layout below 760 px:

- toolbar becomes a compact disclosure panel
- watchlist becomes a single-column row list
- selecting a symbol navigates to or reveals a full-width detail view
- no multi-column trading table is rendered
- any genuinely two-dimensional table scrolls inside its own labelled container, never at document level

Wishlist editing, notes, and Finviz import move to a dedicated management view and do not occupy the primary monitoring viewport.

### 7.2 Native Android application

Bottom navigation:

1. **Watch** - compact watchlist and candidates
2. **Positions** - open positions and protection state
3. **Activity** - signals, alerts, orders, and fills
4. **More** - research, warmup, settings, and operational tools

Paper/live is a persistent environment badge, not a navigation destination.

The Watch screen uses one root `CollectionView`:

- a `Header` contains environment, connection, market state, wishlist picker, and filters
- list items contain ticker, price, daily movement, strategy state, prediction state, and freshness
- tapping an item opens Symbol Detail
- item actions are limited to familiar icon controls with accessible names
- destructive or transactional actions remain in Symbol Detail or an order ticket

Symbol Detail contains:

- Overview
- Prediction
- Chart and technical state
- Catalyst and news
- Position and order state

Tabs may be a segmented control inside the page. The bottom navigation must not change when switching symbol-detail sections.

## 8. Prediction Presentation Contract

The UI consumes the existing unified prediction response. It must not derive new model logic.

### 8.1 Summary fields

- ticker
- final signal
- readiness status
- generated time
- requested and resolved horizon
- snapshot identifier

### 8.2 Swing block

- probability and decision score
- signal and rank
- one-day return context
- volume context
- catalyst status, direction, relevance, and age
- global-context impact and active flashpoints
- SPY/QQQ and sector context where available
- readiness reasons and source status

### 8.3 Intraday block

- opportunity probability
- downside probability
- decision score and rank
- relative volume, RSI, and MACD state
- modeled stop and target percentages
- catalyst confirmation as a separate overlay
- readiness reasons, feed, benchmark state, and latest price date

### 8.4 Display rules

1. `invalid` readiness disables all model-derived actionable styling.
2. `warn` is presented as incomplete intelligence, never as a Buy recommendation.
3. Catalyst status is visually separate from model probability.
4. Opportunity and downside probabilities appear together.
5. Probability includes its target horizon in the same visual block.
6. Model age and latest feature timestamp are visible.
7. TradingFlow eligibility may veto a valid model signal and must show the exact rejection reason.
8. Model drivers are explanatory evidence, not order defaults.

## 9. Order And Strategy Safety

### 9.1 Row actions

- Watchlist rows expose `View`, `Start strategy`, and `Order` actions.
- Buy and Sell never submit directly from a row.
- `Start strategy` must be labelled as paper or live and name the selected strategy.
- Icon-only controls require a tooltip on web and `SemanticProperties.Description` on MAUI.

### 9.2 Order ticket

The order ticket displays:

- environment and account
- ticker and side
- quantity
- order type
- limit or stop price
- time in force
- regular or extended-hours policy
- latest bid/ask, spread, feed, and quote age
- estimated notional
- available position or buying power
- strategy ownership, when applicable
- stop/target or protection intent
- validation and rejection reasons

Submission flow:

1. User enters or reviews parameters.
2. Server validates position conflict, environment, market session, quote freshness, risk, and broker state.
3. UI presents a review summary.
4. User explicitly confirms.
5. Server revalidates and creates the durable intent.
6. UI reports accepted, rejected, working, partially filled, filled, cancelled, or unknown state.

For paper mode, this flow should be structurally identical to future live mode even though the account and risk policy differ.

## 10. Shared Design System

### 10.1 Tokens

Use one semantic token vocabulary on both platforms:

- page background
- surface
- elevated surface
- primary text
- secondary text
- border
- accent
- gain
- loss
- warning
- information
- disabled
- focus ring

Green and red are reserved for positive/negative financial state and explicit Buy/Sell semantics. Readiness and service status use text, neutral icons, and warning/information tokens.

### 10.2 Typography and numbers

- body text minimum: 14 px web, platform-scaled 14sp equivalent on Android
- secondary text minimum: 12 px only when nonessential and still dynamically scalable
- compact panel headings: 16-18 px
- page headings: 22-24 px
- tabular numerals for prices, percentages, quantity, and P/L
- no viewport-width font scaling
- no fixed text container heights

### 10.3 Controls

- 48 dp minimum native target
- 44 px minimum responsive-web target
- stable dimensions for ticker rows, action bars, tabs, and status chips
- familiar Lucide icons packaged locally for web and equivalent licensed SVG assets for MAUI
- icon buttons require tooltip, accessible name, and visible focus state
- text buttons are reserved for explicit commands
- segmented controls select mode or view; they do not submit work

### 10.4 Live state

- live quote updates use tabular numbers and fixed-width fields to avoid layout movement
- quote updates do not announce every tick to screen readers
- connection loss, stale data, order status, and new actionable signals are announced through bounded live regions
- streaming code patches keyed elements instead of replacing complete focused containers
- animation respects reduced-motion settings

## 11. Implementation Checkpoints

Each checkpoint is independently reviewable and committed only after its acceptance tests pass.

### UI0 - Baseline And Test Harness

Scope:

- preserve audit measurements as automated checks
- add responsive browser tests for core Razor routes
- add accessibility assertions for landmarks, active navigation, names, focus, and document overflow
- define the native device and font-scale test matrix
- capture before screenshots for comparison

Likely files:

- new web UI test project or test folder using Microsoft Playwright
- `src/TradingFlow.Tests` only for shared API/view-model contract tests
- no engine changes

Acceptance:

- tests reproduce current overflow at 320/390 before UI1
- tests can run locally and in CI without provider credentials
- test fixtures do not submit orders or mutate paper state

### UI1 - Shared Accessibility And Responsive Foundation

Scope:

- semantic design tokens on web and MAUI
- web skip link, main target, active page, dead-link removal, focus-visible styles
- responsive navigation that does not widen the document
- 44 px responsive-web and 48 dp Android targets
- MAUI semantic names/descriptions for icon-only and state controls
- remove fixed control heights that block large text

Primary files:

- `src/TradingFlow.Web/Pages/Shared/_Layout.cshtml`
- `src/TradingFlow.Web/wwwroot/css/site.css`
- `src/TradingFlow.Mobile/AppShell.xaml`
- `src/TradingFlow.Mobile/Resources/Styles/Colors.xaml`
- `src/TradingFlow.Mobile/Resources/Styles/Styles.xaml`

Acceptance:

- no document overflow at 320, 390, 768, 1024, or 1440 px on core routes
- keyboard focus is visible and ordered
- current destination is exposed visually and through `aria-current`
- all named actions pass touch-target checks
- TalkBack reads icon-only controls with purpose and ticker context

Commit checkpoint: `UI1 accessible responsive foundation`

### UI2 - Web Workstation Restructure

Scope:

- simplify primary navigation and add operational status strip
- keep dense desktop table but create compact mobile watch rows
- move ticker creation, notes, and Finviz import to Wishlist management
- row selection drives one symbol-detail rail
- standardize loading, empty, error, partial, disconnected, and stale states
- extract shared streaming and formatting JavaScript after behavior is covered

Primary files:

- `src/TradingFlow.Web/Pages/TradeDesk.cshtml`
- `src/TradingFlow.Web/Pages/TradeDesk.cshtml.cs`
- `src/TradingFlow.Web/Pages/Wishlists.cshtml`
- `src/TradingFlow.Web/Pages/RunningTrades.cshtml`
- new partials under `src/TradingFlow.Web/Pages/Shared`
- new modules under `src/TradingFlow.Web/wwwroot/js`

Acceptance:

- first desktop viewport shows operational state, filters, and actionable symbols without administration forms
- mobile web shows one symbol row per item with no trading table
- SSE updates preserve keyboard and screen-reader focus
- stale feed and disconnected states are obvious without using color alone
- no direct order submission remains in watchlist rows

Commit checkpoint: `UI2 web trading workstation`

### UI3 - Native Navigation And Single-Scroll Screens

Scope:

- replace bottom navigation with Watch, Positions, Activity, More
- make environment persistent and remove Paper as a primary destination
- make Watch, Positions, Activity, and News each use one primary `CollectionView`
- move screen controls into collection headers/footers
- remove fixed-height nested lists
- introduce compact reusable row, status, empty, and error controls
- split oversized code-behind by concern only after view behavior is stable

Primary files:

- `src/TradingFlow.Mobile/AppShell.xaml`
- `src/TradingFlow.Mobile/Pages/WishlistsPage.xaml`
- `src/TradingFlow.Mobile/Pages/RunningTradesPage.xaml`
- `src/TradingFlow.Mobile/Pages/NotificationsPage.xaml`
- `src/TradingFlow.Mobile/Pages/NewsPage.xaml`
- new reusable views under `src/TradingFlow.Mobile/Controls`

Acceptance:

- every main screen has one vertical scroll owner
- 200% font size does not clip controls or text
- TalkBack order matches visual order
- state changes are announced selectively
- one-handed primary actions remain in the lower reachable area
- list virtualization remains active with at least 500 synthetic symbols

Commit checkpoint: `UI3 native mobile trading shell`

### UI4 - Symbol Intelligence And Predictor Integration

Scope:

- add one shared symbol-detail contract to TradingFlow Web/Mobile APIs
- call Market Predictor through a typed server-side client
- cache only according to model/data freshness metadata
- render separate Model Intelligence and TradingFlow Decision blocks
- include swing/intraday segmented view, catalyst, global context, SPY/QQQ context, and readiness
- fail closed when the predictor is unavailable, stale, invalid, or schema-incompatible

Primary files:

- new typed predictor client in `src/TradingFlow.Web/Services`
- shared mobile API records in `src/TradingFlow.Web/Models/MobileApiModels.cs`
- `src/TradingFlow.Web/MobileApiEndpoints.cs`
- symbol-detail partials and MAUI pages/controls

Acceptance:

- predictor errors never block ordinary quote/position monitoring
- invalid readiness cannot appear as an actionable recommendation
- catalyst never modifies displayed estimator probability
- horizon, generated time, data age, model identity, and snapshot evidence are available in detail
- response contract tests detect schema drift

Commit checkpoint: `UI4 prediction intelligence integration`

### UI5 - Order Ticket And Strategy Review

Scope:

- replace direct list submissions with an order/strategy ticket
- expose quote age, spread, session, environment, quantity, TIF, and notional
- call existing server-side validation services; do not replicate logic in clients
- add preview and explicit confirmation
- make order lifecycle visible in Activity and Positions
- keep paper/live composition explicit and fail closed

Primary files:

- Razor order-ticket partial/page and handlers
- MAUI order-ticket page or bottom sheet
- existing `AlpacaManualOrderService`, admission, reconciliation, and order services only where API orchestration is required
- API contract tests and UI workflow tests

Acceptance:

- no Buy/Sell operation submits from one tap on a list row
- preview and submission both validate server-side
- a changed or stale quote triggers re-review or a clear rejection
- extended-hours policy is visible before confirmation
- paper and live environment cannot be confused
- duplicate taps cannot create duplicate order intents

Commit checkpoint: `UI5 reviewed order and strategy workflow`

### UI6 - Remaining Pages And Release Audit

Scope:

- apply common navigation, tokens, state components, and reflow to Backtests, Audit, Warmup, Paper jobs, Settings, and Operations
- remove obsolete CSS, duplicated JavaScript, and dead page fragments
- add dark mode only after accessibility and task flows pass
- run final physical-device and browser audit

Acceptance:

- core routes pass WCAG 2.2 AA automated checks with documented manual exceptions
- Android Accessibility Scanner has no high-severity findings
- TalkBack walkthrough completes Watch -> Symbol -> Prediction -> Paper order preview -> Activity
- Playwright screenshot and overflow matrix passes
- no engine or strategy behavior changed as part of UI cleanup
- documentation and operator runbook match the implemented navigation

Commit checkpoint: `UI6 UI release audit`

## 12. Verification Matrix

### 12.1 Web

Viewports:

- 1440 x 900 desktop
- 1024 x 768 compact desktop/tablet landscape
- 768 x 1024 tablet portrait
- 390 x 844 common phone
- 320 x 800 WCAG reflow boundary

For each core route verify:

- no document-level horizontal overflow
- no clipped text at 200% zoom
- keyboard-only completion
- visible focus
- skip navigation
- active navigation state
- accessible names and error association
- touch target size
- light and high-contrast mode
- loading, empty, error, partial, disconnected, stale, and ready states
- live updates preserve focus and do not resize controls

### 12.2 Native Android

Devices:

- narrow phone around 360 dp
- standard phone around 411 dp
- tablet portrait and landscape

Configurations:

- default text
- 200% font scaling
- display scaling increased
- TalkBack enabled
- high-contrast text where available
- reduced motion
- portrait and landscape
- slow network, disconnected backend, and stale stream

Required tools:

- Android Accessibility Scanner
- TalkBack manual walkthrough
- Android Studio Layout Inspector
- automated MAUI/Appium smoke flows where stable

## 13. Production Telemetry

UI telemetry must avoid market or credential leakage and should record:

- route/screen load success and latency
- stream connected, reconnecting, stale, and failed transitions
- predictor availability and readiness category
- order-preview validation failures by rejection code
- confirmation abandonment, without logging confidential account data
- duplicate-action prevention events
- client version, viewport/device class, and accessibility mode where permitted

Do not log API keys, account identifiers, raw broker responses, unrestricted news text, or full predictor payloads.

## 14. Rollout Rules

1. Implement checkpoints in order: UI0 -> UI1 -> UI2 -> UI3 -> UI4 -> UI5 -> UI6.
2. Commit each checkpoint separately after its tests pass.
3. Do not push unless explicitly requested.
4. Stop only TradingFlow-owned runtime processes before UI code changes.
5. Preserve database, paper state, credentials, and cached market data.
6. Do not promote live execution during the redesign.
7. A later checkpoint must not weaken an earlier accessibility or fail-closed gate.
8. Do not claim native accessibility complete until tested with TalkBack on a physical Android device.

## 15. Definition Of Done

The redesign is complete only when:

- desktop and responsive web serve the same workflows without document overflow
- native screens use one primary scrolling surface
- all controls have accessible names, states, and adequate targets
- paper/live, session, connection, quote freshness, and model readiness are always visible before action
- Market Predictor output is clearly separated from TradingFlow strategy/risk decisions
- every order has a review and confirmation path backed by server validation
- live updates remain stable under keyboard, screen reader, and touch use
- automated and manual verification evidence is stored with the release checkpoint
- obsolete UI code and the superseded design paths are removed
