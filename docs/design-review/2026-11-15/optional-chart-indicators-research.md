# Optional indicators for the reusable stock chart

15 November 2026 - Research only; proposed follow-on, not implementation approval.

## Recommendation

Keep the existing C# indicator engine and Skender.Stock.Indicators package as the calculation authority. Use the shared chart service and renderer from the [14 November design](../2026-11-14/candles-support-resistance-design.md) for a dedicated stock chart page and embedded panels. Any supported stock link should open that same page with its canonical symbol and optional horizon. The server still validates symbol scope, source and permissions; a link does not start an arbitrary provider subscription or expose account/run markers.

Offer a small optional set: horizon-specific moving averages, RSI14, ATR14/ATR%, and verified relative volume. Keep MACD and Bollinger Bands in an expanded menu. Defer anchored VWAP and volume profile until their additional semantics are designed. These are descriptive views of observed prices and volume, with no demonstrated predictive edge or automatic trading permission.

This ranking is an engineering/product recommendation based on information diversity, existing code and data cost. It is not a ranking of strategy profitability. No performance experiment, library benchmark, installation, provider-data request or broker request was performed.

## Reuse already present

Read-only inspection found:

- `src/TradingFlow.Engine/TradingFlow.Engine.csproj` pins `Skender.Stock.Indicators` **2.7.1**. Its locally installed NuGet manifest and LICENSE identify Apache-2.0; the package includes NOTICE.
- `Indicators/IndicatorEngine.cs` already computes SMA5/10/20/50/150/200, EMA5/10/20/50/200, RSI14/2, ATR14, ADX14, OBV, MACD(12,26,9), Bollinger(20,2), session VWAP and volume evidence.
- `Indicators/MarketEvidenceProfile.cs` defines `us_equities_same_time_rvol_v1`: 20 prior sessions and 20 valid samples by default. The engine uses median-based same-time baselines and separate slot/cumulative measures.
- `Strategies/SignalGenerator.AnchoredVwap.cs` contains strategy-specific moving-anchor selection. Its `high_volume_node` means the highest-volume **bar**, not volume concentrated at a price. It is not a volume-profile implementation. A generic chart must not silently invoke those strategy choices.

Upstream Stock Indicators documentation now describes v3 APIs and explicitly links a v2 migration/legacy site. The legacy pages could not be fetched in this research. Current upstream formula and convergence guidance below is informative; exact 2.7.1 seed, null and numerical behavior must be checked with pinned-package fixtures before exposing new series. Do not copy v3 streaming APIs into the installed v2 application or upgrade as part of chart work.

## Ranked optional presets

Presets select sensible controls; users can hide each item. Existing candles, volume and support/resistance remain the visual priority. Start with no extra oscillator pane; enabling a preset may open one, with RSI and MACD offered as alternatives.

| Preset | First choices, in order | Secondary choices | Structural context that must remain available |
|---|---|---|---|
| Intraday | 1. Existing bar-based RTH VWAP; 2. verified cumulative same-time RVOL readout; 3. EMA20 on the selected completed interval; 4. ATR14/ATR% readout | RSI14 pane; slot RVOL as an explicitly different measure | 15m confirmed zones plus daily structural zones and previous completed week/month/year high-low, each separately labelled |
| Swing | 1. Daily SMA50/200; 2. daily RSI14; 3. daily ATR14/ATR% | Daily Bollinger(20,2), or MACD(12,26,9) instead of RSI; completed daily relative volume only under a separately named formula | Daily zones, optional completed weekly zones, prior week/month/year ranges |
| LongerTerm | 1. Daily SMA50/200; 2. completed weekly candles with optional SMA40-week; 3. trailing-year range context | Weekly RSI14 only when independently warmed; optional Donchian20-week | Weekly structure plus month/year references; daily zones remain selectable |

Lengths are periods of the labelled source, not universal numbers of days. EMA20 on 5m bars is different from EMA20 on daily bars. Keep a daily average daily when overlaying it on an intraday chart. Weekly SMA40 is not an exact replacement for SMA200-day. Preset selection is observational; it neither chooses a strategy nor changes its settings.

