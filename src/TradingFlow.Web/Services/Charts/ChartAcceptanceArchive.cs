using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Web.Services.Charts;

/// <summary>Owns the bounded evidence queue and the only asynchronous archive writer for a session.</summary>
internal sealed class ChartAcceptanceArchive(IRawArchiveWriter archive, Guid sessionId, Action<string> fail)
{
    internal const int ChunkBytes = 1024 * 1024;
    internal const int MaximumChunks = 10;
    internal const int MaximumEvents = 10_000;
    internal const int MaximumQueuedEvents = 1_000;
    internal const int MaximumMetadataBytes = 64 * 1024;
    private const int QueueBookkeepingBytes = 64 * 1024;

    private readonly object gate = new();
    private readonly Channel<Entry> queue = Channel.CreateBounded<Entry>(
        new BoundedChannelOptions(MaximumQueuedEvents) { SingleReader = true, AllowSynchronousContinuations = false });
    private int queuedBytes;
    private int queuedEvents;
    private int totalBytes;
    private int totalEvents;
    private bool sealedQueue;
    private readonly List<ChartAcceptanceChunk> committedChunks = new(MaximumChunks);

    internal IReadOnlyList<ChartAcceptanceChunk> CommittedChunks => committedChunks;

    // Reserve all envelope and payload bytes before allocating or copying the ephemeral input.
    public bool Offer(string header, ReadOnlySpan<byte> original, string? chartPayload, DateTimeOffset received)
    {
        var headerLength = Encoding.UTF8.GetByteCount(header);
        var payloadLength = chartPayload is null ? original.Length : Encoding.UTF8.GetByteCount(chartPayload);
        var size = sizeof(int) * 2L + headerLength + payloadLength;
        lock (gate)
        {
            if (sealedQueue) return false;
            if (size > ChunkBytes || queuedBytes + size > ChunkBytes - QueueBookkeepingBytes ||
                queuedEvents == MaximumQueuedEvents ||
                totalBytes + size > MaximumChunks * ChunkBytes || totalEvents == MaximumEvents)
            {
                fail("The chart check exceeded its event or byte limit.");
                return false;
            }

            var length = (int)size;
            queuedBytes += length;
            totalBytes += length;
            queuedEvents++;
            totalEvents++;
            var bytes = new byte[length];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, headerLength);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(sizeof(int)), payloadLength);
            var content = bytes.AsSpan(sizeof(int) * 2);
            Encoding.UTF8.GetBytes(header, content);
            if (chartPayload is null)
                original.CopyTo(content[headerLength..]);
            else
                Encoding.UTF8.GetBytes(chartPayload, content[headerLength..]);
            return queue.Writer.TryWrite(new Entry(received, bytes));
        }
    }

    // Prevent new payloads while the writer finishes the evidence already admitted.
    public void Complete()
    {
        lock (gate)
        {
            sealedQueue = true;
            queue.Writer.TryComplete();
        }
    }

    // Keep one input chunk and the existing archive writer's one immutable copy in flight.
    public async Task<IReadOnlyList<ChartAcceptanceChunk>> DrainAsync(CancellationToken cancellationToken)
    {
        var chunk = new byte[ChunkBytes];
        var length = 0;
        var firstReceipt = default(DateTimeOffset);
        try
        {
            await foreach (var entry in queue.Reader.ReadAllAsync(cancellationToken))
            {
                if (length + entry.Bytes.Length > ChunkBytes)
                {
                    await WriteChunkAsync(chunk.AsMemory(0, length), firstReceipt, cancellationToken);
                    length = 0;
                }

                if (committedChunks.Count == MaximumChunks)
                    throw new InvalidOperationException("The chart evidence reached the archive chunk limit.");
                if (length == 0) firstReceipt = entry.Received;
                entry.Bytes.CopyTo(chunk, length);
                length += entry.Bytes.Length;
                lock (gate)
                {
                    queuedEvents--;
                    queuedBytes -= entry.Bytes.Length;
                }
            }

            if (length > 0)
                await WriteChunkAsync(chunk.AsMemory(0, length), firstReceipt, cancellationToken);
            return committedChunks;
        }
        catch
        {
            fail("The chart evidence could not be written within its archive limits.");
            Complete();
            while (queue.Reader.TryRead(out _)) { }
            throw;
        }
    }

    // Store only the bounded manifest identities and hashes in the session result.
    private async Task WriteChunkAsync(ReadOnlyMemory<byte> chunk,
        DateTimeOffset firstReceipt, CancellationToken cancellationToken)
    {
        var request = new RawArchiveRequest("alpaca-chart-acceptance", "chart-evidence", firstReceipt,
            "bin", "application/octet-stream", RunId: sessionId.ToString("N"),
            Attributes: new Dictionary<string, string> { ["framing"] = "u32le-header-length-u32le-payload-length-utf8-v1" });
        var receipt = await archive.ArchiveAsync(request, chunk, cancellationToken);
        committedChunks.Add(new(receipt.Manifest.ArchiveId, receipt.Manifest.ReceivedAtUtc,
            receipt.Manifest.ByteLength, receipt.Manifest.Sha256));
    }

    // Publish the result with the immutable archive conventions and a separately bounded metadata payload.
    public async Task<string> WriteResultAsync(ChartAcceptanceResult result, DateTimeOffset receipt,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(result, StockChartHub.Json);
        if (payload.Length > MaximumMetadataBytes)
            throw new InvalidOperationException("The chart check result exceeds its metadata limit.");
        var manifest = await archive.ArchiveAsync(new RawArchiveRequest("alpaca-chart-acceptance", "chart-result",
            receipt, "json", "application/json", RunId: sessionId.ToString("N")), payload, cancellationToken);
        return manifest.Manifest.ArchiveId;
    }

    private readonly record struct Entry(DateTimeOffset Received, byte[] Bytes);
}

internal sealed record ChartAcceptanceChunk(string ArchiveId, DateTimeOffset ReceivedAtUtc, long Bytes, string Sha256);
