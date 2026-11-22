using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TradingFlow.Contracts.V1;
using TradingFlow.Web;

namespace TradingFlow.Tests;

[CollectionDefinition("Chart transport measurements", DisableParallelization = true)]
public sealed class ChartTransportMeasurementCollection { }

public sealed partial class ChartEndpointTests
{
    // Keep 128 authenticated subscriptions through the initial snapshot and two updates from the normal hub poll.
    [Fact]
    public async Task AuthenticatedSocketFanoutMeasuresDenseBaselineAtConnectionLimit()
    {
        var clock = new EndpointClock();
        var baseline = ChartIndicatorResourceTests.MaximumBaseline(clock.Now);
        var versions = new[] { baseline, Revise(baseline, 2), Revise(baseline, 3) };
        var users = Enumerable.Range(0, 32).Select(index => $"fixture-operator-{index}").ToArray();
        var sockets = users.SelectMany(user => Enumerable.Range(0, 4).Select(_ => CreateSocket(user))).ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var sampling = new CancellationTokenSource();
        using var process = Process.GetCurrentProcess();
        var before = ReadMemory(process);
        var peakTask = SamplePeakMemoryAsync(before, sampling.Token);
        var waves = new List<WaveMeasurement>(versions.Length);
        MemorySample idle;
        MemorySample after;
        MemorySample settled;
        int readerLoads;
        TimeSpan activeDuration;
        try
        {
            await using (var fixture = await Fixture.StartAsync(clock, users, baseline))
            {
                try
                {
                    var address = new UriBuilder(fixture.Address) { Scheme = "ws", Path = "/api/v1/charts/hub" }.Uri;
                    var origin = fixture.Address.GetLeftPart(UriPartial.Authority);
                    await Task.WhenAll(sockets.Select(socket => ConnectSocketAsync(socket, address, origin, deadline.Token)));
                    Assert.All(sockets, socket => Assert.Equal(WebSocketState.Open, socket.State));
                    var invocation = JsonSerializer.Serialize(new
                    {
                        type = 4, invocationId = "fanout", target = "Watch", arguments = new[] { baseline.SeriesKey }
                    }, StockChartHub.Json);

                    var started = Stopwatch.GetTimestamp();
                    var initial = await Task.WhenAll(sockets.Select(socket => SubscribeAsync(socket, invocation, baseline, deadline.Token)));
                    waves.Add(ObserveWave(initial, fixture.Reader, process));
                    for (var index = 1; index < versions.Length; index++)
                    {
                        var version = versions[index];
                        var sequence = index + 1;
                        var publishedAt = Stopwatch.GetTimestamp();
                        fixture.Reader.Publish(version);
                        var update = await Task.WhenAll(sockets.Select((socket, reader) =>
                            MeasureBaselineAsync(socket, version, sequence, initial[reader].Epoch, publishedAt, deadline.Token)));
                        waves.Add(ObserveWave(update, fixture.Reader, process));
                    }
                    Assert.All(sockets, socket => Assert.Equal(WebSocketState.Open, socket.State));
                    await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
                    idle = ReadMemory(process);
                    readerLoads = Volatile.Read(ref fixture.Reader.ReadCount);
                    activeDuration = Stopwatch.GetElapsedTime(started);
                }
                finally
                {
                    foreach (var socket in sockets) socket.Dispose();
                }
            }
            after = ReadMemory(process);
            await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
            settled = ReadMemory(process);
        }
        finally
        {
            await sampling.CancelAsync();
        }
        var peak = await peakTask;
        // Each client reads three versions. Any unchanged reads still have to follow the five-second polling interval.
        var maximumPollsPerSocket = 1 + (int)Math.Ceiling(activeDuration.TotalSeconds / 5);
        Assert.InRange(readerLoads, sockets.Length * versions.Length, sockets.Length * maximumPollsPerSocket);
        measurements.WriteLine("Authenticated WS readers={0} users={1} tabs_per_user=4 waves={2} loads_including_polls={3} active_ms={4:F2}",
            sockets.Length, users.Length, waves.Count, readerLoads, activeDuration.TotalMilliseconds);
        for (var index = 0; index < waves.Count; index++)
        {
            Assert.Equal(sockets.Length, waves[index].Reads.Length);
            ReportWave(index + 1, waves[index]);
        }
        ReportMemory("before", before);
        ReportMemory("open_idle_2s", idle);
        ReportMemory("after_shutdown", after);
        ReportMemory("settled_2s", settled);
        var observed = waves.Select(wave => wave.Memory).Append(before).Append(idle).Append(after).Append(settled).Append(peak).ToArray();
        measurements.WriteLine("Combined synthetic server/client process sampled peak working_set_bytes={0} managed_estimate_bytes={1}",
            observed.Max(sample => sample.WorkingSet), observed.Max(sample => sample.Managed));
    }

