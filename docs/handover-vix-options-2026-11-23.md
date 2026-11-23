# Handover: VIX gate comparison and daily options readings (23 November 2026)

For the next agent (Codex). Read `AGENTS.md` first; its rules apply in full (design approval, two independent reviewers per
checkpoint, core data never deleted, plain-language reporting, install on the operator site after each reviewed change).
The design and every decision are in `docs/design-review/2026-11-23/vix-and-options-plan.md` (sections 7 and 8 and the
decision sections at the end). This file says what is done, what is half-way, and the exact next steps.

## State at handover

- Branch `main`, last commit `c83e84d`, pushed to `origin/main`. Working tree clean except the user's own
  `docs/research/tradingflow-audit-2026-11-14.md` (uncommitted, not ours: never stage, edit or revert it).
- Operator site `http://127.0.0.1:10088` runs build `b7d0f88` (installed 23 Nov 12:56 local time). FinBERT on
  `127.0.0.1:8088` is up. Previous binaries kept in `C:\project\tradingflow-site\bin-before-b7d0f88-20261123`.
- Paper account at install time: 0 positions, 0 open orders, no running jobs.
- The user is signed in on 10088 in the Claude app's built-in browser. A new agent cannot use that session; ask the user
  to start runs in the Strategy Lab, or to sign in again in whatever browser you use. Never enter passwords yourself.

## Done (committed, reviewed by both reviewers, installed)

1. **20 temporary gate comparison variants** (step 2 of the plan): `configs/backtest/strategies/<original>.gate-spy-qqq.yaml`
   (VIX lines removed) and `<original>.gate-off.yaml` (regime block removed); catalog versions `<orig>-gate-spy-qqq.1` and
   `<orig>-gate-off.1`, research only, `derivedFromIdentity` = exact original. Originals unchanged (hashes pinned in tests).
   User rule: no variant-specific code; a variant that wins becomes the next version of the same strategy ID through the
   normal promotion decision, losers are deleted (files and catalog lines) with results kept.
2. **Daily options readings** (step 4), off by default:
   - `src/TradingFlow.Engine/Market/OptionsDailyCollector.cs`, `src/TradingFlow.Web/Services/OptionsDailyCollection.cs`,
     `src/TradingFlow.Cli/OptionsDailyCommand.cs` (`options-daily-collect`), tests `OptionsDailyCollectorTests` (7).
   - Writes `V:\trading_flow\data\research\options-daily\<yyyy>\<date>.jsonl` plus `<date>.requests.jsonl`. Sealed: no
     strategy reads it.
   - Switch on only after the user confirms: add `$env:TRADINGFLOW_ENABLE_OPTIONS_DAILY = 'true'` and
     `$env:TRADINGFLOW_OPTIONS_DAILY_SCREEN = 'swing-adr25-top500-20260501-20261030'` to
     `C:\project\tradingflow-site\start-site.ps1`, then restart the site. **The user has not yet said to switch it on.**
     They saw the estimate (about 900 stocks, 5-15 minutes, about 65 MB of raw responses a day; V: has 962 GB free).
   - Measurement folders (keep, provider data): `V:\trading_flow\data\research\options-daily-measurement-20261123-premarket`
     (first attempt, 9 failures from a bug in the command, since fixed) and `...-premarket-run2` (10 of 10 ok).
3. **Decision rule for the comparison** (fixed before any result is read, user decision): per strategy, a variant is
   proposed as the next version if its total of monthly returns over May-October 2026 is higher than the original's, its
   worst month is no worse, and both have at least 20 trades. Report how many days each gate blocked entries. Any
   replacement stays research and is re-checked on data after 30 October 2026. The reviewer's stricter amendment was
   **not** adopted; its warning (chance winners likely) is on record in the plan.

## Half-way: fix this first (small, needed before plan 36306 can start)

Commit `b7d0f88` added a refusal in `RunWorkflowService.ValidateBacktest`
(`src/TradingFlow.Application/Jobs/RunWorkflowService.cs`, the block after the "distinct strategies" check): "Select one
version per strategy. Run another version of the same strategy in its own plan."

