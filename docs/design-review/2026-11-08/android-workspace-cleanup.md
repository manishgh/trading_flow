# Android workspace cleanup — 8 November 2026

The user requested the same quieter workspace on Android after seeing that the first installation changed only the buy ticket. This native presentation pass reuses the existing MAUI pages and Workflow client. It introduces no market-data endpoint, provider request, trading decision, persistence contract, or dependency.

## Navigation and density

Five readable bottom destinations are Desk, Trading, Strategies, News and More. Earnings and Stock groups remain prominent Desk destinations and also appear in More. Desk has compact two-column shortcuts for daily review, candidate checks, earnings and stock groups. Account access, activity, automation, warm-up and settings remain reachable through More. Existing route names are preserved.

Strategies explicitly separates simulated trading from research: links open the existing authorized paper-run page and strategy positions. The research builder retains independent exact strategy selection; name, version, lifecycle and horizon stay visible, while Details contains the complete SHA-256, parent lineage and admission/execution policy. Optional profile, replay, evidence, dates, exclusions and manual cohorts share the existing collapsed plan settings. Frozen plan details remain available alongside a compact portfolio/group summary. Preview, save and explicit start remain separate commands.

Trading keeps Positions and Orders in separate local display sections. The switch makes no API request. The existing portfolio/journal read and polling sequence, account/session guards, exact command dictionaries, journal reconciliation, cancellation and exit review remain intact. Issued-command recovery is outside both sections. Short cards show quantities, state, market value and open unrealized P/L, with cost, order IDs and read times on demand. Missing amounts say unavailable. Open account P/L is never labeled as a monthly strategy return.

Simulated trading keeps its existing paper-mode selection and authorization. Account limit editing and optional run inputs are disclosed on demand. News and Earnings use the native toolbar title once, a smaller environment chip, and a quiet inline filter control. The environment control has a 48dp touch area and exposes UTC, New York and device-local clocks on demand. No clocks depend on a network call.

News and Earnings now distinguish loading, a successful response with no matching records, and a failed/unavailable response. An HTTP error cannot simultaneously claim that no earnings are scheduled or no news exists. The native earnings path matches the current server registration at /api/v1/earnings/today-next-business-day; the observed phone 404 requires checking the saved backend and running host rather than changing that contract.

## Verification

Tier: component UI checkpoint; this is not strategy promotion or authorization to place orders. Edited XAML parses as XML. The parent coordinates the affected Android compiler build, existing contract checks, independent engineering review, installation preserving app data, and phone inspection. No order is sent for UI verification. Device checks must cover navigation, restored input/disclosures, back/close round trips, unavailable feeds, and same-command recovery after interruption. Final build/device results are recorded by the parent after source freeze.

Checkpoint result: Android Debug APK compiled with 0 warnings and 0 errors; the updated app was installed preserving data at 09:52:44 Europe/Berlin on 8 November 2026. The native XAML resource test passed. Independent engineering review passed after fixing stale account counts and the clock touch target. Phone visual interaction was stopped by the user's Escape key, so post-install navigation and backend connection were not claimed verified. The user subsequently requested a more substantial mobile trading layout; that next pass is separate from this checkpoint.
