# Quick trading popup — 8 November 2026

## Approved behavior

The user requested a quick Buy/trading popup on desktop and mobile, with fewer fields and fewer unnecessary data reads. The parent approved reusing the shared OrderWorkflow and removing only reads proven to belong to the removed rail. The user also explicitly requested Android installation. This checkpoint covers the web operator surfaces and the parent task's companion native Android ticket change; installation and runtime verification remain with the parent.

Trading now keeps positions and the order journal across the workspace width. A symbol, position Review, New ticket or compact symbol box opens the same native modal dialog. Desk Buy/Sell and exact-candidate links use that dialog too. The dialog hosts the existing OrderTicket workflow and has an accessible heading, close control, Escape support from the iframe, focus restoration and bounded internal scrolling on narrow screens.

Symbol, side, quantity in shares, order type and price remain immediately available. Strategy/protection and less used order options use disclosures. Browser invalid-field handling reveals the containing disclosure before focus. Review shows quantity, price, stop/target, estimated cost, maximum loss, expiry and server warnings; exact strategy/hash, quote time and execution policy remain available under review details.

## State and service boundaries

Opening loads context only. It cannot send preview, confirmation or close-position commands. Closing keeps the iframe, entered values, server preview token and idempotency key. Reopening the same ticker/side/environment/wishlist or exact candidate/run resumes that workflow even if its quote-prefill price changed. A different target cannot discard a reviewed, busy or unresolved confirmation. Known resolved responses permit an explicit fresh ticket; Resume keeps the resolved result. New review resets the confirmation label and resolution flag after an explicit rejected-command edit.

All request fields for candidate/run/version/strategy identity, authorization, environment, server preview and confirmation remain in the existing workflow. No endpoint schema, order engine, risk calculation, business admission path or broker behavior was changed.

## Native Android scope

The companion native change is limited to `src/TradingFlow.Mobile/Pages/OrderTicketPage.xaml` and its `.xaml.cs` code-behind. The existing OrderTicketPage uses an animated Shell modal with a compact centered card, visible Close control, bounded content and internal scrolling. The four required draft inputs—quantity, limit price, stop price and take-profit price—remain immediately available. Strategy sizing/options and technical review evidence start collapsed. Essential prices, risk amounts, warnings and review expiry remain visible during review.

The native ticket retains the existing shared API workflow, exact strategy/candidate context, preview token, idempotency key and confirmation path. Close and Android hardware/gesture Back cannot dismiss an active request or an uncertain confirmation. If a confirmation request is cancelled before its outcome is known, the ticket enables Check same command and retains the original token/key for reconciliation; it does not create a new command. Independent native review passed. Android compilation and installation passed under the parent. Physical-device visual verification was not completed because the user stopped computer use.

## Actual read reductions

- The permanent Trading ticket no longer fetches orders/context, the strategy catalog or discovery sources merely because it exists in a rail. The OrderTicket iframe and its GET requests are created only by an intentional open (including a validated direct ticker link). A closed/reopened retained ticket does not reload those requests.
- OrdersModel no longer calls ConfigCatalogService.GetStrategiesAsync solely to populate the removed rail.
- OrdersModel no longer calls WishlistDeskService.BuildAsync for the selected ticker's quote/technical/position/list rail snapshot. The selected ticker is no longer added solely for the rail to the order-journal quote subscription.
- Account/status observations, symbol suggestions and working-order quote streaming remain because they supply the visible journal/account screen. Necessary confirmation/risk fields were not removed from API payloads.

These are removed/deferred code paths, not measured production latency or provider-call claims. The focused browser fixtures assert zero ticket-specific startup GETs and zero order writes before opening, exactly one context/catalog/source load on first open, and no second load on resume.

## Review and verification

Independent review was assigned to popup_review. Early findings addressed during implementation: reset completed state on explicit edit/new preview, keep review expiry visible, reset Confirm paper order text after a new review, and permit switching after a known resolved response while preserving pending-command recovery. Final static review passed with no remaining findings. Runtime results are recorded by the parent after its sequential verification.

Child checks: node --check passes for changed implementation scripts and focused popup/scroll/integration specs. Scoped git diff --check with cr-at-eol passes; original per-file newline styles are preserved. No build was launched by the child. The parent subsequently delegated the final isolated no-build browser verification and screenshot export to the child; those results are recorded below.

Updated browser specs: orders-screen, trading-workspace, integration-ui-api, watch-desk, workflow-integration and trading-capabilities. New quick-trade-popup spec covers deferred requests, no order write on open, keyboard traversal/Escape/restore, preserved values, lost confirmation replay, target-switch refusal, rejected/edit/pending transitions and invalid collapsed fields. The Trading scrolling spec captures desktop/mobile popup screenshots and checks viewport bounds, internal wheel scrolling, a reachable close control and unchanged underlying page scroll.

## Product feedback retained

Trading actions should be compact transient surfaces shared by every entry point. Show the fields needed for the immediate decision and disclose audit/technical detail; do not copy a full permanent trading rail onto the phone. A visual simplification must keep the exact command identity and uncertain-response recovery intact.

## Browser follow-up

The parent's initial full browser run completed 179/181 cases. The two failures were in the new keyboard/lazy-loading cases: the desktop assertion could pass before deferred ticket initialization, and Tab from the final iframe link did not reliably return to the host Close button. After the test host stopped, the form was made inert in initial markup, the test awaited positive loaded-context evidence and exact read counts, and both iframe boundaries gained explicit forward/reverse focus handling with the existing source/origin guard. Strict keyboard assertions remain and now cover both directions. Syntax and scoped whitespace checks passed; the final targeted runtime recheck passed as recorded below. The normal operator web host can run while the separate native Android screen refinement continues.

## Final focused browser verification

The final no-build run passed 43/43 cases in 43.2 seconds on the owned host at port 53018 (GUID b7ce1de7-705b-431f-9c8f-d89e9aa57cc8). This included both strict forward/reverse keyboard boundaries, Escape/focus restore, deferred ticket reads, command recovery, desktop/mobile scrolling, shared-theme light/dark comparisons, 44px controls and 16px input text. It also covered the approved Desk density/SMA refinement. The operator app on port 5088 was not a mutation-test target.

Thirteen screenshot deliverables and result.json were exported before cleanup to C:\Users\manis\Documents\Codex\2026-11-08\tra\outputs\quick-trade-final\run-b7ce1de7-705b-431f-9c8f-d89e9aa57cc8. Local screenshot inspection confirmed the compact 1265px Desk keeps both SMA columns in view and the 412px dark popup uses the shared rounded controls and palette. These are isolated browser fixtures, not production market observations.

The original cleanup reporter stopped its owned host and removed both exact GUID-owned .tmp/ui-tests/test-runs and .tmp/ui-test-artifacts roots. The exported result records both roots absent and 3,546,476 bytes measured in them before cleanup. The small temporary config/scripts/logs remain for the parent to delete after the related code commit; screenshot/result evidence is retained. Android build and installation passed under the parent; physical-device visual verification was not completed because the user stopped computer use. No further device or operator UI interaction was performed.
