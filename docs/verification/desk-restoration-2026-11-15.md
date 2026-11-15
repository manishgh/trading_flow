# Desk presentation restoration

The 57ee499 Desk layout reduced the visible stock table to five columns, removed its per-row Buy and Sell actions, and moved the news panel below the table and selected stock. The existing Desk snapshot still returns one-hour stored sentiment counts, completed daily indicators, SIP session volume, and position and strategy observations.

Restore one row per stock with the existing positive and negative counts, verified daily SMA/RSI and RVOL, observed SIP volume, and separate Buy and Sell actions. Keep the compact next action and selected chart. The desktop layout puts news beside the stock table and opens the selected chart under the table, with supplemental stock evidence in one disclosure. The phone layout stacks the panels and contains horizontal scrolling inside the table.

Buy and Sell open the existing reviewed order ticket at the exact symbol or recorded candidate and run. They use the observed ask or bid when present. The ticket and server apply their existing account, role, order, and risk checks. A paused or locked ticket reports its unavailable state from the server. Desk does not supply an assumed holding or a new order path.

Returning from a full chart or primary navigation restores the same authenticated operator's tab-local view. It validates the source and exact strategy identity against the current catalogs and selects a symbol only after a fresh snapshot shows it. Explicit incoming Desk links take precedence. The browser stores display preferences and scroll positions, without order state or market and account observations. A restored current strategy offers Refresh strategy checks without starting a new assessment.

Synthetic verification covers desktop and phone layout, missing technical and news evidence, both review sides, candidate identity, and Desk return without replaying order or strategy requests.

In the isolated 1440×900 fixture after selecting a stock, the table and news both began at y=37 and the full 270-pixel price and volume plot occupied y=498–768. The 390×830 check kept the Buy and Sell actions readable and constrained horizontal scroll to the table. The focused review and evidence cases passed in the first run. The separate final layout and return run passed all three cases and removed its disposable browser data.
