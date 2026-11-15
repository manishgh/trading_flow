using Skender.Stock.Indicators;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;

namespace TradingFlow.Engine.Indicators;

public sealed record ChartBand(
    string Id, string Type, decimal Lower, decimal Upper, int PivotCount, string SourceTimeframe,
    DateTimeOffset FirstPivotUtc, DateTimeOffset LatestConfirmationUtc, DateTimeOffset EffectiveAtUtc,
    IReadOnlyList<DateOnly> SourceDates, bool IsPriceInBand);

/// <summary>Pure display-only wick structure using versioned fixed-window evidence.</summary>
public static class ChartSupportResistance
{
    public const string ProfileVersion = "desk-wick-zones-v1";
    private const int AtrWindow = 120;
    private const int PivotSideBars = 2;

    // Rank at one as-of cutoff. An unresolved gap or a basis boundary starts a new ATR/pivot window.
    public static IReadOnlyList<ChartBand> Calculate(
        string seriesIdentity, IReadOnlyList<ChartCompletedBar> bars, IReadOnlySet<DateOnly> activeConfirmationSessions,
        DateTimeOffset asOfUtc, decimal lastClose)
    {
        var eligible = bars.Where(bar => bar.EndUtc <= asOfUtc && bar.AvailableAtUtc <= asOfUtc)
            .OrderBy(bar => bar.Bar.Timestamp).ToArray();
        if (eligible.Length < AtrWindow)
        {
            return [];
        }

        var currentContinuity = eligible[^1].Continuity;
        var pivots = ReadPivots(eligible, activeConfirmationSessions, currentContinuity, asOfUtc);
        var groups = new List<Band>();
        foreach (var pivot in pivots)
        {
            MergeOrAdd(groups, pivot, seriesIdentity);
        }

        return new[] { "support", "resistance" }.SelectMany(type => groups
                .Where(group => group.First.Type == type && (type == "support" ? group.Lower <= lastClose : group.Upper >= lastClose))
                .OrderBy(group => Distance(group, lastClose))
                .ThenByDescending(group => group.LatestConfirmationUtc)
                .ThenBy(group => group.Id, StringComparer.Ordinal)
                .Take(2)
                .Select(group => group.ToResult(lastClose)))
            .ToArray();
    }

    // Pivots are emitted in source confirmation order, high before low for a wide bar.
    private static IEnumerable<Pivot> ReadPivots(
        ChartCompletedBar[] bars, IReadOnlySet<DateOnly> active, int continuity, DateTimeOffset asOfUtc)
    {
        for (var right = AtrWindow - 1; right < bars.Length; right++)
        {
            var confirmation = bars[right];
            if (confirmation.Continuity != continuity || bars[right - AtrWindow + 1].Continuity != continuity ||
                !active.Contains(confirmation.SessionDate))
            {
                continue;
            }

            var pivotIndex = right - PivotSideBars;
            var center = bars[pivotIndex];
            var isHigh = true;
            var isLow = true;
            for (var neighbor = pivotIndex - PivotSideBars; neighbor <= right; neighbor++)
            {
                if (neighbor == pivotIndex)
                {
                    continue;
                }
                isHigh &= center.Bar.High > bars[neighbor].Bar.High;
                isLow &= center.Bar.Low < bars[neighbor].Bar.Low;
            }

            if (!isHigh && !isLow)
            {
                continue;
            }

            var atr = ReadAtr(bars, right);
            if (atr <= 0m)
            {
                continue;
            }

            var available = confirmation.EndUtc;
            for (var input = right - AtrWindow + 1; input <= right; input++)
            {
                if (bars[input].AvailableAtUtc > available)
                {
                    available = bars[input].AvailableAtUtc;
                }
            }

            if (available > asOfUtc)
            {
                continue;
            }
            if (isHigh && center.Bar.High > atr * 0.15m)
            {
                yield return new Pivot("resistance", center.Bar.High, atr, center, confirmation, available);
            }
            if (isLow && center.Bar.Low > atr * 0.15m)
            {
                yield return new Pivot("support", center.Bar.Low, atr, center, confirmation, available);
            }
        }
    }

