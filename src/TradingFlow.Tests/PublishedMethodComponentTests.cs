using System.Text.Json.Nodes;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

/// <summary>
/// Components added on 8 November 2026 for the published-method versions (design:
/// docs/design-review/2026-11-08/published-method-versions-proposal.md): sizing without a protective
/// stop, the no-stop settings rules, the catalog risk-policy pairing and the close-below-SMA stretch.
/// </summary>
public sealed class PublishedMethodComponentTests
{
    private static string StrategyPath(string name) =>
        Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "strategies", name);

    // No stop: quantity comes from the notional cap alone and the whole position is the planned loss.
    [Fact]
    public void NoStopLong_IsSizedByNotionalCapAlone()
    {
        var plan = new SharedOrderRiskPlanner().Plan(new OrderPlanningRequest("abc", PlannedOrderSide.Long, 100_000m, 50m,
            PlannedStopRequest.NoStop(), new OrderPlanningRiskLimits(1m, 25m), new EstimatedOrderExecutionCosts(0m, 1m, 1m), 10_000m));

        Assert.True(plan.IsAccepted);
        Assert.Equal(200, plan.Order!.Quantity);
        Assert.Equal(0m, plan.Order.StopTriggerPrice);
        Assert.Equal(10_000m + 2m, plan.Order.PlannedLoss);
    }

    // A published no-stop short (research only) is sized by the notional cap alone, mirroring the long; slippage lowers
    // the short's estimated entry and the planned loss is still reported as the deployed notional plus fees.
    [Fact]
    public void NoStopShort_IsSizedByNotionalCapAlone()
    {
        var plan = new SharedOrderRiskPlanner().Plan(new OrderPlanningRequest("abc", PlannedOrderSide.Short, 100_000m, 50m,
            PlannedStopRequest.NoStop(), new OrderPlanningRiskLimits(1m, 25m), new EstimatedOrderExecutionCosts(0m, 1m, 1m), 10_000m));

        Assert.True(plan.IsAccepted);
        Assert.Equal(PlannedOrderSide.Short, plan.Order!.Side);
        Assert.Equal(200, plan.Order.Quantity);
        Assert.Equal(0m, plan.Order.StopTriggerPrice);
        Assert.Equal(10_000m + 2m, plan.Order.PlannedLoss);
    }

    // The two new files load with no stop and no target and keep their declared exits.
    [Fact]
    public void PublishedFiles_DeclareNoStopNoTargetAndTheirOwnExits()
    {
        var reader = new SimpleYamlReader();
        var connors = reader.ReadStrategy(StrategyPath("swing-connors-rsi2-published.bt-v9.yaml"));
        var snapback = reader.ReadStrategy(StrategyPath("swing-snapback.bt-v2.yaml"));

        Assert.All(new[] { connors, snapback }, strategy =>
        {
            Assert.False(strategy.ExitRules.HasProtectiveStop);
            Assert.False(strategy.ExitRules.HasProfitTarget);
            Assert.True(strategy.ExitRules.ExitOnCloseAboveSma5);
            Assert.True(strategy.ExitRules.CheckExitsFromFillDay);
        });
        Assert.Null(connors.Regime);
        Assert.Null(connors.ExitRules.MaxHoldBars);
        Assert.True(connors.EntryRules.RequirePriceAboveSma200);
        Assert.Equal(5, snapback.ExitRules.MaxHoldBars);
        Assert.False(snapback.EntryRules.RequirePriceAboveSma200);
        Assert.Equal(30m, snapback.EntryRules.MaxEntryRsi);
        Assert.Equal((10m, 20), (snapback.EntryRules.MinCloseBelowSmaPct!.Value, snapback.EntryRules.CloseBelowSmaPeriod!.Value));
    }

    // Combinations that need a stop distance, or a stretch without its period, fail when the file is loaded.
    [Theory]
    [InlineData("  profit_target_mode: none", "  profit_target_mode: r_multiple")]
    [InlineData("  enable_atr_trailing_stop: false", "  enable_atr_trailing_stop: true")]
    [InlineData("direction: long", "direction: long_short")]
    [InlineData("  exit_on_close_above_sma5: true", "  exit_on_close_above_sma5: true\n  exit_on_close_below_sma5: true")]
    [InlineData("  close_below_sma_period: 20", "  close_below_sma_period: 30")]
    [InlineData("  close_below_sma_period: 20\n", "")]
    public void InvalidNoStopOrStretchSettings_FailWhenLoaded(string original, string replacement)
    {
        var source = File.ReadAllText(StrategyPath("swing-snapback.bt-v2.yaml")).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(original, source, StringComparison.Ordinal);
        var path = Path.Combine(Path.GetTempPath(), "tf-published-rule-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, source.Replace(original, replacement, StringComparison.Ordinal));
            Assert.Throws<InvalidOperationException>(() => new SimpleYamlReader().ReadStrategy(path));
        }
        finally { File.Delete(path); }
    }

    // Sizing is part of identity: a no-stop strategy must use the notional risk policy, and nothing else may.
    [Theory]
    [InlineData("swing.connors-rsi2", "9.0.0", "account-risk-budget-v1")]
    [InlineData("swing.connors-rsi2", "7.0.0", StrategyArtifactCatalog.NoStopRiskPolicy)]
    public void CatalogRejectsMismatchedRiskPolicy(string strategyId, string version, string riskPolicy)
    {
        var root = TestRepository.FindRoot();
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "configs", "strategy-catalog.json")))!.AsObject();
        var entry = manifest["strategies"]!.AsArray().Single(item =>
            (string?)item!["strategyId"] == strategyId && (string?)item["semanticVersion"] == version)!;
        var profileId = (string)entry["admissionProfileId"]!;
        var profile = manifest["admissionProfiles"]!.AsArray().Single(item => (string?)item!["profileId"] == profileId)!;
        profile["riskPolicy"] = riskPolicy;
        var path = Path.Combine(Path.GetTempPath(), "tf-catalog-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, manifest.ToJsonString());
            var catalog = new StrategyArtifactCatalog(root, path, new SimpleYamlReader());
            Assert.Throws<InvalidDataException>(() => catalog.GetSnapshot());
        }
        finally { File.Delete(path); }
    }

    // Snapback recipe evaluated on 259 flat daily closes followed by a 20% drop on the last completed bar.
    private static (StrategyDefinition Strategy, OhlcvBar[] Bars, IReadOnlyList<IndicatorSnapshot> Snapshots, TradeSignal Signal) SnapbackDrop()
    {
        var start = new DateTimeOffset(2026, 1, 2, 21, 0, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 260).Select(index =>
        {
            var close = index == 259 ? 80m : 100m;
            return new OhlcvBar("TEST", start.AddDays(index), "1d", close, close + 1m, close - 1m, close, 1_000_000m);
        }).ToArray();
        var snapshots = new IndicatorEngine().Compute(bars);
        var strategy = new SimpleYamlReader().ReadStrategy(StrategyPath("swing-snapback.bt-v2.yaml"));
        return (strategy, bars, snapshots, new SignalGenerator().CreateTradeSignal(strategy, bars, snapshots, bars.Length - 1)!);
    }

    // The no-stop mode resolves to "no stop order" for both directions; the risk unit is the entry price.
    [Fact]
    public void NoStopMode_ResolvesForLongAndShort()
    {
        var (strategy, bars, snapshots, signal) = SnapbackDrop();
        var resolver = new StrategyInitialStopResolver();

        var longStop = resolver.Resolve(new StrategyInitialStopRequest(strategy, signal, PlannedOrderSide.Long, 80m, bars.Length - 1, bars, snapshots.ToArray()));
        var shortStop = resolver.Resolve(new StrategyInitialStopRequest(strategy, signal, PlannedOrderSide.Short, 80m, bars.Length - 1, bars, snapshots.ToArray()));

        Assert.All(new[] { longStop, shortStop }, stop =>
        {
            Assert.True(stop.IsResolved);
            Assert.Equal(0m, stop.StopPrice);
            Assert.Equal(80m, stop.StopDistance);
            Assert.Equal(PlannedStopKind.None, stop.Stop!.Kind);
        });
    }

    // Broker order planning (paper/live) never accepts a plan without a protective stop, long or short; for shorts it is the
    // only refusal, because the shared sizing planner now accepts no-stop shorts for research backtests.
    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public void BrokerOrderPlanning_RefusesAPlanWithoutStop(string direction)
    {
        var (strategy, bars, _, signal) = SnapbackDrop();
        var at = bars[^1].Timestamp.AddDays(1);
        var plan = new StrategyOrderPlan(strategy.StrategyId, strategy.StrategyName, direction, at, at, "1d", bars.Length - 1,
            80m, 0m, 0m, ExitRules.NoProfitTarget, null, 10m, at.AddDays(1), bars[^1].Timestamp);

        var result = new StrategyOrderPlanner().Plan(new StrategyOrderPlanningRequest(plan, signal, 100_000m,
            new OrderPlanningRiskLimits(1m, 25m), 0m, 0m));

        Assert.False(result.IsAccepted);
        Assert.Contains("only in research backtests", result.RiskRejection!.Reason, StringComparison.Ordinal);
    }

    // Generated run snapshots keep the no-stop modes and the stretch gate exactly.
    [Fact]
    public void SnapbackSnapshot_RoundTripsThroughTheRunWriter()
    {
        var original = new SimpleYamlReader().ReadStrategy(StrategyPath("swing-snapback.bt-v2.yaml"));
        var path = Path.Combine(Path.GetTempPath(), "tf-snapback-roundtrip-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, TradingFlow.Web.Services.RunConfigWriter.SerializeStrategyYaml(original));
            Assert.Equivalent(original, new SimpleYamlReader().ReadStrategy(path), strict: true);
        }
        finally { File.Delete(path); }
    }

    // The signal reports how far the completed close sits below the configured SMA, from completed bars only.
    [Fact]
    public void CloseBelowSma_IsMeasuredFromTheCompletedCloseAndConfiguredPeriod()
    {
        var (_, _, snapshots, signal) = SnapbackDrop();

        var sma20 = snapshots[^1].Sma20!.Value;
        Assert.Equal(99m, sma20);
        Assert.Equal((sma20 - 80m) / sma20 * 100m, signal!.CloseBelowSmaPct);
    }
}
