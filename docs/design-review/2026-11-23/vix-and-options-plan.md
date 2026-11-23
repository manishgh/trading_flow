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