    private readonly record struct SocketMeasurement(double LatencyMs, int PayloadBytes, int RecordBytes, int Chunks, string Epoch);
    private readonly record struct MemorySample(long WorkingSet, long Managed, int Gen0, int Gen1, int Gen2);
    private readonly record struct WaveMeasurement(SocketMeasurement[] Reads, int Loads, MemorySample Memory);

    // Build one changed source version before the test, preserving the maximum wire shape.
    private static ChartSeriesSnapshot Revise(ChartSeriesSnapshot baseline, int version)
    {
        var candles = baseline.Bars.ToArray();
        candles[^1] = candles[^1] with { Volume = candles[^1].Volume + version };
        var indicators = baseline.Indicators!.ToArray();
        var rows = indicators[0].Samples.ToArray();
        rows[0] = rows[0].ToArray();
        rows[0][^1] = 127 + version;
        indicators[0] = indicators[0] with { Samples = rows, SourceRevision = $"source-rev-{version}" };
        return baseline with { Bars = candles, Indicators = indicators };
    }

    // Capture memory and total synthetic-source reads as soon as the full wave has reassembled.
    private static WaveMeasurement ObserveWave(SocketMeasurement[] reads, Reader reader, Process process) =>
        new(reads, Volatile.Read(ref reader.ReadCount), ReadMemory(process));

    // Distinguish the initial subscription latency from delays measured after publishing each source update.
    private void ReportWave(int sequence, WaveMeasurement wave)
    {
        var latencies = wave.Reads.Select(read => read.LatencyMs).Order().ToArray();
        measurements.WriteLine("Wave {0} {1}-to-last-chunk ms p50={2:F2} p95={3:F2} max={4:F2}; loads_through_wave={5}",
            sequence, sequence == 1 ? "watch" : "fixture-publication",
            Percentile(latencies, 0.50), Percentile(latencies, 0.95), latencies[^1], wave.Loads);
        measurements.WriteLine("Wave {0} baseline JSON min={1} max={2}; SignalR frame bytes total={3} max_per_reader={4}; chunks min={5} max={6}",
            sequence, wave.Reads.Min(read => read.PayloadBytes), wave.Reads.Max(read => read.PayloadBytes),
            wave.Reads.Sum(read => (long)read.RecordBytes), wave.Reads.Max(read => read.RecordBytes),
            wave.Reads.Min(read => read.Chunks), wave.Reads.Max(read => read.Chunks));
        ReportMemory($"after_wave_{sequence}", wave.Memory);
    }

    // Record read-only memory estimates and the cumulative collection counters observed at each phase.
    private void ReportMemory(string phase, MemorySample memory) =>
        measurements.WriteLine("Combined process {0} working_set_bytes={1} managed_estimate_bytes={2} GC_counts(gen0/gen1/gen2)={3}/{4}/{5}",
            phase, memory.WorkingSet, memory.Managed, memory.Gen0, memory.Gen1, memory.Gen2);

