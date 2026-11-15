# Private paper evidence chart

The Paper job detail and Orders issue five-minute protected contexts from their own
local read handlers. The private journal endpoint checks the current local Identity
Administrator membership and an exact grant at issue and on each read and page.
The chart attaches only to a selected durable paper order or position generation.

## Protected host settings

The grant list is empty unless the host operator supplies an entry in protected local
configuration. IDs below are examples. The account must exactly match the enforced
expected Alpaca paper account, and each listed ID must match retained evidence.

```json
{
  "TradingFlow": {
    "PrivatePaperChart": {
      "Grants": [
        {
          "UserId": "11111111-1111-4111-8111-111111111111",
          "PaperAccountId": "the-exact-configured-paper-account-id",
          "PaperJobIds": ["22222222-2222-4222-8222-222222222222"],
          "ManualOrderIntentIds": ["33333333-3333-4333-8333-333333333333"]
        }
      ]
    }
  }
}
```

The private read service depends on SQLite and Identity. Existing paper page refreshes use
their existing read services. Opening or paging private evidence takes a
deferred SQLite snapshot and performs bounded journal reads. The tests require an
enforced synthetic paper account binding with an isolated SQLite and Identity store.

## Query and evidence verification

The synthetic host tests exercise the production HTTP mapper and antiforgery
middleware, role and grant revocation with a retained context, token expiry,
cross-account and profile rejection, exact order and generation evidence, paging,
and a 200-candidate read limit with one extra row to detect overflow. They run
`EXPLAIN QUERY PLAN` over the executed SQL for generation, intent, order lifecycle,
replacement and selected-order fill queries. Each seek must use the matching
identity index without a temporary sort.

The browser fixture checks viewport limits, exact timestamps, raw decimal evidence
in the native Chart details dialog, time-only marker input and withdrawal of details
after an outdated or unauthorized request. The completed SIP chart is on the
adjusted `all` basis. Private prices stay in details until a verified mapping to
the chart basis exists.

## Isolated verification result

The affected Web and Tests projects compiled with warnings treated as errors.
The combined backend run passed 155 checks, including all five private chart
checks. SQLite returned indexed `SEARCH` plans for account/symbol/generation
positions and intents, client-order lifecycle, account/root replacement ownership,
and account/broker-order exact fills. The assertions also confirmed SQL `LIMIT`
and absence of a temporary sort. The 400-extra-row fixture triggered the single
overflow sentinel and performed no lifecycle read.

The first sandboxed VSTest run reached discovery but could not connect to its
local testhost during the 90-second connection window. The focused retry used
local socket permission for VSTest and the synthetic loopback server. Fixture
corrections preserved SQLite's decimal scale and Alpaca's case-sensitive JSON
keys before the passing run.

The isolated Playwright run passed all 14 chart cases in 36.7 seconds, including
the private fixture and forming, weekly, Desk, and standalone chart coverage.
Playwright stopped the owned host and deleted its fixture database, cache, runs
and artifacts. Offline npm lacked the Playwright archive, so the browser run
reused the existing Playwright 1.61.0 installation in a worktree-local copy
after the two package lockfile hashes matched.

Independent code review identified a missing replacement read for an exact
protective order selected from the Orders journal. The exact view now verifies
the opening position identity and its paper run lineage within the candidate
budget. It reads replacements through the selected order's own broker root.
The new endpoint regression granted only the protective order's separate job,
verified that the old stop ends at the successor transition, confirmed the root
index, and checked that the returned rows all belong to the selected order.
After the fixture invalidated the opening run's provenance, the acknowledgement
remained in details and the active stop disappeared. The combined affected
backend suite passed 161 checks, including all six private chart tests, after
the review fixes.

Reconciliation against an existing authorized paper run remains pending a
permitted retained-evidence observation. The synthetic checks used their own
database and Identity rows.
