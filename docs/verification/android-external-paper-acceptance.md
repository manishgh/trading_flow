# Android and external paper acceptance — 27 September 2026

Scope: Android emulator navigation and notification acceptance, plus real Alpaca paper/provider smoke. Market Predictor API integration remains deferred. This is a focused acceptance follow-up, not a strategy promotion or a complete paper-execution release gate.

## External provider evidence

The production Alpaca adapters authenticated against the paper endpoint with the locally configured account binding. The account was ACTIVE, unblocked, and matched the expected account; there were zero open orders and zero positions. Credentials and account identifiers are omitted from this report.

Seven adapter checks passed: account identity/status, existing exposure, SIP quote/trade retrieval, historical news retrieval (10 articles), paper trade stream authentication/subscription acknowledgement, SIP stream authentication, and news stream authentication/subscription. A GET-only broker transport prevented writes. Closed-market quotes were explicitly dated 25 September; they were not treated as fresh executable prices. Stream connection success does not establish live tick continuity or receipt of a new news event.

The real Alpaca candle integration test passed (one test): 1,898 bars read, normalized and grouped; zero incomplete; four derived groups and six indicator sets. This exercised actual provider ingestion and TradingFlow's production pipeline with isolated test storage.

Commands:

    dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter FullyQualifiedName~AlpacaCandlePipelineIntegrationTests --logger "console;verbosity=normal" --logger "trx;LogFileName=external-paper-market-data.trx" --results-directory .test-tmp/external-acceptance -m:1 --disable-build-servers

    dotnet run --project work/external-smoke/Smoke.csproj --verbosity quiet -- work/external-smoke/evidence

The second command is the workspace acceptance harness referencing the real TradingFlow.Alpaca project. Sanitized provider evidence is supplied alongside this report; raw provider/account archives remain private workspace intermediates.

## Android environment and isolation

Signed Release APK on an Android 35 Google APIs x86_64 emulator, WHPX acceleration, 412x915 viewport. An isolated SDK/AVD was installed in the task workspace. No physical phone was attached.

The app connected to a credential-free TradingFlow acceptance host with isolated SQLite state. Login and CSRF used real backend handlers. Candidate snapshots/audits came through a small authenticated fixture proxy using the repository's deterministic news replay fixture. Its displayed four-share partial fill is fixture evidence, not a broker fill performed during this test.

Runtime defects found and repaired:

- Missing XAML resource names crashed startup, order tickets and symbol details; references now use existing theme resources and styles.
- Startup overwrote the saved emulator/backend URL; explicit saved selections now survive cold launch.
- Retained notification query arguments reopened the audit when navigating Back; notification navigation now uses single-use parameters.
- Fixed black text was unreadable in dark mode; generic text, title and placeholder styles now follow the theme.
- Background wishlist failures repeatedly displayed modal alerts; load errors remain inline, and delayed loads cannot restart streams after leaving Watch.

Focused verification:

    dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter FullyQualifiedName~MobileXamlResourceTests -m:1 --disable-build-servers --verbosity quiet

    dotnet build src/TradingFlow.Mobile/TradingFlow.Mobile.csproj -c Release --no-restore -m:1 --disable-build-servers --verbosity quiet

Resource regression: one passed. The resource inventory catches unknown names; it does not prove resource scope, types or visual contrast. Actual emulator checks supplement it. Full offline/backend/browser results from the preceding workflow checkpoint are historical evidence, not rerun counts for these localized mobile changes.

## Remaining execution gate

The provider clock reported the market closed. Next regular open: Monday 28 September 2026, 15:30 Europe/Berlin (09:30 New York). The catalog currently has no paper-execution authorization. No orders were submitted, cancelled or modified. A real discovery/admission → protected paper order → fill → exit → reconciliation/audit smoke therefore remains unverified.

Completing that gate requires an explicitly authorized immutable strategy identity, eligible market evidence and the normal durable TradingFlow order path during an appropriate session. Account access and a paper endpoint alone do not satisfy lifecycle authorization. No permissions or gates were bypassed for testing.

Physical-device behavior, TalkBack/haptics, background delivery while the app is not connected, multi-session fill/impact calibration and live trading remain unverified. Candidate alerts tested here are local notifications generated while the app receives candidate updates.

## Recorded Android results

