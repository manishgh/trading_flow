using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Tests;

public class BacktestRunnerExecutionQualityTests
{
    [Fact]
    public void BuildPortfolioTrades_RegimeOffDay_SkipsEntryOnlyOnOffDays()
    {
        var method = typeof(BacktestRunner).GetMethod(
            "BuildPortfolioTrades",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var strategy = ConfirmedEntryStrategy();
        var portfolio = new PortfolioConfig(
            StartingCapital: 10_000m,
            RiskPerTradePct: 1.0m,
            MaxPositionValuePct: 100m,
            MaxConcurrentPositions: 5,
            FixedBuyFee: 0m,
            FixedSellFee: 0m,
            MaxOpenTradesPerTicker: 1,
            PreventOverlappingTickerPositions: false);
        var candidates = new[]
        {
            Candidate("AAA", "2026-06-02T13:31:00Z", "2026-06-02T13:40:00Z", 10m, 9m, 12m, "take_profit"),
            Candidate("BBB", "2026-06-03T13:31:00Z", "2026-06-03T13:40:00Z", 10m, 9m, 12m, "take_profit")
        };
        // Regime is on only 2026-06-02, so the 06-03 entry must be gated out.
        var regime = RegimeCalendar.FromOnDates(new HashSet<DateOnly> { new DateOnly(2026, 6, 2) });

        var trades = (IReadOnlyList<BacktestTrade>)method.Invoke(
            null, [portfolio, strategy, candidates, null, regime])!;

        Assert.Single(trades);
        Assert.Equal("AAA", trades[0].Ticker);
    }

    [Fact]
    public void ResolveSecretForTesting_LoadsLocalAlpacaSettings_WhenEnvironmentIsMissing()
    {
        var settingsPath = CreateTemporaryAlpacaSettings("local-test-key");
        try
        {
            var key = BacktestRunner.ResolveSecretForTesting("Alpaca", "KeyId", settingsPath, null);

            Assert.Equal("local-test-key", key);
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    [Fact]
    public void ResolveSecretForTesting_PrefersLocalAlpacaSettings_OverEnvironment()
    {
        var settingsPath = CreateTemporaryAlpacaSettings("local-test-key");
        try
        {
            var key = BacktestRunner.ResolveSecretForTesting(
                "Alpaca",
                "KeyId",
                settingsPath,
                "environment-test-key");

            Assert.Equal("local-test-key", key);
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    [Fact]
    public void ResolveSecretForTesting_UsesEnvironment_WhenLocalSettingsAreUnavailable()
    {
        var key = BacktestRunner.ResolveSecretForTesting(
            "Alpaca",
            "KeyId",
            settingsPath: null,
            environmentValue: "environment-test-key");

        Assert.Equal("environment-test-key", key);
    }

    private static string CreateTemporaryAlpacaSettings(string keyId)
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"tradingflow-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(settingsPath, $$"""
            {
              "Alpaca": {
                "KeyId": "{{keyId}}"
              }
            }
            """);
        return settingsPath;
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

        CatalystSnapshotAttacher.AttachToSnapshots(snapshots, catalysts, CancellationToken.None);

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

    [Fact]
    public void StrategyWorkItemTimeout_HonorsConfiguredResearchTimeout()
    {
        var method = typeof(BacktestRunner).GetMethod(
            "ResolveStrategyWorkItemTimeout",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var timeout = (TimeSpan)method.Invoke(null, [1800])!;

        Assert.Equal(TimeSpan.FromMinutes(30), timeout);
    }

    [Fact]
    public void VolumeConfirmationSoftMarker_DoesNotRejectBacktestCandidate()
    {
        var baseStrategy = ConfirmedEntryStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                MinVolumeSpike = 2.0m,
                VolumeConfirmationMode = "soft_marker"
            }
        };
        var snapshot = Snapshot("2026-06-01T13:30:00Z");

        // One brain: soft_marker volume mode must not reject in the shared StrategyDecisionBrain that
        // both backtest and live use (relative volume 0.25 < MinVolumeSpike 2.0, but soft_marker is advisory).
        var rejection = new TradingFlow.Engine.Strategies.StrategyDecisionBrain()
            .GetVolumeConfirmationRejection(strategy, snapshot, 0.25m);

        Assert.Null(rejection);
    }

    [Fact]
    public void BuildPortfolioTrades_WhenTickerDailyLossGuardTrips_SkipsLaterSameTickerCandidateOnly()
    {
        var method = typeof(BacktestRunner).GetMethod(
            "BuildPortfolioTrades",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var baseStrategy = ConfirmedEntryStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                EnablePerTickerDailyLossGuard = true,
                MaxPerTickerDailyFailedTrades = 1,
                MaxPerTickerDailyLossR = 1.0m,
                MaxPerTickerDailyLossPctOfAccount = 1.0m
            }
        };
        var portfolio = new PortfolioConfig(
            StartingCapital: 10_000m,
            RiskPerTradePct: 1.0m,
            MaxPositionValuePct: 100m,
            MaxConcurrentPositions: 5,
            FixedBuyFee: 1m,
            FixedSellFee: 1m,
            MaxOpenTradesPerTicker: 1,
            PreventOverlappingTickerPositions: true);
        var candidates = new[]
        {
            Candidate("LOSS", "2026-06-01T13:31:00Z", "2026-06-01T13:32:00Z", 10m, 9m, 9m, "stop_loss"),
            Candidate("LOSS", "2026-06-01T13:35:00Z", "2026-06-01T13:40:00Z", 10m, 9m, 12m, "take_profit"),
            Candidate("OKAY", "2026-06-01T13:36:00Z", "2026-06-01T13:41:00Z", 10m, 9m, 12m, "take_profit")
        };

        var trades = (IReadOnlyList<BacktestTrade>)method.Invoke(null, [portfolio, strategy, candidates, null, null])!;

        Assert.Equal(2, trades.Count);
        Assert.Single(trades, trade => trade.Ticker == "LOSS");
        Assert.Single(trades, trade => trade.Ticker == "OKAY");
        Assert.DoesNotContain(trades, trade => trade.Ticker == "LOSS" && trade.NetProfit > 0);
    }

    [Fact]
    public void SelectBestActiveStrategy_IgnoresNoTradeStrategyWithZeroLoss()
    {
        var method = typeof(BacktestRunner).GetMethod(
            "SelectBestActiveStrategy",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var noTrade = StrategyResult("no-trade", "No Trade", 0m, 0m, 0m, 0, 0, 0);
        var activeLoss = StrategyResult("active-loss", "Active Loss", -50m, -0.5m, 2m, 4, 1, 3);
        var activeWorse = StrategyResult("active-worse", "Active Worse", -100m, -1.0m, 3m, 4, 0, 4);

        var best = (StrategyBacktestResult?)method.Invoke(null, [new[] { noTrade, activeLoss, activeWorse }]);

        Assert.NotNull(best);
        Assert.Equal("active-loss", best.StrategyId);
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

    private static StrategyBacktestResult StrategyResult(
        string id,
        string name,
        decimal netProfit,
        decimal totalReturnPct,
        decimal maxDrawdownPct,
        int acceptedTrades,
        int wins,
        int losses)
    {
        return new StrategyBacktestResult(
            id,
            name,
            "test",
            10_000m,
            10_000m + netProfit,
            netProfit,
            totalReturnPct,
            0m,
            1,
            maxDrawdownPct,
            acceptedTrades,
            acceptedTrades,
            0,
            wins,
            losses,
            Array.Empty<BacktestTrade>());
    }
    private static BacktestCandidateTrade Candidate(
        string ticker,
        string entryTimestamp,
        string exitTimestamp,
        decimal entry,
        decimal stop,
        decimal exit,
        string exitReason)
    {
        return new BacktestCandidateTrade(
            ticker,
            "Confirmed Entry Test",
            "long",
            DateTimeOffset.Parse(entryTimestamp, System.Globalization.CultureInfo.InvariantCulture),
            entry,
            stop,
            12m,
            DateTimeOffset.Parse(exitTimestamp, System.Globalization.CultureInfo.InvariantCulture),
            exit,
            exitReason,
            Math.Abs(entry - stop));
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
