
## Shared workflow acceptance

The app uses shared catalog, run, candidate, reviewed-order and anti-forgery contracts. See ../../docs/application-workflows.md for backend semantics.

Broader device acceptance checklist (navigation/notification subset executed on Android 35 emulator; remaining items are listed in ../../docs/verification/android-external-paper-acceptance.md):

- Sign in, capture a universe, start an explicitly authorized run and open its candidate list.
- Change selected jobs while requests are pending; confirm old results do not replace the new selection.
- Lose and restore connectivity while starting a run or reviewing an order; confirm retries retain the same command.
- Open a candidate alert from foreground and cold start, including after login; inspect the exact attached article.
- Switch backend and confirm prior alerts cannot navigate into the wrong backend.
- Deny notification permission and confirm candidate alert history remains usable.
- Leave and return to pages, cancel a run, and inspect terminal results without losing input or duplicating commands.

A successful Release build does not establish these device results. No paper or live order should be submitted merely to satisfy this checklist.

On 27 September 2026, signed Release emulator checks covered login, saved backend, candidate audit/article links, notification denial/history, warm/cold notification navigation through login, Back navigation, wrong-backend rejection, order draft validation, and Watch/Symbol/Paper/Backtests navigation. Real provider checks are separately documented; these fixture-backed Android checks did not place a broker order.
