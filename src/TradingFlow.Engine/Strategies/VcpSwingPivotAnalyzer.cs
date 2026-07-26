using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Strategies;

/// <summary>
/// Detects VCP geometry from confirmed alternating swing pivots. The current
/// completed bar is evaluated as a breakout candidate and is never used to form
/// the prior pivot structure.
/// </summary>
public sealed class VcpSwingPivotAnalyzer
{
    public VcpSwingPivotAnalysis AnalyzeCompletedBar(
        IReadOnlyList<OhlcvBar> bars,
        int completedBarIndex,
        VcpSwingPivotOptions options)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        if (completedBarIndex < 0 || completedBarIndex >= bars.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedBarIndex),
                completedBarIndex,
                "The completed bar index must identify a bar in the supplied series.");
        }

        var currentBar = bars[completedBarIndex];
        var windowStart = Math.Max(0, completedBarIndex - options.LookbackBars);
        ValidateSeries(bars, windowStart, completedBarIndex);

        var structureEndExclusive = completedBarIndex;
        var pivots = FindAlternatingPivots(
            bars,
            windowStart,
            structureEndExclusive,
            options.PivotStrengthBars);

        var structure = FindMostRecentValidStructure(bars, pivots, windowStart, options);
        if (structure is null)
        {
            return new VcpSwingPivotAnalysis(
                false,
                false,
                "valid_vcp_pivot_structure_not_found",
                null,
                pivots,
                Array.Empty<VcpContractionAnalysis>(),
                null);
        }

        var breakoutLevel = structure.Pivots[^1].Price *
            (1m + (options.BreakoutBufferPct / 100m));
        var isBreakout = currentBar.Close > breakoutLevel;
        var finalContractionVolume = structure.Contractions[^1].AverageVolume;
        var breakoutVolumeRatio = finalContractionVolume <= 0m
            ? (decimal?)null
            : currentBar.Volume / finalContractionVolume;

        return new VcpSwingPivotAnalysis(
            true,
            isBreakout,
            isBreakout ? null : "close_not_above_final_pivot",
            structure.Pivots[^1],
            structure.Pivots,
            structure.Contractions,
            breakoutVolumeRatio);
    }

    private static VcpStructure? FindMostRecentValidStructure(
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<VcpSwingPivot> pivots,
        int windowStart,
        VcpSwingPivotOptions options)
    {
        if (pivots.Count < (options.MinimumContractions * 2) + 1)
        {
            return null;
        }

        var end = pivots.Count - 1;
        while (end >= 0 && pivots[end].Type != VcpPivotType.High)
        {
            end--;
        }

        if (end < 0)
        {
            return null;
        }

        // Only the latest confirmed resistance pivot is eligible. Falling back to
        // an older valid shape after a newer malformed pattern would create a stale
        // breakout signal that no longer describes current price structure.
        for (var count = options.MaximumContractions;
             count >= options.MinimumContractions;
             count--)
        {
            var pivotCount = (count * 2) + 1;
            var start = end - pivotCount + 1;
            if (start < 0)
            {
                continue;
            }

            var candidate = pivots.Skip(start).Take(pivotCount).ToArray();
            if (!HasRequiredAlternation(candidate))
            {
                continue;
            }

            var contractions = BuildContractions(bars, candidate, windowStart, options);
            if (contractions is not null &&
                HasShrinkingDepths(contractions, options) &&
                HasRisingLows(candidate, options) &&
                HasRequiredVolumeDryUp(contractions, options))
            {
                return new VcpStructure(candidate, contractions);
            }
        }

        return null;
    }

    private static IReadOnlyList<VcpContractionAnalysis>? BuildContractions(
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<VcpSwingPivot> pivots,
        int windowStart,
        VcpSwingPivotOptions options)
    {
        var contractions = new List<VcpContractionAnalysis>((pivots.Count - 1) / 2);
        for (var pivotIndex = 0; pivotIndex < pivots.Count - 1; pivotIndex += 2)
        {
            var high = pivots[pivotIndex];
            var low = pivots[pivotIndex + 1];
            if (high.Type != VcpPivotType.High ||
                low.Type != VcpPivotType.Low ||
                high.Price <= 0m ||
                low.Price >= high.Price)
            {
                return null;
            }

            var contractionBars = SliceInclusive(bars, high.BarIndex + 1, low.BarIndex);
            if (contractionBars.Count == 0)
            {
                return null;
            }

            IReadOnlyList<OhlcvBar> referenceBars;
            if (pivotIndex == 0)
            {
                var referenceCount = Math.Max(
                    options.MinimumVolumeReferenceBars,
                    contractionBars.Count);
                var referenceStart = Math.Max(windowStart, high.BarIndex - referenceCount + 1);
                referenceBars = SliceInclusive(bars, referenceStart, high.BarIndex);
            }
            else
            {
                var previousLow = pivots[pivotIndex - 1];
                referenceBars = SliceInclusive(bars, previousLow.BarIndex + 1, high.BarIndex);
            }

            if (referenceBars.Count < options.MinimumVolumeReferenceBars)
            {
                return null;
            }

            var contractionAverageVolume = AveragePositiveVolume(contractionBars);
            var referenceAverageVolume = AveragePositiveVolume(referenceBars);
            if (contractionAverageVolume is null || referenceAverageVolume is null)
            {
                return null;
            }

            var volumeRatio = contractionAverageVolume.Value / referenceAverageVolume.Value;
            contractions.Add(new VcpContractionAnalysis(
                contractions.Count + 1,
                high,
                low,
                ((high.Price - low.Price) / high.Price) * 100m,
                contractionAverageVolume.Value,
                referenceAverageVolume.Value,
                volumeRatio,
                volumeRatio <= options.MaximumContractionToAdvanceVolumeRatio));
        }

        return contractions;
    }

    private static IReadOnlyList<VcpSwingPivot> FindAlternatingPivots(
        IReadOnlyList<OhlcvBar> bars,
        int start,
        int endExclusive,
        int strength)
    {
        var raw = new List<VcpSwingPivot>();
        var firstCandidate = start + strength;
        var lastCandidate = endExclusive - strength - 1;
        for (var index = firstCandidate; index <= lastCandidate; index++)
        {
            var isHigh = IsPivotHigh(bars, index, strength);
            var isLow = IsPivotLow(bars, index, strength);
            if (isHigh == isLow)
            {
                continue;
            }

            raw.Add(new VcpSwingPivot(
                isHigh ? VcpPivotType.High : VcpPivotType.Low,
                index,
                bars[index].Timestamp,
                isHigh ? bars[index].High : bars[index].Low));
        }

        var alternating = new List<VcpSwingPivot>(raw.Count);
        foreach (var pivot in raw)
        {
            if (alternating.Count == 0 || alternating[^1].Type != pivot.Type)
            {
                alternating.Add(pivot);
                continue;
            }

            var previous = alternating[^1];
            var isMoreExtreme = pivot.Type == VcpPivotType.High
                ? pivot.Price > previous.Price
                : pivot.Price < previous.Price;
            if (isMoreExtreme)
            {
                alternating[^1] = pivot;
            }
        }

        return alternating;
    }

    private static bool IsPivotHigh(IReadOnlyList<OhlcvBar> bars, int index, int strength)
    {
        var value = bars[index].High;
        var strictlyGreater = false;
        for (var offset = 1; offset <= strength; offset++)
        {
            var left = bars[index - offset].High;
            var right = bars[index + offset].High;
            if (value < left || value < right)
            {
                return false;
            }

            strictlyGreater |= value > left || value > right;
        }

        return strictlyGreater;
    }

    private static bool IsPivotLow(IReadOnlyList<OhlcvBar> bars, int index, int strength)
    {
        var value = bars[index].Low;
        var strictlyLower = false;
        for (var offset = 1; offset <= strength; offset++)
        {
            var left = bars[index - offset].Low;
            var right = bars[index + offset].Low;
            if (value > left || value > right)
            {
                return false;
            }

            strictlyLower |= value < left || value < right;
        }

        return strictlyLower;
    }

    private static bool HasRequiredAlternation(IReadOnlyList<VcpSwingPivot> pivots)
    {
        if (pivots.Count < 5 ||
            pivots[0].Type != VcpPivotType.High ||
            pivots[^1].Type != VcpPivotType.High)
        {
            return false;
        }

        for (var index = 1; index < pivots.Count; index++)
        {
            if (pivots[index].Type == pivots[index - 1].Type)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasShrinkingDepths(
        IReadOnlyList<VcpContractionAnalysis> contractions,
        VcpSwingPivotOptions options)
    {
        for (var index = 1; index < contractions.Count; index++)
        {
            if (contractions[index].DepthPct >=
                contractions[index - 1].DepthPct * options.MaximumDepthRatioToPrevious)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasRisingLows(
        IReadOnlyList<VcpSwingPivot> pivots,
        VcpSwingPivotOptions options)
    {
        var lows = pivots.Where(x => x.Type == VcpPivotType.Low).ToArray();
        for (var index = 1; index < lows.Length; index++)
        {
            var requiredLow = lows[index - 1].Price *
                (1m + (options.MinimumLowRisePct / 100m));
            if (lows[index].Price <= requiredLow)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasRequiredVolumeDryUp(
        IReadOnlyList<VcpContractionAnalysis> contractions,
        VcpSwingPivotOptions options)
    {
        if (contractions.Any(x => !x.HasVolumeDryUp))
        {
            return false;
        }

        if (!options.RequireProgressiveContractionVolume)
        {
            return true;
        }

        for (var index = 1; index < contractions.Count; index++)
        {
            if (contractions[index].AverageVolume >=
                contractions[index - 1].AverageVolume *
                options.MaximumVolumeRatioToPreviousContraction)
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<OhlcvBar> SliceInclusive(
        IReadOnlyList<OhlcvBar> bars,
        int start,
        int end)
    {
        if (start > end)
        {
            return Array.Empty<OhlcvBar>();
        }

        return bars.Skip(start).Take(end - start + 1).ToArray();
    }

    private static decimal? AveragePositiveVolume(IReadOnlyList<OhlcvBar> bars)
    {
        var volumes = bars.Where(x => x.Volume > 0m).Select(x => x.Volume).ToArray();
        return volumes.Length == 0 ? null : volumes.Average();
    }

    private static void ValidateSeries(
        IReadOnlyList<OhlcvBar> bars,
        int start,
        int completedBarIndex)
    {
        var first = bars[completedBarIndex];
        for (var index = start; index <= completedBarIndex; index++)
        {
            if (!bars[index].Ticker.Equals(first.Ticker, StringComparison.OrdinalIgnoreCase) ||
                !bars[index].Timeframe.Equals(first.Timeframe, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "VCP analysis requires one ticker and one timeframe.",
                    nameof(bars));
            }

            if (index > start && bars[index].Timestamp <= bars[index - 1].Timestamp)
            {
                throw new ArgumentException(
                    "VCP analysis bars must be in strictly increasing timestamp order.",
                    nameof(bars));
            }
        }
    }

    private static void ValidateOptions(VcpSwingPivotOptions options)
    {
        if (options.LookbackBars < 5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.LookbackBars,
                "VCP lookback must contain at least five bars.");
        }

        if (options.PivotStrengthBars < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.PivotStrengthBars,
                "Pivot strength must be at least one bar on each side.");
        }

        if (options.MinimumContractions < 2 ||
            options.MaximumContractions < options.MinimumContractions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "VCP contraction bounds must allow at least two contractions.");
        }

        if (options.MaximumDepthRatioToPrevious <= 0m ||
            options.MaximumDepthRatioToPrevious > 1m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaximumDepthRatioToPrevious,
                "The maximum depth ratio must be greater than zero and no greater than one.");
        }

        if (options.MinimumLowRisePct < 0m ||
            options.MaximumContractionToAdvanceVolumeRatio <= 0m ||
            options.MaximumVolumeRatioToPreviousContraction <= 0m ||
            options.MinimumVolumeReferenceBars < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "VCP volume and rising-low options must be positive.");
        }
    }

    private sealed record VcpStructure(
        IReadOnlyList<VcpSwingPivot> Pivots,
        IReadOnlyList<VcpContractionAnalysis> Contractions);
}

public sealed record VcpSwingPivotOptions(
    int LookbackBars = 60,
    int PivotStrengthBars = 2,
    int MinimumContractions = 2,
    int MaximumContractions = 4,
    decimal MaximumDepthRatioToPrevious = 0.90m,
    decimal MinimumLowRisePct = 0m,
    decimal MaximumContractionToAdvanceVolumeRatio = 0.70m,
    bool RequireProgressiveContractionVolume = true,
    decimal MaximumVolumeRatioToPreviousContraction = 1m,
    int MinimumVolumeReferenceBars = 1,
    decimal BreakoutBufferPct = 0m);

public sealed record VcpSwingPivotAnalysis(
    bool HasValidStructure,
    bool IsBreakout,
    string? RejectionReason,
    VcpSwingPivot? FinalPivot,
    IReadOnlyList<VcpSwingPivot> Pivots,
    IReadOnlyList<VcpContractionAnalysis> Contractions,
    decimal? BreakoutVolumeRatio);

public sealed record VcpSwingPivot(
    VcpPivotType Type,
    int BarIndex,
    DateTimeOffset Timestamp,
    decimal Price);

public sealed record VcpContractionAnalysis(
    int Sequence,
    VcpSwingPivot High,
    VcpSwingPivot Low,
    decimal DepthPct,
    decimal AverageVolume,
    decimal ReferenceAdvanceAverageVolume,
    decimal ContractionToAdvanceVolumeRatio,
    bool HasVolumeDryUp);

public enum VcpPivotType
{
    High,
    Low
}
