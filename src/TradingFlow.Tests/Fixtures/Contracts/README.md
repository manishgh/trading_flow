# Swing prediction golden fixture

Source: `C:\project\market-predictor\tests\fixtures\contracts\swing_prediction_response.json`
SHA-256: `59288471c249b0422443f0a68ce7623f4b03956826877ad1158dc79ebf1fea51`
Accepted change log: 1 November 2026, the user chose the canonical V1 contract kept by Market Predictor commit af9e9b4. Wire field: `contract_version`.
Contract: `market_predictor.prediction.v1`; five scored/abstaining outcomes including `sector_peer_floor`.

Copied byte-for-byte. Client tests verify the hash and require byte equality with
the producer fixture whenever its checkout exists locally. A missing source file
inside an existing checkout fails. Only an absent checkout skips the local comparison.

Update only after a published contract change and dated consumer acknowledgement.
Names inside hash-pinned evidence are preserved exactly as published.

# Candidate workflow audit fixture

`candidate_workflow_audit.json` records the production HTTP audit handler for an
isolated SQLite workflow: source capture, candidate start, then durable transition,
gate, partial market, catalyst, order and position records. It covers absent
evidence and present evidence with unknown timestamps left null. The fixture
contains synthetic test records and is not broker execution or strategy evidence.

`CandidateWorkflowEndpointTests` compares the complete JSON projection, including
nulls. Candidate ID and source hash are normalized because the snapshot identity
is generated per capture; the test separately checks those relationships against
the saved preview/run/source before normalization. Market/catalyst evidence hashes
and all remaining values are compared verbatim. The hosted test uses the real
route extension and repositories with a test principal and controlled discovery
source/strategy resolver. Real catalog identity/authorization checks are separate
in `ConfigCatalogServiceTests`; full cookie/CSRF acceptance is checkpoint 4.
