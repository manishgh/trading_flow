# News Sidecar API

The Go news sidecar is used by paper and live modes to provide a fast event/sentiment veto check. Backtests can disable news explicitly with a mode-local news config.

## Mode Config

Example: `configs/paper/news/manual-webhook.yaml`

```yaml
enabled: true
sidecar_endpoint: http://localhost:8080
provider:
  name: manual_webhook
  sentiment_field: sentiment_score
pipeline:
  worker_count: 4
  channel_buffer: 1024
veto:
  ttl_minutes: 360
  negative_threshold: -0.6
```

Backtest example: `configs/backtest/news/disabled.yaml`

```yaml
enabled: false
```

## Provider Sentiment

If the news provider sends sentiment, the sidecar uses it directly:

```json
{
  "ticker": "MU",
  "published_at": "2026-05-31T13:05:00Z",
  "headline": "Micron cuts guidance after weak memory demand",
  "source": "provider",
  "provider": "benzinga",
  "provider_id": "benzinga-123",
  "provider_sentiment": -0.82,
  "provider_relevance": 0.91,
  "catalyst_tags": ["guidance", "earnings"]
}
```

If provider sentiment is absent, the sidecar falls back to an auditable local lexicon analyzer.

## Submit News

Request:

```http
POST /v1/news
Content-Type: application/json
```

Response:

```json
{
  "accepted": true,
  "disabled": false,
  "queued": 1,
  "at": "2026-05-31T13:05:01Z"
}
```

## Check Veto

The C# engine should call this before paper/live order routing:

```http
GET /v1/veto/MU
```

Response:

```json
{
  "ticker": "MU",
  "sentiment_score": -0.82,
  "relevance": 0.91,
  "is_action_vetoed": true,
  "reason": "provider_sentiment",
  "headline": "Micron cuts guidance after weak memory demand",
  "source": "provider",
  "provider": "benzinga",
  "provider_id": "benzinga-123",
  "catalyst_tags": ["guidance", "earnings"],
  "analyzed_by": "provider",
  "evaluated_at": "2026-05-31T13:05:01Z",
  "expires_at": "2026-05-31T19:05:01Z"
}
```

When no active news exists:

```json
{
  "ticker": "MU",
  "sentiment_score": 0,
  "relevance": 0,
  "is_action_vetoed": false,
  "reason": "no_active_news",
  "analyzed_by": "none"
}
```

