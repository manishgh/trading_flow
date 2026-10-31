from contextlib import asynccontextmanager
from math import isfinite
from typing import List

from fastapi import FastAPI, HTTPException
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field
from starlette.concurrency import run_in_threadpool

from model_runtime import MODEL_NAME, ModelRuntime, InputTooLongError


class AnalyzeRequest(BaseModel):
    text: str = Field(min_length=1, max_length=8000)
    headline: str | None = None
    symbols: List[str] = Field(default_factory=list)
    require_full_text: bool = False


class AnalyzeResponse(BaseModel):
    label: str
    score: float
    confidence: float
    model: str = MODEL_NAME
    model_revision: str
    model_loaded: bool
    input_sha256: str | None = None
    full_text_processed: bool = False
    input_token_count: int | None = None
    model_max_tokens: int | None = None
    model_artifact_sha256: str | None = None


runtime = ModelRuntime()


@asynccontextmanager
async def lifespan(_app):
    """Load cached pinned weights at startup while keeping failed-load health inspectable."""
    await run_in_threadpool(runtime.load)
    yield


app = FastAPI(title="TradingFlow FinBERT Sentiment", version="1.1.0", lifespan=lifespan)


def normalize_score(label: str, confidence: float) -> float:
    """Map only a valid three-class model result into the existing signed sentiment range."""
    normalized = label.lower()
    if normalized not in {"positive", "negative", "neutral"} or not isfinite(confidence) or not 0 <= confidence <= 1:
        raise ValueError("FinBERT returned an unsupported label or confidence.")
    if normalized == "positive":
        return confidence
    if normalized == "negative":
        return -confidence
    return 0.0


@app.get("/health")
def health():
    """Report the actual loaded revision; a reachable process with missing weights returns HTTP 503."""
    state = runtime.health()
    return JSONResponse(state, status_code=200 if state["status"] == "ready" else 503)


@app.post("/analyze", response_model=AnalyzeResponse)
def analyze(request: AnalyzeRequest):
    """Bind strict evidence responses to complete input bytes; ordinary operational requests retain their existing behavior."""
    try:
        result, revision = runtime.analyze(request.text, require_full_text=request.require_full_text)
        label = str(result["label"]).lower()
        confidence = float(result["score"])
        score = normalize_score(label, confidence)
    except InputTooLongError as error:
        raise HTTPException(status_code=422, detail=str(error)) from error
    except Exception as error:
        raise HTTPException(status_code=503, detail=f"FinBERT inference unavailable: {error}") from error
    return AnalyzeResponse(
        label=label,
        score=score,
        confidence=confidence,
        model_revision=revision,
        model_loaded=True,
        input_sha256=result.get("input_sha256"),
        full_text_processed=result.get("full_text_processed", False),
        input_token_count=result.get("input_token_count"),
        model_max_tokens=result.get("model_max_tokens"),
        model_artifact_sha256=result.get("model_artifact_sha256"),
    )
