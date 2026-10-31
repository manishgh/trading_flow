from model_runtime import MODEL_NAME, configured_revision, load_pipeline


def main() -> None:
    """Download an explicitly reviewed immutable revision, then verify both cached components use that revision."""
    revision = configured_revision()
    load_pipeline(revision, local_files_only=False)
    print(f"Downloaded and verified {MODEL_NAME}@{revision}")


if __name__ == "__main__":
    main()
