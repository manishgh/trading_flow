"""Offline tests: fake model objects exercise provenance/load behavior without downloading or importing weights."""

import os
import sys
import types
import tempfile
from hashlib import sha256
import unittest
from unittest.mock import Mock, patch

from model_runtime import MODEL_NAME, ModelRuntime, InputTooLongError, configured_revision, load_pipeline

REVISION = "a" * 40
ARTIFACT_SHA256 = "b" * 64


class FakePipeline:
    def __call__(self, text):
        """Return deterministic inference without a model runtime or network."""
        return [{"label": "positive", "score": 0.9}]


class ModelRuntimeTests(unittest.TestCase):
    def strict_pipeline(self, token_count: int, tokenizer_limit: int = 512):
        """Expose an actual-tokenizer-shaped fake whose sequence length can differ sharply from character length."""
        analyzer = Mock(return_value=[{"label": "positive", "score": 0.9}])
        analyzer.tokenizer = Mock(model_max_length=tokenizer_limit)
        analyzer.tokenizer.encode.return_value = list(range(token_count))
        analyzer.model.config.max_position_embeddings = 512
        runtime = ModelRuntime(Mock(return_value=(analyzer, REVISION, REVISION, ARTIFACT_SHA256)), lambda: REVISION)
        return runtime, analyzer

    def test_strict_inference_counts_special_tokens_and_never_truncates(self):
        """A sequence fitting exactly at the loaded model boundary is scored intact and binds its exact UTF-8 bytes."""
        runtime, analyzer = self.strict_pipeline(512)
        result, _ = runtime.analyze("ABC café earnings", require_full_text=True)
        analyzer.tokenizer.encode.assert_called_once_with("ABC café earnings", add_special_tokens=True, truncation=False)
        analyzer.assert_called_once_with("ABC café earnings", truncation=False)
        self.assertTrue(result["full_text_processed"])
        self.assertEqual(512, result["input_token_count"])
        self.assertEqual(512, result["model_max_tokens"])
        self.assertEqual(sha256("ABC café earnings".encode("utf-8")).hexdigest(), result["input_sha256"])
        self.assertEqual(ARTIFACT_SHA256, result["model_artifact_sha256"])

    def test_strict_inference_rejects_one_extra_token_without_running_the_model(self):
        """Short character strings can tokenize beyond capacity; special-token-inclusive counts control admission."""
        runtime, analyzer = self.strict_pipeline(513)
        with self.assertRaisesRegex(InputTooLongError, "513 tokens.*512"):
            runtime.analyze("short text", require_full_text=True)
        analyzer.assert_not_called()
        self.assertEqual("ready", runtime.health()["status"])

    def test_strict_inference_respects_lower_tokenizer_capacity(self):
        """When the tokenizer declares a stricter real maximum, the model configuration cannot override it."""
        runtime, analyzer = self.strict_pipeline(257, tokenizer_limit=256)
        with self.assertRaisesRegex(InputTooLongError, "257 tokens.*256"):
            runtime.analyze("short text", require_full_text=True)
        analyzer.assert_not_called()

    def test_strict_inference_rejects_absent_artifact_identity(self):
        """The operational legacy fake loader remains usable, but it cannot certify weights for strict evidence."""
        runtime = ModelRuntime(Mock(return_value=(FakePipeline(), REVISION, REVISION)), lambda: REVISION)
        with self.assertRaisesRegex(ValueError, "artifact SHA256"):
            runtime.analyze("news", require_full_text=True)

    def test_health_does_not_load_a_model(self):
        """A health read must not turn endpoint reachability into claimed model availability."""
        loader = Mock()
        runtime = ModelRuntime(loader, lambda: REVISION)
        self.assertEqual("not_loaded", runtime.health()["status"])
        self.assertFalse(runtime.health()["model_loaded"])
        self.assertIsNone(runtime.health()["model_revision"])
        loader.assert_not_called()

    def test_success_reports_actual_revision_and_loads_once(self):
        """Only a verified callable pipeline can publish the actual inference revision."""
        loader = Mock(return_value=(FakePipeline(), REVISION, REVISION))
        runtime = ModelRuntime(loader, lambda: REVISION)
        self.assertTrue(runtime.load())
        self.assertTrue(runtime.load())
        result, revision = runtime.analyze("earnings")
        self.assertEqual(REVISION, revision)
        self.assertEqual("positive", result["label"])
        self.assertEqual("ready", runtime.health()["status"])
        self.assertEqual(REVISION, runtime.health()["model_revision"])
        self.assertTrue(runtime.health()["model_loaded"])
        loader.assert_called_once_with(REVISION)

    def test_mismatched_loaded_revision_is_not_success(self):
        """Configured metadata must not replace a conflicting actual model/tokenizer identity."""
        for model_revision, tokenizer_revision in [("b" * 40, REVISION), (REVISION, "b" * 40)]:
            runtime = ModelRuntime(Mock(return_value=(FakePipeline(), model_revision, tokenizer_revision)), lambda: REVISION)
            self.assertFalse(runtime.load())
            self.assertEqual("load_failed", runtime.health()["status"])
            self.assertFalse(runtime.health()["model_loaded"])
            self.assertIsNone(runtime.health()["model_revision"])

    def test_load_failure_is_visible_and_not_retried_for_every_article(self):
        """Missing cached files must fail concretely and require a deliberate service restart after setup."""
        loader = Mock(side_effect=OSError("pinned model is not cached"))
        runtime = ModelRuntime(loader, lambda: REVISION)
        self.assertFalse(runtime.load())
        with self.assertRaisesRegex(RuntimeError, "not cached"):
            runtime.analyze("news")
        self.assertIn("not cached", runtime.health()["detail"])
        self.assertFalse(runtime.health()["model_loaded"])
        loader.assert_called_once()

    def test_inference_failure_is_reported_without_claiming_the_model_unloaded(self):
        """A loaded model and successful inference are distinct facts in health."""
        analyzer = Mock(side_effect=RuntimeError("inference failed"))
        runtime = ModelRuntime(Mock(return_value=(analyzer, REVISION, REVISION)), lambda: REVISION)
        with self.assertRaisesRegex(RuntimeError, "inference failed"):
            runtime.analyze("news")
        self.assertTrue(runtime.health()["model_loaded"])
        self.assertEqual("inference_failed", runtime.health()["status"])

    def test_missing_or_floating_revision_is_rejected(self):
        """Never accept a branch, tag, shortened SHA or implicit model revision."""
        for revision in ["", "main", "model-v1", "a" * 39, "z" * 40]:
            with patch.dict(os.environ, {"FINBERT_MODEL_REVISION": revision}):
                with self.assertRaisesRegex(ValueError, "40-character"):
                    configured_revision()
        with patch.dict(os.environ, {"FINBERT_MODEL_REVISION": "A" * 40}):
            self.assertEqual(REVISION, configured_revision())

    def test_production_loader_pins_both_components_and_uses_only_local_files(self):
        """Check production loader arguments and resolved metadata while transformers itself remains a stub."""
        transformer = types.ModuleType("transformers")
        transformer.AutoTokenizer = Mock()
        transformer.AutoModelForSequenceClassification = Mock()
        transformer.AutoModelForSequenceClassification.from_pretrained.return_value = types.SimpleNamespace(
            config=types.SimpleNamespace(_commit_hash=REVISION))
        transformer.pipeline = Mock(return_value=FakePipeline())
        metadata = types.ModuleType("transformers.models.auto.tokenization_auto")
        metadata.get_tokenizer_config = Mock(return_value={"_commit_hash": REVISION})
        hub = types.ModuleType("transformers.utils.hub")
        modules = {"transformers": transformer, "transformers.models.auto.tokenization_auto": metadata,
                   "transformers.utils.hub": hub}
        with tempfile.TemporaryDirectory(prefix="finbert-loader-synthetic-") as directory:
            weights_path = os.path.join(directory, "pytorch_model.bin")
            with open(weights_path, "wb") as weights:
                weights.write(b"synthetic pinned weights")
            hub.cached_file = Mock(return_value=weights_path)
            with patch.dict(sys.modules, modules):
                analyzer, actual, tokenizer_actual, artifact_sha256 = load_pipeline(REVISION)
        self.assertEqual(sha256(b"synthetic pinned weights").hexdigest(), artifact_sha256)
        self.assertTrue(callable(analyzer))
        self.assertEqual((REVISION, REVISION), (actual, tokenizer_actual))
        options = {"revision": REVISION, "local_files_only": True, "trust_remote_code": False}
        transformer.AutoTokenizer.from_pretrained.assert_called_once_with(MODEL_NAME, **options)
        transformer.AutoModelForSequenceClassification.from_pretrained.assert_called_once_with(MODEL_NAME, use_safetensors=False, **options)
        metadata.get_tokenizer_config.assert_called_once_with(MODEL_NAME, **options)
        hub.cached_file.assert_called_once_with(MODEL_NAME, "pytorch_model.bin", revision=REVISION, local_files_only=True)


if __name__ == "__main__":
    unittest.main()
