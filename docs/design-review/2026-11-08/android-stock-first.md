# Native Stocks-first redesign — 8 November 2026

The user requested a substantial Android redesign after the earlier installed navigation cleanup still felt like a collection of forms and large buttons. The approved next pass makes followed stocks the main surface and retains the same native MAUI contracts and reviewed execution path.

## Primary references and adaptation

The parent inspected official [Robinhood lists guidance](https://robinhood.com/us/en/support/articles/watchlist-and-cards/), [Robinhood stock buying flow](https://robinhood.com/us/en/support/articles/buying-a-stock/) and [Trading 212 watchlist guidance](https://helpcentre.trading212.com/hc/en-us/articles/360008728357-How-do-I-create-and-customise-a-watchlist). The adopted patterns are a stock list as the investing entry, stock detail before trade review, and explicit list management. Their order defaults, execution behavior and financial claims are not imported.

## Native changes

Stocks replaces the button hub as the first tab. The existing WishlistsPage still loads saved groups and a single selected desk, retaining existing quote/activity streams. Compact auto-height rows show ticker, supplied company name, real quote and quote time. No price-change percentage is available in this contract, so none is invented. Older-day quotes include the date and year. US quotes and account amounts use explicit USD formatting. The list is virtualized; row height is a minimum rather than fixed, and font autoscaling remains enabled.

Group selection and local stock search stay at the top. Add/suggestions, source metadata and optional scopes appear only after Manage, inside the scrolling list header so landscape and large fonts cannot strand those controls. Tapping a stock opens the existing SymbolDetailPage. A secondary swipe preserves direct article access. The former Desk hub remains Daily tools under More. Existing named routes remain registered. Five code-native SVG icons accompany Stocks, Trading, Strategies, News and More. Purple and neutral tokens match the web workspace; Android resource colors no longer introduce the template blue.

Stock detail shows an actual mid quote with bid/ask and its source date, holdings, selected strategy and its current status. Recorded strategy/model metadata moves behind disclosures while unavailable/error states stay visible. Review buy stays in a fixed bottom footer, clearly marked paper trading. It still checks the existing operator/candidate authorization and opens the same protected ticket. The modal card is bottom aligned; preview, confirm, unknown-outcome retention and Close/Back guards are unchanged.

Trading adds two quiet account facts, Cash and Invested, only after an available read less than 30 seconds old. No account total, return curve or pooled profit is inferred. A refresh or another guarded operation hides the summary, and failed or changed-session reads clear it. Existing position/order recovery remains outside the local display switch.

## Request ownership and failure states

The existing LatestMarketRead service now fences Stocks snapshots and stock intelligence reads. Group selection begins a replacement read. Late snapshots must match the visible session and exact selected group. Stream callbacks recheck page, session, group and cancellation inside their queued main-thread callback. Navigation/account changes cancel streams and remove former rows/selections/suggestions. The In trade scope now uses row.Trade rather than the display string No open trade.

Stocks distinguishes sign-in required, a missing backend, failed reads and a successful empty group. Sign in/Open settings use existing routes and never modify stored credentials or preferences. The observed expired backend is corrected through normal Settings; no raw preference or authentication bypass is used.

The separate native News/Earnings pass is owned and documented by the parent/other implementation agent. This change does not edit web source or create a new provider, market-data endpoint, order path or dependency. The redundant health request on each stock refresh is removed; saved-group/desk responses supply the actual read result.

## Verification scope

Tier: component UI checkpoint. Parse edited XAML/XML/SVG, check resource references and original line endings, compile the affected Android target, and run MobileMarketReadTests for the reused cancellation/session guard. Existing native resource checks cover packaging. Independent review additionally checks queued stream ownership, quote currency/time labels, structured holding scopes, unavailable states and the retained order path. Parent records final build, Wi-Fi installation and device results. No broker order is placed for verification. No new shared guard abstraction or synthetic market fixture is introduced.

## Final checkpoint verification

On 8 November 2026 the final Android Debug APK compiled with zero warnings and zero errors in 2m29.65s. The final source includes the reviewed stock-detail Buy handler fix: duplicate taps are serialized and connection/navigation failures remain inline only for the same visible stock, group and account session.

The focused MobileMarketReadTests and NativeExitReviewTests passed 47/47; the native XAML resource check passed 1/1. Independent senior engineering review by popup_review passed the final Stocks, detail, order-sheet, account-summary and News/Earnings source with no remaining static findings. These checks do not claim visual phone validation or real broker execution.

The APK was installed using adb install -r over the authorized Wi-Fi debugger on CPH2747, preserving application data. Package com.manishgh.tradingflow reports lastUpdateTime 2026-11-08 10:18:51 (Europe/Berlin). Its display version remains 1.0; use the installation timestamp to distinguish this update. The reverse connection for localhost port 5088 is configured. The phone's saved ngrok backend was observed returning HTTP 404; the operator was instructed to use More > Settings > http://127.0.0.1:5088 > Save & Test. No stored preferences or credentials were changed outside the normal Settings flow.

Physical-device screen inspection remains paused by the user's Escape key. Computer Use must be re-enabled before the final visual and interactive phone check; the installation result alone is not proof that every screen fits the device. The web operator app remains running at http://127.0.0.1:5088 on main. Its preceding popup/Desk checkpoint passed 43/43 affected browser checks and is recorded in commit 7cfc274. No broker order was placed. SMA50/200 and recent-news-count ideas remain discussion only.
