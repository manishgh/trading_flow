# ORB stocks in play: whole-market stock source and one-minute bars in backtests

Status: design proposal awaiting user approval (11 November 2026); nothing here is built. Research
only. Extends `docs/design-review/2026-11-02/orb-stocks-in-play-design.md` and the uncommitted code.

## What is missing today (checked in the code)

1. Saved plans take stocks only from operator wishlists or saved Finviz lists
   (`DeskSources.BacktestChoicesAsync`, `UniverseSourceResolver`). Neither gives every eligible
   stock on past dates, and copying 2,000 symbols into a wishlist is forbidden.
2. Plan groups (`BacktestPlanService.BuildCohorts`) each get their own capital, so groups of 5 would
   each choose from their own slice of the alphabet. The paper trades one portfolio of up to 20.
3. Ranking inside a run would download every five-minute bar of every candidate (about 16 million),
   because prepared runs request whole date ranges per stock. The in-run screen also skips the
   "apply splits known on that date" step of `research-daily-screen`
   (`HistoricalScreenerUniverseProvider.Evaluate` passes unadjusted bars), so the two can disagree.
4. `IntradayPortfolioExecution` accepts one-minute bars (`MinuteBars`), but
   `BacktestRunner.Intraday.cs` never passes them: every stop inside the entry's five-minute bar
   still counts as a loss.
5. `FindIntradaySourceGap` demands complete five-minute bars on every session of the window, so a
   stock with bars only on its selected days would stop the portfolio's results at its first gap.

Existing frozen inputs: as-traded daily bars 1 Sep 2024 - 30 Oct 2026 (`5fdf1ee3...`), security
master with inactive stocks (`6720a7d9...`), corporate actions (`864016f2...`), XNYS exchange sessions
(`5e055c6f...`), and the screen `data/research/screens/orb-stocks-in-play-20260401-20261030`: 148
sessions, 1,161-1,530 eligible stocks per day (mean 1,308), 2,076 distinct stocks from 1 May (127
sessions). The ranked top-20 list does not exist yet; it needs the opening five-minute bars.

## Part 1. A whole-market stock source

Options: (A) copy symbols into a wishlist: forbidden, and still today's list. (B) the existing
screener universe with a Finviz pool: today's members only (later-delisted stocks missing), and the
intraday replay refuses it. (C) rank inside every run: 16 million bars per run plus the split
difference. (D) **Recommended:** a new read-only stock source kind, `research_screen`, that points at
a published, fingerprinted research screen.

1. **Publication.** `research-daily-screen` also writes `screen-manifest.json`: input dataset IDs
   (daily bars, corporate actions, opening bars), every rule value, code version, and the SHA-256 of
   the fund-exclusion list and of each output file. (SHA-256 is a fingerprint: changing one byte
   changes it.) Ranked output goes to a new folder; the existing folder is untouched.
2. **Choice.** The Backtests page lists published screens as a third group, "Research screens". The
   paper desk never offers it.
3. **Preview.** `UniverseSourceResolver` gets a third branch that reads the manifest, re-checks every
   fingerprint and returns the pool: every stock eligible on at least one session of the plan window.
   It only reads files; nothing is written to wishlists, Finviz lists or other operator data.
4. **Freezing.** The saved plan also keeps the manifest and ranked-list fingerprints. Starting it
   re-checks them and refuses ("the ranked list changed since the plan was saved") on any difference.
   The plan's own eligibility and ranking fields must be empty; the rules come from the manifest.
5. **Per-day membership.** New run setting `universe.ranked_membership` (manifest path and
   fingerprint), written by `RunConfigWriter`. `ResolveDailyEligibilityAsync` reads the ranked list
   instead of recomputing it and builds the same `UniverseMembership` object as today (members per
   session, `SessionRankings`, `KnownFromNewYorkTime` = 09:35 New York). The rest is unchanged:
   `IsMemberAt` refuses entries before 09:35 or on non-member days, and `daily-eligibility.json`
   also carries the manifest fingerprint.
