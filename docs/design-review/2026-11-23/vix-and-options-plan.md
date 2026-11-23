# Plan: VIX and options information for the swing strategies

Requested by the user on 23 November 2026 ("VIX + options first, make a plan"; idea: puts, calls and VIX together).
**Status: plan for the user's decision. Nothing is built, collected or run.**

## 1. The question in plain words

Can information from the options market make the ten swing stock strategies better: fewer bad entries, better position size,
or a signal to stand aside? And later, separately: should the system ever trade options itself?

## 2. What data exists (checked 23 November 2026)

| Data | From | Source | Per stock? | Notes |
|---|---|---|---|---|
| VIX close | 1990 | FRED `VIXCLS` (already used) and Cboe `VIX_History.csv` | no, whole market | Already the sizing rule: below 18 full, 18–22 half, above 22 no new longs |
| VIX3M (3-month VIX) | Sep 2009 | Cboe `VIX3M_History.csv`, checked: 4,321 days to 20 Nov 2026 | no | VIX ÷ VIX3M above 1 means short-term fear is higher than longer-term fear |
| VIX9D (9-day VIX) | Jan 2011 | Cboe `VIX9D_History.csv`, checked: 3,995 days | no | Very short-term fear |
| SKEW (price of crash protection) | 1990 | Cboe `SKEW_History.csv`, checked: 9,275 days | no | How expensive far out-of-the-money puts are |
| VVIX (volatility of VIX) | 2006 | Cboe `VVIX_History.csv`, checked: 5,152 days | no | How unsettled the fear gauge itself is |
| Market put/call ratio | 2006 to Oct 2019 only | Cboe archive | no | **Stops in 2019: not usable** |
| Per-stock options: prices, implied volatility, Greeks, skew, put/call volume | live now | Alpaca OPRA (paid, already on the Desk) | yes | Current only; nothing is stored today |
| Per-stock options history | Feb 2024 | Alpaca `options/bars`, expired contracts (verified 20 Nov) | yes | Prices only; implied volatility and skew would have to be calculated from candle closes, without bid/ask |

The Cboe files were downloaded today, so their receipt time is today. The plan assumes, as for VIX now, that a day's value is
known only from the next session's open. That assumption is frozen before any test.

## 3. The constraint that decides the order

Your research rules split history into a **development period, 3 Jan 2017 – 29 Dec 2023**, and an **untouched test period,
2 Jan 2024 – 30 Oct 2026**, used once at the end. May–October 2026 is already spent by your earlier decision.

- The market-wide gauges (VIX3M, VIX9D, SKEW, VVIX) cover the whole development period. They can be developed and tested properly.
- Per-stock options history starts in February 2024, **inside the untouched test period**. Any rule built on it can only be
  developed by using up that period, or by collecting our own history from now on.

## 4. Proposed steps

### Step 1. Run the VIX test that is ready (plan 36306), as a check
Ten strategies, top-500 screen, May–October 2026, full gate (SPY + QQQ + VIX). Result per strategy and month. It needs you to
sign in on 10088 to start it, and about 4 GB of memory. To be read together with step 2.

### Step 2. Add the comparison versions to the saved plan
So that the VIX effect can be measured: the same ten strategies **without the gate** and **with SPY + QQQ only**.

- Proposal: one plan setting "market gate: as the strategy says / SPY+QQQ only / off", recorded with the plan and the results.
  This means one shared setting rather than 20 copies of strategy files.
- Small engine change, no change to any strategy file or hash.

### Step 3. Market-wide options gauges as extra gate rules (development period 2017–2023)
Read daily Cboe files through the existing FRED-style frozen file path (`research-daily-screen` publishes them with the screen,
paper reads them once a day). New, optional recipe fields, shared by backtest, paper and Desk through `RegimeGateService`:

| Rule | Meaning | Source of the idea |
|---|---|---|
| `vix_term_inversion_max` (for example 1.0) | No new longs while VIX ÷ VIX3M is above it | Published work on the VIX term structure (Simon and Campasano 2014; Johnson 2017) |
| `vix9d_spike_max` (for example 1.1) | No new longs while VIX9D ÷ VIX is above it | Short-term stress; a variant of the same idea |

At most two new rules, thresholds fixed in writing before the first run, to limit the number of tries. SKEW and VVIX are shown on the
Desk and in reports only, not used as rules, because their evidence for stock entries is weak.

