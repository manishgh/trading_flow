# FinBERT Sentiment Sidecar

TradingFlow uses this sidecar to score Alpaca news without embedding Python/Hugging Face runtime inside the .NET engine.

## Local Setup

```powershell
cd C:\project\trading_flow\sidecars\finbert-sentiment
python -m venv .venv
.\.venv\Scripts\pip install -r requirements.txt
.\.venv\Scripts\python download_model.py
.\.venv\Scripts\uvicorn app:app --host 127.0.0.1 --port 8088
```

Set the .NET engine to use it:

```powershell
$env:FINBERT_SENTIMENT_URL = "http://127.0.0.1:8088"
```

If the sidecar is unavailable, TradingFlow falls back to VADER so paper trading does not stop.

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
  "model": "ProsusAI/finbert"
}
```

Scores are normalized to `-1..1`: negative labels become negative, positive labels become positive, neutral is zero.
