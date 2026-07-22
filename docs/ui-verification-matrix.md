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
