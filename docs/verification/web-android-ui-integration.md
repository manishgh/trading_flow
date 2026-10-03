# Web, Android and API integration verification

Date: 2026-10-03. This records the local source integration and offline behavior; it does not authorize a strategy, place a broker order or claim external-provider acceptance.

## Preserved work and merge

Original checkout: `C:\project\trading_flow`, branch `unified-swing-product`, HEAD `8525f8070a5115f0d27dc869da716fc5a9803c42`. Its source, secrets, trading database and candle cache were preserved. The integration uses a separate worktree at `C:\Users\manis\Documents\Codex\2026-10-03\c\work\tf-integration` on `integration/web-android-ui-20261003`.

Local source changes were copied to that worktree and committed as `4736337` before merging `main` (`d4729fa`). Claude settings and prior test outputs were excluded. The original deleted legacy ticket stays deleted. A final SHA-256/deletion check matched all 233 recorded original entries, and the original HEAD remained unchanged. The subsequent local AGENTS explanation rule was read and carried into this integration; it was not overwritten in the original checkout.

## Resulting behavior

- `/Desk` remains the default stock desk: all ordinary active followed lists, available account holdings even without strategy jobs, bounded paging and complete-source search. Historical preparation revision lists remain excluded. Finviz discovery exposes the signal catalog and safely retains combined `f` and `s` selections.
- `/TradeDesk` keeps exact candidate review and a separate market explorer. Market-data warmup is labeled separately from immutable nightly/morning preparation. Previous-close change is shared by the stock view and legacy explorer.
- Web Orders and market rails use the shared order workflow. Legacy direct ticket handlers are removed. Strategy prices come through `POST /api/v1/orders/plan`, match exact ID/version/hash, and remain empty with reasons when unavailable. Neither UI supplies a fallback stop/target percentage.
- Research runs cannot offer executable entries. Ordinary operators enter through an authorized paper candidate; Administrator overrides capture a discovery universe and still pass server checks.
- Review summaries include quantity, stop, target, order type, time in force, extended-hours setting, exact strategy hash and expiry. Unknown confirmation outcomes retain the same immutable request and command key; Edit is disabled until a known rejection allows correction. A new ticket explicitly leaves the old order active.
- Android Trading now reads the account portfolio and the latest 200 account-bound journal entries independently of running strategy jobs. Cancellation and limit/day exits use shared durable commands. Existing strategy positions remain reachable. Web still offers the established wider exit types and time-in-force choices.
- Fractional positions remain visible, but full fractional exits are not offered because the audited close service accepts whole shares. No quantity is silently rounded.
- Symbol-only preparation links now reach `/api/v1/symbols/{ticker}/intelligence`. Android preparation child views own cancellation and session subscriptions; reappearance restores controls and a changed backend/session clears retained commands, including changes made while the page was hidden.
- Market Predictor remains advisory API v4. The consumer fixture hash `96cbcd133b8e96253b034fabba10624be79b0d80550f043f3869905251bec522` matches the committed producer fixture. The local producer worktree has a modified fixture hash `bc1f5109be83fc2985c28b0c0ded75e0487e34a68c237a1f147babc8e117a08b` with model/bundle lineage changes; no claim of byte equality with that dirty file is made, and it was not modified.

## Verification

Broad offline integration checks ran one heavyweight job at a time. Requirements and independent code/design reviewers inspected actual files; findings about Edit during uncertain confirmation, hidden-page backend changes, preparation navigation locks, duplicate strategy selectors and fractional exits were fixed.

Commands from the integration worktree:

```powershell
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter 'FullyQualifiedName!~AlpacaCandlePipelineIntegrationTests' -m:1 --disable-build-servers --logger 'trx;LogFileName=final.trx' --results-directory .tmp/integration-tests --verbosity quiet
dotnet build src/TradingFlow.Mobile/TradingFlow.Mobile.csproj --no-restore -f net10.0-android -c Release -m:1 --disable-build-servers --verbosity quiet
# tests/ui, against the isolated UI-test backend
$env:TRADINGFLOW_UI_BASE_URL='http://127.0.0.1:53019'
npx playwright test
```

- Offline .NET: **1,833 passed, zero failed**, including authentication/CSRF, durable order safety, immutable workflows, preparation persistence, deterministic replay, daily observations, previous-close selection and Finviz parsing.
- Android Release: **succeeded with zero warnings and errors** after the final rejection-display correction (1m12s).
- Browser: final 135-test run pending. Earlier complete run passed 134/135; navigation was corrected for Orders Ã¢â€ â€™ Positions. A separate startup test was stabilized by completing login navigation before injecting its single failure.
- JavaScript syntax checks passed for changed order, candidate and stock-desk scripts. Merge conflicts were resolved and staged. `git -c core.whitespace=cr-at-eol diff --check` passed.
- Desktop and phone screenshots cover the stock desk and shared Orders ticket; assertions cover 320Ã¢â‚¬â€œ1440px route fit, accessible names/IDs, 44px phone actions, internal links, live patches without focus loss, actual authenticated read APIs and request-bound strategy prices. No browser check places a real broker order.

## Android/ngrok status and unrun checks

A separate Debug package `com.manishgh.tradingflow.integration` / TradingFlowReview was installed on the attached phone, preserving the usual TradingFlow apps and their settings. Its initial backend was the old hard-coded ngrok URL; all screens returned 404 from that address. The user's preference is to use ngrok for this workflow. A tunnel to the isolated review backend on port 53019 is awaiting explicit approval after automatic approval review rejected public exposure of this specific test service. No new public tunnel has been created. Phone interaction paused while the device was in use.

The review backend runs with UI-test mode and a disposable database below `.tmp/ui-tests/phone-data`; operational hosted services are disabled and no local broker credentials were copied. Device navigation after the corrected ngrok connection, notifications/reconnect, real Alpaca previous-close and strategy price evidence, bound broker positions, Finviz provider results and warmup service acceptance remain to be checked. The live Alpaca candle pipeline integration test was deliberately excluded from the offline suite. No market-order/paper-order provider acceptance is inferred from fixture tests.

No GitHub push is part of this checkpoint. Any continuing ngrok/backend host and installed verification package should be tracked separately from the source merge.
