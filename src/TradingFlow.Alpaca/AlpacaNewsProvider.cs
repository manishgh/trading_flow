using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Alpaca;

public sealed class AlpacaNewsProvider : ICatalystProvider
{
    private readonly HttpClient _httpClient;
    private readonly AlpacaOptions _options;

    public AlpacaNewsProvider(HttpClient httpClient, AlpacaOptions options)
    {
        _httpClient = httpClient;
        _options = options;
        _httpClient.BaseAddress = new Uri("https://data.alpaca.markets");
        _httpClient.DefaultRequestHeaders.Add("APCA-API-KEY-ID", _options.KeyId);
        _httpClient.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", _options.SecretKey);
    }

    public string ProviderName => "alpaca";

    public async Task<IReadOnlyList<CatalystEvent>> GetCatalystsAsync(string ticker, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        var events = new List<CatalystEvent>();
        var startStr = windowStart.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var endStr = windowEnd.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var url = $"/v1beta1/news?symbols={ticker.ToUpperInvariant()}&start={startStr}&end={endStr}&limit=50";

        var response = await TradingFlow.Domain.Logging.ApiProfiler.ProfileAsync("Alpaca", url, "GET", () => _httpClient.GetAsync(url, cancellationToken));
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.WriteLine($"[Alpaca News Error] {response.StatusCode}: {error}");
            return events; // Silently fail or log in real scenario
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(content);
        var newsArray = doc.RootElement.GetProperty("news");

        var analyzer = new VaderSharp2.SentimentIntensityAnalyzer();

        foreach (var item in newsArray.EnumerateArray())
        {
            var headline = item.GetProperty("headline").GetString() ?? "Unknown";
            var createdAt = item.GetProperty("created_at").GetDateTimeOffset();

            var sentimentScore = (decimal)analyzer.PolarityScores(headline).Compound;

            events.Add(new CatalystEvent(
                ticker,
                createdAt,
                CatalystType.NewsReport,
                headline,
                sentimentScore
            ));
        }

        return events;
    }
}
