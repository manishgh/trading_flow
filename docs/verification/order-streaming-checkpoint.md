# Order and streaming verification — 2026-09-27

Component verification: 30 tests passed, zero failed. The tests use isolated SQLite,
controlled broker/market adapters and in-process Kestrel with the production
authentication registrations and route handlers. No provider request or real order ran.

Command:
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter "FullyQualifiedName~CandidateStreamIntegrityTests|FullyQualifiedName~CandidateApplicationProjectionTests|FullyQualifiedName~CandidateAuditIntegrityTests|FullyQualifiedName~OrderWorkflowIntegrityTests|FullyQualifiedName~OrderPreviewRepositoryTests|FullyQualifiedName~OperatorAccessTests" --verbosity minimal -m:1 --disable-build-servers

Orders bind exact candidate/version/run/strategy/universe/account, fresh quote and
risk state. Strategy entries use the real entry submission pipeline; override
requires Administrator at preview and confirmation. Durable command identity,
fenced validation, broker outcome reconciliation and exact replay cover duplicate,
concurrent, interrupted and expired requests, including cancel and close.
A crash after the durable submission marker but before an intent exists remains
reconciliation-required. It never automatically resubmits an ambiguous order.

All unsafe cookie-authenticated API requests require anti-forgery validation.
Anonymous/forbidden, malformed requests, token rejection, login/logout and cookie
expiry were tested using the same cookie transport as Android.

Candidate invalidations commit alongside orders/position fills. Ownership changes
notify both previous and new generations. Snapshot watermarks precede projection
reads. Replay is run-scoped, capped at 100 events per batch; SSE writes are awaited
with a timeout, heartbeat and cancellation, with explicit expired/invalid/malformed
cursor resynchronization. Hosted tests cover actual SSE and polling exposure parity,
disconnect/reconnect, heartbeats, malformed records and durable restart replay.

Independent requirements and code/design reviews completed. Both reported no
remaining substantive blocker after expired-command and owner-change fixes.
