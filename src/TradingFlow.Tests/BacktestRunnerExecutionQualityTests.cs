using TradingFlow.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Tests;

public class BacktestRunnerExecutionQualityTests
{
    [Fact]
    public void ResolveSecretForTesting_LoadsLocalAlpacaSettings_WhenEnvironmentIsMissing()
    {
        var previous = Environment.GetEnvironmentVariable("ALPACA_KEY_ID");
        try
        {
            Environment.SetEnvironmentVariable("ALPACA_KEY_ID", null);

            var key = BacktestRunner.ResolveSecretForTesting("Alpaca", "KeyId", "ALPACA_KEY_ID");

            Assert.False(String.IsNullOrWhiteSpace(key));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALPACA_KEY_ID", previous);
        }
    }

    [Fact]
    public void ResolveSecretForTesting_PrefersLocalAlpacaSettings_OverEnvironment()
    {
        var previous = Environment.GetEnvironmentVariable("ALPACA_KEY_ID");
        try
        {
            Environment.SetEnvironmentVariable("ALPACA_KEY_ID", "bad-env-key");

            var key = BacktestRunner.ResolveSecretForTesting("Alpaca", "KeyId", "ALPACA_KEY_ID");

            Assert.False(String.IsNullOrWhiteSpace(key));
            Assert.NotEqual("bad-env-key", key);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALPACA_KEY_ID", previous);
        }
    }

    [Fact]
    public void DynamicSlippageModel_AppliesAdverseSlippageForLongEntryAndExit()
    {
        var entry = DynamicSlippageModel.ApplyLongSlippage(100m, 1.0m, 10m, 20_000m);
        var exit = DynamicSlippageModel.ApplyLongExitSlippage(100m, 1.0m, 10m, 20_000m);

        Assert.True(entry > 100m);
        Assert.True(exit < 100m);
        Assert.Equal(100.10m, Math.Round(entry, 2));
        Assert.Equal(99.90m, Math.Round(exit, 2));
    }

    [Fact]
    public void AttachCatalystsToSnapshots_UsesLatestFreshCatalyst()
    {
        var attachMethod = typeof(BacktestRunner).GetMethod(
            "AttachCatalystsToSnapshots",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(attachMethod);

        var snapshots = new List<IndicatorSnapshot>
        {
            Snapshot("2026-06-01T10:00:00Z"),
            Snapshot("2026-06-02T10:00:00Z"),
            Snapshot("2026-06-04T10:00:00Z"),
            Snapshot("2026-06-06T10:00:00Z")
        };
        var catalysts = new List<CatalystEvent>
        {
            Catalyst("2026-06-01T09:00:00Z", "first"),
            Catalyst("2026-06-05T09:00:00Z", "second")
        };

        attachMethod.Invoke(null, [snapshots, catalysts, CancellationToken.None]);

        Assert.Equal("first", snapshots[0].Catalyst?.Headline);
        Assert.Equal("first", snapshots[1].Catalyst?.Headline);
        Assert.Null(snapshots[2].Catalyst);
        Assert.Equal("second", snapshots[3].Catalyst?.Headline);
    }

    [Fact]
    public void ConfirmedEntryPlan_FillsAfterConfirmationBar_ToAvoidLookAhead()
    {
        var runner = new BacktestRunner(new SimpleYamlReader());
        var method = typeof(BacktestRunner).GetMethod(
            "GetExecutionEntryPlan",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var signalTime = DateTimeOffset.Parse("2026-06-01T13:30:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var strategy = ConfirmedEntryStrategy();
        var signal = new TradeSignal(
            "POET",
            signalTime,
            "5m",
            10m,
            100_000m,
            60m,
            0.25m,
            true,
            false,
            true,
            false,
            false,
            true,
            false,
            true,
            false,
            true,
            true,
            false,
            false,
            0.5m,
            true,
            true,
            true,
            SignalBarMidpoint: 9.90m,
            BollingerPosition: 0.80m);
        var bars = new[]
        {
            Bar("2026-06-01T13:35:00Z", 10.00m, 11.00m, 10.00m, 10.70m),
            Bar("2026-06-01T13:40:00Z", 10.80m, 11.20m, 10.70m, 11.00m)
        };

        var plan = (ValueTuple<string?, int>)method.Invoke(runner, [strategy, signal, bars])!;

        Assert.Null(plan.Item1);
        Assert.Equal(1, plan.Item2);
    }

    private static IndicatorSnapshot Snapshot(string timestamp)
    {
        return new IndicatorSnapshot(
            "POET",
            DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture),
            "5m",
            10m,
            1000m,
            10m,
            55m,
            0.5m,
            10m,
            9m,
            null,
            null,
            null,
            null,
            1.2m,
            0.1m,
            0.05m,
            0.05m);
    }

    private static StrategyDefinition ConfirmedEntryStrategy()
    {
        return new StrategyDefinition(
            "test.confirmed-entry",
            "Confirmed Entry Test",
            "test",
            1,
            "5m",
            "long",
            new EntryRules(
                "ross_gap_go_bull_flag",
                2m,
                0m,
                100m,
                "vwap",
                "not_bearish",
                false,
                false,
                true,
                true,
                false,
                false,
                3m,
                5,
                8,
                6,
                EnableEntryBarConfirmation: true,
                MinEntryBarCloseLocationValue: 0.55m,
                RejectEntryBarCloseLocationBelowMinimum: true,
                RejectEntryBarBreaksSignalMidpoint: true,
                MaxVwapExtensionPctForDirectEntry: 4m,
                ExtendedVwapMinEntryBarCloseLocationValue: 0.70m,
                MaxBollingerPositionForDirectEntry: 1.05m,
                ExtendedBollingerMinEntryBarCloseLocationValue: 0.70m),
            new ConfluenceRules(false, "15m", 50, "none"),
            new ExitRules(3m, 3.5m, 6.5m, true, 3m, 2m, false, false, false, 2),
            new ExecutionRules("5m", 10m),
            new SessionRules("America/New_York", 1, 30, 30));
    }

    private static OhlcvBar Bar(string timestamp, decimal open, decimal high, decimal low, decimal close)
    {
        return new OhlcvBar(
            "POET",
            DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture),
            "5m",
            open,
            high,
            low,
            close,
            100_000m);
    }

    private static CatalystEvent Catalyst(string timestamp, string headline)
    {
        return new CatalystEvent(
            "POET",
            DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture),
            CatalystType.NewsReport,
            headline,
            0.8m,
            "test",
            headline);
    }
}
