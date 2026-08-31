using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingFlow.Alpaca;
using TradingFlow.Domain.Earnings;
using TradingFlow.Earnings;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Web.Services;

public class AlpacaNewsStreamService : BackgroundService
{
    private readonly AlpacaOptions _options;
    private readonly INewsFeedRepository _repository;
    private readonly IEarningsRepository _earnings;
    private readonly ISentimentAnalyzer _sentimentAnalyzer;
    private readonly ILogger<AlpacaNewsStreamService> _logger;
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(10);

    public AlpacaNewsStreamService(
        TradingFlow.Web.Services.AlpacaCredentialProvider credentials,
        INewsFeedRepository repository,
        IEarningsRepository earnings,
        ILogger<AlpacaNewsStreamService> logger)
    {
        _options = TradingFlow.Alpaca.AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
        {
            KeyId = credentials.KeyId,
            SecretKey = credentials.SecretKey
        };
        _repository = repository;
        _earnings = earnings;
        _sentimentAnalyzer = new TradingFlow.Alpaca.VaderSentimentAnalyzer();
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var client = new AlpacaNewsStreamClient(_options);
                await client.ConnectAsync(stoppingToken);
                await client.SubscribeAllNewsAsync(stoppingToken);

                _logger.LogInformation("Listening for real-time news from Alpaca...");
                
                await foreach (var message in client.ReadMessagesAsync(stoppingToken))
                {
                    await ProcessMessageAsync(message, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Alpaca news stream disconnected. Reconnecting in {Delay}...", _reconnectDelay);
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_reconnectDelay, stoppingToken);
            }
        }
    }

    private async Task ProcessMessageAsync(JsonElement element, CancellationToken cancellationToken)
    {
        var receivedAt = DateTimeOffset.UtcNow;
        // Alpaca stream messages have format {"T":"n", "headline":"...", "symbols":["AAPL"], ...}
        if (!element.TryGetProperty("T", out var typeElement) || typeElement.GetString() != "n")
        {
            return;
        }

        var headline = GetString(element, "headline");
        if (string.IsNullOrWhiteSpace(headline)) return;

        var createdAt = GetDate(element, "created_at") ?? DateTimeOffset.UtcNow;
        var id = GetString(element, "id") ?? GetString(element, "news_id") ?? Guid.NewGuid().ToString();
        var symbols = GetSymbols(element);

        var article = new TradingFlow.Domain.Market.NewsArticle(
            "alpaca",
            id,
            headline,
            createdAt,
            symbols,
            GetString(element, "summary"),
            GetString(element, "content"),
            GetString(element, "source"),
            GetString(element, "url"),
            GetDate(element, "updated_at")
        );

        // Analyze sentiment once for the article
        var sentiment = await _sentimentAnalyzer.AnalyzeAsync(article, cancellationToken);
        var decisionAvailableAt = DateTimeOffset.UtcNow;
        
        var events = symbols.Select(ticker => new CatalystEvent(
            ticker.ToUpperInvariant(),
            article.CreatedAt,
            CatalystType.NewsReport,
            article.Headline,
            sentiment.Score,
            article.Provider,
            article.Id,
            article.Summary,
            article.Source,
            article.Url,
            receivedAt,
            article.UpdatedAt,
            CatalystAvailabilityEvidence.ObservedReceiptTime,
            decisionAvailableAt,
            CatalystAvailabilityEvidence.NewsAndAssessmentObservedTime,
            sentiment.AnalyzerName
        )).ToArray();

        if (events.Length > 0)
        {
            await _repository.UpsertAsync(events, cancellationToken);
            _logger.LogInformation("Saved {Count} real-time Alpaca news events. Example: {Headline}", events.Length, headline);
            
            // Try to extract earnings if requested
            await TryExtractEarningsAsync(events, cancellationToken);
        }
    }

    /// <summary>
    /// Writes results carried by a structured headline straight onto the calendar event. The
    /// provider calendar does not publish after-close actuals on the evening of the release, so
    /// without this an after-close reporter shows no EPS until the provider backfills it.
    /// </summary>
    private async Task TryExtractEarningsAsync(CatalystEvent[] events, CancellationToken cancellationToken)
    {
        // E.g. "Airbnb Q2 EPS $1.37 Beats $1.25 Estimate, Sales $3.608B Beat $3.576B Estimate".
        // The shared extractor is deliberate: it only accepts explicit actual-vs-estimate
        // headlines, so guidance and preview articles cannot masquerade as a reported result.
        foreach (var ev in events)
        {
            if (!EarningsNewsResultExtractor.TryExtract(ev.Headline, out var result) || !result.HasResult)
            {
                continue;
            }

            var applied = await _earnings.TryApplyNewsResultAsync(
                ev.Ticker,
                ev.Timestamp,
                result,
                cancellationToken);
            if (applied)
            {
                _logger.LogInformation(
                    "Applied headline earnings result for {Ticker}: EPS={Eps} vs {EpsEstimate}, Revenue={Revenue}M. Headline={Headline}",
                    ev.Ticker,
                    result.EpsActual,
                    result.EpsEstimate,
                    result.RevenueActualMillions,
                    ev.Headline);
            }
            else
            {
                _logger.LogDebug(
                    "Parsed a headline result for {Ticker} but no calendar event accepted it. Headline={Headline}",
                    ev.Ticker,
                    ev.Headline);
            }
        }
    }

    private static string? GetString(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static DateTimeOffset? GetDate(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static IReadOnlyList<string> GetSymbols(JsonElement item)
    {
        if (!item.TryGetProperty("symbols", out var symbolsElement) || symbolsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }
        var symbols = new List<string>();
        foreach (var symbol in symbolsElement.EnumerateArray())
        {
            if (symbol.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(symbol.GetString()))
            {
                symbols.Add(symbol.GetString()!.ToUpperInvariant());
            }
        }
        return symbols;
    }
}
