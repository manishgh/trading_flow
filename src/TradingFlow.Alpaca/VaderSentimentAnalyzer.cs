using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Alpaca;

public sealed class VaderSentimentAnalyzer : ISentimentAnalyzer
{
    private readonly VaderSharp2.SentimentIntensityAnalyzer _analyzer = new();

    public string AnalyzerName => "vader";

    public Task<SentimentResult> AnalyzeAsync(NewsArticle article, CancellationToken cancellationToken)
    {
        var text = String.Join(". ", new[] { article.Headline, article.Summary, article.Content }
            .Where(part => !String.IsNullOrWhiteSpace(part)));

        var scores = _analyzer.PolarityScores(text);
        var score = (decimal)scores.Compound;
        var label = score switch
        {
            >= 0.15m => "positive",
            <= -0.15m => "negative",
            _ => "neutral"
        };

        return Task.FromResult(new SentimentResult(score, label, AnalyzerName));
    }
}