    // Fix the ATR14 seed and input size regardless of chart date range or zoom.
    private static decimal ReadAtr(ChartCompletedBar[] bars, int right)
    {
        var quotes = new Quote[AtrWindow];
        for (var index = 0; index < quotes.Length; index++)
        {
            var bar = bars[right - AtrWindow + 1 + index].Bar;
            quotes[index] = new Quote
            {
                Date = bar.Timestamp.UtcDateTime,
                Open = bar.Open, High = bar.High, Low = bar.Low, Close = bar.Close, Volume = bar.Volume
            };
        }
        return quotes.GetAtr(14).Last().Atr is { } atr ? (decimal)atr : 0m;
    }

    // Compare every candidate group to its first pivot and impose the union width cap.
    private static void MergeOrAdd(List<Band> groups, Pivot pivot, string seriesIdentity)
    {
        var selected = groups.Where(group => group.First.Type == pivot.Type &&
                Math.Abs(group.First.Price - pivot.Price) <= pivot.Atr * 0.35m &&
                Math.Max(group.Upper, pivot.Upper) - Math.Min(group.Lower, pivot.Lower) <= pivot.Atr * 0.75m)
            .OrderBy(group => Math.Abs(group.First.Price - pivot.Price))
            .ThenBy(group => group.First.Confirmation.EndUtc)
            .ThenBy(group => group.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        if (selected is null)
        {
            var id = EvidenceCanonicalJson.ComputeSha256(new
            {
                ProfileVersion, seriesIdentity, pivot.Type, pivot.Source.Bar.Timestamp
            });
            groups.Add(new Band(id, pivot));
        }
        else
        {
            selected.Add(pivot);
        }
    }

    // Distance to the band edge is zero while the last eligible close is inside.
    private static decimal Distance(Band band, decimal lastClose) =>
        Math.Max(0m, Math.Max(band.Lower - lastClose, lastClose - band.Upper));

    private sealed record Pivot(string Type, decimal Price, decimal Atr, ChartCompletedBar Source,
        ChartCompletedBar Confirmation, DateTimeOffset EffectiveAtUtc)
    {
        public decimal Lower => Price - Atr * 0.15m;
        public decimal Upper => Price + Atr * 0.15m;
    }

    private sealed class Band(string id, Pivot first)
    {
        public string Id { get; } = id;
        public Pivot First { get; } = first;
        public decimal Lower { get; private set; } = first.Lower;
        public decimal Upper { get; private set; } = first.Upper;
        public DateTimeOffset LatestConfirmationUtc { get; private set; } = first.Confirmation.EndUtc;
        private DateTimeOffset effectiveAt = first.EffectiveAtUtc;
        private int count = 1;
        private readonly SortedSet<DateOnly> dates = [first.Source.SessionDate];

        // Append exactly one logical pivot produced by the ordered unique source series.
        public void Add(Pivot pivot)
        {
            Lower = Math.Min(Lower, pivot.Lower);
            Upper = Math.Max(Upper, pivot.Upper);
            LatestConfirmationUtc = pivot.Confirmation.EndUtc;
            effectiveAt = pivot.EffectiveAtUtc > effectiveAt ? pivot.EffectiveAtUtc : effectiveAt;
            count++;
            dates.Add(pivot.Source.SessionDate);
        }

        // Snapshot band geometry and the maximum accepted availability of its evidence.
        public ChartBand ToResult(decimal lastClose) =>
            new(Id, First.Type, Lower, Upper, count, First.Source.Bar.Timeframe, First.Source.Bar.Timestamp,
                LatestConfirmationUtc, effectiveAt, dates.ToArray(), lastClose >= Lower && lastClose <= Upper);
    }
}