6. **One portfolio.** A `research_screen` plan must have exactly one group; `BuildCohorts` refuses
   manual groups or a group size below the pool size. One capital pool, up to 20 open positions, the
   4x gross limit shared in trigger order. ORB 2.1.0 with `orb-published-recipe-profile.yaml`; 2.0.0
   may run beside it as a control in its own portfolio.
7. **Work only where needed.** Candidates are evaluated only on a stock's member sessions, and the
   coverage check covers only those sessions plus the earlier sessions their ATR (average true range,
   the average daily price range) and opening-volume baselines read.

Files: `DiscoverySourceKinds`, `UniverseSourceResolver`, `DeskSources`, `BacktestPlanService`,
`BacktestRunConfig`/`SimpleYamlReader`/`RunConfigWriter`, `BacktestRunner.Universe.cs`,
`BacktestRunner.Intraday*.cs`, `DailyScreenCommand`/`DailyRankingOutput`, plus a small ranked-list
reader in `TradingFlow.Engine/Universe`. The catalog's `UniverseMembership` dataset kind was
considered; its publisher expects a raw provider snapshot as source, which a computed ranking lacks.

## Part 2. One-minute bars in real backtests

Options: (A) two-stage preparation inside the plan: the prepared cache serves exact ranges per stock,
so about 2,540 single-stock minute requests plus 2,540 overlapping 16-session five-minute ranges
(days downloaded repeatedly), with no "request completed" record to tell a minute without trades from
a failed download, and a ranking stage duplicating the research command. (B) minutes for the whole
pool: 2,076 x 127 x 390 = about 103 million bars. (C) **Recommended:** collect through the existing
evidence path and read the frozen datasets in the run.

1. `evidence-plan-minute-bars` (exists) writes the minute request from `ranked-daily-list.csv`:
   09:30-16:00, one request per session for its 20 stocks. A small option on the same planner,
   `--timeframe 5Min --prior-sessions 15`, requests full-session five-minute bars for each selected
   stock-session and the 15 sessions before it, which ORB 2.1.0 needs for its 14-day ATR and
   14-session opening-volume check. Both record `ranked_list_sha256`, tying the data to one list.
   `evidence-collect-normalize` (exists) downloads, keeps raw responses, converts them to Parquet
   files and registers immutable dataset IDs with a completion record per request.
2. A new read-only adapter, `EvidenceCatalogMarketDataProvider`, implements the interfaces prepared
   runs already demand (`IMarketDataProvider`, the source descriptor,
   `IHistoricalMarketDataEvidenceProvider`, session schedules from the XNYS dataset, after checking it
   reaches 30 October). It serves bars only from dataset IDs frozen in the run settings (daily
   `5fdf1ee3...`, plus the five-minute and minute datasets found by `ranked_list_sha256`), checks each
   manifest fingerprint and never calls Alpaca. Prepared data records those IDs as receipts, so a
   rerun reads identical inputs; the immutable catalog is the cache.
3. `RunIntradayPreparedAsync` passes the member sessions' minute bars as `MinuteBars`. They answer
   only the two existing questions: did the stop come after the entry inside the entry's five-minute
   bar, and which stop orders triggered first under the 4x limit.

**Missing minutes.** Alpaca returns no bar for a minute without a price-setting trade. Minutes order a
five-minute bar only when they reproduce its high and low exactly (`CompleteMinutes`). Otherwise the
existing worst case stands: the trade counts as a loss, and the 4x limit uses admission order with
`trigger_order_assumed`. Entry and stop in the same minute stays a loss. Results report entry bars
settled by minutes, same-minute losses, and unknown bars split into "no minute bars" and "minutes do
not match". A missing five-minute bar on a member session still goes through the existing trade-record
check (archived Alpaca trade prints) before it stops results.

