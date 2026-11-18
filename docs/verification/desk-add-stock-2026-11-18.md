# Desk: add any stock to an own stock group — 18 November 2026

Approved by the user on 18 November 2026 ("Approve it"). Stock-group membership only: no strategy, order, risk or
research behaviour changed. Buying still requires group membership and the normal strategy admission.

## Observed problem

"Follow stock" on the Desk (`POST /api/desk/follow`) only accepted a symbol that the current view already listed.
Any other symbol was refused with "Stock or wishlist is no longer available in this view", so a stock could only be
added from the Groups page.

## Change

- **Desk page.** A "+ Add stock" toolbar button opens a form with a symbol box and a picker of the operator's own
  stock groups (preparation revisions are excluded, as before). The status line shows the server's added or refused
  message; the stock appears on the next Desk update (about every 5 seconds).
- **Server.** `POST /api/desk/stocks` calls `DeskSources.AddStockAsync`:
  1. The symbol is trimmed and upper-cased and must look like an exchange symbol (`AAPL`, `BRK.B`); otherwise it is
     refused without contacting Alpaca.
  2. The group must be one of the operator's own groups.
  3. A symbol already active in that group is left unchanged (notes kept) and Alpaca is not asked.
  4. Otherwise Alpaca's asset record (`GET /v2/assets/{symbol}`, the existing `GetEligibilityAsync` lookup with its
     one-hour cache) must show class `us_equity`, `active` and `tradable`. Unknown, crypto/option, inactive (for
     example delisted) and non-tradable (for example over-the-counter) symbols are refused with that reason.
  5. The stock is added with the existing `IWishlistRepository.AddOrUpdateItemAsync`. A stock removed earlier
     (inactive) is re-activated with its name and notes.

  Answers (the status line shows each message; nothing is added unless the answer is "Added"):

  | Answer | HTTP | When |
  |---|---|---|
  | Added | 200 | Alpaca confirmed the stock and it was saved |
  | AlreadyInGroup | 200 | already active in the group, or another request added it a moment earlier |
  | Refused | 400 | malformed symbol; group missing or a preparation revision; unknown, non-stock, inactive or non-tradable on Alpaca |
  | Unavailable | 503 | Alpaca keys not set; Alpaca rejected the keys (HTTP 401/403); Alpaca answered another error (its HTTP status is named); Alpaca unreachable; no answer within 10 seconds; the database did not save it |

  Provider details (raw archive identifiers, exception text) go to the site log only.
- `AssetTradingEligibility` gained an optional `AssetClass` read from the asset record's `class` field.
  `IAssetTradingEligibilityProvider` is registered as a read-only view of the existing broker client, so the Desk
  service does not receive the order-capable interface.
- The existing in-view "Follow stock" control is unchanged.

Additions beyond the approved file list, made on review: the Alpaca key check (`AlpacaCredentialProvider`), the
10-second lookup limit (the broker's HTTP client otherwise waits 100 seconds), and the separate answers for a
concurrent add and a failed database save. All stay inside `DeskSources`; no new files, settings or schema.

## Verification

- Debug build of the test project: 0 warnings, 0 errors.
- `dotnet test … --filter DeskReadModelTests|AlpacaBrokerClientTests|BacktestStockSourcesTests|EntryGateChainTests|CurrentStockStrategyAssessmentTests`:
  **78/78** (new: `AddStock_AddsAnyCheckedSymbolToAnOwnGroupAndRefusesWithTheReason`,
  `AddStock_ReportsMissingKeysLookupFailuresAndSaveFailuresSeparately`; asset class added to
  `GetEligibilityAsync_MapsOvernightEligibilityAndCachesAsset`). No test contacts Alpaca.
- Browser specs `watch-desk.spec.js` and `desk-replacement.spec.js`: **51/51** (new: the Add stock form, and the real
  endpoint's 400/200/503 answers on an isolated test host with blank Alpaca keys). The last edit after that run (a
  failed re-read after a failed save also answers "not saved") is server-only and covered by the .NET tests above.
- Reviewer A (plan/execution) and reviewer B (code/design) requested changes: missing keys reported as a connection
  failure, no lookup time limit, a database failure reported as "already added", rejected keys and an unreachable
  Alpaca merged into one message, a duplicate add answered with HTTP 500, missing tests, and an incomplete note. All
  were fixed as described above, and both accepted on re-check. Their remaining small points were also applied: a
  comment at `AlpacaTradingRestClient.DescribeFailure` says the Desk reads its "failed with HTTP nnn" wording.

## Installation on the local site, 18 November 2026

Before the restart the Alpaca paper account had no positions and no open orders, no job was queued or running, and
the time (16:05 UTC) was outside the Finviz capture windows. Release `a24f387` was installed at 16:06 UTC with
`update-site.ps1`. The build it replaced (`9a65f6b`) is kept as `bin-before-a24f387-20261118`, and the
otherwise-unkept 14 November build that the script would have deleted from `bin-old` was renamed `bin-old-20261114`.
Readiness `ready`, order stream connected, no errors after start; the served `watch-desk.js` calls `/api/desk/stocks`.

Live check, signed in: "+ Add stock" opened the form with the operator's two groups ("volatile", "long"). The
made-up symbol ZZZQX was answered by Alpaca's real asset lookup with "Alpaca has no asset with the symbol ZZZQX.
Check the spelling. Nothing was added." No stock was added to the operator's groups during the check.
