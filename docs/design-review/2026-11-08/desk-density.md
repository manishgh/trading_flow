# Compact Desk rows and wider news — 8 November 2026

## Approved scope

The user asked for less space per Desk stock, visible SMA10/SMA20 daily indicators and a wider news panel while keeping the web app available. The parent approved UI-only changes to Desk.cshtml, watch-desk.js and watch-desk.css and coordinated the one Razor rebuild. Later CSS/JavaScript fit refinements were explicitly authorized while the operator app remained running. SMA50/SMA200 grouping and recent-news badges were discussed only and are not added here.

The desktop workspace uses three fifths for stocks and two fifths for news, with news beside the table/detail from 1151px upward. Narrower screens remain stacked. Stock rows use a 40px desktop action minimum and 44px through the shared 700px mobile breakpoint. Compact cell spacing and two-line price/change and holding/P&L keep the row near 47px. Buy/Sell retain full ask/bid prices in accessible names and tooltips, and the selected-stock view and existing reviewed popup retain the exact trade price. Quote freshness remains visible in abbreviated form; full age, spread and source time remain available in the row title and selected detail. Membership, previous close and failed-check explanations remain in titles/detail instead of repeating beneath every row cell.

Initial desktop review found the 750px table minimum put SMA columns outside a normal 1265px pane. The approved follow-up uses a 540px minimum and shorter trade labels. Desktop tests now assert the SMA20 cell is fully inside the stock pane, rather than merely present in the document. Mobile horizontal table scrolling remains available without widening the page.

## Existing evidence reused

DeskReadService.DeskRow.Technical already carries Domain.Market.IndicatorSnapshot from DailyObservationService. The shared IndicatorEngine already calculates SMA10/SMA20 from completed daily closes. There are no endpoint, DTO, provider, engine, strategy, risk or admission changes and no additional data requests.

The browser uses only ready/insufficient daily observation snapshots with matching ticker (when supplied), 1d timeframe and a finite bar timestamp no later than the server snapshot's read time. Missing, failed, wrong-ticker, future and non-daily snapshots show an em dash. The same verified snapshot feeds both table and selected detail, which shows bar timestamp, New York/UTC time and feed. Market closure or an old quote does not hide otherwise valid completed daily evidence. Stream disconnection labels retained daily values as last observed; the UI does not calculate a new SMA or pretend the quote is current.

## Review and verification

The independent reviewer identified two global-theme boundary conflicts during static review: news positioning at 1151–1250px and 44px actions at 651–700px. Scoped rules now cover both boundaries. The focused browser cases include 1200px and 680px, in addition to 412/1280/1600px, and check row height, action size, 60/40 proportions, visible SMA20 bounds, genuine fixture values, missing/future values and valid daily indicators during a closed market.

JavaScript syntax and scoped whitespace checks passed. Parent-managed compilation/restart succeeded before the final static CSS/JavaScript fit adjustment; no child build was run. The next browser run must include the four affected popup/Trading/Desk specs, matching light/dark popup screenshots, exported Desk screenshots and original GUID-owned test cleanup. That final runtime run passed 43/43 cases in 43.2 seconds (GUID b7ce1de7-705b-431f-9c8f-d89e9aa57cc8).

The previous targeted popup run used GUID bbdc1181-e762-41c9-a367-897c4fc30b7c and completed 39/40 cases. Its one failure counted an unrelated in-flight Desk catalog request after sign-in as a Trading startup read. The fixture now counts only requests whose referring document is Trading or OrderTicket. All other cases, including desktop keyboard wrapping, pending-command recovery, bounded scrolling and shared Desk actions, passed. The owned test host stopped and original cleanup removed its GUID-owned database/cache/artifacts. The operator app remained available on port 5088.

Final evidence is retained in C:\Users\manis\Documents\Codex\2026-11-08\tra\outputs\quick-trade-final\run-b7ce1de7-705b-431f-9c8f-d89e9aa57cc8, including 1265px desktop, 412px mobile, 680/1200px boundary views and dark-theme Desk. All density, pane ratio, SMA20-fit, completed-day/missing/future-value assertions passed. Local screenshot review confirmed the requested density and visible indicators. The original owned-root cleanup succeeded; the exported manifest records 3,546,476 bytes before cleanup and both temporary roots removed.
