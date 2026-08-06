using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingFlow.Alpaca;
using TradingFlow.Domain.Earnings;
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
        var receivedAt = DateTimeOffset.UtcNow;
        
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
            CatalystAvailabilityEvidence.ProviderTimestampOnly
        )).ToArray();

        if (events.Length > 0)
        {
            await _repository.UpsertAsync(events, cancellationToken);
            _logger.LogInformation("Saved {Count} real-time Alpaca news events. Example: {Headline}", events.Length, headline);
            
            // Try to extract earnings if requested
            await TryExtractEarningsAsync(events, cancellationToken);
        }
    }

    private async Task TryExtractEarningsAsync(CatalystEvent[] events, CancellationToken cancellationToken)
    {
        // Very simple regex parser for earnings
        // E.g. "Airbnb Q2 EPS $1.37 Beats $1.25 Estimate, Sales $3.608B Beat $3.576B Estimate"
        var epsRegex = new Regex(@"EPS\s+\$?([0-9.]+)", RegexOptions.IgnoreCase);
        var salesRegex = new Regex(@"(?:Sales|Revenue)\s+\$?([0-9.]+)[MB]", RegexOptions.IgnoreCase);

        foreach (var ev in events)
        {
            var epsMatch = epsRegex.Match(ev.Headline);
            var salesMatch = salesRegex.Match(ev.Headline);

            if (epsMatch.Success || salesMatch.Success)
            {
                // We'd ideally need the specific EarningsEventId here, but we can look up by Ticker
                // This is a fast path, but we'd need to coordinate with EarningsMonitor.
                // For safety, we will just log this capability for now as requested.
                _logger.LogInformation("Potential earnings extracted from {Ticker}: EPS={Eps}, Sales={Sales}", 
                    ev.Ticker, 
                    epsMatch.Success ? epsMatch.Groups[1].Value : "N/A",
                    salesMatch.Success ? salesMatch.Groups[1].Value : "N/A");
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
