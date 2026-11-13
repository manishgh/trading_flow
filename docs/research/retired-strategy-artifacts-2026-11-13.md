# Retired strategy artifacts — 13 November 2026

The user retired single-stock RSI(2) mean reversion and its fund version on 13 November 2026
("Retire, go to ETFs") after the ten-year signal study found no edge over simply holding the
eligible stocks (`docs/research/reversal-signal-study-2026-11-13.md`). The retirement covers
every Connors RSI(2) long and short version, the snapback long and short versions and the
news-avoidance recovery version. They were removed from `configs/strategy-catalog.json`
together with their settings files, the two run profiles that served only them and the
admission profiles nothing else referenced. Research moves to the ETF trend core
(`docs/design-review/2026-11-13/etf-trend-core-design.md`).

Removing an artifact from the catalog is not a lifecycle decision about its results and
deletes no evidence. Each removed settings file is unchanged in git at commit `26d9448`
(the last commit before removal); restore one with `git show 26d9448:<source path>`.
Test-owned copies of all twelve files, the two run profiles and a catalog of their own live
under `src/TradingFlow.Tests/Fixtures/RetiredStrategies/` (same layout, same identities), so
the generic engine tests that used them keep running; `KeptStrategyIdentityTests` pins the
hashes below against those copies.

## Kept

| Strategy ID | Version | Idea | Content SHA-256 |
|---|---|---|---|
| `technical.opening-range-breakout` | 1.2.0 | Intraday opening-range breakout (next-open entry) | `bc73189a1c26ae2741402c2ad84a3a3e88aec709d43253590f7c7fb3c6fdbcc4` |
| `technical.opening-range-breakout` | 2.0.0 | Opening-range breakout, published recipe (stop-market entry) | `83b58ddd3b6b2017c717066e98ce92b513537b9aa467283b32f72375ded14429` |
| `technical.opening-range-breakout` | 2.1.0 | Opening-range breakout, published recipe | `b71cf7b8a017004937efb28d6d25c57d931061c522a7444067d2463e6ac33750` |

## Removed

All were in the research lifecycle at removal. "Source SHA-256" is the hash of the settings
file bytes; "content SHA-256" is the catalog's canonical identity hash. Source paths are at
`26d9448`.

| Strategy ID | Version | Content SHA-256 | Source SHA-256 | Source path | Admission profile |
|---|---|---|---|---|---|
| `technical.recovery-sma5-news-avoidance` | 1.0.0 | `47dd423636627dfd8a482291b3e905f56c6f278ac80a354afa33fc8acf6ff69d` | `12aba717916fbb56109f70bfaf5eb648af86c6c357c1c93ccf33be341412ad8c` | `configs/backtest/strategies/recovery-sma5-news-avoidance.bt-v1.yaml` | `historical-news-presence-v1` |
| `swing.connors-rsi2` | 7.0.0 | `a16f4e27ae3e85d19f537d57ac0189a5058d8de0b0cffa39ce993c0220a70e8c` | `17e70742de063af7303cb37eb2a61945d810ee0321011e1ca658bec65329b75b` | `configs/backtest/strategies/swing-connors-rsi2-sma5-risk-managed.bt-v7.yaml` | `causal-market-exits-kept-gaps-v4` |
| `swing.connors-rsi2` | 7.1.0 | `f3dc47e30b4eee880b9d615a4c8c02a1db3fecede713912f2d482c3303e6fd11` | `3df4839ca71bf73de54e214a0a8a28aaeb7d333b7ec502ad1b98fe820cd880ec` | `configs/backtest/strategies/swing-connors-rsi2-sma5-risk-managed-as-traded.bt-v7-1.yaml` | `causal-market-exits-kept-gaps-as-traded-v1` |
| `swing.connors-rsi2` | 7.2.0 | `87bfbc48a88cf563b04c22300a4f9283b5635daac3e16979d7414243505cb067` | `143b0df9eb69d59333c54fddc90b6870bc5efc7acd6468c71926675f143e9ee0` | `configs/backtest/strategies/swing-connors-rsi2-sma5-risk-managed-decision-hour.bt-v7-2.yaml` | `causal-market-exits-decision-hour-as-traded-v1` |
| `swing.connors-rsi2` | 9.0.0 | `882e2a32d00ccdcc17737aa05d9ff466ed714b0be86f5da04b9b570aa86884c5` | `b67b69d3543feb5303009dcae74d375490bdaf9ed254f43aaedd20fa37f008b0` | `configs/backtest/strategies/swing-connors-rsi2-published.bt-v9.yaml` | `published-no-stop-kept-gaps-v1` |
| `swing.connors-rsi2` | 9.1.0 | `0d052799b949ee9c76f0de44b1825087edc4eda4edb288c83ec8a0492674b258` | `fa8d713f3e2d1a434428baa8bf5aa12985d8cbe74646cfd2e9c9a2b149b56264` | `configs/backtest/strategies/swing-connors-rsi2-published-as-traded.bt-v9-1.yaml` | `published-no-stop-kept-gaps-as-traded-v1` |
| `swing.connors-rsi2` | 9.2.0 | `5938933c5800110d3574b4d5d0a067e70ba0ede4e86cb620d09ccb64c4568534` | `bf0ab037102c45d6f1e87414fc77c8d5292bfd21ae8e956730b18fc0bd5e03ee` | `configs/backtest/strategies/swing-connors-rsi2-published-decision-hour.bt-v9-2.yaml` | `published-no-stop-decision-hour-as-traded-v1` |
| `swing.connors-rsi2-short` | 2.0.0 | `323989e84141d4338ca45d2acb7fb6f75a236aba4b2c53b94187d1bd27ba85ea` | `c0dbc00a7090ccd9750d4e17eaf33fa7b6ee5f535d23fc1f1ac8b0f678d26d9b` | `configs/backtest/strategies/swing-connors-rsi2-short.bt-v2.yaml` | `published-no-stop-kept-gaps-v1` |
| `swing.connors-rsi2-short` | 2.1.0 | `f3e7c6c7c4f25e2dec7aa5135b211628dff767b0067d8d49c54dcca4dac51d32` | `2cb1a156171e1b5a64301361bc58fd2a3f7c9658b9dd875111f13621f9f12980` | `configs/backtest/strategies/swing-connors-rsi2-short-as-traded.bt-v2-1.yaml` | `published-no-stop-kept-gaps-as-traded-v1` |
| `swing.connors-rsi2-short` | 2.2.0 | `34a4438d39a56020ba11ac08b6364cd003885ca681888025c1f4cbc26aa27a3e` | `224295d51164b8a46daa8c57dba888e053682dca1bf28085160713a5cb06a54c` | `configs/backtest/strategies/swing-connors-rsi2-short-decision-hour.bt-v2-2.yaml` | `published-no-stop-decision-hour-as-traded-v1` |
| `swing.snapback` | 2.0.0 | `7bd1719285bdbb0e50cb1b94397a0353eb2f221e554b64b183a429096f7fd983` | `48d4a7dde467107cba6ec5fc03a80cd8a5630c27e55299d12b00422cdf570a56` | `configs/backtest/strategies/swing-snapback.bt-v2.yaml` | `historical-news-presence-no-stop-v1` |
| `swing.snapback-short` | 2.0.0 | `7deb6a30ddaaaa1844e0b9b2e2762eef9eadb8e5cf04839e5bd8cd412e11ed8e` | `3b50e5f1f3d1cd4f443012e42c31ba515e0a4e0d62eeebd42107c5c335c7f473` | `configs/backtest/strategies/swing-snapback-short.bt-v2.yaml` | `historical-news-presence-no-stop-v1` |

