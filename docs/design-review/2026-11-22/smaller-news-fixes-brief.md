# Brief: three smaller news fixes (for a separate Sol 6.1 agent)

Requested by the user on 22 November 2026. Read `AGENTS.md` first; its rules apply in full (design shown to the user and
approved before code, purpose comments, focused tests, two independent reviewers, no GitHub push, no data deletion).

- Work in `C:\project\trading_flow-ui2`, branch `ui/redesign-v2`. Do not create a worktree or a new branch.
- Another session changes the Desk table and the chart on the same branch. **Touch only the files named below** and stage
  them by name. Do not edit `workspaces.css`, `watch-desk.css`, `watch-desk.js`, `desk-*.js`, `stock-chart*.js` or `Desk.cshtml`.
- Do not change, re-score or delete stored news rows. Do not write to the operator database
  (`V:\trading_flow\data\tradingflow.db`); tests use their own temporary database.
- Do not start a site. Build and test with `--disable-build-servers -m:1 /nr:false -p:UseSharedCompilation=false` and
  `--artifacts-path` under `.tmp`; run one heavy job at a time (the machine has 16 GB and little of it free).

## 1. Headlines are squeezed out in the Desk news panel

**Seen:** in the Desk's right column at 1440 px wide, a row shows the score chip, the ticker, the time and "Details", but
the headline has no width left, so nothing of it is readable.

**Cause:** `.news-row-main` is one line that never wraps. The chip, ticker, time and link refuse to shrink, so the headline
(the only part that shrinks) reaches zero. The rule that gives the headline its own line applies only when the whole
window is below 680 px, not when the panel is narrow.

**Files:** `src/TradingFlow.Web/wwwroot/css/components.css` (the `.news-entry.news-compact-row` block).

**Proposed:** let the row react to its own width (CSS container query, for example below 480 px): chip, ticker, time and
link on the first line, the headline on the next line across the full width, at most two lines. The News page, whose rows
are wide, must look exactly as it does now.

**Done when:** every headline is readable in the Desk panel at 1440 and 1920 px, dark and light, with no sideways scroll
bar, and News page rows measure the same before and after.

## 2. Read earnings results before FinBERT scoring

**Seen:** a live Alpaca article is saved only after FinBERT scored it (the user's decision of 22 November, keep it).
`TryExtractEarningsAsync` runs after that, so EPS and revenue from a headline wait for the scoring too. It can take several
seconds, or longer when FinBERT hangs.

**File:** `src/TradingFlow.Web/Services/AlpacaNewsStreamService.cs`, plus its tests.

**Proposed:** call the earnings extraction before `AnalyzeAsync`. It needs only ticker, publication time and headline;
keep the order score, then save, for the article itself. An error in the extraction must not stop the article from being scored and
saved; log it. Check `EarningsMonitor` and `TryApplyNewsResultAsync` first: a result may now exist a few seconds before
its article is stored. If a consumer needs the stored article, stop and report that.

**Done when:** a test with a scorer that never answers shows that the earnings result is applied; a test shows that a failing
extraction still stores the article; a headline is applied once, not twice.

## 3. Store an article about several stocks under each of them

**Seen:** the live feed builds one record per stock (`symbols.Select(...)`), but
`SqliteNewsFeedRepository.StorageId` uses only provider and article ID, so all those records have the same ID and one row
stays. A search for the other stocks does not find the article. The gap-fill read and `NewsFeedService.StorageDedupeKey`
(Alpaca branch) use the same ID.

**Files:** `src/TradingFlow.Data/News/SqliteNewsFeedRepository.cs`, `src/TradingFlow.Web/Services/NewsFeedService.cs`, and their tests.
`NewsStories.Combine` already merges several records of one headline into one story with all its tickers.

**Show the user this design first, because it changes the identity of stored rows:**

- New rows: the ID includes the stock (`alpaca:<article id>:<ticker>`).
- Existing rows keep their old ID and are not rewritten. The lookup must treat an old row
  (`alpaca:<article id>`, with its ticker) as already stored for **that** ticker only.
- FinBERT is asked once per article, not once per stock (today's live path already does this; the gap-fill cache does too).
- Re-scoring and enrichment update all rows of the article by exact ID; the rule that a fallback score never replaces a
  FinBERT score stays.
- Check each consumer that counts or limits rows: the 500-headline refresh limit, the 1,000-record Desk limit, the News page
  paging and story count, Earnings news, Preparation, and `NewsItems` reads for strategy decisions. One article must
  not count as several independent stories in any sentiment average for one stock.

**Done when:** an article tagged `DASH` and `UBER` appears under both; the News page shows it once with both tickers;
a second refresh adds nothing; old rows are unchanged byte for byte; the number of FinBERT calls per article is one.

## Handoff

Report the commands and results of the tests, the two reviewers' findings and what was done about them, and what was
deleted or kept. Commit locally, one commit per fix. Do not push, merge or publish a site.
