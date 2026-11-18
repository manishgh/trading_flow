# Earnings page: releases without figures and stock-scoped news — 18 November 2026

Approved by the user on 18 November 2026 ("Approve both"). Display and monitoring only: no strategy, order or
research behaviour changed.

## Observed problem

- ICCM and KNDI published results before the open on 18 November ("IceCure Reports Financial & Operational Results
  for the Nine Months Ended September 30, 2026…" at 13:25 UTC; "Kandi Technologies Reports Unaudited Financial Results
  for the Nine Months Ended September 30, 2026" at 12:00 UTC, both via Finviz news). The Finviz calendar, last read at
  13:56 UTC, still had no EPS or revenue actuals, so both rows read "Earnings pending" although the app had matched
  each release. A matched headline counted as proof of release only when figures could be assessed.
- News scoping worked only through a "Show evidence" button, and the news list it changed sat about 7,300 pixels
  below on the page. The "$2B+" filter already narrowed the news (56 to 37 stories, all for listed stocks); today's
  82 stories came from Finviz (60) and Alpaca (22).

## Change

- **Fix A.** `EarningsWatchWindow.HeadlineShowsRelease` is the single rule: a structured result headline always counts;
  a release headline without figures (`EarningsNewsResultExtractor.IsResultsRelease`) counts only once the slot's
  release window has opened. Announcements, delays, filing notices, expectations and non-financial "results" (annual
  meetings, trials, studies, tender offers, offerings, transcripts) are excluded. The analyzer, the watch window and
  the API use it. Such a row reads "Reported · figures not yet published" (EPS badge "Figures not yet published"), no
  figure is inferred, the API returns `FirstResultSignAtUtc` and `ReleasedWithoutFigures`, and the page reads the
  server's first result sign instead of repeating the rule. The Result filter counts these rows as observed; under the
  EPS buttons they appear only under "All results".
- **Fix B.** The ticker on each card and a "<TICKER> news" button show only that stock's news (both providers) and
  bring the start of the news list into view unless it is already in the upper half of the screen (instant under reduced motion; first tried as "nearest", which left only 63 of 630 pixels visible on the live site, reviewed again and accepted). "All news" is always visible and shows news for every
  stock the filters list; a selected stock that the filters stop listing returns to all news.

## Known limits (from review)

- The exclusion list deliberately misses some genuine figure-less releases whose headline also says, for example,
  "expects", "today", "phase" or "study" (common in small biotech headlines). Those rows stay "Earnings pending" until
  figures arrive, as before.
- A figure-less release published before the slot's release window (the Sony case) is not counted until figures
  arrive.
- The phone app (`TradingFlow.Mobile/Services/TradingFlowApiClient.cs`) still applies the old rule for its "Results
  out" line; it shows the new label text but not that line for these rows. Not changed; raised with the user.

## Verification

Debug build 0 warnings/0 errors. Earnings and news .NET tests **150/150** (new: release-headline theory with the ICCM
and KNDI headlines and 23 negative cases, window test, release-without-figures watch-window test, analyzer test, view
filter test). Earnings browser spec **9/9**; six other browser specs that open the Earnings page **80/80**. Reviewer A
(plan/execution) and reviewer B (code/design) first requested changes (preview-style headlines counted as releases;
no window check; missing view test; scroll behaviour) and accepted after the fixes.

## Installation on the local site, 18 November 2026

Exposure checked before each restart (Alpaca paper positions and open orders `[]`; no queued or running jobs). Release
`0a84409` was installed at 14:38 UTC (previous build kept as `bin-before-0a84409-20261118`), then `9a65f6b` with the
scroll change at 14:41 UTC (the intermediate `0a84409` copy it replaced was removed). Readiness `ready`, order stream
connected, no errors and no result-marker warnings after start.

Live check: ICCM and KNDI are the two rows on today's page with "Reported · figures not yet published" (EPS badge
"Figures not yet published"), first result sign 13:25 and 12:00 UTC, card line "Results out · first seen … · figures
not yet published". Their explanation sentence was saved by the previous build and still uses the old wording until
their next analysis pass. Selecting KNDI showed "KNDI · 1" story and "All news" returned all 65; selecting ICCM from the
top of the page scrolled the news column into view with only ICCM's results story.
