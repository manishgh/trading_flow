# Web groups and progress checkpoint

Scope: Web/API only; Android work is excluded at the user's request.

The desk now exposes source-bound removal and untagging. The shared group dialog can create an empty destination through a durable authenticated command, then requires a separate membership Apply. Exact retries survive closing and reloading; duplicate names cannot replace groups. Creation Undo rejects subsequent edits, memberships and immutable preparation revisions. Existing notes and memberships remain protected by the existing transaction and before/after checks.

Six remaining dynamic Razor bar styles now use native progress values. State colors apply to the measured segment, including zero values, instead of painting the entire track. Audit live refresh updates the native value.

Requirements review covered this Web-only scope. Independent review found color specificity and a stale receipt status message; both were corrected. The review confirmed creation identity, guarded Undo, inherited authentication and CSRF enforcement.

Verification: focused `GroupNewsQueryTests`: 13 passed, with Web compilation. Browser `group-news-earnings.spec.js` and `audit-paging.spec.js`: 10 passed after the review fixes. The cases include separate creation/membership, guarded Undo, unknown-outcome retry after reload, anonymous API rejection, selected-symbol isolation, preserved notes and native progress updates. JavaScript syntax and CRLF-aware diff checks are run before committing. No operational database or broker order was used.

The broader desk fields, shared News presentation and remaining plan acceptance continue in subsequent checkpoints; this receipt does not certify the whole plan.
