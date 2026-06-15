# ML Research Analyzer

`tools/research/ml_strategy_analyzer.py` is a dependency-free Python research tool for TradingFlow backtest results.

It is not a trading brain. It reads completed backtest result JSON files, optionally reconstructs entry-time features from normalized `bars_1m.csv` files, and writes descriptive reports:

- `strategy_summary.csv`
- `trade_features.csv`
- `feature_correlations.csv`
- `feature_buckets.csv`
- `research_report.md`

Example:

```powershell
python tools/research/ml_strategy_analyzer.py `
  --results data/backtest/results/shared/portfolio/all-intraday-rgti-nvts-mxl-poet-20260513-20260612.json `
            data/backtest/results/shared/portfolio/all-intraday-known-runners-20260527-20260611.json `
  --normalized-root data/backtest/normalized/20260513-20260613 `
                    data/backtest/normalized/20260527-20260612 `
  --output-dir data/research/ml/all-intraday-two-batch-20260613
```

The analyzer intentionally excludes outcome/leaky fields such as exit price, exit reason, holding time, return, realized R, and net profit from feature ranking. Those fields remain labels/targets only.

Use its findings to revise deterministic strategy configs: volume floors, EMA/VWAP distance filters, close-location filters, session timing, exits, and risk guards. Do not use it to place paper/live orders directly.

## Senior ML Engineering Rules

TradingFlow ML is a research assistant, not the live trading brain.

- Keep structured market/trade features in CSV/Parquet/SQLite/Postgres style tables. Do not put candle features primarily in a vector DB.
- Use a vector DB only for unstructured context: news, catalyst text, trader research notes, strategy documents, screenshots/OCR text, and post-trade explanations.
- One training row should represent one decision point: a trade candidate or a completed trade entry.
- Features must be available at or before the decision timestamp.
- Labels can use the future outcome after costs: win/loss, return, realized R, max adverse excursion, and net profit.
- Never use exit price, exit reason, holding time, realized R, return, net profit, or future day high/low as input features.
- Split by time, not random rows. A normal workflow is train on older months, validate on the next month, and test on the latest untouched window.
- Profile and flag data quality before modeling. Very low-priced stocks, split-like gaps, missing bars, and corporate-action-looking days should be visible to the modeler.

## Current Feature Sources

`tools/research/intraday_data_profile.py` builds no-lookahead daily ticker profiles from cached 1-minute candles:

- `intraday_runner_score_pct`: max of open-to-high move and best 30-minute gain. This is the clean day-trading runner score.
- `prev_close_to_high_pct`: prior-close context only. It should not dominate intraday research because it can include overnight gaps or adjustment artifacts.
- `relative_first_30m_volume`: first 30 minutes volume versus prior sessions.
- `peak_cumulative_rvol`: highest cumulative same-time RVOL during the session.
- `data_quality_flag`: `ok` or a reason such as `sub_25c_previous_close`, `thin_regular_session`, or dislocation checks.

`tools/research/ml_strategy_analyzer.py` then joins completed trades with:

- entry-time price/trend/momentum features,
- same-local-minute volume baselines from prior sessions,
- prior daily-profile behavior such as prior runner rate and quality-flag rate.

It writes `feature_manifest.csv` and `feature_diagnostics.csv` so every model input can be audited before training.
