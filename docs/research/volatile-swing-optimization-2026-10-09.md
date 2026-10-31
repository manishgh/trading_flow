# Volatile-stock swing optimization: cycle 3

User mandate: optimize established swing strategy families as complete entry/exit systems, reuse the shared engine, and add features only when required. Swing is the objective; daily setups may eventually use subdaily confirmation/execution. Same-day exits do not make this an intraday strategy. This document supersedes the proposed intraday-focused research direction, not previous published results.

## Objective and decision

Find robust monthly net performance across the original Volatile cohorts at unchanged account risk. Compare monthly return, monthly close drawdown, activity, exposure, holding duration, fees and concentration. Do not maximize accumulated profit, fit each ticker separately, raise risk to improve reported returns, or mistake zero trades for consistency. Each monthly return uses that month's opening marked equity; April and October are partial. Daily-close drawdown does not measure intraday loss.

All April 7–October 6, 2026 outcomes have already been inspected. This is development and replication evidence, never an untouched holdout. Current wishlist membership and retrospectively adjusted SIP bars also limit this to diagnostic research. Final disposition is REJECT or RETAIN_RESEARCH; these runs cannot authorize trading or promotion.

## Reuse and minimal engineering

- Reuse SignalGenerator, BasicStrategyEvaluator, shared risk/execution, BacktestRunner, immutable strategy sidecars, frozen SIP/calendar replay, monthly ledger valuation, and owned-input cleanup.
- Mean reversion: existing recent-stretch recovery setup, stretch-low stop, fixed-R and optional SMA5 exit. Its lookback permits repeated recovery signals after a position closes; do not call it one-shot.
- Pullback: existing quality reclaim with an explicit optional ordered-pullback requirement. Anchor the most recent prior highest high, choose the low strictly after it and before the signal, reject an empty interval. Legacy default/identity remains unchanged. ATR stops remain in use; the existing swing-low resolver does not consume reclaim lows.
- Base breakout: existing step_breakout, shortest qualifying completed base first. Prior rise means base high relative to the earlier window minimum; it is a quantitative proxy. Persist all existing step parameters in YAML; the writer previously omitted them.
- No new optimizer framework, ML model, news classifier, sector gate or UI is needed for this phase.

## Frozen experiment budget

Two exact controls (cycle2 RSI5 and EMA trend state), plus three 2-by-2 family experiments. Each cell is a separate immutable strategy/version/hash. Two levels of one entry choice crossed with two levels of one exit choice reveal their interaction without a broad parameter grid.

| Family | Entry A0 | Entry A1 | Exit B0 | Exit B1 |
|---|---|---|---|---|
| Mean-reversion recovery | Prior 5 bars contain RSI2<10 OR 3 down closes OR lower-band stretch; current close above prior high | Same recovery, RSI2<10 stretch only | Stretch-low stop, 2.5R target, 5 completed-session cap | Same, add completed-close above SMA5 exit |
| Ordered quality reclaim | Prior20 peak then later low, 6–45% depth; simultaneous SMA10 and SMA20 reclaim, above both; RSI14 35–90, MACD not bearish, close location >=0.55 | Same but only SMA10 crossing required; still above SMA20 | ATR1.5 stop, 4R target, ATR2 trail after3R, SMA10/20 downside-cross exit, 10-session cap | Same, trail activation after1R |
| Base breakout proxy | Prior60 minimum to base-high rise >=30%; base10–40 bars/depth<=35%; base/prior volume<=0.8; breakout/base volume>=1.5; close above base high and EMA20>EMA50, stock>SMA200 | Same, breakout/base volume>=2.0 | ATR2 stop, 4R target, ATR2 trail after3R, confirmed EMA20-loss exit, 10-session cap | Same, trail activation after1R |

All family recipes use the existing SPY>SMA200 regime. MR retains stock>SMA200. Reclaim retains the legacy stock trend gates instead of silently adding SMA200. Default deterministic ranking is used for reclaim/base; MR uses RSI2 ascending. Step volume is not the VCP volume-ranking field. Session caps explicitly replace the legacy calendar-hour caps; a long emergency hour cap is inactive over the study. Controls retain their exact prior definitions.