### Week, month and year support/resistance

The user's scope includes both intraday detail and week/month/year context. Treat display interval, structural source interval, and lookback horizon as separate controls. Scrolling a 5m chart must not discard higher-timeframe context.

Retain confirmed pivot zones as **structure** and label calendar high/low pairs as **prior ranges**. Previous calendar year, year-to-date through the latest completed session, and trailing 52 calendar weeks are different ranges; never label all three "yearly". Proposed defaults are previous completed week/month/year high-low, with a separately named trailing-52-week range available. A 252-session range must be labelled 252 sessions rather than presented as an exact calendar year.

Use verified daily constituents and the New York exchange calendar for calendar ranges; use complete weeks for weekly pivots. Partial current weeks/months/years cannot become completed-period references. A weekly pivot requiring two right-hand weeks becomes available only after those weeks complete and are observed. Historical line segments must start at that availability time, not at the pivot date. Thin listings show available coverage and omit unsupported ranges. Monthly-candle pivots and multi-year structural windows need separate history limits and confirmation rules; daily/weekly context over those horizons can come first.

Show only the nearest useful bands from each enabled source, with the full set in an accessible list. Do not merge daily and intraday confirmations into an unexplained strength score.

## Data and formula requirements

All initial candidates below use completed OHLCV. Trade prints are not needed for their stated bar-based versions.

