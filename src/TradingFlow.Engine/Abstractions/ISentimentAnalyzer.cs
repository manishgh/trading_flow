using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Abstractions;

public interface ISentimentAnalyzer
{
    string AnalyzerName { get; }

    Task<SentimentResult> AnalyzeAsync(NewsArticle article, CancellationToken cancellationToken);
}

public sealed record SentimentResult(
    decimal Score,
    string Label,
    string AnalyzerName,
    decimal? Confidence = null);
