# Web provider and preparation evidence

These are authorized finite provider reads and an isolated warmup check on 5 October 2026. They do not enable the operational review host, move collection ownership, authorize a strategy or place an order. Keys were read into process memory and omitted from every receipt.

| Check | Observed result | Limit |
| --- | --- | --- |
| Alpaca account and market clock, 21:02 UTC | ACTIVE paper account, configured account identity matched, tradingBlocked false; market closed | No broker execution tested. |
| SIP quote / previous close, 21:02 UTC | AAPL mid 332.895 versus prior close 333.69: −0.2382%; MSFT mid 525.00 versus 517.53: +1.4434% | After-hours capture only. Quotes/prices change; not a future trading recommendation. Before-open/in-session live capture remains separate operational acceptance. |
| SIP/all daily history, 21:38 UTC | 440 bars each for AAPL/MSFT; all last 260 completed exchange-calendar sessions present through 5 October, no remaining page | Two symbols; no claim for the whole universe. |
| Finviz overview export, 21:37 UTC | Two rows AAPL/MSFT; company, Technology sector and explicit USD-million cap parsed | Finviz discovery metadata does not confer admission. |
| Finviz pasted `f=` filter | 498 unique symbols for `ind_stocksonly,sh_avgvol_o500,sh_price_o10,ta_sma50_pa,ta_sma200_pa` | Export hit count at capture; not historical membership evidence. |
| Finviz catalogue signal `s=ta_topgainers` | 200 rows / 200 unique symbols | Local preset/query normalization is separately covered by focused tests; no API exists for listing the user's browser-saved Finviz screens. |
| Alpaca News | 20 recent AAPL/MSFT stories; latest publication 21:03:18 UTC, Benzinga source | Read-only provider sample; no classification or signal invented. |
| Finviz Earnings through production parser | 5 October page 1 parsed: zero items / zero total pages | Empty date-scoped response is disclosed, not filled with synthetic events or treated as whole-calendar coverage. |
| Strategy-derived ticket replay, 21:44 UTC | Real completed SIP/all daily candles and current SIP asks passed through IndicatorEngine and DeskTicketPlanService.Build | Captured-data replay, not an operational live stream or an execution grant. Both catalogue artifacts remain research-only. |
| Isolated warmup queue, 21:47 UTC | AAPL/MSFT pending → queued → warmed; run succeeded 2, failed 0; 426 daily bars each, 5/25 catalysts | Storage under `.tmp/provider-warmup-20261005`; nightly scheduler/Blob disabled; owned service stopped afterward. This verifies collection/cache preparation, not strategy qualification. |

The real-candle ticket replay identifies each exact strategy file hash and the latest completed session at 20:00 UTC. Results at that capture:

| Symbol | Strategy rule | Ask/limit | Stop | Target |
| --- | --- | --- | --- | --- |
| AAPL | Minervini ATR 2.5, target 10R | 332.90 | 315.76 | 504.31 |
| AAPL | Connors ATR 2.0, target 2.5R | 332.90 | 319.19 | 367.18 |
| MSFT | Minervini ATR 2.5, target 10R | 524.47 | 494.94 | 819.80 |
| MSFT | Connors ATR 2.0, target 2.5R | 524.47 | 500.84 | 583.54 |

No fixed stop/target percentages were substituted. Missing live/warmed state still produces an explicit unavailable plan; the existing focused ticket tests cover ATR, structural/VWAP/R targets, unstreamed symbols, insufficient bars and equity-based risk sizing. These prices are observations of planning logic and do not assert an entry signal, admitted strategy or authorized trade. Paper fills, resting protection, replace/cancel races and broker reconciliation require their separately authorized execution acceptance.

Sanitized detailed receipts are under `.tmp/integration-tests`: `alpaca-read-only-provider-receipt.json`, `alpaca-history-receipt.json`, `provider-history-news-finviz-receipt.json`, `provider-ticket-calendar-receipt.json` and `isolated-warmup-receipt.json`. The first history receipt script had local timestamp-conversion errors; the corrected typed-timestamp check succeeded and supersedes that failed script result. The existing operational warmup endpoint was unreachable; no operational runtime was started to conceal that observation. The finite isolated check succeeded instead.
