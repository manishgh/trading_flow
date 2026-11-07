# Retired strategy artifacts — 7 November 2026

The user narrowed active strategy research to three ideas: the intraday opening-range
breakout, the swing mean-reversion dip-buy (Connors RSI-2) and the swing snapback.
Every other catalog artifact was removed from `configs/strategy-catalog.json`, together
with its settings file and the code and tests only it used.

Removing an artifact from the catalog is not a lifecycle decision about its results and
deletes no evidence. Each removed settings file is unchanged in git at commit
`a3e7de5b` (the last commit before removal); restore one with
`git show a3e7de5:<source path>`. Result write-ups remain in `docs/` (listed below).
The full run output folders for the October studies were already deleted on
31 October 2026; they are not recoverable from this repository.

## Kept

| Strategy ID | Version | Idea | Content SHA-256 |
|---|---|---|---|
| `technical.opening-range-breakout` | 1.2.0 | Intraday opening-range breakout | `bc73189a1c26ae2741402c2ad84a3a3e88aec709d43253590f7c7fb3c6fdbcc4` |
| `technical.recovery-sma5-news-avoidance` | 1.0.0 | Swing snapback (interim; news-free drop rebound) | `47dd423636627dfd8a482291b3e905f56c6f278ac80a354afa33fc8acf6ff69d` |
| `swing.connors-rsi2` | 7.0.0 | Swing mean reversion (Connors RSI-2) | `a16f4e27ae3e85d19f537d57ac0189a5058d8de0b0cffa39ce993c0220a70e8c` |

## Removed

Lifecycle is the catalog disposition at removal. "Source SHA-256" is the hash of the
settings file bytes; "content SHA-256" is the catalog's canonical identity hash.

