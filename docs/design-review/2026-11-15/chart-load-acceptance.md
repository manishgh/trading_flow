# Chart load acceptance

15 November 2026. The three-wave WebSocket fixture passed in the review run, which recorded 587 passes, zero failures and two opt-in skips. Its raw measurements are in `.tmp/approved-ui-20261115/review-fixes-and-load.trx`. The earlier measurement run recorded two passes, including the warm intraday source test below, in `.tmp/approved-ui-20261115/chart-load-measured.trx`.

## Authenticated WebSocket fanout

The test held open 128 authenticated loopback SignalR connections, four tabs for each of 32 synthetic operators. Each connection sent one subscription. The fixture supplied an initial 500-candle baseline and two revisions on the hub's ordinary five-second poll, with nine dense studies on each baseline. Each reader verified its sequence and epoch, the latest candle and revised indicator values, and the history cursor. The test made 384 chart-reader calls across the three waves. The active window was 12,825.66 ms, including two seconds idle with all sockets open. The test completed in 15.185 s.

| Wave | Time to last chunk, p50 / p95 / max | Cumulative reader calls | Snapshot JSON bytes per reader | SignalR record bytes, total / maximum per reader |
| --- | --- | ---: | ---: | ---: |
| Initial subscription | 464.90 / 509.06 / 522.33 ms | 128 | 260,297–260,299 | 37,441,840 / 292,515 |
| First revision | 5,065.05 / 5,095.23 / 5,096.43 ms | 256 | 260,292–260,294 | 37,441,200 / 292,510 |
| Second revision | 5,089.25 / 5,128.59 / 5,129.56 ms | 384 | 260,292–260,294 | 37,441,200 / 292,510 |

Initial latency starts when each client sends its subscription. Revision latency starts when the fixture publishes a source version and includes the ordinary polling wait. Each baseline used 33 chunks. SignalR byte totals cover JSON stream records and their separators. The 256 KiB ceiling applies to the unescaped snapshot, including the history cursor.

| Process sample | Working set, bytes | Estimated managed heap, bytes | Cumulative GC counts, gen 0 / 1 / 2 |
| --- | ---: | ---: | ---: |
| Before | 433,815,552 | 215,636,808 | 178 / 50 / 18 |
| After initial subscription wave | 823,226,368 | 319,291,656 | 217 / 76 / 20 |
| After first revision | 885,727,232 | 520,353,672 | 248 / 96 / 21 |
| After second revision | 1,122,615,296 | 531,368,696 | 278 / 115 / 22 |
| After two seconds idle with sockets open | 1,123,033,088 | 531,977,128 | 278 / 115 / 22 |
| After shutdown | 1,123,495,936 | 533,071,392 | 278 / 115 / 22 |
| Two seconds after shutdown | 1,124,069,376 | 533,596,056 | 278 / 115 / 22 |
| Sampled peak | 1,124,069,376 | 854,818,400 | |

The test process hosted the server and all 128 clients, including client-side JSON reconstruction. Samples were taken every 25 ms without forced collection. Collection counts rose during the waves and stayed fixed in the idle and shutdown samples. The working set remained elevated through the final sample. The review run also executed other tests in the process. This short loopback test describes transport memory and latency for prepared synthetic snapshots. A production memory measurement or a longer endurance test would cover different conditions. Numerical cache bounds and source reuse are covered by separate fixtures.

The earlier single-burst run used the same 128-connection setup and stopped after the initial subscription. It recorded 128 reads, p95 latency of 735.53 ms and a sampled working-set peak of 646,979,584 bytes for the combined test process. That run is retained as historical evidence in `chart-load-measured.trx`.

## Warm intraday source reuse

The series-service test prewarmed two compatible minute windows and one native daily window. Nine waves of four readers requested VWAP, cumulative RTH RVOL, a daily SMA overlay, and daily structural contexts on the same 5m candles. RVOL used 20 verified matching-time prior sessions.

| Measurement | After prewarm | After nine waves |
| --- | ---: | ---: |
| Minute history acquisitions | 30 | 30 |
| Native daily history acquisitions | 1 | 1 |
| Exchange-calendar reads | 2 | 2 |
| Indicator calculations started | 0 | 4 |

Each distinct study profile calculated once across the four readers. Further context-only or disabled selections left the calculation count at four. The final derived cache held four profiles, 1,872 sample positions and 62,912 accounted bytes. The fixture used deterministic synthetic archive responses and an unstarted stream. No live provider subscription, data request, broker call or economic run was involved.
