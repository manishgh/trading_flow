# Configuration

TradingFlow is swing-only. Runtime profiles select providers, universe sources,
portfolio limits, execution policy, and exact strategy artifacts; strategy YAML owns
only strategy behavior.

## Retained Profiles

```text
configs/
  backtest/swing-backtest-profile.yaml
  backtest/strategies/*.yaml
  paper/alpaca-paper.yaml
  live/local-live-disabled.yaml
  strategies/*.yaml
  strategy-catalog.json
```

There is no intraday run profile. Sub-daily bars may be downloaded or derived only
to confirm or execute a daily-primary swing setup.

## Universe

Backtest and paper runs resolve symbols from a persisted wishlist and, when enabled,
current Finviz discovery. Generated run snapshots contain the resolved symbols and
source provenance for audit; they are not reusable ticker-list configuration.

```yaml
universe:
  mode: unresolved_wishlist
```

The Web/API layer resolves the selected wishlist at run creation. Finviz membership
is discovery evidence only and never bypasses strategy admission.

## Market Data

The retained profiles use Alpaca SIP:

```yaml
market_data:
  provider: alpaca
  download_timeframes:
    - 1h
    - 1d
  derive_from: 1h

providers:
  alpaca:
    data_feed: sip
```

Daily bars drive swing setup logic. Completed `1h`, `15m`, or `5m` bars may drive a
strategy's declared execution confirmation. They do not create a separate strategy
horizon.

## Warm-Up And Time Window

The backtest profile currently requests a six-month diagnostic evaluation window and
400 calendar days of pre-window history. Promotion-grade research uses frozen,
point-in-time datasets and records exact observed coverage.

```yaml
time_window:
  type: rolling
  lookback_days: 180
  warmup_lookback_days: 400
```

Warm-up bars are never scored or traded. A strategy requiring SMA200 must have at
least 260 completed daily bars available before admission.

## Strategy Identity And Lifecycle

`configs/strategy-catalog.json` is authoritative. The current bootstrap contains two
research artifacts and thirteen archived artifacts. No artifact is authorized for
paper or live merely because it is stored under `configs/strategies`.

Identity is `(strategy_id, semantic_version, content_sha256)`. A generated run file
records that identity but cannot promote it. Unknown YAML keys fail strict parsing;
removed behavior is not accepted through compatibility aliases.

Every executable strategy must:

- declare `holding_period: swing`;
- use `1d` as its primary strategy timeframe;
- declare any completed sub-daily execution timeframe explicitly;
- use completed-bar decisions and next executable-bar fills;
- define a pre-fill stop that satisfies account-risk policy.

## Portfolio And Risk

Portfolio configuration owns account-level controls such as starting capital,
account risk budget, maximum position notional, concurrent positions, costs, and
participation limits. Strategy YAML may request a structural stop or target but may
not override the account risk ceiling.

## Execution

Backtests use the deterministic simulator. Paper uses Alpaca paper routing. Live is
disabled. Extended-hours observation is independent from execution permission;
extended-hours entries default to disabled and fail closed when broker-native
protection cannot be attached.

## News

News is optional per run and sourced from Alpaca when enabled. Point-in-time catalyst
evidence uses provider timestamps plus first local receipt time. A strategy without a
declared catalyst rule must not acquire a hidden news gate.

## Generated Artifacts

Generated run snapshots and results are immutable audit artifacts. They belong under
the run-specific data roots and must use atomic writers. They are not hand-edited
configuration and are not catalog authority.
