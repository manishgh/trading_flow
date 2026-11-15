using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using TradingFlow.Contracts.V1;
using TradingFlow.Web.Services.Charts;

namespace TradingFlow.Web;

public sealed record ChartFrame(string Kind, ChartSeriesKey Key, string Epoch, long Sequence, int Part, int Parts, string Payload);

/// <summary>Read-only, backpressured chart snapshots over the existing server-owned market source.</summary>
[Authorize]
public sealed class StockChartHub(IChartSeriesReader series, ChartAccessPolicy access, ChartTransportLimits limits,
    ChartHistoryCursor cursors, TimeProvider clock, ILogger<StockChartHub> logger) : Hub
{
    public const int MaximumSnapshotBytes = 256 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Active = "chart.active";

    // Admission happens before any source data read and is released on disconnect.
    public override async Task OnConnectedAsync()
    {
        if (!limits.Connect(Context.ConnectionId, ChartAccessPolicy.UserId(Context.User!)))
        { Context.Abort(); throw new HubException("Close another chart tab before opening this one."); }
        await base.OnConnectedAsync();
    }

    // Closing the tab cancels its source polling and releases its connection budget.
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        lock (Context.Items)
            if (Context.Items.TryGetValue(Active, out var active) && active is CancellationTokenSource source) source.Cancel();
        limits.Disconnect(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    // Every changed view is a complete atomic baseline, so no missing delta can leave an incorrect candle or zone.
    public async IAsyncEnumerable<ChartFrame> Watch(ChartSeriesKey requested,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var user = ChartAccessPolicy.UserId(Context.User!);
        if (!limits.Request(user)) throw new HubException("Too many chart changes. Try again shortly.");
        ChartSeriesKey key;
        try { key = ChartSeriesService.Normalize(requested); }
        catch (ArgumentException ex) { throw new HubException(ex.Message); }
        // Claim the selection before any asynchronous authorization read; an older lookup must never replace a newer view.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Context.ConnectionAborted);
        lock (Context.Items)
        {
            if (Context.Items.TryGetValue(Active, out var prior) && prior is CancellationTokenSource previous) previous.Cancel();
            Context.Items[Active] = linked;
        }
        try
        {
            await RequireAccessAsync(key, linked.Token);
            var epoch = Guid.NewGuid().ToString("N");
            long sequence = 0;
            string? fingerprint = null;
            var expires = clock.GetUtcNow().AddMinutes(5);
            while (!linked.IsCancellationRequested && clock.GetUtcNow() < expires)
            {
                await RequireAccessAsync(key, linked.Token);
                var page = await ReadPageAsync(key, linked.Token);
                await RequireAccessAsync(key, linked.Token);
                var content = JsonSerializer.Serialize(page.Snapshot with { AsOfUtc = default, Epoch = null, Sequence = 0 }, Json);
                var current = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
                if (fingerprint != current)
                {
                    var snapshot = page.Snapshot with
                    {
                        Epoch = epoch, Sequence = ++sequence, HistoryCursor = cursors.Create(user, key, page.NextBeforeUtc)
                    };
                    var payload = JsonSerializer.Serialize(snapshot, Json);
                    if (System.Text.Encoding.UTF8.GetByteCount(payload) > MaximumSnapshotBytes)
                        throw new HubException("Chart exceeds the bounded snapshot size.");
                    var chunks = SplitSnapshot(payload);
                    for (var part = 0; part < chunks.Count; part++)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        yield return new ChartFrame("snapshot", key, epoch, sequence, part, chunks.Count, chunks[part]);
                    }
                    fingerprint = current;
                }
                else yield return new ChartFrame("heartbeat", key, epoch, sequence, 0, 1, clock.GetUtcNow().ToString("O"));
                await Task.Delay(TimeSpan.FromSeconds(5), linked.Token);
            }
        }
        finally
        {
            lock (Context.Items)
                if (Context.Items.TryGetValue(Active, out var active) && ReferenceEquals(active, linked)) Context.Items.Remove(Active);
        }
    }

    // Recheck transient membership around each potentially slow read, rather than extending its lifetime by a new watch.
    private async Task RequireAccessAsync(ChartSeriesKey key, CancellationToken token)
    {
        if (!await access.CanReadAsync(Context.User!, key.Symbol, token))
            throw new HubException("This stock is not in the recorded workspace. Open it from a stock list.");
        token.ThrowIfCancellationRequested();
    }

    // Never split a UTF-16 surrogate pair: each frame is separately JSON encoded by SignalR.
    internal static IReadOnlyList<string> SplitSnapshot(string payload)
    {
        var chunks = new List<string>();
        for (var offset = 0; offset < payload.Length;)
        {
            var length = Math.Min(8000, payload.Length - offset);
            if (offset + length < payload.Length && Char.IsHighSurrogate(payload[offset + length - 1])) length--;
            chunks.Add(payload.Substring(offset, length));
            offset += length;
        }
        return chunks;
    }

    // Keep provider exceptions out of the browser while preserving the server-side diagnostic.
    private async Task<ChartSeriesPage> ReadPageAsync(ChartSeriesKey key, CancellationToken token)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            return await series.ReadAsync(key, null, deadline.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Chart read failed for {Symbol} {Interval}", key.Symbol, key.Interval);
            throw new HubException("Chart data is temporarily unavailable. Retry this stock.");
        }
    }
}
