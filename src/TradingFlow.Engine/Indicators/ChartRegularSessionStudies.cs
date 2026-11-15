using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Indicators;

public sealed record ChartRegularPrefix(
    DateTimeOffset StartUtc, decimal[] Volume, decimal?[]? Vwap, DateTimeOffset[] Available, bool[] Reconstructed);

/// <summary>Reads true cumulative regular-session values over a verified completed minute prefix.</summary>
public static class ChartRegularSessionStudies
{
    // Stop at the first unknown minute. Quiet proof adds no invented prices or volume.
    public static ChartRegularPrefix Read(ChartTimeRange range, DateTimeOffset cutoff, DateTimeOffset asOf,
        IReadOnlyDictionary<DateTimeOffset, ChartSourceBar> source, IReadOnlyDictionary<DateTimeOffset, DateTimeOffset> quiet,
        IReadOnlySet<DateTimeOffset> breaks, bool calculateVwap)
    {
        var volume = new List<decimal>();
        var available = new List<DateTimeOffset>();
        var reconstructed = new List<bool>();
        var prices = new List<OhlcvBar>();
        var positions = new List<int>();
        var clock = range.StartUtc;
        var cumulative = 0m;
        var reconstructedPrefix = false;
        for (var minute = range.StartUtc; minute < range.EndUtc && minute.AddMinutes(1) <= cutoff; minute = minute.AddMinutes(1))
        {
            if (minute > range.StartUtc && breaks.Contains(minute)) break;
            DateTimeOffset receipt;
            if (source.TryGetValue(minute, out var bar) && bar.AvailableAtUtc <= asOf)
            {
                cumulative += bar.Bar.Volume;
                receipt = bar.AvailableAtUtc;
                reconstructedPrefix |= bar.Bar.KnownAtUtc is null;
                if (calculateVwap) prices.Add(bar.Bar);
            }
            else if (quiet.TryGetValue(minute, out var proof) && proof <= asOf)
            {
                receipt = proof;
            }
            else break;
            var end = minute.AddMinutes(1);
            if (end > clock) clock = end;
            if (receipt > clock) clock = receipt;
            volume.Add(cumulative);
            available.Add(clock);
            reconstructed.Add(reconstructedPrefix);
            if (calculateVwap) positions.Add(prices.Count - 1);
        }
        var vwap = calculateVwap ? Vwap(prices, positions) : null;
        return new ChartRegularPrefix(range.StartUtc, volume.ToArray(), vwap, available.ToArray(), reconstructed.ToArray());
    }

    // Use the shared bar-based HLC3 formula and map real observations back onto covered minute offsets.
    private static decimal?[] Vwap(IReadOnlyList<OhlcvBar> bars, IReadOnlyList<int> positions)
    {
        var values = new IndicatorEngine().ComputeVwap(bars);
        return positions.Select(index => index >= 0 ? values[index] : null).ToArray();
    }
}
