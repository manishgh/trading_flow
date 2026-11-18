# FinBERT news sentiment on the local site — 18 November 2026

Approved by the user on 18 November 2026 ("lets do that", after the ~440 MB model download was named). Before this,
`FINBERT_SENTIMENT_URL` was unset, so every stored news story was scored by the VADER word-list fallback.

## Model choice

- `ProsusAI/finbert`, pinned to commit `4556d13015211d73dccd3fdd39d39232506f3e43` (23 May 2023, the long-standing
  revision). The current `main` (`0bc2ac10…`, 12 October 2026) adds four commits by a different account: license and
  usage text, a `model.safetensors` copy and a tag. Hugging Face's file listing shows `pytorch_model.bin`, `config.json`,
  `vocab.txt`, `tokenizer_config.json` and `special_tokens_map.json` byte-identical in both commits, so the pin loads
  the same weights without trusting the newer commits.
- Downloaded `pytorch_model.bin`: 437,992,753 bytes, SHA-256
  `e15a7b5738df7f17553399b6d94c6e2ff69c89245d066e8e5d183f5803a554e3`, equal to the published LFS hash and to the
  `model_artifact_sha256` the sidecar reports.
- The model card now states licence CC BY-NC-SA 4.0 (non-commercial). Fine for this personal paper-trading desk; it
  would need review before any commercial use.

## Setup (operator site folder, not in git)

- Cache: `V:\trading_flow\data\cache\huggingface` (`HF_HOME`), 438 MB, downloaded once with `download_model.py`.
- Python: the existing `sidecars/finbert-sentiment/.venv` (Python 3.12.13, torch 2.5.1 CPU, transformers 4.47.1, the
  pinned `requirements.txt`). Offline sidecar tests: 17/17.
- `C:\project\tradingflow-site\start-finbert.ps1` runs `uvicorn app:app` on `127.0.0.1:8088` with the pinned revision,
  `HF_HUB_OFFLINE=1` (no download at start) and log `logs\finbert.log`.
- `update-site.ps1` starts the sidecar when it is not running (including `-StartOnly` after a reboot) and leaves it
  running across site updates. `start-site.ps1` sets `FINBERT_SENTIMENT_URL=http://127.0.0.1:8088`. If the sidecar is
  down the site still falls back to VADER.

## Verification

- Sidecar `/health`: `ready`, loaded and configured revision `4556d130…`, weights hash as above. Test headlines: a
  profit/revenue-growth headline scored positive +0.76, a guidance-cut headline negative −0.97.
- Exposure checked before the site restart at 19:15 UTC (no Alpaca paper positions or open orders); readiness `ready`.
- Within two minutes the site sent 161 stories to `POST /analyze`, all HTTP 200; no sidecar errors and no
  FinBERT-related site warnings.
- Not measured: whether FinBERT labels financial news more accurately than VADER for this desk. News scores remain
  display and monitoring evidence; they do not admit or veto trades.