Inherited gates are explicit: base also requires price above EMA20. Reclaim retains the lookback-low anchored VWAP extension ceiling of4ATR, even though being above that anchor is not required. New hypotheses derive from the executable RSI6 research baseline with full resolved overrides; archived YAMLs are reference specifications with recorded hashes, never activated archived identities. The full frozen definition is authoritative beyond this compact table.

Total: 14 recipes x 5 original cohorts x 2 cost levels = 140 runs, executed one original five-stock cohort at a time. Base modeled slippage is10bps; adverse sensitivity is20bps, with fixed and regulatory fees unchanged. Stress variants have separate identities. No extra thresholds or post-result recipe changes are allowed inside this registry. Each cohort is independently capitalized at$10,000, planned order risk1%, position cap25%, four slots; independent balances are not a combined portfolio.

First cohort is ASTS/BKSY/BXDC/CRCL/CRDO. Preserve all25 roster entries and coverage dispositions, no replacements. Actual runs exclude symbols that cannot complete260 real prior sessions within the interval; later warm-up remains explicit. Reuse the frozen dates and scheduled final-session-open liquidation. Native daily signals fill only at a later eligible bar. Existing daily protection assumptions and adjusted-price limitations must accompany results. First-day protection and intrabar ordering require finer execution evidence before promotion.

## Evidence-dependent branches

Read operator database and evidence catalog in read-only transactions. Verify wishlist membership before/after. Inspect historical news receipt/revision/classification and human-label evaluation before any catalyst claim. Archived positive-sentiment catalyst YAML is not a validated classified-event strategy. Missing input evidence means the catalyst family is not executed, not a zero-return row. News absence cannot establish absence of adverse news. Sector gates similarly require historical mappings and completed benchmark data; SPY-relative strength is not sector-relative strength.

After technical phase, use a separate registry for justified news/sector additions or finer swing execution, with explicit coverage. Reuse the existing production design only if every required input is available. Do not invent receipt times, classifications, sector membership or prelisting history.

## Review, checks and selection

Independent requirements and engine-design reviews precede replay; code/recipe review follows changes. Focused tests cover ordering, ties, future-prefix invariance, YAML roundtrip, unchanged default identities and affected execution consumers. Freeze source/data/config hashes before outcomes; validate every manifest, ledger and monthly valuation. Stop a run on missing required coverage, hash mismatch, or unreconciled fills/equity; retain the failed attempt.

For each family compare A1B0-A0B0 (entry), A0B1-A0B0 (exit), and A1B1-A1B0-A0B1+A0B0 (interaction) within each month/cohort/cost. No pooled return. Present the nondominated configurations on monthly return/drawdown alongside trade count, exposure and concentration. A cell with fewer than10 closed trades in a cohort is exploratory, not sufficient confirmation; zero trades is inactivity. This10-trade flag is a descriptive screen, not a statistical promotion threshold. Mixed or sparse evidence remains research. Stop after the140 predeclared runs, even if none succeeds.

Before any later promotion: exact frozen strategy, newly observed chronological evidence with position/outcome embargo, costs/quotes/protection checks, the existing formal promotion gates and explicit human approval. Preserve published results/configs/manifests/failures; delete owned downloaded inputs, caches and spools after independent review. Never mutate operator source lists.

## Primary methodological references

- Cesar Alvarez, [mean-reversion strategy construction](https://alvarezquanttrading.com/blog/the-abcs-of-creating-a-mean-reversion-strategy-part1/): candidate stretch measures and complete-strategy construction. Its historical claims are not evidence for our roster; we retain protective risk controls.
- John Bollinger, [authored methods](https://www.bollingerbands.com/tradestation-methods): distinct volatility breakout, trend and reversal hypotheses. Our step proxy and parameters are explicitly our diagnostic specification, not a claimed exact reproduction.
- Repository authority: AGENTS.md, operating-boundaries.md, non-ml-strategy-research-program.md, catalyst-human-labeling-v1.md, and the previous cycle receipts.
