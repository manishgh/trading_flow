# UI Verification Matrix

This file records the executable and manual verification surfaces for the approved UI redesign.

## Web browser matrix

- 1440 x 900 desktop
- 1024 x 768 compact desktop
- 768 x 1024 tablet portrait
- 390 x 844 phone
- 320 x 800 WCAG reflow boundary

The Playwright suite in `tests/ui` is read-only and starts TradingFlow with
`TRADINGFLOW_UI_TEST_MODE=true`. That mode uses a temporary SQLite database and does not start
news ingestion, wishlist observation, account synchronization, paper-job recovery, automation
recovery, or database backup services.

## Native Android matrix

- 360 dp narrow phone, portrait and landscape
- 411 dp standard phone, portrait and landscape
- tablet portrait and landscape
- default and 200% font scale
- TalkBack on
- increased display scale
- high-contrast text where available
- reduced motion
- connected, slow, disconnected, and stale backend states

Physical-device TalkBack and Android Accessibility Scanner results are manual release gates. A
successful compile or emulator smoke test must not be reported as completion of those gates.

## Completed checkpoints

### UI0 - executable baseline

- Playwright starts the web application in isolated UI test mode with a temporary SQLite store.
- The baseline matrix covers desktop, tablet, phone, and 320 px reflow widths.
- Baseline screenshots and DOM assertions establish repeatable regression evidence.

### UI1 - shared responsive foundation

- Primary navigation, semantic status colors, focus treatment, input sizing, and reflow behavior are
  covered by Playwright.
- The Android Release target compiles with generated XAML bindings and no warnings.

### UI2 - web trading workstation

- Desktop visual check completed at 1440 x 900: operational status, controls, market table, and the
  selected-symbol evidence rail fit without overlap or clipped text.
- Responsive web visual check completed at 390 x 844: the desktop table is replaced by compact
  symbol rows, controls remain in bounds, and state is communicated with text as well as color.
- Wishlist management visual check completed at 390 x 844 with administration isolated from order
  and strategy actions.
- Browser console check completed with no warnings or errors on Trade Desk and Wishlist Management.
- Internal links rendered by both screens are fetched and required to return successful responses.
- Full verification: 455 .NET tests, Android Release build, and the complete Playwright suite.

Native screen and TalkBack verification remains a UI3 release gate and requires a connected Android
device or emulator. It is intentionally not inferred from the successful Android build.

### UI3 - mobile operator workflow

- The primary shell is reduced to Watch, Positions, Activity, and More so monitoring and execution
  states are reachable without duplicating research and administration destinations.
- Watch, Positions, Activity, and News use one virtualized root collection each; nested page
  scrolling and fixed-height result lists are removed from the primary mobile workflow.
- Watch rows remain read-only until the operator opens Details or News. Position exits require an
  explicit review confirmation instead of executing from an ambiguous row action.
- Activity combines strategy signals and system notifications with explicit All, Signals, and
  System filters and a bounded 500-item view.
- Android Release verification completed for `net10.0-android` with generated XAML bindings:
  0 warnings and 0 errors.
- Cross-surface regression verification completed: 455 .NET tests and 22 Playwright tests passed.

Physical-device screenshots, font-scale checks, TalkBack, and Android Accessibility Scanner remain
manual release gates. They require a connected Android device or emulator and are not inferred from
the successful generated-XAML Release build.

### UI4 - prediction intelligence integration

- TradingFlow eligibility, position state, and risk gates remain authoritative. Market Predictor is
  exposed only as read-only evidence after the operator explicitly selects a symbol.
- The prediction adapter has a bounded timeout, no automatic POST retries, explicit contract
  validation, maximum evidence age, promoted-model validation, and fail-closed states for
  unconfigured, unavailable, stale, invalid, and incompatible responses.
- Trade Desk keeps wishlist-wide news visible at all times. Explicit symbol selection adds separate
  TradingFlow Decision, Model Intelligence, and Selected Symbol News panels without replacing the
  wishlist news timeline.
- The mobile Watch Details action opens a dedicated Symbol Detail route that reads the same shared
  API contract and keeps TradingFlow decision evidence separate from model evidence.
- Verification completed: web build with 0 warnings and 0 errors, Android Release build with 0
  warnings and 0 errors, 461 .NET tests, and 23 Playwright tests passed.
- Live browser checks completed on desktop and 390 x 844 mobile: no implicit selection, no page
  overflow, correct desktop/mobile market views, all evidence scopes present after selection, and
  no browser warnings or errors.

Physical-device screenshots, font-scale checks, TalkBack, and Android Accessibility Scanner remain
manual release gates and are not inferred from the successful Android Release build.

### UI5 - reviewed order and strategy workflow

- Trade Desk market rows remain read-only. A selected symbol exposes a distinct `Review protected
  buy` command that opens a paper-only order ticket; no list-row action can submit an order.
- Preview is server-owned and displays quantity, limit, notional, bid/ask, quote age, spread,
  session, time in force, stop, target, entry policy, extended-hours choice, and ticket expiry.
- Confirmation uses an encrypted, immutable, two-minute review token. It rechecks quote freshness,
  spread, quote drift, market session, asset eligibility, buying power, position state, admission
  blocks, and manual-entry policy before using a stable durable intent identity.
- Data Protection keys persist beneath the configured TradingFlow data root, so process restarts do
  not invalidate active review tokens merely because the web process restarted.
- Android Symbol Detail opens the same shared review/confirm API contract. The phone flow keeps the
  draft, review, blocked, and accepted states separate and never exposes confirmation before a
  successful server review.
- Verification completed: web and Android Release builds with 0 warnings and 0 errors, 466 .NET
  tests, and 24 Playwright tests passed. Live desktop and 390 px checks found no horizontal
  overflow; the real paper profile correctly exposed a strategy-gated blocked state and no confirm
  command. Browser console contained no warnings or errors.

Physical-device order-page screenshots, font-scale checks, TalkBack, and Android Accessibility
Scanner remain manual release gates and are not inferred from the successful Android Release build.

### UI6 - remaining pages and release audit

- Backtests strategy parameters and Paper quick-edit controls now have stable identifiers and explicit
  labels. The quick editor uses shared responsive tokens instead of a dark inline-only treatment.
- Audit summary, heading, filters, and table containment reflow at the phone boundary; the decision
  table scrolls inside its panel without widening the document.
- The obsolete one-tap manual-order dialog, its unused view model, and its dead CSS were removed. The
  reviewed order ticket is the only manual buy surface.
- The browser matrix now covers 11 core and detail routes at 320, 390, 768, 1024, and 1440 px. It
  asserts no document-level overflow, one page heading, unique element IDs, named visible controls
  and links, resolved internal links, 44 px mobile shell actions, and no order submission from the
  read-only audit suite.
- Implemented web and Android navigation and order-safety boundaries are documented in
  `docs/architecture.md` and `docs/ui-operator-runbook.md`.
- Final automated verification completed: web and Android Release builds with 0 warnings and 0
  errors, 466 .NET tests, and 30 Playwright tests passed.

The structural browser assertions are not a claim of complete WCAG 2.2 AA conformance. Physical
Android screenshots, 200% font-scale checks, TalkBack walkthrough, and Android Accessibility Scanner
remain documented manual release gates and must be completed on a connected device before release.
