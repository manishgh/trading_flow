# Shared application workflows

Web and Android submit the same immutable universe, exact strategy identity and durable command contracts. Presentation code does not recalculate strategy eligibility. Market Predictor integration is deferred and its output remains advisory.

## Runs and results

The existing /api/v1 routes expose POST /backtests/runs and /paper/runs, GET /workflow/jobs and /workflow/jobs/{id}, and POST /workflow/jobs/{id}/cancel. Start requests carry an idempotency key, captured universe snapshot, exact strategy identity and configuration key. Backtests bind a fixed UTC window. Paper requests require an explicit authorized mode. Transport retries retain the complete original request; editing inputs creates a new command.

Job details expose bounded projections: at most 100 events, 100 strategy groups and 20 events per group. Result references do not disclose server filesystem paths. Paper job identity is also its candidate decision-run identity; queued jobs can legitimately have no candidate projection yet. Capturing discovery alone does not evaluate or start a paper strategy.

Paper chart reads use the persisted attempt artifact directory, with a legacy configuration fallback. When the generated config is available, reads are restricted to its results root; otherwise the server-persisted attempt directory remains authoritative. Reads are bounded to 500 points and 1 MiB per chart file. Recent decision audits use a 50-row repository page.

## Candidate evidence and updates

Candidate queries combine durable lifecycle state, immutable discovery-source provenance, completed-bar market evidence, rule failures, actual order fills and position generations. Consumed does not mean in trade. Only durable open exposure produces that label.

The canonical decision records the catalyst actually used by the signal. Its article revision, publication time and first-received time are stored atomically with the candidate decision. A later article revision cannot rewrite prior decision evidence. Unavailable evidence remains absent.

Run-wide durable event cursors support SSE replay and polling fallback. Clients reject stale responses and retain editing, selection and focus during updates. Audit links identify the exact candidate; attached-news links identify its recorded article.

## Orders and authentication

Entry review binds candidate version, run, exact strategy, universe, quote, risk state and expiry. Confirmation revalidates those bindings. Operator overrides require Admin authorization. Entry tickets submit protected limit/day orders; exit tickets preserve market, limit, stop and stop-limit support and the applicable time-in-force options. Durable cancellation and confirmation use idempotency keys. An ambiguous confirmation remains pending for reconciliation rather than authorizing another submission.

Browser and Android mutations share the authenticated cookie session and anti-forgery token obtained from /api/auth/csrf. Logout and backend changes cancel pending mobile requests and invalidate retained review state.

## Android

The mobile client consumes shared catalog, run, candidate and order contracts. Page and selection cancellation prevent late responses from replacing a newer selection. Failed transport retries preserve the submitted command.

Candidate alerts retain backend, run, candidate and article identity. Navigation rejects a different backend and resumes after authentication. The local alert history is bounded to 50 records and offers exact candidate navigation even when Android notification permission is unavailable.

## Acceptance boundaries

Deterministic SQLite, hosted HTTP, browser and Android compiler evidence verify the implemented workflows. Device navigation, reconnect and notification delivery still require a connected device or emulator. External provider paper smoke and strategy promotion remain separate gates. No live orders or strategy authorization are implied by application completion.
