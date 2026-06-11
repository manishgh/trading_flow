from transformers import AutoModelForSequenceClassification, AutoTokenizer

MODEL_NAME = "ProsusAI/finbert"


def main() -> None:
    AutoTokenizer.from_pretrained(MODEL_NAME)
    AutoModelForSequenceClassification.from_pretrained(MODEL_NAME)
    print(f"Downloaded {MODEL_NAME}")


if __name__ == "__main__":
    main()
