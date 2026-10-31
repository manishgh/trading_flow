FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1
ENV HF_HOME=/models/huggingface

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app.py model_runtime.py download_model.py ./
ARG FINBERT_MODEL_REVISION
ENV FINBERT_MODEL_REVISION=${FINBERT_MODEL_REVISION}
RUN python download_model.py

EXPOSE 8088

CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8088"]
