using TradingFlow.Domain.Market;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public sealed class VcpSwingPivotAnalyzerTests
{
    private static readonly VcpSwingPivotOptions Options = new(
        LookbackBars: 20,
        PivotStrengthBars: 1,
        MinimumContractions: 2,
        MaximumContractions: 2,
        MaximumDepthRatioToPrevious: 0.90m,
        MinimumLowRisePct: 0m,
        MaximumContractionToAdvanceVolumeRatio: 0.70m,
        RequireProgressiveContractionVolume: true,
        MaximumVolumeRatioToPreviousContraction: 1m,
        MinimumVolumeReferenceBars: 1,
        BreakoutBufferPct: 0m);

    private readonly VcpSwingPivotAnalyzer analyzer = new();

    [Fact]
    public void AnalyzeCompletedBar_AcceptsAlternatingShrinkingRisingLowStructure()
    {
        var bars = ValidVcpBars();

        var result = analyzer.AnalyzeCompletedBar(bars, bars.Count - 1, Options);

        Assert.True(result.HasValidStructure);
        Assert.True(result.IsBreakout);
        Assert.Null(result.RejectionReason);
        Assert.Equal(5, result.Pivots.Count);
        Assert.Equal(
            [
                VcpPivotType.High,
                VcpPivotType.Low,
                VcpPivotType.High,
                VcpPivotType.Low,
                VcpPivotType.High
            ],
            result.Pivots.Select(x => x.Type).ToArray());
        Assert.Equal(115m, result.FinalPivot!.Price);
        Assert.Equal(2, result.Contractions.Count);
        Assert.True(result.Contractions[1].DepthPct < result.Contractions[0].DepthPct);
        Assert.True(result.Contractions.All(x => x.HasVolumeDryUp));
        Assert.True(result.Contractions[1].AverageVolume < result.Contractions[0].AverageVolume);
    }

    [Fact]
    public void AnalyzeCompletedBar_DoesNotUseBreakoutBarToFormFinalPivot()
    {
        var bars = ValidVcpBars();

        var result = analyzer.AnalyzeCompletedBar(bars, bars.Count - 1, Options);

        Assert.Equal(7, result.FinalPivot!.BarIndex);
        Assert.Equal(115m, result.FinalPivot.Price);
        Assert.DoesNotContain(result.Pivots, x => x.BarIndex == bars.Count - 1);
    }

    [Fact]
    public void AnalyzeCompletedBar_RejectsNonRisingContractionLows()
    {
        var bars = ValidVcpBars().ToArray();
        bars[6] = bars[6] with { Low = 99m };

        var result = analyzer.AnalyzeCompletedBar(bars, bars.Length - 1, Options);

        Assert.False(result.HasValidStructure);
        Assert.Equal("valid_vcp_pivot_structure_not_found", result.RejectionReason);
    }

    [Fact]
    public void AnalyzeCompletedBar_RejectsContractionThatDoesNotShrink()
    {
        var bars = ValidVcpBars().ToArray();
        bars[4] = bars[4] with { Low = 108m };
        bars[5] = bars[5] with { High = 130m };
        bars[6] = bars[6] with { Low = 109m, High = 120m };
        bars[7] = bars[7] with { High = 128m, Low = 115m };
        bars[8] = bars[8] with { High = 126m, Low = 116m };
        bars[9] = bars[9] with { High = 131m, Close = 130m };

        var result = analyzer.AnalyzeCompletedBar(bars, bars.Length - 1, Options);

        Assert.False(result.HasValidStructure);
        Assert.Equal("valid_vcp_pivot_structure_not_found", result.RejectionReason);
    }

    [Fact]
    public void AnalyzeCompletedBar_RejectsOneContractionWithoutItsOwnVolumeDryUp()
    {
        var bars = ValidVcpBars().ToArray();
        bars[6] = bars[6] with { Volume = 900m };

        var result = analyzer.AnalyzeCompletedBar(bars, bars.Length - 1, Options);

        Assert.False(result.HasValidStructure);
        Assert.Equal("valid_vcp_pivot_structure_not_found", result.RejectionReason);
    }

    [Fact]
    public void AnalyzeCompletedBar_SeparatesValidStructureFromFinalPivotBreakout()
    {
        var bars = ValidVcpBars().ToArray();
        bars[^1] = bars[^1] with { High = 115m, Close = 114.50m };

        var result = analyzer.AnalyzeCompletedBar(bars, bars.Length - 1, Options);

        Assert.True(result.HasValidStructure);
        Assert.False(result.IsBreakout);
        Assert.Equal("close_not_above_final_pivot", result.RejectionReason);
    }

    [Fact]
    public void AnalyzeCompletedBar_DoesNotInferVcpFromSmoothEqualBuckets()
    {
        var bars = Enumerable.Range(0, 12)
            .Select(index => Bar(
                index,
                open: 100m + index,
                high: 101m + index,
                low: 99m + index,
                close: 100.50m + index,
                volume: 1_000m - (index * 20m)))
            .ToArray();

        var result = analyzer.AnalyzeCompletedBar(bars, bars.Length - 1, Options);

        Assert.False(result.HasValidStructure);
        Assert.Empty(result.Contractions);
    }

    [Fact]
    public void AnalyzeCompletedBar_DoesNotFallBackToStaleStructure()
    {
        var bars = ValidVcpBars().Take(9).ToList();
        bars.Add(Bar(9, 105m, 110m, 100m, 102m, 300m));
        bars.Add(Bar(10, 115m, 130m, 112m, 128m, 1_000m));
        bars.Add(Bar(11, 108m, 115m, 95m, 100m, 250m));
        bars.Add(Bar(12, 112m, 125m, 110m, 123m, 900m));
        bars.Add(Bar(13, 116m, 120m, 113m, 118m, 200m));
        bars.Add(Bar(14, 124m, 132m, 121m, 130m, 1_400m));

        var result = analyzer.AnalyzeCompletedBar(
            bars,
            bars.Count - 1,
            Options with { LookbackBars = 30 });

        Assert.False(result.HasValidStructure);
        Assert.Equal("valid_vcp_pivot_structure_not_found", result.RejectionReason);
    }

    private static IReadOnlyList<OhlcvBar> ValidVcpBars()
    {
        return
        [
            Bar(0, 89m, 90m, 88m, 89m, 1_200m),
            Bar(1, 90m, 95m, 89m, 94m, 1_150m),
            Bar(2, 95m, 105m, 94m, 104m, 1_100m),
            Bar(3, 111m, 120m, 110m, 118m, 1_000m),
            Bar(4, 108m, 110m, 100m, 103m, 400m),
            Bar(5, 109m, 116m, 108m, 114m, 900m),
            Bar(6, 111m, 112m, 106m, 108m, 250m),
            Bar(7, 111m, 115m, 110m, 114m, 700m),
            Bar(8, 112m, 114m, 111m, 113m, 200m),
            Bar(9, 114m, 118m, 113m, 116m, 1_400m)
        ];
    }

    private static OhlcvBar Bar(
        int index,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        decimal volume)
    {
        return new OhlcvBar(
            "TEST",
            DateTimeOffset.Parse("2026-04-01T20:00:00Z").AddDays(index),
            "1d",
            open,
            high,
            low,
            close,
            volume);
    }
}