| Strategy ID | Version | Lifecycle | Content SHA-256 | Source SHA-256 | Source path at a3e7de5 |
|---|---|---|---|---|---|
| `technical.zone-breakout-retest` | 1.0.0 | research | `4fc1de5443c3112a411a702a2daa31b3c67779984451280fccc790cb619c69c7` | `b5028ceb011aeafe8fb497eeb0206a47032bf76dee7d28f13e99ac9454f77078` | `configs/backtest/strategies/zone-breakout-retest.bt-v1.yaml` |
| `technical.zone-two-candle-stop` | 1.0.0 | research | `191b6772ff0c24c23900d3ca27d13104379d0ff425f61455cfc529ac3df435d9` | `f14ff8289350548415df877e1ab1288e7e5226faf6acbeb3be096525b209a652` | `configs/backtest/strategies/zone-two-candle-stop.bt-v1.yaml` |
| `technical.zone-bounce-volume` | 1.0.0 | research | `b2489c77b47bb3b8b7da2d491c66b97f1f5646d3219ff60b35a40f1ccafa13f1` | `f2f9035012324b1c9bddc46ebb6bc86ec5acaf20db47635fba625ef2ac535ea2` | `configs/backtest/strategies/zone-bounce-volume.bt-v1.yaml` |
| `technical.zone-risk-target` | 1.0.0 | research | `983f24fe4030e1852b29f6fbea41fd76a89acc1821260c60b6ba2ae8883cd848` | `38d8aeb5cbb761022f3cdf6189e8fe3e792bf35dca25e2f11e1b30322701092e` | `configs/backtest/strategies/zone-risk-target.bt-v1.yaml` |
| `technical.zone-bounce-volume-risk-target` | 1.0.0 | research | `bdc4a74d5f5d5cce493e2d7ad25f67cf31f5d7bb24a5798ac9dfa8f940290329` | `f942b08f630e609c7d10417686f2efb6d4f783418f6c9c182d44320b2b98732d` | `configs/backtest/strategies/zone-bounce-volume-risk-target.bt-v1.yaml` |
| `technical.zone-volume-spread-no-rr` | 1.0.0 | research | `8da17b5cbe5a09051135bce2de3f6ba551a14dbfc5f23339898d454f3bdeb620` | `16be544390d268c4aed145be8016fc44ba334d7cff06b8a5a4fef21131e77b65` | `configs/backtest/strategies/python-zone-volume-spread-no-rr.bt-v1.yaml` |
| `technical.zone-volume-spread-comparison` | 1.0.0 | research | `b0ed33501f19b6850d82afe06ef22534a73f81a776f0b8afec9b2d6831316362` | `5c4aa6d61d721bd061f9c612922c160bc6900f7ee1b42acb49090a1c9b41a5b1` | `configs/backtest/strategies/python-zone-volume-spread-comparison.bt-v1.yaml` |
| `technical.zone-python-comparison` | 1.0.0 | research | `1fd41fa79b31bd1f2adedd6c373b46c96070bdfce79ee236c6290746e830bef2` | `8dc7f05485ee5b0f4e10daffc23334bd6e971ab38f28c579254116cae2c67a29` | `configs/backtest/strategies/python-zone-comparison.bt-v1.yaml` |
| `technical.zone-intraday` | 1.0.0 | research | `3283148c8c20116f4e40538d597e5444ed9939d28d84736de96967bc2bb80c9b` | `1aad0e47e574aaa62af211c20c1b515b5956d172bb2569445beeeba917e54f7c` | `configs/backtest/strategies/intraday-zone.bt-v1.yaml` |
| `technical.previous-day-breakout` | 1.0.0 | research | `4f8e176fbaeb71d854863bc0e15b07a91494b0425da29c71f50964f83a1289a4` | `301a811c604dbe7629d31bd1b16dc2ddd33e41827e518791b0d5f9fdf9da39b2` | `configs/backtest/strategies/previous-day-breakout.bt-v1.yaml` |
| `technical.recovery-sma5-qualified-technical` | 1.1.0 | research | `a7829066a22254a4c0613f1cec155f195c87a49087cd2a5b9c8853c68bab72dd` | `60e6affb5ed5953227075aeb42713b3c418a5f0c25973ccb3585cfcaf7cb430f` | `configs/backtest/strategies/recovery-sma5-qualified-technical.bt-v1.yaml` |
| `technical.recovery-sma5-qualified-coverage` | 1.1.0 | research | `21e3d059a8b7c6a266ffa6f05d43b2125f22b9149f9be138cc24fe6d9c4caa02` | `4a17df11c1d9671e7b63240a9aa93ec613fbad11c61506b3b5c7f7963b284599` | `configs/backtest/strategies/recovery-sma5-qualified-coverage.bt-v1.yaml` |
| `technical.recovery-sma5-qualified-negative-veto` | 1.1.0 | research | `71ecf2c22155371286f2a26fd999f3db57105dfa487fef7f33dcee7d3581f299` | `5b5f6c76fe766ab967aab4e50605c6bc462b5273a5c6efb2e1422ee06c861d5a` | `configs/backtest/strategies/recovery-sma5-qualified-negative-veto.bt-v1.yaml` |
| `technical.recovery-sma5-qualified-positive-required` | 1.1.0 | research | `7657cf62cef7890108b5759df97c8a9d79c6c40e8c21b3d5a6efe3e29767170a` | `9037e153d8ef83af91791e553ed738e12bbedc45dadc6285c90b364205185b96` | `configs/backtest/strategies/recovery-sma5-qualified-positive-required.bt-v1.yaml` |
| `technical.recovery-sma5-news-evidence` | 1.0.0 | research | `ab9db8fd6aaa0ee9546d8a31a2939629bbf109a682572e669e64b556ea0429db` | `51d7e20e91db2b13385db27a787956763394088d164cb12672d3fa08ce23225c` | `configs/backtest/strategies/recovery-sma5-news-evidence.bt-v1.yaml` |
| `swing.connors-rsi2` | 6.0.0 | research | `f56ef8e2505d7e6118bfb7c534c5c71ad03936da4cbf13b5e13e5566e6876df0` | `1e8551ce260967d082f9b368b9b7fa897bd6e00ce874c5c281968c5e08f8b823` | `configs/backtest/strategies/swing-connors-rsi2-sma5-risk-managed.bt-v6.yaml` |
| `technical.recovery-exit-control` | 2.0.0 | research | `d4a2042aab1fca4a08f298b875cf80f3c241d3eb11d3b4a43c111eae2c97f54d` | `5c595fc95954bbb9288dd1cbc1b4e43fc939ecd16e64413a9fa0fdfd2b90176f` | `configs/backtest/strategies/recovery-exit-control.bt-v2.yaml` |
| `technical.recovery-sma5-exit` | 2.0.0 | research | `ad27d9ed0855760bf17fb4fec8ed9b97938dc3a1f52710097b6c63dc4a64c4a8` | `0c014b646c6e45fbf1cc05c3b38ca8ab87663b7cc9a2372384deb3964bc27e94` | `configs/backtest/strategies/recovery-sma5-exit.bt-v2.yaml` |
| `technical.opening-range-breakout` | 1.1.0 | archived | `a4a9d3b736f7eb98868e77ae863b86e86d9d5ffcbd66559d0869411ea6a6f1a6` | `bc6e2b091aea599c751cadc6c6c014f560547baeef66de547716f96cf3ae37c1` | `configs/backtest/strategies/opening-range-breakout.bt-v2.yaml` |
| `technical.opening-range-breakout` | 1.0.0 | archived | `ac466ff8ad90d1777c2411ae3ec06f32a68a362a09e0cd75515dcc0c20e4c496` | `4fb1f80212f40f1bc77fcac53e7987d0d52fa466cddb262e2dbf9c8129486c39` | `configs/backtest/strategies/opening-range-breakout.bt-v1.yaml` |
| `swing.qullamaggie-episodic-pivot` | 1.0.0 | archived | `24ee40ba184b3f0c664458c2c81307fca12f527849376e49e49cc70b7251ad68` | `8a008070421fc43f152eeaf46896c8b87a78310d51629eb6efaddc6d17668eb3` | `configs/strategies/kristjan_qullamaggie_stream_methodology.yaml` |
| `swing.minervini-vcp` | 2.0.0 | archived | `081d6edd9b9127272222ee8b42a4db24f9036c47f266b68ded6e12638c5ef9ed` | `baeb31675bf3950f463467560bbe03ce808314b102e1e2b50f8609e99bcfbfab` | `configs/strategies/minervini-trend-template-vcp.v2.yaml` |
| `swing.minervini-vcp` | 4.0.0 | archived | `f21bfc161f8d3a0fe201e5b5d698298c621ae413dda3e0a2fa2f22af8d8756d7` | `c335a7eac72eb3a14b2ef5e77db4f4dfce2c6e0e7b20a9a01ff7eaf486686fb1` | `configs/strategies/minervini-trend-template-vcp.v4-trend-rider.yaml` |
| `swing.catalyst-drift` | 5.0.0 | archived | `c583cbdd760802b0168baa22192564808b556da0a458fdb162f9113dff886e9c` | `c29c5e5e7ed042ef831b37c17ea3019f68ca6fc558293598be75f1b027d717a9` | `configs/strategies/swing-catalyst-drift.v5.yaml` |
| `swing.mean-reversion-reclaim` | 1.0.0 | archived | `d1dee007f2d9e5a4dd43f5d11ff1de725c56be48609cfb6722724bae009f2469` | `4722e104b012de2b952efca8ee2763b76ec997e357ef568804e15c0d8f13eebd` | `configs/strategies/swing-mean-reversion-reclaim.v1.yaml` |
| `swing.overbought-rollover-short` | 5.0.0 | archived | `991df022780fa19f9c754394440bb98e6900554ff0a778f01a4daac53a9c2c94` | `bca2af092493509a6b6dc0a9da1058b22a0784ee46673010a212aa1d20daeafe` | `configs/strategies/swing-overbought-rollover-short-no-news.v5.yaml` |
| `swing.reversal-reclaim` | 1.0.0 | archived | `bb38af7da12563cb1b760d2789a25ff6e7c07c90bacbb81791c0d3c0a526ad73` | `94a53069778defa1f2876946f3b562106ea98f1ef7a74415abcd1f3d9a1bdd40` | `configs/strategies/swing-reversal-reclaim-bull-quality-no-news.v1.yaml` |
| `swing.minervini-vcp` | 5.0.0 | archived | `a02d8ce8d02cd0d6e09083dd60dd48669e4d0bff8396cd32ed49c393b3935f24` | `f74ea2afcd47930a8b6b28ebf112247b8b1fc0dda145f822fc03d2282642919a` | `configs/backtest/strategies/minervini-trend-template-breakout-proxy.bt-v5.yaml` |
| `swing.minervini-vcp` | 6.0.0 | archived | `1f76572d51b434d649dbd9d222e65bb5fdb28e1e07d39f2b7c0d0dfc3fc552b1` | `8e5f09f533f0b6b1c4f42a95f8b9b915d6d0988d1e3c4d7163bfbca0df8b0e4a` | `configs/backtest/strategies/minervini-trend-template-pivot-vcp.bt-v6.yaml` |
| `swing.minervini-vcp` | 7.0.0 | archived | `690e7677f723a5cc743a61cd6a114640c6f19bbcff012279c892dec1eb17e0a9` | `021543aef60c9e4ea09258297bc566bfb0c5b2526c8a636cbd9bac2347d00e23` | `configs/backtest/strategies/minervini-trend-template-pivot-vcp.bt-v7.yaml` |
| `swing.minervini-vcp` | 8.0.0 | archived | `bfa573192d090ff606abd990f2205077c6f93af5c6e52b2492186d27d4d45029` | `aa132c3209f2942d94fc88905f24abc8a2d1b943aa5c226c8013da3fa0127433` | `configs/backtest/strategies/minervini-trend-template-pivot-vcp.bt-v8-hourly-confirmation.yaml` |
| `swing.connors-rsi2` | 4.0.0 | archived | `395403a4fdd0357681667e753e74acd61e670788469de803599f028ebb90660d` | `5f8da83a0e33ce2c949e0e493e3d7971faff854656fad3e3d4cc75d69eb0ab28` | `configs/backtest/strategies/swing-connors-rsi2-deep-oversold.bt-v4.yaml` |
| `swing.connors-rsi2` | 3.0.0 | archived | `eb5064132b0345ec15416b854fad1ba5aa2edbc81a8c10f948bfb4617363a75e` | `02a42afc0f49a488b5548ae1029618a3f168d74b2d2a623c1bbf48beddf9c2da` | `configs/backtest/strategies/swing-connors-rsi2-oversold.bt-v3.yaml` |
| `swing.mean-reversion-reclaim-technical-only` | 1.0.0 | archived | `2d9fbe03a63df8b80797edb9285b7a530ea9903c054fc48d0a78b36edbe7abfd` | `489710abf0f949113e4d4e1d2b3bf7369f920f2076f33fa5b9133f46d5e1602a` | `configs/backtest/strategies/swing-mean-reversion-reclaim.tech-only.bt-v1.yaml` |
| `swing.connors-rsi2` | 2.0.0 | archived | `72710fcbdefb72714efda44fb0ddfec24c27581fb768e8343033597fc0ac11be` | `74dba1243373ac8523d1e74f8a0abe2df604e72020503b424217253a3e56d825` | `configs/backtest/strategies/swing-mean-reversion-rsi2-recovery.bt-v2.yaml` |
| `swing.minervini-vcp` | 9.0.0 | archived | `36a5ee9e4bb8bb99d4c76770cf118e15db7f51f84fe8e1a750e9cc232ceca026` | `45ef435fde2a227733169847c0cb80d9c823dacfd36da922f1f546c4c915b2e4` | `configs/backtest/strategies/minervini-technical-template-vcp.bt-v9.yaml` |
| `swing.connors-rsi2` | 5.0.0 | archived | `599a03365e622d22a4fcbf61f38c680ee16a908aa19fe965361718af2609dd79` | `e9855687f0a18d302b32eb6347ad69f6571f3c3be2676037f820b7e2025a6325` | `configs/backtest/strategies/swing-connors-rsi2-sma5-risk-managed.bt-v5.yaml` |
| `swing.minervini-vcp` | 10.0.0 | archived | `e06acd1e43b45259fb294af8ce25b973e17aee6e29a9aba73051c4e8238a1256` | `4e7a05ad2e53dbf735a5c575057400422bb44eadae60e25b9f054b062630290d` | `configs/backtest/strategies/minervini-technical-template-vcp.bt-v10.yaml` |

