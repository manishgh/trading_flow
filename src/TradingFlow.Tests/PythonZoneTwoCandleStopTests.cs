using System.Text.Json;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Strategies;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed partial class PythonZoneVolumeSpreadComparisonTests
{
    // Both low observations can set the long anchor; outward rounding preserves the ATR buffer and never clamps to the zone.
    [Theory]
    [InlineData(99.05, 99.8, 98.75)]
    [InlineData(99.9, 99.055, 98.75)]
    [InlineData(97.955, 99.8, 97.65)]
    public void TwoCandleStop_UsesMinimumLowAndCanNarrowOrWiden(double previousLow, double confirmationLow, double expectedStop)
    {
        var previous = Bar(0, 100m, 101m, (decimal)previousLow, 100m);
        var current = Bar(5, 100m, 102m, (decimal)confirmationLow, 101m);
        var control = EvaluateWith(Treatment(true, true), previous, current, Indicator(current), Session(), DefaultZones());
        var treatment = EvaluateWith(TwoCandleRules(), previous, current, Indicator(current), Session(), DefaultZones());
        var signal = Assert.Single(treatment.OrderedAlternatives!);
        Assert.Equal((decimal)expectedStop, signal.InitialStopPrice);
        Assert.Equal(Math.Min(previous.Low, current.Low) - .3m, signal.Evidence.RawStopPrice);
        Assert.Equal(PythonZoneVolumeSpreadComparisonRules.TwoCandleLowStop, signal.Evidence.StopBasis);
        Assert.Equal(control.Signal!.Evidence.Zone, signal.Evidence.Zone);
        Assert.Equal(control.Signal.FixedTargetPrice, signal.FixedTargetPrice);
        Assert.Equal(control.Signal.Evidence.TargetZone, signal.Evidence.TargetZone);
        Assert.Equal(98.7m, control.Signal.InitialStopPrice);
        var trace = Assert.Single(treatment.Trace!.Alternatives, t => t.Direction == "long" && t.Zone.ZoneId == 1);
        Assert.Equal(signal.Evidence.RawStopPrice, trace.RawStopPrice);
        Assert.Equal(signal.InitialStopPrice, trace.RoundedStopPrice);
        Assert.Equal(signal.Evidence.StopBasis, trace.StopBasis);
        Assert.True(PythonZoneComparisonEvaluator.MatchesDecision(treatment, current, current.Timestamp.AddMinutes(5),
            Indicator(current), previous, TwoCandleRules()));
    }

    // A recipe selecting two-candle long stops cannot alter short prices, ordering, targets, or evidence.
    [Fact]
    public void TwoCandleStop_ShortRemainsSourceZoneTop()
    {
        var previous = Bar(0, 100m, 102m, 99m, 101m);
        var current = Bar(5, 100m, 101m, 97m, 98m);
        var zones = new[] { Zone(1, IntradayZoneRole.Resistance, 94m, 95m), Zone(2, IntradayZoneRole.Support, 99m, 100m) };
        var control = EvaluateWith(Treatment(true, true), previous, current, Indicator(current), Session(), zones);
        var treatment = EvaluateWith(TwoCandleRules(), previous, current, Indicator(current), Session(), zones);
        Assert.Equal(control.Signal, treatment.Signal);
        Assert.Equal(control.OrderedAlternatives, treatment.OrderedAlternatives);
        Assert.Equal(100.3m, treatment.Signal!.InitialStopPrice);
        Assert.Equal(IntradayZoneStopGeometry.SourceZone, treatment.Signal.Evidence.StopBasis);
    }

    // The existing geometry guard rejects a nonpositive new stop even when the source-zone stop would be valid.
    [Fact]
    public void TwoCandleStop_InvalidLevelIsRejectedAndExplained()
    {
        var previous = Bar(0, 100m, 101m, .2m, 100m);
        var current = Bar(5, 100m, 102m, 99.8m, 101m);
        Assert.NotNull(EvaluateWith(Treatment(true, true), previous, current, Indicator(current), Session(), DefaultZones()).Signal);
        var result = EvaluateWith(TwoCandleRules(), previous, current, Indicator(current), Session(), DefaultZones());
        Assert.Null(result.Signal);
        var trace = Assert.Single(result.Trace!.Alternatives, t => t.Direction == "long" && t.Zone.ZoneId == 1);
        Assert.Equal("rounded_structural_stop_invalid", trace.Reason);
        Assert.Equal(-.1m, trace.RawStopPrice);
        Assert.Equal(-.1m, trace.RoundedStopPrice);
    }

    // Two-candle anchors must remain adjacent, completed and on one feed before their lows can contribute to an entry.
    [Theory]
    [InlineData("feed")]
    [InlineData("adjustment")]
    [InlineData("gap")]
    [InlineData("future_receipt")]
    public void TwoCandleStop_RejectsUnavailablePreviousCandle(string failure)
    {
        var previous = Bar(0, 100m, 101m, 99.8m, 100m);
        if (failure == "feed") previous = previous with { DataFeed = "iex" };
        if (failure == "adjustment") previous = previous with { AdjustmentPolicy = "split" };
        if (failure == "future_receipt") previous = previous with { KnownAtUtc = Open.AddMinutes(11) };
        var current = Bar(failure == "gap" ? 10 : 5, 100m, 102m, 99.8m, 101m);
        var result = EvaluateWith(TwoCandleRules(), previous, current, Indicator(current), Session(), DefaultZones());
        Assert.Null(result.Signal);
    }

    // Only an explicit supported optional selection is written; omitted defaults retain old canonical content.
    [Fact]
    public void TwoCandleStop_SelectionRoundTripsAndRejectsUnsupportedBasis()
    {
        var root = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var control = reader.ReadStrategy(Path.Combine(root, "configs", "backtest", "strategies", "zone-bounce-volume-risk-target.bt-v1.yaml"));
        Assert.Null(control.EntryRules.IntradayZone!.PythonVolumeSpreadComparison!.LongStopBasis);
        Assert.DoesNotContain("LongStopBasis", JsonSerializer.Serialize(control));
        Assert.DoesNotContain("long_stop_basis", RunConfigWriter.SerializeStrategyYaml(control));
        var treatment = control with { EntryRules = control.EntryRules with { IntradayZone = TwoCandleRules() } };
        var yaml = RunConfigWriter.SerializeStrategyYaml(treatment);
        Assert.Contains("long_stop_basis: two_candle_low", yaml);
        var path = Path.Combine(Path.GetTempPath(), "tf-zone-stop-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, yaml);
            Assert.Equivalent(treatment, reader.ReadStrategy(path), strict: true);
            File.WriteAllText(path, yaml.Replace("long_stop_basis: two_candle_low", "long_stop_basis: unknown"));
            Assert.Throws<InvalidOperationException>(() => reader.ReadStrategy(path));
        }
        finally { File.Delete(path); }
    }

    // Apply the approved single stop-basis delta to the existing D-style rule selections.
    private static IntradayZoneRules TwoCandleRules() => Treatment(true, true) with {
        PythonVolumeSpreadComparison = Treatment(true, true).PythonVolumeSpreadComparison! with {
            LongStopBasis = PythonZoneVolumeSpreadComparisonRules.TwoCandleLowStop } };
}
