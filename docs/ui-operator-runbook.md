# UI Operator Runbook

This runbook describes the implemented web and Android navigation. It does not replace broker,
incident, kill-switch, or disaster-recovery procedures.

## Web

- **Desk**: monitor the selected wishlist, prices, eligibility, signals, and wishlist-wide news.
  Selecting a symbol adds its decision, model, and news evidence without hiding the wishlist feed.
- **Positions**: inspect broker positions and protection state. A close is a separate reviewed action.
- **Research**: run and inspect wishlist-backed backtests, optimization jobs, and decision audits.
- **Operations**: start and monitor paper runs, inspect paper jobs, warm data, and administer wishlists.

Trade Desk rows do not submit orders. To buy, select a symbol, open **Review protected buy**, review
the server-calculated quantity, quote, spread, session, stop, target, and expiry, then confirm only if
the server still exposes the confirmation command. A blocked review is final for that ticket; correct
the reported condition and create a new review.

## Android

- **Watch**: compact wishlist candidates and current state.
- **Positions**: open positions, P/L, and protection state.
- **Activity**: strategy signals and system notifications with explicit filters.
- **More**: secondary research, news, automation, settings, and paper-operation destinations.

Open a watch item to reach Symbol Detail. Prediction evidence is advisory and separate from the
TradingFlow decision. A paper buy uses the same reviewed-order API as the web ticket.

## State And Safety

- Paper/live environment, connection, freshness, and blocked states are text labels, not color alone.
- Browser lists and monitoring rows are read-only unless an explicit review workflow is opened.
- Refresh or stream failures leave an explicit stale/disconnected state; they do not imply eligibility.
- The UI must never bypass strategy, admission, sizing, broker, session, or protection controls.
- Physical Android TalkBack and Accessibility Scanner checks remain release gates before deployment.
