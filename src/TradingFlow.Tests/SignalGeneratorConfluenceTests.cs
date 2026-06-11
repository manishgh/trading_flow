using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Strategies;
using Xunit;

namespace TradingFlow.Tests;

public sealed class SignalGeneratorConfluenceTests
{
    [Fact]
    public void GetConfluenceRejection_WhenBearishConfluenceMatches_ReturnsNull()
    {
        var strategy = CreateStrategy(macdFilter: "bearish");
        var snapshots = CreateSnapshots(price: 95m, ema20: 100m, macdHistogram: -0.25m);

        var rejection = new SignalGenerator().GetConfluenceRejection(strategy, DateTimeOffset.Parse("2026-06-10T15:00:00Z"), snapshots);

        Assert.Null(rejection);
    }

    [Fact]
    public void GetConfluenceRejection_WhenBearishConfluencePriceAboveEma_ReturnsReason()
    {
        var strategy = CreateStrategy(macdFilter: "bearish");
        var snapshots = CreateSnapshots(price: 105m, ema20: 100m, macdHistogram: -0.25m);

        var rejection = new SignalGenerator().GetConfluenceRejection(strategy, DateTimeOffset.Parse("2026-06-10T15:00:00Z"), snapshots);

        Assert.StartsWith("confluence_price_above_ema20", rejection);
    }

    [Fact]
    public void GetConfluenceRejection_WhenBearishConfluenceMacdPositive_ReturnsReason()
    {
        var strategy = CreateStrategy(macdFilter: "bearish");
        var snapshots = CreateSnapshots(price: 95m, ema20: 100m, macdHistogram: 0.05m);

        var rejection = new SignalGenerator().GetConfluenceRejection(strategy, DateTimeOffset.Parse("2026-06-10T15:00:00Z"), snapshots);

        Assert.StartsWith("confluence_macd_not_bearish", rejection);
    }

    private static StrategyDefinition CreateStrategy(string macdFilter)
    {
        return new StrategyDefinition(
            "test.confluence",
            "Test Confluence",
            "test",
            1,
            "1h",
            "short",
            new EntryRules("momentum", 0m, 0m, 100m, "none", "none", false, false, false, false, false, false, null, 60, 20, 10),
            new ConfluenceRules(true, "1d", 20, macdFilter),
            new ExitRules(1.5m, 3m, 96m, false, 2m, 1m, false, false, false, 1),
            new ExecutionRules("1h", 15m),
            new SessionRules("America/New_York", 0, 0, 0));
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<IndicatorSnapshot>> CreateSnapshots(decimal price, decimal ema20, decimal macdHistogram)
    {
        var snapshot = new IndicatorSnapshot(
            "TEST",
            DateTimeOffset.Parse("2026-06-09T04:00:00Z"),
            "1d",
            price,
            1_000_000m,
            null,
            50m,
            2m,
            ema20,
            100m,
            100m,
            null,
            null,
            null,
            1m,
            null,
            null,
            macdHistogram);

        return new Dictionary<string, IReadOnlyList<IndicatorSnapshot>>(StringComparer.OrdinalIgnoreCase)
        {
            ["1d"] = new[] { snapshot }
        };
    }
}