    // Represent an operator identity using the existing fixture's real authentication-handler request.
    private static ClientWebSocket CreateSocket(string user)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Test-User", user);
        return socket;
    }

    // Complete the protocol handshake before every client participates in the concurrent subscription wave.
    private static async Task ConnectSocketAsync(ClientWebSocket socket, Uri address, string origin, CancellationToken token)
    {
        socket.Options.SetRequestHeader("Origin", origin);
        await socket.ConnectAsync(address, token);
        await SendAsync(socket, "{\"protocol\":\"json\",\"version\":1}", token);
        Assert.Contains("{}", await ReceiveAsync(socket, token));
    }

    // Send a single Watch per connection. Later snapshots come from that same subscription's periodic reads.
    private static async Task<SocketMeasurement> SubscribeAsync(ClientWebSocket socket, string invocation,
        ChartSeriesSnapshot baseline, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        await SendAsync(socket, invocation, token);
        return await MeasureBaselineAsync(socket, baseline, 1, null, started, token);
    }

    // Check each updated candle and indicator after reassembly from one epoch and exact next sequence.
    private static async Task<SocketMeasurement> MeasureBaselineAsync(ClientWebSocket socket, ChartSeriesSnapshot baseline,
        int sequence, string? expectedEpoch, long started, CancellationToken token)
    {
        var (text, bytes, chunks, epoch) = await ReceiveBaselineAsync(socket, sequence, expectedEpoch, token);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var result = JsonSerializer.Deserialize<ChartSeriesSnapshot>(text, StockChartHub.Json)!;
        var size = Encoding.UTF8.GetByteCount(text);
        Assert.InRange(size, 200_000, StockChartHub.MaximumSnapshotBytes);
        Assert.True(bytes > size);
        Assert.True(chunks > 1);
        Assert.Equal((long)sequence, result.Sequence);
        Assert.Equal(epoch, result.Epoch);
        Assert.Equal(baseline.SeriesKey, result.SeriesKey);
        Assert.Equal(500, result.Bars.Count);
        Assert.Equal(baseline.Bars[^1], result.Bars[^1]);
        Assert.Equal(5, result.Indicators!.Count);
        Assert.Equal(baseline.Indicators![0].Samples[0][499], result.Indicators[0].Samples[0][499]);
        Assert.Equal(baseline.Indicators[0].SourceRevision, result.Indicators[0].SourceRevision);
        Assert.NotNull(result.HistoryCursor);
        return new SocketMeasurement(elapsed, size, bytes, chunks, epoch);
    }

    // WebSocket messages can pack or fragment SignalR records, so bound and inspect the record stream itself.
    private static async Task<(string Text, int WireBytes, int Chunks, string Epoch)> ReceiveBaselineAsync(
        ClientWebSocket socket, int sequence, string? expectedEpoch, CancellationToken token)
    {
        const byte separator = 0x1e;
        var buffer = new byte[16 * 1024];
        using var record = new MemoryStream();
        var payload = new StringBuilder();
        var wireBytes = 0;
        var part = 0;
        var parts = 0;
        string? epoch = null;
        while (true)
        {
            var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            Assert.Equal(WebSocketMessageType.Text, received.MessageType);
            var cursor = 0;
            while (cursor < received.Count)
            {
                var end = Array.IndexOf(buffer, separator, cursor, received.Count - cursor);
                record.Write(buffer, cursor, (end < 0 ? received.Count : end) - cursor);
                Assert.InRange(record.Length, 0, 64 * 1024);
                if (end < 0) break;

                using (var message = JsonDocument.Parse(record.GetBuffer().AsMemory(0, (int)record.Length)))
                {
                    var root = message.RootElement;
                    var type = root.GetProperty("type").GetInt32();
                    if (type == 3) throw new InvalidOperationException($"Chart stream completed before its baseline: {root}.");
                    if (type == 2)
                    {
                        Assert.Equal("fanout", root.GetProperty("invocationId").GetString());
                        var frame = root.GetProperty("item");
                        var kind = frame.GetProperty("kind").GetString();
                        var frameEpoch = frame.GetProperty("epoch").GetString();
                        var frameSequence = frame.GetProperty("sequence").GetInt64();
                        if (expectedEpoch is not null) Assert.Equal(expectedEpoch, frameEpoch);
                        if (kind == "heartbeat")
                        {
                            Assert.Equal(0, part);
                            Assert.Equal((long)sequence - 1, frameSequence);
                        }
                        else
                        {
                            Assert.Equal("snapshot", kind);
                            Assert.Equal((long)sequence, frameSequence);
                            Assert.Equal(part, frame.GetProperty("part").GetInt32());
                            if (part == 0)
                            {
                                parts = frame.GetProperty("parts").GetInt32();
                                epoch = frameEpoch;
                                Assert.False(String.IsNullOrWhiteSpace(epoch));
                            }
                            Assert.Equal(parts, frame.GetProperty("parts").GetInt32());
                            Assert.Equal(epoch, frameEpoch);
                            var chunk = frame.GetProperty("payload").GetString()!;
                            Assert.InRange(chunk.Length, 1, 8000);
                            wireBytes += (int)record.Length + 1;
                            payload.Append(chunk);
                            Assert.InRange(payload.Length, 1, StockChartHub.MaximumSnapshotBytes);
                            if (++part == parts) return (payload.ToString(), wireBytes, parts, epoch!);
                        }
                    }
                }
                record.SetLength(0);
                cursor = end + 1;
            }
        }
    }

    // Report wall-clock percentiles descriptively without imposing a machine-specific latency target.
    private static double Percentile(double[] sorted, double fraction) => sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];

    // Read process and managed memory without forcing garbage collection or altering runtime behavior.
    private static MemorySample ReadMemory(Process process)
    {
        process.Refresh();
        return new MemorySample(process.WorkingSet64, GC.GetTotalMemory(forceFullCollection: false),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }

    // Sample both metrics through the three socket waves and shutdown without claiming an absolute peak.
    private static async Task<MemorySample> SamplePeakMemoryAsync(MemorySample peak, CancellationToken token)
    {
        using var process = Process.GetCurrentProcess();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var current = ReadMemory(process);
                peak = peak with { WorkingSet = Math.Max(peak.WorkingSet, current.WorkingSet), Managed = Math.Max(peak.Managed, current.Managed) };
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        return peak;
    }
}