Test: each of the ten strategies with and without each rule on 2017–2023, monthly results, then decide what goes to the untouched test.

### Step 4. Start collecting per-stock options data now, sealed
Every trading day after the close, store one options snapshot per stock in your stock groups and the top-500 screen: at-the-money
implied volatility, 25-delta put minus call skew, near versus 30-day volatility, call and put volume, implied move.
These are the same measures as the Desk options strip, stored with their receipt time.

- The data stays sealed, meaning no rule is built on it, until about 12 months exist.
- Then a rule such as "no long entry when put skew is extreme" can be developed on our own history, without touching the untouched period.
- Size and load must be measured first: about 874 stocks, likely 1–3 requests each per day.

### Step 5 (only if you decide). Per-stock options on the Feb 2024 – Oct 2026 history
This is possible from Alpaca's candles, with calculated implied volatility, but it uses up the untouched test period, and
calculating from candle closes without bid and ask is rough. I do not recommend it before steps 2–4.

## 5. Trading options itself (long calls and puts, selling options)

Your operating boundaries say options are outside the system ("Options, futures, crypto … are outside the current system scope").
The engine, risk rules, broker code and backtester handle shares only. Trading options would need:

- your decision to change that boundary;
- a separate design: contract choice, expiry handling, early assignment, Greeks-based risk, margin for sold options, and the Alpaca options
  permission level of the account;
- options price history with bid and ask for a fair backtest, which we do not have before 2024.

Selling options can lose far more than the premium received. That is a reason to keep it out until steps 2–4 show that the options
information helps at all. I do not give investment advice; the tests will show what the data supports.

## 6. Order and size

| Step | Work | Data |
|---|---|---|
| 1 | Start the run (your sign-in) | none new |
| 2 | Plan setting + tests, about one day | none new |
| 3 | Cboe file reader, two rule fields, tests, runs on 2017–2023 | four small files (under 1 MB) |
| 4 | Daily collector job, storage, size measurement first | to be measured, reported before collecting |
| 5, options trading | only after your decision | |

Each step: your approval of its detail, two independent reviewers, focused tests, install on 10088.

## 7. Detailed design for steps 2 and 4 (user approved steps 1, 2 and 4 on 23 November 2026)

User rule, same day: ideas are tested as **temporary strategy variants**. A variant proven better replaces the original under
the same name (as the next version of the same strategy ID, with the old version retired through the normal promotion decision);
a variant that is not better is deleted, and its results are kept.

### Step 2: comparison variants (replaces the "plan setting" idea of section 4)

- For each of the ten strategies, two temporary variant files next to the original, copied unchanged except for the `regime:` block:
  - `<file>.gate-spy-qqq.yaml`: SPY + QQQ rule kept, the four `vix_*` lines removed;
  - `<file>.gate-off.yaml`: the whole `regime:` block removed.
- Same strategy ID; version `<original>-gate-spy-qqq.1` and `<original>-gate-off.1` (to be checked against the catalog's version
  rules; a plain next version is used if pre-release versions are refused); `derivedFromIdentity` points to the original.
- 20 catalog entries, research only, with the reason "temporary comparison variant, 23 Nov 2026". Original files and their hashes
  do not change.
- One saved plan per version (full gate = plan 36306, then SPY+QQQ, then off), same screen, window and money settings, run one after another.
- After the three runs: monthly results per strategy and version side by side. A variant that does better is proposed to you as the next
  version; the rest are deleted (files and catalog entries), results kept.
- Tests: the catalog accepts the 20 variants, their hashes differ from the originals only by the regime block, and the originals' hashes are unchanged.

### Step 4: daily options readings, stored and sealed

- **What:** for each stock, once per trading day at 16:30 New York, one reading from the existing Alpaca OPRA client and calculator (the same
  numbers as the Desk options strip): at-the-money implied volatility, 25-delta put minus call skew, near versus 30-day volatility,
  call and put volume, implied move, contracts used, plus the raw provider responses (already archived before parsing by the client).
- **Which stocks:** the members of your stock groups plus the newest published research screen's stocks (about 900 today).
- **Where:** `V:\trading_flow\data\research\options-daily\<yyyy>\<yyyy-mm-dd>.jsonl`, one line per stock with request, receipt and
  calculation times; failures recorded as failures, never as zero.
- **How it runs:** a daily job in the operator site, using the existing durable job queue and schedule pattern, one request
  at a time with the provider's rate limit; it does nothing on market holidays.
