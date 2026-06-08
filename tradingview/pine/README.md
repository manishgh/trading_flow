# TradingView Pine Scripts

These Pine v5 indicators mirror the active `trading_flow` strategies and emit webhook JSON for paper/live modes.

Use them on a 5-minute TradingView chart. Intraday strategies calculate directly from the 5-minute chart. Swing strategies request their 15-minute or 1-hour signal timeframe with `lookahead_off` and still manage entry/exit alerts on the 5-minute execution chart.

Alert setup:

1. Add the script to the chart.
2. Open TradingView Alerts.
3. Choose this script as the condition.
4. Select `Any alert() function call`.
5. Set the webhook URL for the paper/live signal intake service.
6. Leave the message box as-is; the script sends JSON dynamically.

Payload contract:

- `schema_version`: `1.0`
- `source`: `tradingview`
- `strategy_id`, `strategy_name`, `strategy_version`: must match the enabled mode config strategy.
- `action`: `entry` or `exit`
- `side`: `long`
- `signal_timeframe`: the strategy signal timeframe.
- `execution_timeframe`: `5m`
- `bar_time_epoch_ms`: TradingView bar timestamp in epoch milliseconds.
- `price`, `stop_loss`, `take_profit`: bracket values for entries and current trade context for exits.
- `signal_values`: diagnostic values used by validation, logs, and later analyzers.

The C# validator accepts only enabled strategies for the current mode, rejects duplicate event IDs, rejects stale signals, and checks strategy/timeframe/bracket consistency before routing risk/orders.
