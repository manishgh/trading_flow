# Shared Web News and Earnings acceptance

News, candidate desk, stock desk and Earnings now share a normalized presentation contract, Razor partial and live renderer. Text is escaped; only absolute HTTP(S) URLs become links. Provider/source/classification, UTC timestamp, nullable sentiment and Earnings why-it-matters remain visible. Sentiment bands match the News query's ±0.15 boundaries. Exact normalized symbol membership replaces substring/whole-combined-string comparisons. Selected-symbol scope/count survives live patches. Authenticated group actions exclude the MARKET sentinel and invalid equity symbols, including transitions between a macro-only and mixed-equity story. Opening an action still changes nothing until Apply.

The Earnings field review uses this matrix. Provider values remain nullable and advisory; numeric bounds exclude unknowns. Current-result and previous-quarter evidence remain distinct.

| Returned evidence | Available control or presentation | Reason for display-only fields |
| --- | --- | --- |
| Ticker, company | Search; ticker detail/group actions | Exact identity remains visible. |
| Scheduled time, exchange/operator day, release window | From/to range, today/next reporting day/week, session and day filters; chronological sort | Formatted UTC/NY text is derived, not an independent filter dimension. |
| Estimated schedule flag | Estimated / provider-reported schedule filter | This describes schedule certainty, not whether a release happened. |
| EPS estimate/actual/surprise and outcome | Numeric estimate/actual/surprise ranges, absolute surprise, outcome filter/sort | Negative EPS values are valid; absent actuals stay pending. |
| Revenue estimate/actual/surprise | Amount ranges in USD millions; surprise ranges and sort | Missing values do not become zero. |
| Observed-result timestamp/actual presence | Observed/pending result filter; provenance disclosure | Exact first-seen timestamp is provenance, not another release-time estimate. |
| Market cap/band | Cap bands, numeric range in USD millions and sort | Units are explicit. |
| Event return and provider one-day reaction | Separate numeric percent ranges; event-move sort | They are different measures and are never substituted for each other. |
| Slot RVOL and news sentiment | Numeric ranges; RVOL sort | Slot RVOL is distinct from daily/vendor RVOL. |
| Earnings/news providers, headline/news presence | Provider filters and Has news; shared News rail | Source URLs remain safe links, not a filter target. |
| Result/breakout assessment | Outcome/breakout filters, attention lanes, visible reason | AnalysisReason/label are explanations of the underlying states. |
| Holdings/group memberships | Authenticated held/group intersections | Anonymous callers cannot read private scope. |
| Reference high/close, latest completed bar/session/close, EMA10/20 and MACD | Expanded event analysis and breakout explanation | These are the particular event's derived technical context; filtering the breakout state retains its declared assessment rather than constructing a new trading rule. |
| Previous quarter | Separate expanded historical-result comparison | Historical amounts/outcome belong to a different event and are not silently used as current-result filters. |
| IDs, generated/received timestamps, URLs, labels, monitoring/window status | Provenance, coverage and stale/error display | Identity/transport/formatting fields are not business filter dimensions. |

Existing 35-day range limit, New York calendar grouping, 2,000-event response cap, total/truncation disclosure and authenticated scope checks remain intact. All new controls round-trip through the URL.

Requirements and independent code/design reviews completed. Findings about combined symbol scope and MARKET group actions, including server/live transition consistency, were corrected. Focused News/Earnings tests: 25 passed with Web compilation. The affected browser slice had 35 existing passes plus a fixture targeting the wrong calendar route; the corrected complete group/News/Earnings slice passed all eight tests, followed by a final focused recheck after the server/live transition fix. No Android code, broker order, operational data, GitHub push or cloud resource changed.