| Indicator | Definition and data | Important interpretation |
|---|---|---|
| SMA / EMA | SMA averages N closes; EMA recursively weights closes using 2/(N+1), with an explicit seed | Lagging price summaries; initialization changes early EMA values. Use the engine's pinned implementation. [EMA documentation](https://dotnet.stockindicators.dev/indicators/ema) |
| RSI14 | Wilder-smoothed gains/losses of close changes; 100 - 100/(1 + average gain/average loss) | Define flat and zero-loss cases through pinned fixtures. 30/70 guides are optional reference lines, not buy/sell instructions. [RSI documentation](https://dotnet.stockindicators.dev/indicators/rsi) |
| ATR14 / ATR% | True range is max(high-low, abs(high-previous close), abs(low-previous close)); Wilder smoothing. ATR% = 100 x ATR/close | Includes price gaps; describes range, not direction or a mandatory stop. [ATR documentation](https://dotnet.stockindicators.dev/indicators/atr) |
| MACD(12,26,9) | EMA12 - EMA26; signal EMA9 of MACD; histogram is their difference | Adds a second view of trend/momentum and requires substantial pre-roll. Optional rather than another default pane. [MACD documentation](https://dotnet.stockindicators.dev/indicators/macd) |
| Bollinger(20,2) | SMA20 plus/minus twice close standard deviation | Pin population/sample convention. Bands are neither confirmed support/resistance nor a guaranteed 95% price interval. [Bollinger documentation](https://dotnet.stockindicators.dev/indicators/bollinger-bands) |
| Same-time RVOL | Completed current slot or cumulative session volume divided by the existing median of eligible historical counterparts | Show mode, session cohort, baseline length, sample count and SIP source. Do not substitute Finviz RVOL or a generic last-N-bar ratio. TradingView documents these distinct concepts but its mean/carry-forward choices are not TradingFlow's contract. [Relative Volume at Time](https://www.tradingview.com/support/solutions/43000705489-relative-volume-at-time/) |
| Anchored VWAP | Sum(HLC3 x volume) / sum(volume), from an explicit fixed anchor and source interval | Reasonable later addition for a user-selected bar/event; label bar approximation and anchor time. An event's occurrence and receipt differ. Moving retrospective anchors must not rewrite past available evidence. [VWAP documentation](https://dotnet.stockindicators.dev/indicators/vwap) |
| Donchian | Highest high / lowest low over N completed source bars, with optional midpoint | Simple range alternative when pivot zones feel too complex. Specify whether the endpoint excludes the current bar; use prior-N for a pre-existing reference. It is not a confirmation count. [Donchian documentation](https://dotnet.stockindicators.dev/indicators/donchian) |

**Volume profile is different.** OHLCV does not reveal how much traded at every price inside a candle. An approximate profile can allocate lower-timeframe volume to price bins, but must disclose source resolution and allocation; TradingView itself documents lower-timeframe bar calculations and up/down-bar volume. Exact eligible trade volume by price requires trade prices/sizes plus condition, correction and cancellation handling. Buyer/seller-initiated volume additionally needs an explicitly justified classification method; candle colour does not supply it. Defer profile, value area, POC and order-flow labels until a separate bounded-data design exists. [TradingView methodology](https://www.tradingview.com/support/solutions/43000502040-volume-profile-indicators-basic-concepts/)

## Sessions, adjustments and revisions

Carry symbol identity, feed, source interval, session view, price/volume adjustment policy, formula/package version, parameters, source cutoff, availability time, coverage and warm-up status with each result. Display compact labels such as "RSI14 · 1d · SIP" or "VWAP · 1m inputs · RTH · bar-based"; put full provenance in details.

Filter RTH inputs before calling the existing VWAP math: it currently resets by New York date and includes whatever bars it receives. Use one-minute inputs so a chart interval switch does not change the underlying VWAP definition. EMA/RSI/ATR on RTH observations normally continue across sessions; do not reset all indicators at each open. Explicitly retain the prior eligible close for overnight true range. Premarket and shortened sessions require their own coverage and cohort handling.

Do not fill gaps with invented flat candles or silently carry indicator values through unknown intervals. Verified no-trade intervals differ from missing observations. Suppress affected dependent results, show the reason, and rebuild after repair. Late corrections invalidate the affected suffix; recursive EMA/RSI/ATR/MACD can influence all later values, while fixed-window calculations have bounded dependency windows. VWAP revisions affect the remainder of its anchor segment. Preserve historical availability/version evidence separately from today's reconstructed values.

Every price overlay must use the candle's basis. Splits adjust both prices and volumes; dividend adjustments affect prices and change the visual meaning of historical levels. Never combine adjusted historical inputs with raw current prices without a verified transformation. The provider's symbol `asof` mapping is not proof that current adjusted history was available at a past decision time. [Alpaca bars and adjustment contract](https://docs.alpaca.markets/us/reference/stockbars)

## Warm-up and resource implications

A non-null first value is not proof that a recursive indicator is stable. Current upstream guidance recommends roughly 140 pre-roll observations for RSI14, 264 for ATR14, 270 for EMA20 and 285 for MACD(12,26,9). Treat these as starting guidance for pinned-version parity checks, not a new approval to expand data budgets. SMA200 needs 200 genuine observations; Bollinger20 needs 20. Weekly calculations need weekly observations, not the same number of daily bars.

Two concrete conflicts with the earlier chart design need a follow-on decision:

1. Its 15-session intraday seed limit cannot produce the existing 20-prior-session RVOL baseline from cold. Reuse compatible already-warm evidence, otherwise show unavailable until an explicitly bounded history expansion is approved.
2. A 500-daily-input ceiling cannot show 252 fully warmed MACD points using 285 pre-roll bars (roughly 537 inputs); ATR has a similar smaller excess. Shorten the indicator's available plotted suffix or separately review a larger input budget. Do not silently draw unstable early values. A 550-daily-input weekly projection also cannot furnish 140 weeks of RSI14 pre-roll.

Share calculations by canonical series, indicator profile, revision and cutoff, not by viewer. Reuse published engine results where compatible; expose reusable pure primitives when needed without invoking strategy admission. Compute requested families only, cache bounded results, and coalesce identical requests. Initial bounded batch calculation is acceptable pending measurement; calling today's whole indicator suite separately for every viewer and every preview would multiply scans and allocations. Keep previews outside these completed-bar calculations.

For later incremental optimization, preserve fixed seed/checkpoint state and replay corrected suffixes. Re-seeding an EMA from a shifting viewport changes values even when the underlying history did not change. Check parity before optimizing. Measure CPU, allocations, cache size and update latency at the already-approved chart fanout; no benchmark results are claimed here.

## Ready-made library assessment

| Candidate | License and practical fit | Recommendation |
|---|---|---|
| Stock Indicators for .NET, installed 2.7.1 | Apache-2.0; already integrated, broad standard indicators, nullable series output. Preserve LICENSE/NOTICE. New upstream v3 streaming interfaces are a separate migration. [Project](https://github.com/facioquo/stock-indicators-dotnet), [license](https://raw.githubusercontent.com/facioquo/stock-indicators-dotnet/main/LICENSE) | First choice: existing C# engine behind the shared chart service |
| TradingView Lightweight Charts | Apache-2.0 plus documented attribution/link and NOTICE requirements. Renders lines, histograms, candles and panes; the MA example supplies calculation outside the renderer. [Guide](https://tradingview.github.io/lightweight-charts/docs), [example](https://tradingview.github.io/lightweight-charts/tutorials/demos/moving-average), [license](https://raw.githubusercontent.com/tradingview/lightweight-charts/master/LICENSE) | Keep as the rendering layer chosen in the original design; do not mistake it for a server indicator engine |
| trading-signals (JS/TS) | MIT. Current package documents streaming, replacement, rolling windows and no runtime dependencies. No local benchmark; replacement support does not prove arbitrary older-revision replay. [Package README](https://raw.githubusercontent.com/bennycode/trading-signals/main/packages/trading-signals/README.md), [license](https://raw.githubusercontent.com/bennycode/trading-signals/main/packages/trading-signals/LICENSE) | Credible JS alternative for a separate JS product, unnecessary duplication here |
| technicalindicators (JS/TS) | MIT; batch/next-value browser/Node usage, common indicators including a volume-profile implementation. Presence of an indicator is not evidence of matching data semantics or current build compatibility. [Project](https://github.com/anandanand84/technicalindicators), [license](https://raw.githubusercontent.com/anandanand84/technicalindicators/master/LICENSE) | Lower fit: requires parity, revision and package compatibility assessment with no current need |
| TALib.NETCore | Independent C# adaptation; README identifies LGPL-3.0 and zero dependencies. Its license is not interchangeable with original TA-Lib's terms. [Maintainer README](https://raw.githubusercontent.com/hmG3/TA-Lib.NETCore/main/README.md) | Credible alternative, but changing numerical conventions and distribution obligations brings no demonstrated benefit here |

Pin exact artifacts and retain notices if any new dependency is later selected. No claim of comparative speed, audit quality, or newest release suitability is made from README claims alone.

## Proposed acceptance for a later approved checkpoint

- A stock link from Desk, news, wishlist or another authorized host opens the same chart component/service; equivalent symbol/profile/cutoff returns equivalent indicators. Symbol-only responses contain no private run/account markers.
- Source interval stays explicit across 1m/5m/15m/daily/weekly switches. Intraday plus week/month/year context is accessible. Completed calendar ranges and rolling ranges cannot be confused.
- Pinned fixtures cover flat prices, one-sided prices, zero volume, gaps, split basis, session opens, holidays/early closes, DST, thin listings and missing warm-up. Verify seeds and standard-deviation convention.
- Full batch, incremental append, reconnect and correction replay agree within a declared numerical tolerance. Loading older pages cannot change already-established values through a different seed.
- A later bar/revision never changes an earlier point-in-time snapshot. Future weekly confirmations, an event anchor or a current corporate-action adjustment cannot be backdated.
- Indicator toggles leave candles legible, cap additional panes/lines, expose keyboard-readable values and show clear unavailable reasons. Disabled indicators do not generate unnecessary history requests.
- Bounded fanout stays within chart budgets; test slow readers, canceled symbol loads, correction bursts and shared-cache reuse. Record focused engine/Web/UI results plus required independent checkpoint reviews.

Research verification: inspected the named code and installed package metadata, checked primary-source documentation and document whitespace. No production files, checkpoint plan, settings, datasets or running services were changed; no builds or economic runs were executed. No temporary files were created or deleted (0 bytes freed). This research note is retained as design context.