- Problem found after install: `ValidateBacktest` also runs for saved plans (preview, save, and every cohort execution
  in `RunWorkflowService.Cohorts.cs:PrepareCohortAsync`). Plan 36306 holds two versions each of
  `technical.ema21-trend-pullback` and `technical.ema21-rejection-short` (1.0.0 and the approved MTR-14 1.1.0), so it
  would now be refused. Saved plans give each strategy its own run (`BacktestPlanService.PrepareExecutionsAsync`,
  `Strategies = [strategy]`), so versions in one plan never share a run and do not collide.
- The collision is real only where several strategies share one run: the direct start `RunWorkflowService.StartAsync`
  (one research book with all selected strategies). The crash sites are maps keyed by strategy name/ID, e.g.
  `BacktestRunner.Diagnostics.cs:131` and `MonthlyRunResultWriter.cs:121`.
- Fix: move the one-version check out of `ValidateBacktest` into `StartAsync` only (right after its `ValidateBacktest`
  call). Change the test `TwoVersionsOfOneStrategy_AreRefusedBeforePreviewOrSave` in `BacktestPlanTests.cs` to: a plan
  with two versions previews and saves; a direct `fixture.Workflow.StartAsync(...)` with two versions is refused with the
  message. Update the "User decision" item 3 in the plan document to say where the rule now applies and why.
- Verify: build `src/TradingFlow.Tests` (`--disable-build-servers`, artifacts under `.tmp/`), run
  `BacktestPlan|RunWorkflow|BacktestJobService|ResearchScreenWebWorkflow|UniverseCandidateWorkflowTests`; two reviewers;
  commit, push main, install (`C:\project\tradingflow-site\update-site.ps1`, back up `bin` to
  `bin-before-<sha>-<date>` first, check positions/orders/jobs first).
- Tell the user plainly: the rule they approved was placed too broadly by me and blocked plans that compare versions;
  it now applies only to direct starts, where versions really share one run.

## Next steps after the fix

1. Start plan 36306 (full gate, ten strategies, screen `swing-adr25-top500-20260501-20261030`, window
   2026-05-01T04:00Z to 2026-10-31T04:00Z, $100k, 0.5% risk, 20% notional, 10 positions, profile
   `swing-research-screen-profile.yaml`, policy `daily-rth5m-nextslice-ioc-v1`, group size 1000). Needs the user signed in.
   Memory: about 2.6-3.5 GB free; a run may need about 4 GB. Run plans one at a time.
2. Save two more plans with identical settings: one with the ten `-gate-spy-qqq.1` versions, one with the ten
   `-gate-off.1` versions (the MTR-14 1.1.0 strategies have their own variants). Run them after 36306, one after another.
3. Compare monthly per strategy and version (New York session months, month-opening equity, never pooled), apply the
   frozen rule, report blocked-entry days per gate. Propose winners to the user; delete losing variant files and catalog
   lines after the user agrees, keeping all results.
4. Step 3 of the plan (Cboe VIX3M/VIX9D gate rules on 2017-2023) was not approved yet; do not start it.

## Other open items (unchanged from earlier sessions)

- Operator site uses 1-2 CPU cores while idle; diagnosing needs a `dotnet-stack` download, which needs the user's OK.
- eToro project kept (a test requires its dormant source); removal is the user's decision.
- Old worktrees (`.claude/worktrees` x2, `trading_flow_merge`, three Codex) and 27 `bin-before-*` folders remain; ask
  before deleting any.
- Smaller news fixes brief for a separate agent: `docs/design-review/2026-11-22/smaller-news-fixes-brief.md`.

## Session temporary files

- `C:\project\trading_flow\.tmp\gatevar` (this session's build output, several GB) can be deleted once no build uses it:
  `Remove-Item -Recurse -Force C:\project\trading_flow\.tmp\gatevar`.
- Nothing else temporary was left in the repository. The measurement folders on V: are provider data and stay.
