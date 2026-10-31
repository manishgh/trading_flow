"""Exercise sidecar response contracts directly with fake weights and no HTTP server or downloads."""

import json
from hashlib import sha256
import unittest
from unittest.mock import Mock, patch

from fastapi import HTTPException

import app
from model_runtime import MODEL_NAME, ModelRuntime

REVISION = "a" * 40


class SidecarContractTests(unittest.TestCase):
    def test_strict_response_binds_complete_input_and_actual_weight_hash(self):
        """Strict evidence returns the actual full-input and loaded-weight proof consumed by the .NET client."""
        pipeline = Mock(return_value=[{"label": "positive", "score": 0.9}])
        pipeline.tokenizer = Mock(model_max_length=512)
        pipeline.tokenizer.encode.return_value = [1, 2, 3]
        pipeline.model.config.max_position_embeddings = 512
        runtime = ModelRuntime(Mock(return_value=(pipeline, REVISION, REVISION, "b" * 64)), lambda: REVISION)
        with patch.object(app, "runtime", runtime):
            result = app.analyze(app.AnalyzeRequest(text="ABC earnings", require_full_text=True))
        self.assertEqual(sha256(b"ABC earnings").hexdigest(), result.input_sha256)
        self.assertTrue(result.full_text_processed)
        self.assertEqual(3, result.input_token_count)
        self.assertEqual(512, result.model_max_tokens)
        self.assertEqual("b" * 64, result.model_artifact_sha256)
        pipeline.assert_called_once_with("ABC earnings", truncation=False)

    def test_strict_oversize_response_is_422_and_has_no_model_prediction(self):
        """Too many actual tokenizer IDs reject the request instead of returning a silently truncated score."""
        pipeline = Mock(return_value=[{"label": "positive", "score": 0.9}])
        pipeline.tokenizer = Mock(model_max_length=512)
        pipeline.tokenizer.encode.return_value = list(range(513))
        pipeline.model.config.max_position_embeddings = 512
        runtime = ModelRuntime(Mock(return_value=(pipeline, REVISION, REVISION, "b" * 64)), lambda: REVISION)
        with patch.object(app, "runtime", runtime):
            with self.assertRaises(HTTPException) as error:
                app.analyze(app.AnalyzeRequest(text="short but tokenized beyond capacity", require_full_text=True))
        self.assertEqual(422, error.exception.status_code)
        self.assertIn("513 tokens", error.exception.detail)
        pipeline.assert_not_called()

    def test_health_requires_actual_load(self):
        """An idle endpoint returns 503 until its fake model has genuinely completed the load operation."""
        pipeline = Mock(return_value=[{"label": "positive", "score": 0.9}])
        runtime = ModelRuntime(Mock(return_value=(pipeline, REVISION, REVISION)), lambda: REVISION)
        with patch.object(app, "runtime", runtime):
            self.assertEqual(503, app.health().status_code)
            self.assertTrue(runtime.load())
            response = app.health()
            payload = json.loads(response.body)
            self.assertEqual(200, response.status_code)
            self.assertTrue(payload["model_loaded"])
            self.assertEqual(REVISION, payload["model_revision"])
            self.assertEqual(REVISION, payload["configured_revision"])
            pipeline.assert_not_called()

    def test_analyze_returns_actual_loaded_identity(self):
        """The response consumed by .NET contains the revision of the pipeline that supplied this exact score."""
        pipeline = Mock(return_value=[{"label": "negative", "score": 0.9}])
        runtime = ModelRuntime(Mock(return_value=(pipeline, REVISION, REVISION)), lambda: REVISION)
        with patch.object(app, "runtime", runtime):
            result = app.analyze(app.AnalyzeRequest(text="Company lowers guidance", symbols=["MSFT"]))
        self.assertEqual(-0.9, result.score)
        self.assertEqual("negative", result.label)
        self.assertEqual(MODEL_NAME, result.model)
        self.assertEqual(REVISION, result.model_revision)
        self.assertTrue(result.model_loaded)
        pipeline.assert_called_once_with("Company lowers guidance")

    def test_failed_load_is_http_503(self):
        """Missing cache data is an unavailable inference response rather than neutral sentiment or success."""
        runtime = ModelRuntime(Mock(side_effect=OSError("pinned cache missing")), lambda: REVISION)
        with patch.object(app, "runtime", runtime):
            with self.assertRaises(HTTPException) as error:
                app.analyze(app.AnalyzeRequest(text="news"))
            self.assertEqual(503, error.exception.status_code)
            self.assertEqual(503, app.health().status_code)
            self.assertFalse(json.loads(app.health().body)["model_loaded"])

    def test_unknown_label_is_not_treated_as_neutral(self):
        """Malformed model output must fail so downstream clients can identify their actual fallback."""
        with self.assertRaisesRegex(ValueError, "unsupported"):
            app.normalize_score("unknown", 0.9)


if __name__ == "__main__":
    unittest.main()
