# Retired strategy artifacts — 21 November 2026

The user retired the opening-range breakout versions and the three ETF strategies on
21 November 2026, confirming in the Claude Code session that day that the removal made in
commit `b98c08e` ("UI-9 … disabled YAML cleanup") on branch `ui/redesign-v2` was requested.
All six were already switched off in the catalog before removal:

- Opening-range breakout 1.2.0, 2.0.0, 2.1.0 — "Set aside 18 November 2026: same-day strategy;
  the user moved research to swing strategies."
- `etf.trend-10m` and `etf.buy-and-hold-control` — "Blocked 18 November 2026 by the user: index and
  sector funds are used only to judge the market and sector trend (regime gate, sector ranking),
  not traded as a strategy."
- `etf.rotation-12-1` — "Sector rotation is parked for later standalone research."

Removing an artifact from the catalog is not a lifecycle decision about its results and deletes
no evidence. Published backtest results, monthly records and run settings for these versions are
kept where they were written: the job records and their monthly results in `tradingflow.db`, and
any result and evidence files under the data root (`V:\trading_flow\data`). Nothing in the data
root was changed by this retirement. Each removed file is unchanged in git at commit `31ddb98` (the last commit before removal);
restore one with `git show 31ddb98:<source path>`. Test-owned copies of all six strategy files and
the four run profiles live under `src/TradingFlow.Tests/Fixtures/RetiredStrategies/` (same layout,
same identities), and `KeptStrategyIdentityTests` pins the content hashes below against them.

## Removed strategies

All were in the research lifecycle and disabled at removal. "Content SHA-256" is the catalog's
canonical identity hash (as pinned in `KeptStrategyIdentityTests`); "source SHA-256" is the hash of
the settings-file bytes at `31ddb98`.

| Strategy ID | Version | Content SHA-256 | Source SHA-256 | Source path at `31ddb98` | Admission profile |
|---|---|---|---|---|---|
| `technical.opening-range-breakout` | 1.2.0 | `bc73189a1c26ae2741402c2ad84a3a3e88aec709d43253590f7c7fb3c6fdbcc4` | `dc5f730fadfdee72bbf845e47a9a85b4cf261493adc8025e29fffdb15c333797` | `configs/backtest/strategies/opening-range-breakout.bt-v3.yaml` | `opening-range-breakout-next-open-research-v1` |
| `technical.opening-range-breakout` | 2.0.0 | `83b58ddd3b6b2017c717066e98ce92b513537b9aa467283b32f72375ded14429` | `5f6d15bbaef00f75a9a0af1543fc48765b3e08e42956f3661c7857ad18a82ad0` | `configs/backtest/strategies/opening-range-breakout.bt-v4.yaml` | `opening-range-breakout-recipe-research-v1` |
| `technical.opening-range-breakout` | 2.1.0 | `b71cf7b8a017004937efb28d6d25c57d931061c522a7444067d2463e6ac33750` | `8ee0c89282806383569ebbdacf1926623743717fe442f9038fc188a8efa5626f` | `configs/backtest/strategies/opening-range-breakout.bt-v5.yaml` | `opening-range-breakout-recipe-research-v1` |
| `etf.trend-10m` | 1.0.0 | `e998f9ccd93d4630c21123e8d89826c5a02c1e93e601793d3f96333e8ec60b08` | `1f573d573df19d8a18f71f4df7bdc3d733e99b77fc80547c2d9c244e3ceeeed9` | `configs/backtest/strategies/etf-trend-10m.bt-v1.yaml` | `month-end-no-stop-kept-gaps-as-traded-v1` |
| `etf.buy-and-hold-control` | 1.0.0 | `b39db129ba80d31c5dc9b03ae20b52453dbee90f8e6b12390436699147781ec5` | `48c68b55940671847f2aa06f7b5236c81e7f2e7174e130157e74d789257eeefc` | `configs/backtest/strategies/etf-buy-and-hold-control.bt-v1.yaml` | `month-end-no-stop-kept-gaps-as-traded-v1` |
| `etf.rotation-12-1` | 1.0.0 | `15dd1e26911df66c54f2f5f1c9cdd7bffa5c83ca21091efa50e08c0ab761ff9b` | `bc858e532c1d1367b5c72503f77d9960e8fce8554b7b23d7a7133456ac711d84` | `configs/backtest/strategies/etf-rotation-12-1.bt-v1.yaml` | `month-end-no-stop-kept-gaps-as-traded-v1` |

Admission profiles removed from the catalog (each referenced only by removed versions):
`opening-range-breakout-next-open-research-v1`, `opening-range-breakout-recipe-research-v1`,
`month-end-no-stop-kept-gaps-as-traded-v1`.

## Removed run profiles

They served only removed versions. Source SHA-256 at `31ddb98`:

| Path at `31ddb98` | Source SHA-256 |
|---|---|
| `configs/backtest/orb-published-recipe-profile.yaml` | `1f986bbeed6d89d77bafe8ef5244d8db0d14cf90be46499f4020fd65bec4ff56` |
| `configs/backtest/orb-published-recipe-whole-market-profile.yaml` | `0803c1f6bf1d9fb28f1e3aa8595372873fe352132fc6b3589a1fe9fc8142d320` |
| `configs/backtest/etf-trend-profile.yaml` | `c74d095f575f380ca0a46e56e1378754ac0ed92711f36044e65e1e502647c1b1` |
| `configs/backtest/etf-rotation-profile.yaml` | `81cda239ae2d198e7e22681d50fd593488c5323ecce44cf1cf274fac738af41c` |

## What stays

- The catalog now holds the ten swing research artifacts listed in `AGENTS.md` ("Active Configs").
- Generic engine code used by these versions (opening-range summaries, month-end timing, the
  regime gate and sector ranking that read index and sector funds) stays; the fixture copies keep
  it under test.
- SPY, QQQ, VIX and sector-fund data remain in use as market-regime and sector-ranking inputs.
