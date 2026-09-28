# Swing prediction golden fixture

Source: `C:\project\market-predictor\tests\fixtures\contracts\swing_prediction_response.json`
SHA-256: `96cbcd133b8e96253b034fabba10624be79b0d80550f043f3869905251bec522`
Accepted change log: September 28 API v4. Wire field: `contract_version`.
Contract: `market_predictor.prediction.v4`; five scored/abstaining outcomes including `sector_peer_floor`.

Copied byte-for-byte. Client tests verify the hash and require byte equality with
the producer fixture whenever its checkout exists locally. A missing source file
inside an existing checkout fails. Only an absent checkout skips the local comparison.

Update only after a published contract change and dated consumer acknowledgement.
Names inside hash-pinned evidence are preserved exactly as published.
