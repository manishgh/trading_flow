using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using TradingFlow.Contracts.V1;
using TradingFlow.Web;
using TradingFlow.Web.Services.Charts;

namespace TradingFlow.Tests;

public sealed class ChartIndicatorResourceTests
{
    // Fan out to the configured 128-reader limit. Exact profiles share work, the 33rd queued profile gets a retry,
    // and leaving one shared computation does not cancel its remaining readers.
    [Fact]
    public async Task FanoutCoalescesAndCapsActiveCalculationsAndQueue()
    {
        var cache = new ChartIndicatorCache();
        using var allow = new ManualResetEventSlim();
        using var cancelled = new CancellationTokenSource();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var counts = new ConcurrentDictionary<string, int>();
        var history = new ChartHistoryKey("AAPL", false, new DateOnly(2026, 11, 13));
        var first = Read(0, cancelled.Token);
        var readers = Enumerable.Range(1, 127).Select(index => Read(index % 35, limit.Token)).ToArray();
        Assert.Equal(2, cache.Measure().Running);
        Assert.Equal(32, cache.Measure().Queued);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);

        allow.Set();
        var output = await Task.WhenAll(readers).WaitAsync(limit.Token);
        Assert.Equal(3, output.Count(item => item is null));
        Assert.Equal(34, counts.Count);
        Assert.All(counts, pair => Assert.Equal(1, pair.Value));
        Assert.True(cache.Measure().Profiles <= 4);
        Assert.True(cache.Measure().Points <= 100_000);
        Assert.True(cache.Measure().Bytes <= 32 * 1024 * 1024);

        // Block each unique job once and release normal work independently of any one browser cancellation.
        Task<IReadOnlyList<ChartIndicatorSeries>?> Read(int profile, CancellationToken token)
        {
            var key = new ChartCalculationKey(history, $"profile-{profile}", "source-rev");
            return cache.ReadAsync(key, workToken =>
            {
                counts.AddOrUpdate(key.Profile, 1, (_, count) => count + 1);
                allow.Wait(workToken);
                return [Series("sma20", 500, 1)];
            }, _ => true, token);
        }
    }

    // Revisions replace an older profile; a burst of large valid-shaped profiles evicts points process-wide.
    [Fact]
    public async Task ProfileAndProcessLimitsEvictDeterministically()
    {
        var cache = new ChartIndicatorCache();
        var first = new ChartHistoryKey("AAPL", true, new DateOnly(2026, 11, 13));
        var loads = new List<string>();
        for (var profile = 0; profile < 5; profile++)
            await Read(first, $"p{profile}", "r1");
        Assert.Equal(4, cache.Measure().Profiles);
        await Read(first, "p4", "r1");
        Assert.Equal(5, loads.Count);
        await Read(first, "p4", "r2");
        Assert.Equal(6, loads.Count);
        Assert.Equal(4, cache.Measure().Profiles);
        await Read(first, "p0", "r1");
        Assert.Equal(7, loads.Count);

        for (var entry = 1; entry < 32; entry++)
        {
            var history = first with { Anchor = first.Anchor.AddDays(-entry) };
            for (var profile = 0; profile < 4; profile++)
                await Read(history, $"p{profile}", "r1");
        }
        var retained = cache.Measure();
        Assert.InRange(retained.Points, 90_000, 100_000);
        Assert.InRange(retained.Bytes, 1, 32 * 1024 * 1024);
        Assert.True(retained.Profiles < 128);

        // Use the allowable maximum line/column shape and independently allocate each cached result.
        Task<IReadOnlyList<ChartIndicatorSeries>?> Read(ChartHistoryKey history, string profile, string revision) =>
            cache.ReadAsync(new ChartCalculationKey(history, profile, revision), _ =>
            {
                loads.Add($"{profile}:{revision}");
                return [Series("bb20", 500, 3), Series("sma50", 500, 1), Series("ema9", 500, 1), Series("ema50", 500, 1),
                    Series("macd", 500, 3), Series("adx14", 500, 3), Series("atr14:readout", 500, 1),
                    Series("atrpct14:readout", 500, 1), Series("rvol", 500, 1)];
            }, _ => true, default);
    }

    // A large, densely populated allowed selection shares candle indices and remains transportable in safe chunks.
    [Fact]
    public void MaximumSelectedFiveHundredBarBaselineFitsWireAndReassembles()
    {
        var snapshot = MaximumBaseline(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var json = JsonSerializer.Serialize(snapshot, StockChartHub.Json);
        Assert.InRange(Encoding.UTF8.GetByteCount(json), 200_000, StockChartHub.MaximumSnapshotBytes);
        var frames = StockChartHub.SplitSnapshot(json);
        Assert.All(frames, frame => Assert.InRange(frame.Length, 1, 8000));
        var reconstructed = JsonSerializer.Deserialize<ChartSeriesSnapshot>(String.Concat(frames), StockChartHub.Json)!;
        Assert.Equal(500, reconstructed.Bars.Count);
        Assert.Equal(5, reconstructed.Indicators!.Count);
        Assert.Equal(snapshot.Markers[0], reconstructed.Markers[0]);
    }

    // Share the same dense maximum-profile fixture between the serializer and live loopback transport tests.
    internal static ChartSeriesSnapshot MaximumBaseline(DateTimeOffset now)
    {
        var key = ChartSeriesService.Normalize(new("AAPL", "5m", "regular", "year",
            "bb20,bb21,bb22,macd,adx14"));
        var bars = Enumerable.Range(0, 500).Select(index =>
        {
            var start = now.AddMinutes(5 * (index - 500));
            return new ChartCandle(start, start.AddMinutes(5), DateOnly.FromDateTime(start.UtcDateTime), "regular",
                301.234567m, 310.876543m, 287.123456m, 308.345678m, 167832.987654m, "derived_1m",
                null, now, now);
        }).ToArray();
        var selected = ChartIndicatorProfile.Read(key).Select(study => Series(study.Id, 500, study.Columns.Length)).ToArray();
        return new ChartSeriesSnapshot(key, now, "history_only", null,
            new ChartSeriesEvidence("alpaca", "sip", "all", "complete", now, now, null, null, true, [], []),
            bars, [], [], [new ChartMarker("event", "news", now, now, "Fixture · 📰", null, "stored")],
            Indicators: selected);
    }

    // Create dense typed values with observable receipt runs and realistic short display numbers.
    private static ChartIndicatorSeries Series(string id, int bars, int columns)
    {
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        return new ChartIndicatorSeries(id, "5m", "regular", "all", "fixture", "14", now, now.AddDays(-5), "source-rev",
            true, now, "ready", null, Enumerable.Range(0, columns).Select(column => $"c{column}").ToArray(),
            Enumerable.Range(0, columns).Select(column => Enumerable.Range(0, bars)
                .Select(index => (double?)Math.Round(127.123456 + column * 0.01 + index / 72d, 6)).ToArray()).ToArray(),
            [0], [[0, bars, now.ToUnixTimeMilliseconds()]], SegmentAnchorsUtc: [now.AddDays(-5)]);
    }
}
