from functools import lru_cache
from typing import List

from fastapi import FastAPI
from pydantic import BaseModel, Field
from transformers import AutoModelForSequenceClassification, AutoTokenizer, pipeline

MODEL_NAME = "ProsusAI/finbert"


class AnalyzeRequest(BaseModel):
    text: str = Field(min_length=1, max_length=8000)
    headline: str | None = None
    symbols: List[str] = Field(default_factory=list)


class AnalyzeResponse(BaseModel):
    label: str
    score: float
    confidence: float
    model: str = MODEL_NAME


app = FastAPI(title="TradingFlow FinBERT Sentiment", version="1.0.0")


@lru_cache(maxsize=1)
def sentiment_pipeline():
    tokenizer = AutoTokenizer.from_pretrained(MODEL_NAME)
    model = AutoModelForSequenceClassification.from_pretrained(MODEL_NAME)
    return pipeline("sentiment-analysis", model=model, tokenizer=tokenizer, truncation=True)


def normalize_score(label: str, confidence: float) -> float:
    normalized = label.lower()
    if normalized == "positive":
        return confidence
    if normalized == "negative":
        return -confidence
    return 0.0


@app.get("/health")
def health():
    return {"status": "ok", "model": MODEL_NAME}


@app.post("/analyze", response_model=AnalyzeResponse)
def analyze(request: AnalyzeRequest):
    result = sentiment_pipeline()(request.text[:8000])[0]
    label = str(result["label"]).lower()
    confidence = float(result["score"])
    return AnalyzeResponse(
        label=label,
        score=normalize_score(label, confidence),
        confidence=confidence,
    )
