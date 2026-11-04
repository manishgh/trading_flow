namespace TradingFlow.Domain.News;

/// <summary>
/// Time limit for the news classifiers' text patterns. The patterns run in .NET's non-backtracking mode or are
/// simple enough to finish in time proportional to the text, and supporting text is capped at 32,000 characters,
/// so a match takes microseconds. The limit only guards against an unforeseen pattern. It must stay well above
/// what a busy machine needs on the first match, when the engine builds its tables; at 100 ms a loaded host threw
/// instead of classifying an ordinary headline.
/// </summary>
internal static class NewsPatterns
{
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);
}
