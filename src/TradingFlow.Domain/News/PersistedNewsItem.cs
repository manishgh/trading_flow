namespace TradingFlow.Domain.News;

public sealed class PersistedNewsItem
{
    public string Id { get; set; } = String.Empty;

    public string Ticker { get; set; } = String.Empty;

    public DateTimeOffset Timestamp { get; set; }

    public string Headline { get; set; } = String.Empty;

    public decimal SentimentScore { get; set; }

    public string Provider { get; set; } = String.Empty;

    public string? Source { get; set; }

    public string? Url { get; set; }

    public string? Summary { get; set; }

    public DateTimeOffset IngestedAt { get; set; }
}
