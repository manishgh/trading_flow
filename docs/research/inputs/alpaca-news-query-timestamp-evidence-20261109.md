# Alpaca news query: which timestamp do `start` and `end` filter on?

Observed 9 November 2026 against `GET https://data.alpaca.markets/v1beta1/news` (Benzinga via
Alpaca), with `symbols`, `start`, `end`, `limit=50`, `sort=asc`. Credentials were sent in request
headers and are not recorded here.

Method: pick articles whose `updated_at` is minutes after `created_at`, then query one window that
contains only the creation time and one that contains only the update time.

| Symbol | Article id | created_at (UTC) | updated_at (UTC) | Query window (UTC) | HTTP | Articles | Article returned | Response SHA-256 |
|---|---|---|---|---|---|---|---|---|
| DASH | 60861965 | 2026-08-02 13:45:00 | 2026-08-02 14:01:29 | 13:40:00 to 13:55:00 (created only) | 200 | 0 | no | fd2922d04324c5f4cf46253dbb251be9171d70f71cdffa6ccc1f0b056e31ef25 |
| DASH | 60861965 | 2026-08-02 13:45:00 | 2026-08-02 14:01:29 | 13:55:00 to 14:10:00 (updated only) | 200 | 1 | yes | dad4330b49e48fec6a7e3ee4aea78f587cbbe5e6713c0d983d2f27fb58d3ec0a |
| CRDO | 60012717 | 2026-06-22 13:05:01 | 2026-06-22 13:18:16 | 13:00:00 to 13:10:00 (created only) | 200 | 0 | no | fd2922d04324c5f4cf46253dbb251be9171d70f71cdffa6ccc1f0b056e31ef25 |
| CRDO | 60012717 | 2026-06-22 13:05:01 | 2026-06-22 13:18:16 | 13:10:00 to 13:25:00 (updated only) | 200 | 1 | yes | 3b3ec3bf7ccf917ac85e06fc325b91fe88da1c63dcebcc2782d222f3a4164aaf |

Conclusion: the news endpoint's `start` and `end` select articles by `updated_at`. A collection
window therefore contains every article whose latest update falls inside it, which is the
`updated_at` query semantics the historical-news diagnostic package records
(`--query-timestamp-field updated_at`, basis `observed_provider_query_behavior`).

Limits: two articles, one provider, observed on one day; the provider could change this behaviour.
Earlier article revisions are not returned by the endpoint, so revision history stays unproven.
