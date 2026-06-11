using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Engine.Pipeline;
using Xunit.Abstractions;

namespace TradingFlow.Tests;

public class AlpacaCandlePipelineIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RunAsync_AlpacaMarketData_ProcessesCandlesThroughTplDataflowBlocks()
    {
        var options = TryLoadAlpacaOptions();
        if (options is null)
        {
            return;
        }

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        var provider = new AlpacaMarketDataProvider(httpClient, options);
        var engine = new CandlePipelineEngine();
        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-7);

        var result = await engine.RunAsync(
            new CandlePipelineRequest(
                ["AAPL", "AMD"],
                ["5m"],
                ["5m", "15m", "1h"],
                "5m",
                start,
                end,
                BoundedCapacity: 25,
                WorkerCount: 4),
            provider,
            CancellationToken.None);

        Assert.Empty(result.Failures);
        Assert.Equal(2, result.TickerStates.Count);
        Assert.True(result.Metrics.ReadCount > 100);
        Assert.Equal(result.Metrics.ReadCount, result.Metrics.NormalizedCount);
        Assert.Equal(result.Metrics.ReadCount, result.Metrics.GroupedCount);
        Assert.Equal(4, result.Metrics.DerivedTimeframeCount);
        Assert.Equal(6, result.Metrics.IndicatorWorkItemCount);
        Assert.Equal(6, result.Metrics.IndicatorSnapshotSetCount);
        output.WriteLine(
            "Alpaca Dataflow metrics: read={0}, normalized={1}, grouped={2}, derived={3}, indicatorWork={4}, indicatorSets={5}",
            result.Metrics.ReadCount,
            result.Metrics.NormalizedCount,
            result.Metrics.GroupedCount,
            result.Metrics.DerivedTimeframeCount,
            result.Metrics.IndicatorWorkItemCount,
            result.Metrics.IndicatorSnapshotSetCount);

        foreach (var state in result.TickerStates.Values)
        {
            Assert.True(state.BarsByTimeframe["5m"].Count > 20);
            Assert.NotEmpty(state.BarsByTimeframe["15m"]);
            Assert.NotEmpty(state.BarsByTimeframe["1h"]);
            Assert.NotEmpty(state.SnapshotsByTimeframe["5m"]);
            Assert.NotEmpty(state.SnapshotsByTimeframe["15m"]);
            Assert.NotEmpty(state.SnapshotsByTimeframe["1h"]);
        }
    }

    private static AlpacaOptions? TryLoadAlpacaOptions()
    {
        var keyId = Environment.GetEnvironmentVariable("ALPACA_KEY_ID");
        var secretKey = Environment.GetEnvironmentVariable("ALPACA_SECRET_KEY");
        if (String.IsNullOrWhiteSpace(keyId) || String.IsNullOrWhiteSpace(secretKey))
        {
            var settingsPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "TradingFlow.Web",
                "appsettings.local.json"));
            if (!File.Exists(settingsPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (!document.RootElement.TryGetProperty("Alpaca", out var alpaca))
            {
                return null;
            }

            keyId = alpaca.TryGetProperty("KeyId", out var keyElement) ? keyElement.GetString() : null;
            secretKey = alpaca.TryGetProperty("SecretKey", out var secretElement) ? secretElement.GetString() : null;
        }

        if (String.IsNullOrWhiteSpace(keyId) || String.IsNullOrWhiteSpace(secretKey))
        {
            return null;
        }

        return AlpacaOptions.CreateDefault() with
        {
            KeyId = keyId,
            SecretKey = secretKey,
            MarketDataFeed = "sip"
        };
    }
}