## Where the removed artifacts' results are written up

- Zone family: `docs/design-review/2026-10-19/14-` to `20-` (29–30 October 2026).
- Previous-day breakout: `docs/verification/previous-day-breakout-2026-10-25.md`,
  `docs/verification/day-continuation-protection-2026-10-25.md`.
- Opening-range breakout 1.0.0 and 1.1.0: `docs/design-review/2026-10-19/12-` and `13-`,
  `docs/verification/orb-*-2026-10-28.md`.
- Recovery variants: `docs/verification/qualified-recovery-sentiment-2026-10-24.md`,
  `docs/research/volatile-next-experiment-design-2026-10-22.md`,
  `docs/research/qualified-recovery-sentiment-proposal-2026-10-24.md`.
- Minervini VCP and Connors 2.0.0–6.0.0: `docs/research/volatile-six-month-cohorts-2026-10-21.md`.
- Qullamaggie episodic pivot: `docs/research/post-earnings-swing-research.md`.
- Catalyst drift, mean-reversion reclaim, overbought rollover short and reversal reclaim:
  June 2026 runs, summarised in `data/research/backtests/2026-06-11_swing_6tickers_180d`
  (tracked in git; long quality v1 and short rollover v5).

Known defects at removal, from the 31 October recalculation: the VCP analyzer ignored the
price leg after the last pivot and had no first-cross rule; the zone spread filter could
not reject; zone touches used a flat $0.15 tolerance. These defects are a reason not to
restore those artifacts without fixing them first.

