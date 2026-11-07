using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class ContinuationProtectionConsumerTests
{
    // Existing strategies remain accepted without an optional continuation policy.
    [Fact]
    public void ExistingStrategyKeepsExistingExitPath() =>
        TechnicalExecutionEngine.RequireSupportedExitPolicy(ReadBaseline());

    // A caller cannot silently run this policy as a fixed-target or ATR-only strategy.
    [Fact]
    public void ExistingExitPathRejectsContinuationProtection()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TechnicalExecutionEngine.RequireSupportedExitPolicy(WithContinuation()));
        Assert.Contains("stop and target updates", error.Message);
    }

    // Missing candles must not mask the unsupported policy as an ordinary hold decision.
    [Fact]
    public void GuardianRejectsBeforeReturningMissingDataHold() =>
        Assert.Throws<NotSupportedException>(() => new PositionGuardianEngine().EvaluateLong(
            WithContinuation(), Array.Empty<OhlcvBar>(), Array.Empty<IndicatorSnapshot>(),
            DateTimeOffset.Parse("2026-10-23T14:00:00Z"), 100m, 99m, 103m));

    // The web ticket explains missing integration instead of offering a misleading fixed bracket.
    [Fact]
    public void DeskTicketReportsUnsupportedProtection()
    {
        var plan = DeskTicketPlanService.Build(WithContinuation(), 100m,
            Array.Empty<OhlcvBar>(), Array.Empty<IndicatorSnapshot>(),
            Array.Empty<OhlcvBar>(), Array.Empty<IndicatorSnapshot>(), 100m);
        Assert.Null(plan.Stop);
        Assert.Null(plan.Target);
        Assert.Contains("not connected yet", plan.StopNote);
    }

    // Reuse the checked-in source recipe; no catalog or operator configuration is changed.
    private static StrategyDefinition ReadBaseline() => new SimpleYamlReader().ReadStrategy(Path.Combine(
        TestRepository.FindRoot(), "src", "TradingFlow.Tests", "Fixtures", "Strategies", "daily-trailing-swing.fixture.yaml"));

    // Enable the policy only in this isolated test object's exit rules.
    private static StrategyDefinition WithContinuation()
    {
        var strategy = ReadBaseline();
        return strategy with
        {
            ExitRules = strategy.ExitRules with
            {
                EnableAtrTrailingStop = false,
                TargetRMultiple = 3m,
                ContinuationProtection = new ContinuationProtectionRules()
            }
        };
    }
}
