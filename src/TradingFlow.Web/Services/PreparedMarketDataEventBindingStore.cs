using System.Collections.Concurrent;
using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Web.Services;

/// <summary>Anchors a comparison's selected input hash in the existing application journal before any portfolio consumes it.</summary>
public sealed class PreparedMarketDataEventBindingStore(IApplicationEventRepository events) : IPreparedMarketDataBindingStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    /// <summary>Reads the authoritative single selection; duplicate conflicting journal entries fail explicitly.</summary>
    public async Task<PreparedMarketDataBinding?> GetAsync(string comparisonId, CancellationToken cancellationToken)
    {
        var records = await events.ListAsync(Stream(comparisonId), 0, 2, cancellationToken);
        if (records.Count == 0) return null;
        var first = records[0];
        if (first.EventType != "backtest.candles.selected" || records.Any(record => record.PayloadJson != first.PayloadJson))
            throw new InvalidDataException("Comparison has conflicting prepared-data selections.");
        return JsonSerializer.Deserialize<PreparedMarketDataBinding>(first.PayloadJson, Json)
            ?? throw new InvalidDataException("Comparison data binding is empty.");
    }

    /// <summary>Persists the first verified selection once; current-host preparation also holds its comparison file lock.</summary>
    public async Task BindAsync(string comparisonId, PreparedMarketDataBinding binding, CancellationToken cancellationToken)
    {
        var gate = Gates.GetOrAdd(comparisonId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var serialized = JsonSerializer.Serialize(binding, Json);
            var prior = await GetAsync(comparisonId, cancellationToken);
            if (prior is not null)
            {
                if (JsonSerializer.Serialize(prior, Json) != serialized)
                    throw new InvalidDataException("This comparison already has a different immutable data selection.");
                return;
            }
            await events.AppendAsync(Stream(comparisonId), "backtest.candles.selected", binding.Summary.PreparedAtUtc,
                serialized, cancellationToken);
        }
        finally { gate.Release(); }
    }

    /// <summary>Uses a bounded server comparison identity, excluding arbitrary stream names.</summary>
    private static string Stream(string comparisonId)
    {
        if (String.IsNullOrWhiteSpace(comparisonId) || comparisonId.Length > 100 ||
            comparisonId.Any(character => !Char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new InvalidDataException("Prepared comparison identity contains unsupported characters.");
        return "backtest-candles:" + comparisonId;
    }
}
