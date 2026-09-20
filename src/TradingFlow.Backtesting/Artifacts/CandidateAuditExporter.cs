using System.Text.Json;
using TradingFlow.Data.Backtesting;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Backtesting.Artifacts;

internal static class CandidateAuditExporter
{
    /// <summary>Merges indexed worker histories with one candidate per cursor; transitions stream individually.</summary>
    public static async Task<long> WriteAsync(Stream destination, IReadOnlyList<string> journals, CancellationToken token)
    {
        var stores = new List<SqliteBacktestCandidateStateStore>();
        var cursors = new List<IEnumerator<CandidateRecord>>();
        var comparer = Comparer<CandidateRecord>.Create((left, right) =>
        {
            var value = left.DiscoveredAtUtc.CompareTo(right.DiscoveredAtUtc);
            if (value == 0) value = StringComparer.OrdinalIgnoreCase.Compare(left.Symbol, right.Symbol);
            return value == 0 ? left.CandidateId.CompareTo(right.CandidateId) : value;
        });
        var queue = new PriorityQueue<int, CandidateRecord>(comparer);
        long count = 0;
        try
        {
            foreach (var path in journals)
            {
                token.ThrowIfCancellationRequested();
                var store = new SqliteBacktestCandidateStateStore(BacktestCandidateJournal.StatePath(path), readOnly: true);
                stores.Add(store);
                var cursor = store.ReadCandidates(token).GetEnumerator();
                cursors.Add(cursor);
                if (cursor.MoveNext()) queue.Enqueue(cursors.Count - 1, cursor.Current);
            }
            using var writer = new Utf8JsonWriter(destination);
            writer.WriteStartArray();
            while (queue.TryDequeue(out var index, out var candidate))
            {
                token.ThrowIfCancellationRequested();
                writer.WriteStartObject();
                writer.WritePropertyName("Candidate");
                JsonSerializer.Serialize(writer, candidate);
                await writer.FlushAsync(token);
                writer.WriteStartArray("Transitions");
                foreach (var transition in stores[index].ReadTransitions(candidate.CandidateId))
                {
                    token.ThrowIfCancellationRequested();
                    JsonSerializer.Serialize(writer, transition);
                    await writer.FlushAsync(token);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                await writer.FlushAsync(token);
                count++;
                var cursor = cursors[index];
                if (cursor.MoveNext()) queue.Enqueue(index, cursor.Current);
            }
            writer.WriteEndArray();
            await writer.FlushAsync(token);
            return count;
        }
        finally
        {
            foreach (var cursor in cursors) cursor.Dispose();
            foreach (var store in stores) store.Dispose();
        }
    }
}
