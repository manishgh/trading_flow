# API gaps

What these designs need that the backend does not expose today. Ordered by how much
they block.

## Blocking

### 1. Per-strategy equity series — Backtest Lab
The overlaid equity curves need a time series of portfolio equity per strategy over the
scoring window. `BacktestResult` carries end-state figures
(`EndingCapital`, `NetProfit`, `TotalReturnPct`, `MaxDrawdownPct`) and
`CompletedTrades`, but no equity curve.

Two options:
- **Emit it.** Add `IReadOnlyList<EquityPoint> EquityCurve` to `BacktestStrategyResult`,
  one point per trading day: `(DateOnly Date, decimal Equity, decimal DrawdownPct)`. This
  is the honest version — drawdown is already computed, so the intermediate state exists
  during the run.
- **Derive it client-side** by replaying `CompletedTrades` cumulatively. Cheaper, but it
  only reflects realised P/L at exit timestamps, so the curve will be a step function and
  within-session drawdown will be understated. Acceptable as a first pass; label the axis
  "realised equity" if you do this.

### 2. Promotion gate evaluation — Backtest Lab
The seven gates (Universe · Timing · Execution · Statistical · Cost stress ·
Concentration · Paper shadow) are defined in `docs/operating-boundaries.md` and
`docs/research/non-ml-strategy-research-program.md` but there is no runtime evaluator —
the prototype hardcodes them.

Needed: a per-run gate result set, e.g.
`record PromotionGate(string Key, string Label, bool Passed, string Reason)`, plus the
integrated decision (`RETAIN_RESEARCH` / `PAPER_SHADOW` / `PROMOTED` / `FAILED_GATE`).
Some are already computable from data on hand:
- **Universe** — false whenever the universe came from a current wishlist rather than
  point-in-time membership. `WishlistUniverseResolver` knows this.
- **Concentration** — computable from `CompletedTrades` grouped by ticker against the
  stated ceiling.
- **Timing / Execution** — properties of the run config; assert rather than measure.
- **Statistical / Cost stress / Paper shadow** — need real work.

Until this exists, do not ship the gates panel with invented values. Render the ones you
can compute and mark the rest `not evaluated` in `--color-neutral-700` with a `–` glyph.

## Non-blocking, small

### 3. Buying power — desk operational strip
Not in `OperationalStatusSnapshot`. `AlpacaBrokerClient` can read the account; surface
`buying_power` and `equity` on the snapshot. Until then, drop the cell — do not show a
placeholder number on a trading screen.

### 4. Average hold and profit factor — Backtest Lab matrix
`StrategyAuditSummary.AverageHold` exists for the audit line, but
`BacktestStrategyResult` has neither for the current run. Both are derivable from
`CompletedTrades` (mean of `ExitTimestamp − EntryTimestamp`; gross profit ÷ gross loss).
Compute server-side so the matrix does not have to.

### 5. R multiple per trade — Backtest Lab trades table
Needs the entry stop distance alongside the fill. Add `decimal? StopPrice` (or the
realised R) to the completed-trade record.

### 6. Universe and decision on research snapshots
The snapshot table shows which wishlist a run used and its decision. Neither is on the
snapshot record today.

### 7. Screener sync as a first-class endpoint
The desk screener bar and the wishlist import both need: normalise a Finviz URL, saved
screener name, or bare query string → return symbols → diff against a wishlist → return
the count not yet present. `ScreenerVerificationService` and the existing
`ImportFinviz` handler cover most of it. The read-only preview must carry source,
observation time, query identity and expiry for swing discovery. Persisted wishlist
membership is not trading admission or historical point-in-time universe evidence.

### 8. Unprotected-position flag — Positions
`MobileRunningTrade` has `StopLossPrice` and `ProtectionSummary`, so "no broker stop on
file" is inferable. Better: assert it server-side against the broker's working orders, so
the flag reflects the broker's view rather than the local book's.

### 9. Slots used vs cap — Positions
The summary shows `N of MaxConcurrentPositions`. The cap lives in run config, not in the
running-trades response. Surface it, and treat a count over the cap as a gate block rather
than a plain figure.

## Already available — no work needed

For the avoidance of doubt, these screens need **nothing new** for:

- The whole Earnings screen. `EarningsCalendarResponse` already carries every field,
  including `BreakoutAssessment`, `AnalysisReason`, `PreReleaseReferenceHigh`,
  `SlotRelativeVolume`, `ResultFirstSeenAtUtc`, `LatestCompletedBarAtUtc` and
  `PreviousEarnings`. The three alert lanes are client-side derivations.
- The desk grid, the evidence rail, and the agreement flag —
  `WishlistDeskRow` + `MobileSymbolIntelligenceResponse`.
- The inline order ticket — `ManualOrderTicketService` preview/confirm and
  `MobileOrderPreviewRequest`. Only the navigation changes.
- Positions, Orders and Wishlists — the existing page models cover every column.
- The Operations screen — `OperationalStatusService` plus the hosted services already
  expose everything except buying power.