## Code changes made with the removal

- Removed the code only the retired artifacts used: intraday zone evaluators, zone result
  pages and API fields, the zone historical-quote replay and its preparation service, the
  previous-session breakout evaluator, the VCP analyzer (and the unused VCP cell of the
  momentum study, which the published momentum runs never selected), and the swing setups
  step breakout/breakdown, swing reclaim, swing rollover, catalyst drift, anchored-VWAP
  bounce, episodic pivot gap and volatility contraction pattern.
- Kept as generic primitives: the ORB stop-market entry path, the pending-limit order type and
  the daily SIP event portfolio. The records the ORB shares moved to
  `IntradayBreakoutEvidence.cs` and `FixedIntradayBreakoutBaseline.cs`.
- Entry-rule parameter fields of removed setups stay in `EntryRules`, because the kept snapback
  file lists them at their defaults and they feed its identity hash. The settings reader now
  rejects, at load time, any config that sets a threshold whose rule was removed (for example
  `min_contractions`) or that enables daily shorts, instead of trading without that filter.
- `swing.connors-rsi2@7.0.0` uses timing policy `prior-close-plan-next-open-fill-kept-gap-exits-v4`,
  which commit `04b8782` (31 October) registered but never added to the decision kernel, the
  market-evidence price basis or the monthly results display. With the user's approval
  (8 November 2026) it is now accepted in all three places exactly like the 6.0.0 policy
  (`completed-bar-next-slice-market-exits-v3`). Its rules and identity hash are unchanged.
- The three kept identity hashes above were recomputed after the change and are identical;
  `KeptStrategyIdentityTests` pins them.
- Tests that used retired files only as generic inputs now use the kept files or checked-in
  fixtures under `src/TradingFlow.Tests/Fixtures/Strategies/` (a daily trailing-stop swing
  fixture and the two ORB stop-market versions, copied unchanged from commit `a3e7de5`).