- **Sealed:** no strategy, gate or Desk rule reads this folder until you decide, after about 12 months of data.
- **Before it starts:** I measure one day for 10 stocks and report requests, time and megabytes, then the full-day estimate, and you confirm.
- Tests: calculation unchanged from the Desk, file format, holiday skip, failure rows, no strategy code references the folder.

## 8. Built on 23 November 2026

### Step 2: comparison variants
- 20 files `configs/backtest/strategies/<original>.gate-spy-qqq.yaml` and `<original>.gate-off.yaml`, each a copy of the
  original with a two-line header and only the `regime:` block changed (VIX lines removed, or the block removed). Checked by
  `diff`: no other line differs.
- 20 catalog entries, versions `<original>-gate-spy-qqq.1` and `<original>-gate-off.1` (the catalog accepts pre-release
  versions), research only, `derivedFromIdentity` = the original's exact identity. The catalog loads all 30; the ten
  originals keep their content hashes.
- User instruction during the work: "variants will run and after success replace current one, we should not be writing
  custom code, except the part option n vix". So no variant-specific code or test was added. Existing tests that picked a
  strategy by ID alone now name its version; the two catalog list tests compare released versions only, and one general
  test requires every pre-release version to be a research-only copy of an exact catalog version.
- While the variants exist, the stock page's strategy picker also lists them (each with its version).

### Step 4: daily options readings
- `src/TradingFlow.Engine/Market/OptionsDailyCollector.cs`: one request at a time through the Desk's
  `IOptionsSentimentSource` (raw responses archived by the client), one JSON line per stock, resumes after a restart.
  Lines are `ok` (the calculator reading; a stock without options carries `no_liquid_options`) or `failed` (the provider
  error or exception text, no values).
- `src/TradingFlow.Web/Services/OptionsDailyCollection.cs`: every 5 minutes; on a New York trading day at or after 16:30
  it collects active wishlist stocks plus the stocks of the research screen named by `OptionsDaily:ScreenName` /
  `TRADINGFLOW_OPTIONS_DAILY_SCREEN`. Off unless `TRADINGFLOW_ENABLE_OPTIONS_DAILY=true`, market feeds on and Alpaca keys set.
- Deviation from section 7: it runs as a scheduled service like the Finviz capture and the database backup, not as a durable
  queue job. The day file itself is the record of progress, so a restart continues where it stopped; a queue job would add a
  handler, worker settings and job-type permissions without changing that.
- `options-daily-collect` command (same collector, explicit stocks and folders), used for the measurement below.

