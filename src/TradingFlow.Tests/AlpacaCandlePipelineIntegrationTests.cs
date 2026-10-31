using System.Net;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Engine.Pipeline;
using Xunit.Abstractions;

namespace TradingFlow.Tests;

public class AlpacaCandlePipelineIntegrationTests(ITestOutputHelper output)
{
    /// <summary>Exercises archived fake HTTP bars and calendars through the real bounded pipeline without operator credentials.</summary>
    [Fact]
    public async Task RunAsync_AlpacaMarketData_ProcessesCandlesThroughTplDataflowBlocks()
    {
        using var archive = new OwnedAlpacaTestArchive();
        using var httpClient = new HttpClient(new FixtureMarketHandler());
        var options = AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
        {
            KeyId = "fixture",
            SecretKey = "fixture",
            MarketDataFeed = "sip"
        };
        var provider = new AlpacaMarketDataProvider(httpClient, options, archive.Writer);
        var engine = new CandlePipelineEngine();
        var end = new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero);
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
        Assert.Equal(
            result.Metrics.ReadCount,
            result.Metrics.NormalizedCount + result.Metrics.IncompleteBarFilteredCount);
        Assert.Equal(result.Metrics.NormalizedCount, result.Metrics.GroupedCount);
        Assert.Equal(4, result.Metrics.DerivedTimeframeCount);
        Assert.Equal(6, result.Metrics.IndicatorWorkItemCount);
        Assert.Equal(6, result.Metrics.IndicatorSnapshotSetCount);
        output.WriteLine(
            "Alpaca Dataflow metrics: read={0}, normalized={1}, incomplete={2}, grouped={3}, derived={4}, indicatorWork={5}, indicatorSets={6}",
            result.Metrics.ReadCount,
            result.Metrics.NormalizedCount,
            result.Metrics.IncompleteBarFilteredCount,
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

    private sealed class FixtureMarketHandler : HttpMessageHandler
    {
        /// <summary>Returns fixed five-minute sessions and their official-shaped calendars with no network transport.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var sessions = Enumerable.Range(0, 5).Select(day => new DateTimeOffset(2026, 6, 1 + day, 13, 30, 0, TimeSpan.Zero)).ToArray();
            string payload;
            if (request.RequestUri!.AbsolutePath == "/v2/calendar")
            {
                payload = JsonSerializer.Serialize(sessions.Select(session => new
                {
                    date = session.ToString("yyyy-MM-dd"), open = "09:30", close = "16:00"
                }));
            }
            else
            {
                var bars = new[] { "AAPL", "AMD" }.ToDictionary(ticker => ticker, ticker =>
                    sessions.SelectMany(session => Enumerable.Range(0, 78).Select(index => new
                    {
                        t = session.AddMinutes(index * 5), o = 100m + index, h = 102m + index,
                        l = 99m + index, c = 101m + index, v = 1000m
                    })).ToArray());
                payload = JsonSerializer.Serialize(new { bars, next_page_token = (string?)null });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload), RequestMessage = request });
        }
    }
}
