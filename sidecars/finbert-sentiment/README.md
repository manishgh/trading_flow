# FinBERT Sentiment Sidecar

TradingFlow uses this sidecar to score Alpaca news without embedding Python/Hugging Face runtime inside the .NET engine.

The model and tokenizer must use an explicitly reviewed full 40-character Hugging Face commit. Set `FINBERT_MODEL_REVISION` to that commit; there is no default revision and branches such as `main` are rejected. This change does not select a new model commit or download weights automatically.

## Local Setup

```powershell
cd C:\project\trading_flow\sidecars\finbert-sentiment
python -m venv .venv
.\.venv\Scripts\pip install -r requirements.txt
# Set FINBERT_MODEL_REVISION to the reviewed full model commit before continuing.
.\.venv\Scripts\python download_model.py
.\.venv\Scripts\uvicorn app:app --host 127.0.0.1 --port 8088
```

Set the .NET engine to use it:

```powershell
$env:FINBERT_SENTIMENT_URL = "http://127.0.0.1:8088"
```

If the sidecar is unavailable, TradingFlow falls back to VADER so paper trading does not stop.

Only `download_model.py` is intended to download model files. Runtime startup loads the pinned model and tokenizer from the local Hugging Face cache. Missing files, missing revision or revision mismatch leave health unavailable; after correcting the cache/configuration, restart the sidecar. Loading is attempted once per process rather than retried for every story.

Both Dockerfiles require `--build-arg FINBERT_MODEL_REVISION=<reviewed-full-commit>` and retain that revision in the image environment. The local build uses `sidecars/finbert-sentiment/Dockerfile`; the repository-root build uses `deploy/docker/Dockerfile.news`.

## API

`POST /analyze`

```json
{
  "headline": "Company raises annual guidance after stronger demand",
  "text": "Company raises annual guidance after stronger demand",
  "symbols": ["MU"]
}
```

Response:

```json
{
  "label": "positive",
  "score": 0.91,
  "confidence": 0.91,
  "model": "ProsusAI/finbert",
  "model_revision": "<actual-loaded-40-character-commit>",
  "model_loaded": true
}
```

Scores are normalized to `-1..1`: negative labels become negative, positive labels become positive, neutral is zero.

Company-assessment evidence sends `require_full_text: true`. The sidecar counts the
actual loaded tokenizer's IDs, including special tokens, and rejects inputs beyond
the lesser of its declared capacity and the loaded model's positional capacity
with HTTP 422. Accepted strict requests explicitly disable truncation. Responses
include `full_text_processed`, `input_sha256`, `input_token_count`,
`model_max_tokens`, and `model_artifact_sha256`; the evidence client verifies these
against its exact text and frozen model artifact before accepting a score.
Operational requests that omit this flag retain their existing inference behavior.

The loader explicitly selects `pytorch_model.bin` with `use_safetensors=False`,
hashes that exact cached file once at startup, and checks it did not change during
loading. This loaded-weight hash is reported by health and strict inference; it is
not copied from a caller-supplied policy. Model/tokenizer commit checks remain
mandatory. No model download occurs during startup or inference.

`GET /health` returns HTTP 200 only after model and tokenizer have loaded at the configured immutable revision. It reports `status`, `model_loaded`, actual `model_revision`, `configured_revision` and a failure detail. Before loading or after a load/inference failure it returns HTTP 503; reading health never starts a load or inference.

The .NET client rejects responses without loaded-model provenance or valid three-class scores. Successful classifications carry `finbert-http:ProsusAI/finbert@<actual-commit>` in the existing analyzer/classification-version field. A fallback classification retains `vader`; it is never attributed to FinBERT. The Operations screen probes health rather than inferring success from a configured URL. This identifies the model used; it does not establish news-classification accuracy or authorize trading.

## Offline checks

```powershell
python -m unittest discover -s sidecars/finbert-sentiment -p "test_*.py"
```

Run this from the repository root with the sidecar dependencies installed. These tests stub transformer objects and do not load weights or access the network. They cover explicit revision selection, actual model/tokenizer identity, failed loading, inference failure, health reads and the API response fields. `test_model_runtime.py` alone needs only the standard library. .NET fake-HTTP checks are in `FinbertSentimentProvenanceTests`.

The pinned package supports immutable `revision` and `local_files_only` loading: [Transformers 4.47.1 model loading](https://huggingface.co/docs/transformers/v4.47.1/en/main_classes/model). Tokenizer revision metadata is checked against the [same version's tokenizer loader](https://github.com/huggingface/transformers/blob/v4.47.1/src/transformers/models/auto/tokenization_auto.py).