## Downloads (estimates; the planners print exact counts before any download)

| Data | Requests | Bars |
|---|---|---|
| Opening five-minute bars, 1 Apr - 30 Oct plus 14 baseline sessions, 100 stocks per request | about 2,400 (about 15 per session x 162) | about 210,000 |
| One-minute bars, 20 stocks x 127 sessions x 390 minutes | about 130 (7,800 bars each, under Alpaca's 10,000-bar page) | at most about 990,000 |
| Full-session five-minute bars, selected stock-sessions plus 15 prior sessions | about 300-450 | at most about 3.2 million |

About 3,000 requests: about 15 minutes at 200 requests per minute (Alpaca's lowest plan limit), one to
two hours with storage and conversion. The earlier "30,000 requests" assumed smaller batches. One run
then holds up to about 4.2 million bars (roughly 1-2 GB of memory), so the profile's per-portfolio
`max_retained_replay_bars: 1000000` must rise to 4,500,000, a new profile version.

## Verification (component level of the Risk-Based Verification policy)

- Source: a changed fingerprint is refused; preview writes nothing to operator lists; two groups are
  refused.
- Membership parity: on a fixture without splits, the frozen list and the in-run ranking give the same
  members (both use `OpeningVolumeRanking`); entries before 09:35 are refused.
- Adapter: exact stored bars for a range, in order; a changed dataset is refused; no network call.
- Coverage: no failure on non-member days; a missing member-session bar still fails from that bar.
- Minutes: fixtures for stop before entry (no trade), after (loss), same minute (loss), incomplete
  minutes (loss, counted).
- Replay: two runs give identical trade-file fingerprints.
- Real data: 30 October count near Finviz's 1,262; every session has 20 members or a published reason.

## Risks

- One portfolio of about 2,000 stocks: one unexplained five-minute gap stops results from that date
  for everything. Your decision: keep this rule, or exclude only that stock-session with a reason.
- Alpaca builds minute and five-minute bars separately; a mismatch leaves the order unknown (counted).
- Memory is measured on one month first.

## What stays unproven

- **Survivorship:** Alpaca's inactive list missed 39 of 60 known delisted companies, so some stocks
  that would have ranked in the top 20 are absent and their places went to survivors. Reported as the
  design requires: share of places held by delisted stocks, their trades against survivors, and a
  stress test counting each estimated missing place as a loss of 1R (one unit of planned risk) plus
  costs.
- **Issuer identity:** Alpaca's security identifier stands in for the company, so two share classes
  of one company (GOOG and GOOGL) can both be selected. 242 symbols used by two companies were
  excluded. The fund list is dated 2 November 2026, so a fund that closed earlier could pass.
- Quote-based spread costs and historical borrow availability are not built; long-only stays the
  executable result. Six months is one market period: the best possible decision is RETAIN_RESEARCH.

## Order of work to a first result

| # | Step | Your approval |
|---|---|---|
| 1 | Approve this design and decide: one-portfolio rule, gap rule, replay limit, and the pending 100%-of-equity position cap | Needed |
| 2 | Commit the reviewed uncommitted ORB work | Needed (commit) |
| 3 | Plan and collect opening five-minute bars with existing commands (about 2,400 requests) | Needed (download) |
| 4 | Rank with the existing `research-daily-screen --opening-bars` into a new folder; check daily counts | Covered by 1 |
| 5 | Build Parts 1 and 2 with tests (manifest included); independent engineering and quant reviews | Covered by 1 |
| 6 | Plan and collect minute and selected-session five-minute bars (about 600 requests) | Needed (download) |
| 7 | Save the plan: Research screens source, one group, ORB 2.1.0 (and 2.0.0 control), 1 May - 30 Oct; start | Needed (start) |
| 8 | Signal study (gross and net R per trade), then the portfolio result by month; quant audit | Results to you |

Steps 3 and 4 need no new code and can run while step 5 is reviewed.
