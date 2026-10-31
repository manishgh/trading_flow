"""Load one explicitly pinned model and report the revision actually available for inference."""

import os
import re
from hashlib import sha256
from threading import Lock

MODEL_NAME = "ProsusAI/finbert"


class InputTooLongError(ValueError):
    """Report a complete-text request that cannot fit the actual loaded model without truncation."""


def configured_revision() -> str:
    """Require a full immutable Hugging Face commit instead of silently following a branch or tag."""
    revision = os.environ.get("FINBERT_MODEL_REVISION", "").strip().lower()
    if re.fullmatch(r"[0-9a-f]{40}", revision) is None:
        raise ValueError("Set FINBERT_MODEL_REVISION to the reviewed 40-character model commit before loading.")
    return revision


def load_pipeline(revision: str, *, local_files_only: bool = True):
    """Load only cached files for the same model/tokenizer commit; importing this module never downloads weights."""
    from transformers import AutoModelForSequenceClassification, AutoTokenizer, pipeline
    from transformers.models.auto.tokenization_auto import get_tokenizer_config
    from transformers.utils.hub import cached_file

    options = {"revision": revision, "local_files_only": local_files_only, "trust_remote_code": False}
    tokenizer_metadata = get_tokenizer_config(MODEL_NAME, **options)
    tokenizer = AutoTokenizer.from_pretrained(MODEL_NAME, **options)
    # Explicitly select the same single-file format whose actual cached bytes are fingerprinted below.
    weights_path = cached_file(MODEL_NAME, "pytorch_model.bin", revision=revision, local_files_only=local_files_only)
    before = os.stat(weights_path)
    weights_hash = file_sha256(weights_path)
    model = AutoModelForSequenceClassification.from_pretrained(MODEL_NAME, use_safetensors=False, **options)
    after = os.stat(weights_path)
    if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise ValueError("Pinned model weights changed while loading; no artifact identity can be claimed.")
    actual_revision = getattr(model.config, "_commit_hash", None)
    tokenizer_revision = tokenizer_metadata.get("_commit_hash")
    if actual_revision != revision or tokenizer_revision != revision:
        raise ValueError("Cached model or tokenizer revision does not match FINBERT_MODEL_REVISION.")
    analyzer = pipeline("sentiment-analysis", model=model, tokenizer=tokenizer, truncation=True)
    return analyzer, actual_revision, tokenizer_revision, weights_hash


def file_sha256(path: str) -> str:
    """Fingerprint the exact selected cached weights once at model startup without retaining their full bytes."""
    digest = sha256()
    with open(path, "rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


class ModelRuntime:
    """Keep loading, failure and actual inference identity separate from endpoint configuration."""

    def __init__(self, loader=load_pipeline, revision_reader=configured_revision):
        """Allow offline tests to supply a fake loader while production uses the pinned cached model."""
        self._loader = loader
        self._revision_reader = revision_reader
        self._load_lock = Lock()
        self._pipeline = None
        self._requested_revision = None
        self._actual_revision = None
        self._artifact_sha256 = None
        self._status = "not_loaded"
        self._error = None
        self._attempted = False

    def load(self) -> bool:
        """Attempt loading once per process and expose success only after both revisions and the pipeline are verified."""
        with self._load_lock:
            if self._attempted:
                return self._pipeline is not None
            self._attempted = True
            self._status = "loading"
            try:
                self._requested_revision = self._revision_reader()
                loaded = self._loader(self._requested_revision)
                analyzer, actual_revision, tokenizer_revision = loaded[:3]
                artifact_sha256 = loaded[3] if len(loaded) == 4 else None
                if artifact_sha256 is not None and re.fullmatch(r"[0-9a-f]{64}", artifact_sha256) is None:
                    raise ValueError("Loaded model artifact SHA256 is invalid.")
                if not callable(analyzer) or actual_revision != self._requested_revision or tokenizer_revision != actual_revision:
                    raise ValueError("Loaded model/tokenizer do not identify the configured immutable commit.")
                self._pipeline = analyzer
                self._actual_revision = actual_revision
                self._artifact_sha256 = artifact_sha256
                self._status = "ready"
                return True
            except Exception as error:
                self._pipeline = None
                self._actual_revision = None
                self._status = "load_failed"
                self._error = f"{type(error).__name__}: {error}"[:500]
                return False

    def health(self) -> dict:
        """Read current load state without starting a download, retry or inference."""
        return {
            "status": self._status,
            "model": MODEL_NAME,
            "model_loaded": self._pipeline is not None,
            "model_revision": self._actual_revision,
            "configured_revision": self._requested_revision,
            "model_artifact_sha256": self._artifact_sha256,
            "detail": self._error,
        }

    def _complete_text_metadata(self, text: str) -> dict:
        """Count actual tokenizer IDs including special tokens and reject text exceeding either loaded capacity."""
        if self._artifact_sha256 is None:
            raise ValueError("Loaded model weights have no verified artifact SHA256 for evidence inference.")
        tokenizer = self._pipeline.tokenizer
        model_limit = getattr(self._pipeline.model.config, "max_position_embeddings", None)
        tokenizer_limit = getattr(tokenizer, "model_max_length", None)
        if not isinstance(model_limit, int) or isinstance(model_limit, bool) or model_limit <= 0:
            raise ValueError("Loaded model does not expose a positive token capacity for complete-text inference.")
        limits = [model_limit]
        # Transformers can use a huge integer sentinel when the tokenizer declares no separate maximum.
        if isinstance(tokenizer_limit, int) and not isinstance(tokenizer_limit, bool) and 0 < tokenizer_limit < 1_000_000_000:
            limits.append(tokenizer_limit)
        maximum = min(limits)
        tokens = tokenizer.encode(text, add_special_tokens=True, truncation=False)
        count = len(tokens)
        if count > maximum:
            raise InputTooLongError(f"Complete text requires {count} tokens including special tokens; loaded model capacity is {maximum}. No inference was performed.")
        return {"input_sha256": sha256(text.encode("utf-8")).hexdigest(), "full_text_processed": True,
                "input_token_count": count, "model_max_tokens": maximum,
                "model_artifact_sha256": self._artifact_sha256}

    def analyze(self, text: str, *, require_full_text: bool = False) -> tuple[dict, str]:
        """Preserve operational inference while strict evidence requests verify capacity and explicitly disable truncation."""
        if not self.load():
            raise RuntimeError(self._error or "The pinned FinBERT model has not loaded.")
        metadata = self._complete_text_metadata(text) if require_full_text else None
        try:
            result = self._pipeline(text, truncation=False)[0] if require_full_text else self._pipeline(text)[0]
            if metadata is not None:
                result = {**result, **metadata}
            self._status = "ready"
            self._error = None
            return result, self._actual_revision
        except Exception as error:
            self._status = "inference_failed"
            self._error = f"{type(error).__name__}: {error}"[:500]
            raise