Admission profiles removed from the catalog (each referenced only by removed versions):
`causal-market-exits-kept-gaps-v4`, `causal-market-exits-kept-gaps-as-traded-v1`,
`causal-market-exits-decision-hour-as-traded-v1`, `published-no-stop-kept-gaps-v1`,
`published-no-stop-kept-gaps-as-traded-v1`, `published-no-stop-decision-hour-as-traded-v1`,
`historical-news-presence-v1`, `historical-news-presence-no-stop-v1`.

Run profiles removed (they served only removed versions): `configs/backtest/connors-whole-market-daily-profile.yaml`,
`configs/backtest/connors-whole-market-hourly-profile.yaml`. The generic `configs/backtest/swing-backtest-profile.yaml`
stays; its strategy list is now empty because a run names its strategies when it is planned.

## Reason

- **Single stocks:** the ten-year study (2017-2023, point-in-time top-500 list, next-open entry, 10 bps per side) measured
  +0.06% per trade over the eligible stocks with a daily t of -0.42, below the plan's stop rule (0.10% and t of 2); the
  501-1,500 tier was negative and even the untradable same-close entry earned only +0.08%. Snapback and recovery are the
  same idea and were retired with it.
- **Index and sector funds:** -0.09% per trade over holding the same funds (t -2.07, 649 trades).
- **Survivor bias** was measured on the complete-stocks list: it inflates the benchmark, not the rule's edge (zero on both).

## Where the results are written up

- `docs/research/reversal-signal-study-2026-11-13.md`: results, survivor-bias check, fund study and the decision.
- `docs/research/fable-review-2026-11-12.md`, sections 2.2 and 4.1: the plan, pass rules and stop rule.
- `docs/design-review/2026-11-11/connors-whole-market-design.md`: the whole-market and decision-hour designs.

## Code kept

All generic engine code stays: indicators, the kept-gap and decision-hour timings with their skip, gap, split and
data-ended rules, research screens, the evidence-catalog reader, the signal study, the news components and the short-side
exit rules. None of it exists only for a retired version, and the fixture copies keep it under test.

## Paper experiment

The `swing.connors-rsi2@9.0.0` paper experiment had already been cancelled with no positions or orders. Its grant and
artifact files under the operator data folder were not touched by this removal.
