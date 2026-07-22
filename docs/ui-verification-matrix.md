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