### Measurement (10 stocks spread over the 874-stock screen `swing-adr25-top500-20260501-20261030`)
Run 23 November 2026 at about 04:40 New York (before the open, so volumes and prices are Friday's), folder
`V:\trading_flow\data\research\options-daily-measurement-20261123-premarket-run2` (kept; provider data).

| | 10 stocks | Estimate for about 900 stocks |
|---|---|---|
| Requests | 20 (one stock snapshot and one OPRA page each) | about 1,800; up to about 6,300 if every stock needed all follow-up pages |
| Time | 3.2 s | about 5 minutes; up to about 15 minutes |
| Raw provider responses kept | 0.72 MB | about 65 MB a day, about 16 GB a year (V: has 962 GB free) |
| Readings file | 7.6 KB | about 0.7 MB a day |
| Result | 10 ok, 0 failed | |

Before the open several stocks had no implied volatility or skew (no liquid contracts near the money); the after-close
run will show how many have them. The first attempt (folder without `-run2`) failed for 9 of 10 stocks because the
command reused a connection object the client closes after each stock; the command was fixed and that folder is kept.

### Reviews of 23 November 2026 (code and design reviewer; plan and quant reviewer)
Both: accept with conditions. Fixed:
- One failed attempt no longer ends a stock's day. Only `ok` lines count as done; a failed stock is asked again on later
  passes, 30 minutes apart, at most 3 attempts, not from 20:00 New York on. Every attempt keeps its line; readers take the
  last line per stock.
- An unreadable research screen is logged and left out; the wishlist stocks are still collected.
- Each pass appends a request record (`<date>.requests.jsonl`: stocks asked for, wishlist names, screen name and manifest
  fingerprint, or why the screen was left out), because the screen is a fixed past cohort and the wishlists change.
- The command labels readings only with today's New York date.
- `ObservedAtUtc` inside a reading is when the provider data was read. The Desk and the collector share the client's
  5-minute cache, so a reading can be up to 5 minutes older than the line's request time.

Open, for the user's decision before collection is switched on or the comparison is read:
1. Scheduled service instead of the approved durable queue job (reasons above). Without a queue job, there is no Jobs-page
   entry and no lease, so two hosts with the switch on would both collect.
2. Price used to choose at-the-money options: today the client uses the latest stock trade. At 16:30 that is an
   after-hours trade, which after an earnings release can differ a lot from the 16:00 close. Proposed: one batched request
   for all stocks' 15:55-16:00 five-minute bars through the existing market-data provider, passing that bar's close as the
   price; the reading records which price was used.
3. Two versions of one strategy in one backtest plan crash the run summary (maps keyed by strategy name in
   `BacktestRunner.Diagnostics.cs:131`, `MonthlyRunResultWriter.cs:121`, `IntradayPortfolioExecution.cs:173`). The
   comparison uses one plan per version, so it is not affected. Proposed general fix: refuse such a selection when a plan
   is saved.
4. Decision rule for the gate comparison, to be fixed before the results are read (May-October 2026 is already spent; three
   versions times ten strategies is thirty tries on six months). Proposed: a variant is proposed as the next version only if,
   per strategy, it has a higher total of monthly returns and no deeper worst month, with at least 20 trades in both versions,
   and the report shows on how many days each gate blocked entries. Any replacement stays research and is re-checked on data
   collected after 30 October 2026.
Also noted: a pre-release version cannot be blocked from a paper grant by code; it relies on the research lifecycle and on
no grant being made. The folder-sealing test searches for two names only.

### User decision, 23 November 2026: "Accept all four"
1. Scheduled service instead of a durable queue job: accepted as built.
2. Price input: built. Each pass reads every stock's last regular-session 5-minute bar (bar start = session close minus
   5 minutes, so early closes are handled) through the site's market-data provider, in batches of 100 stocks, and passes its
   close to the options client. Each line records `regularClose` and `spotBasis`: `regular_close` when the reading used it,
   `provider_latest` when the stock had no closing bar, the bar request failed, or the shared 5-minute cache returned a
   recent Desk reading. Also fixed after the re-check: the 20:00 cut-off stops only re-asks (a site that was down at 16:30
   still takes a first pass that evening), and any screen-reading error leaves the wishlist stocks collected.
3. Built: a backtest start or saved plan that selects two versions of one strategy ID is refused with "Select one version
   per strategy. Run another version of the same strategy in its own plan." (`RunWorkflowService.ValidateBacktest`).
4. Frozen decision rule for the gate comparison, before any result is read: per strategy, a variant is proposed as the next
   version only if its total of monthly returns over May-October 2026 is higher than the original's, its worst month is no
   worse, and both versions have at least 20 trades. The report shows, per gate, on how many days it blocked new entries.
   A replacement stays research and is re-checked on data collected after 30 October 2026 before any paper use.

### Final plan/quant re-check (23 November 2026): accept, with a condition on the decision rule
- Wording: the price input is the close of the 15:55-16:00 bar, the last regular-session trade before 16:00. It normally
  excludes the closing auction print, so it is not the official close. Adequate for choosing the at-the-money strike.
- Condition: the accepted rule ("higher total by any margin") lets chance pick a winner for roughly 4-6 of the 10 strategies,
  and the later re-check has no period, trade minimum or pass rule. Amendment proposed to the user (not yet accepted):
  - a variant must be better in at least 4 of the 6 months **and** have a higher total and no worse worst month, with at
    least 20 trades in each version;
  - the re-check uses the same rule on 2 November 2026 - 30 April 2027 (six months, data collected after the current window),
    at least 20 trades per version, no change to any version in between; only a variant that passes both is proposed as the
    next version for the normal promotion decision.
  This section is committed before any of the three comparison plans is run, so the rule is fixed in advance.

### User decision on the rule, 23 November 2026
The user chose the rule that was already accepted ("rule that performs better"): per strategy, a variant is proposed as the
next version if its total of monthly returns over May-October 2026 is higher, its worst month is no worse, and both
versions have at least 20 trades. The stricter amendment above is **not** adopted. The reviewer's warning stands on record:
by chance alone several strategies may show a winner, so any replacement remains research and is re-checked on data
collected after 30 October 2026 before any paper use. This rule is fixed before any comparison result is read.
