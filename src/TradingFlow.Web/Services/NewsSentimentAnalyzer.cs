using TradingFlow.Alpaca;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Web.Services;

/// <summary>
/// The scorer for stored news: the local FinBERT sidecar when FINBERT_SENTIMENT_URL is set, otherwise (or while FinBERT does
/// not answer) VADER. Each result names the scorer that actually produced it.
/// </summary>
public sealed class NewsSentimentAnalyzer : ISentimentAnalyzer, IDisposable
{
    public const string FallbackName = TradingFlow.Data.News.SqliteNewsFeedRepository.FallbackScorer;

    // The sidecar rejects longer text; the separators between headline, summary and content also count.
    internal const int MaximumTextLength = 8000;
    private const int SeparatorAllowance = 4;
    private const int FailuresBeforePause = 3;
    private static readonly TimeSpan PauseLength = TimeSpan.FromSeconds(60);
    private readonly HttpClient? httpClient;
    private readonly Uri? endpoint;
    private readonly ISentimentAnalyzer inner;
    private readonly ISentimentAnalyzer pausedScorer = new VaderSentimentAnalyzer();
    private readonly TimeProvider clock;
    private readonly object pauseGate = new();
    private int consecutiveFailures;
    private long? pausedSince;
    private volatile string? problem;

    /// <summary>Builds the configured scorer once; the address comes from the process, then the user environment.</summary>
    public NewsSentimentAnalyzer(TimeProvider clock)
    {
        this.clock = clock;
        var address = Environment.GetEnvironmentVariable("FINBERT_SENTIMENT_URL")
            ?? Environment.GetEnvironmentVariable("FINBERT_SENTIMENT_URL", EnvironmentVariableTarget.User);
        if (Uri.TryCreate(address, UriKind.Absolute, out endpoint))
        {
            httpClient = new HttpClient();
            inner = new FinbertHttpSentimentAnalyzer(httpClient, endpoint, fallback: new VaderSentimentAnalyzer());
        }
        else
        {
            inner = new VaderSentimentAnalyzer();
            problem = NotSetUp;
        }

        IsConfigured = endpoint is not null;
    }

    /// <summary>Wraps a given scorer in place of FinBERT (tests).</summary>
    internal NewsSentimentAnalyzer(ISentimentAnalyzer inner, TimeProvider? clock = null, bool configured = true)
    {
        this.inner = inner;
        this.clock = clock ?? TimeProvider.System;
        IsConfigured = configured;
        problem = configured ? null : NotSetUp;
    }

    /// <summary>True when this host has a FinBERT address, so better scores than VADER can be expected.</summary>
    public bool IsConfigured { get; }

    private const string Paused =
        "FinBERT failed several times in a row and is not asked for one minute. Live articles use VADER, a simpler word-list scorer, meanwhile; they are then scored again.";
    private const string NotSetUp =
        "FinBERT is not set up on this host (FINBERT_SENTIMENT_URL is empty). New sentiment scores use VADER, a simpler word-list scorer.";

    /// <summary>
    /// Why new scores use VADER, in plain words, as of the refresh worker's latest health reading; null while FinBERT is ready
    /// or not read yet. Pages read this without contacting FinBERT.
    /// </summary>
    public string? Problem => problem ?? (IsPaused() ? Paused : null);

    public string AnalyzerName => inner.AnalyzerName;

    /// <summary>True when the named scorer is the VADER fallback rather than a verified FinBERT model.</summary>
    public static bool IsFallback(string? scorer) => String.Equals(scorer, FallbackName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Scores one article, shortened to what the sidecar accepts. After several fallback answers in a row FinBERT is not asked
    /// for a minute, so a stopped or hung sidecar cannot hold up every live article; the result then names VADER.
    /// </summary>
    public Task<SentimentResult> AnalyzeAsync(NewsArticle article, CancellationToken cancellationToken) =>
        ScoreAsync(article, countFailures: true, cancellationToken);

    /// <summary>
    /// Scores one article for refresh enrichment or re-scoring. Honours the pause, but its failures never start one, so an
    /// article FinBERT cannot score in the background never affects live articles.
    /// </summary>
    public Task<SentimentResult> AnalyzeInBackgroundAsync(NewsArticle article, CancellationToken cancellationToken) =>
        ScoreAsync(article, countFailures: false, cancellationToken);

    // Asks FinBERT unless paused; a FinBERT answer always ends a pause, a fallback answer counts only for live articles.
    private async Task<SentimentResult> ScoreAsync(NewsArticle article, bool countFailures, CancellationToken cancellationToken)
    {
        var shortened = Shorten(article);
        if (IsPaused()) return await pausedScorer.AnalyzeAsync(shortened, cancellationToken);
        var result = await inner.AnalyzeAsync(shortened, cancellationToken);
        if (IsConfigured) Record(result.AnalyzerName, countFailures);
        return result;
    }

    // True while FinBERT is being left alone after repeated failures.
    private bool IsPaused()
    {
        lock (pauseGate)
        {
            if (pausedSince is not { } since) return false;
            if (clock.GetElapsedTime(since) < PauseLength) return true;
            pausedSince = null;
            consecutiveFailures = 0;
            return false;
        }
    }

    // Counts live fallback answers in a row; a FinBERT answer resets the count and ends a pause.
    private void Record(string scorer, bool countFailures)
    {
        lock (pauseGate)
        {
            if (!IsFallback(scorer))
            {
                consecutiveFailures = 0;
                pausedSince = null;
            }
            else if (countFailures && ++consecutiveFailures >= FailuresBeforePause)
            {
                pausedSince ??= clock.GetTimestamp();
            }
        }
    }

    /// <summary>Reads FinBERT's own health answer for the notice on the News page; called by the refresh worker.</summary>
    public async Task ReadHealthAsync(CancellationToken cancellationToken)
    {
        if (endpoint is null || httpClient is null) return;
        var health = await new FinbertHealthReader(httpClient).ReadAsync(endpoint, cancellationToken);
        problem = health.IsReady ? null
            : "FinBERT is not ready on this host. New sentiment scores use VADER, a simpler word-list scorer, until FinBERT is ready; they are then scored again.";
    }

    // Keeps the headline, then as much of the summary and then the content as fits.
    internal static NewsArticle Shorten(NewsArticle article)
    {
        var room = MaximumTextLength - SeparatorAllowance;
        var headline = Take(article.Headline, ref room) ?? String.Empty;
        var summary = Take(article.Summary, ref room);
        return article with { Headline = headline, Summary = summary, Content = Take(article.Content, ref room) };
    }

    // Returns the part of the text that still fits and reduces the remaining room.
    private static string? Take(string? text, ref int room)
    {
        if (text is null) return null;
        var kept = text.Length <= room ? text : text[..room];
        room -= kept.Length;
        return kept;
    }

    public void Dispose() => httpClient?.Dispose();
}