| Check | Result and evidence boundary |
| --- | --- |
| Signed Release cold launch and saved backend | Passed after repairing resource references and startup URL rewriting. |
| Sign-in / CSRF | Real isolated backend login passed. The sign-in screen remained unobstructed across more than two former refresh intervals; Watch's inline auth error cleared after login. |
| Watch / symbol details | Seeded one isolated wishlist through the real API. Watch and AAPL details loaded; missing quotes and unconfigured predictor were explicit. |
| Candidate list / audit / article | Authenticated replay fixture displayed exact run/candidate and attached article. Opening the article reached Chrome Custom Tab using the fixture URL. |
| Connectivity recovery | A controlled 503 showed Reconnecting, retained selected run/card, and recovered after restoration. This was a server-failure simulation, not a physical radio outage. |
| Notification denied | Android permission denial suppressed the system notification; Activity history retained the candidate and opened its audit. |
| Notification allowed / warm | Actual Android notification delivered; tapping it opened the exact candidate/article. Back returned to the list without reopening the audit. |
| Notification cold / login | App PID was absent while its notification remained posted. Tapping launched sign-in, then opened the correct audit. Back returned to candidates. Saved backend survived. |
| Backend mismatch | Changed from isolated port 53017 to 53019. Tapping the old notification was refused with the explicit another-backend message. Restored 53017 afterward. |
| Order ticket | Opened from the triggered fixture candidate; invalid numeric input produced an inline error, with no confirmation/submission. |
| Paper / Backtests | Both screens navigated and loaded real isolated backend catalog/empty job lists. Paper correctly reported no authorized strategy. No job started. |
| Theme | Inspected sign-in, audit, symbol detail and order-ticket screenshots; switched light/dark while screens were open. Disabled-button caption contrast was corrected in the final visual pass. |

Independent requirements and code reviews approved the changes; the stream-lifetime guard identified during review was implemented. No unresolved critical/high finding was reported. Review agents were closed after recording results.

Unrun broader mobile acceptance: simultaneous notifications for different candidates, reserved-character article IDs at runtime, physical network handoff, pending-job selection races, candidate capture/start and durable mutation/cancel/retry flows on the emulator. Existing automated contract/browser evidence covers parts of these, but is not relabeled as device evidence here. The order horizon selector initially appears blank until a choice is selected; this non-blocking display issue remains recorded for follow-up.

## Final verification and retained evidence

Final Android Release build: succeeded, zero warnings/errors, 1m28.88s. Final resource regression: one passed, zero failed. The last APK was installed and its disabled symbol-action caption inspected in light and dark themes. Notification/navigation tests preceded only that final visual-only color change; they were not unnecessarily repeated after it. An interrupted fixture-proxy connection during a backend restart caused a temporary harness failure; restarting the proxy restored sign-in and the final visual check passed.

The provider summary and selected Android screenshots accompany this report. No secrets or raw account records are included. The isolated SDK/AVD and test workspace remain reusable. Task-owned emulator, fixture proxy and TradingFlow acceptance host were stopped; no acceptance listener remained on ports 53017 or 53019. No operational database, candle cache or broker state was changed. No GitHub synchronization occurred. Final git diff --check passed.

## Execution preflight follow-up

At 20:03:52 UTC on 27 September, Alpaca still reported the market closed, the bound paper account active/unblocked, and zero orders/positions. Next regular open is 28 September at 15:30 Europe/Berlin. The existing administrator-only operator override permits an execution-infrastructure smoke without a strategy promotion; the absent paper-strategy grant blocks strategy-driven execution, not this distinct reviewed override. Real administrator authentication and all normal account/risk/protection/journaling rules remain required. The focused existing execution/access/reconciliation slice passed 59 tests, zero failed/skipped. See [the bounded execution smoke plan](paper-execution-smoke-plan.md). Actual broker fill/protection/exit evidence is still pending; no order was queued or submitted.


## Release follow-up — 28 September 2026

See [the current release verification](release-2026-09-28.md). Full backend: 1,729 passed, one known deferred ML contract-fixture failure. Final browser: 91 passed. Android navigation, visible Swing horizon, authentication fencing, and phone create/retry/queued-cancel/USB-reconnect checks are delivered. The natural nightly-to-first-morning read-only worker is running for September 28–29; its result remains pending. At 15:30 Berlin the real market was open and the paper account was active/unblocked with no exposure; actual execution is awaiting real administrator sign-in, not market opening. Firebase preparation is documented, but background push implementation and device activation remain pending. The detailed ledger names remaining physical-device drills without treating automated tests as device evidence.
