# Swing prediction golden fixture

Source: `C:\project\market-predictor\tests\fixtures\contracts\swing_prediction_response.json`
SHA-256: `b62f577dbd9ca1a09c9959d5da84df0db9040d8e6b3c17d26eabfcb4f369f1cf`
Accepted change log: both September 27 entries, with the later unversioned contract
entry superseding the initial identifier. Contract: `market_predictor.prediction`.

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
