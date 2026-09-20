using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
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
            AccountRiskBudgetPct: 1.0m,
            MaxPositionNotionalPct: 100m,
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
    public void BuildPortfolioTrades_ParticipationLimitedEntry_RecordsRequestedAndFilledQuantity()
    {
        var method = typeof(BacktestRunner).GetMethod(
            "BuildPortfolioTrades",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var strategy = ConfirmedEntryStrategy();
        var portfolio = new PortfolioConfig(
            StartingCapital: 10_000m,
            AccountRiskBudgetPct: 1m,
            MaxPositionNotionalPct: 100m,
            MaxConcurrentPositions: 1,
            FixedBuyFee: 0m,
            FixedSellFee: 0m,
            MaxOpenTradesPerTicker: 1,
            PreventOverlappingTickerPositions: false,
            MaxBarParticipationPct: 5m);
        var candidate = Candidate(
            "AAA",
            "2026-06-02T13:31:00Z",
            "2026-06-02T13:40:00Z",
            10m,
            9m,
            12m,
            "take_profit") with
        {
            EntryLiquidityEvidenceVolume = 1_000m
        };

        var trades = (IReadOnlyList<BacktestTrade>)method.Invoke(
            null, [portfolio, strategy, new[] { candidate }, null, null])!;

        var trade = Assert.Single(trades);
        Assert.Equal(100, trade.RequestedShareQuantity);
        Assert.Equal(50, trade.ShareQuantity);
        Assert.Equal("partial_fill_remainder_canceled", trade.EntryFillStatus);
        Assert.Equal(5m, trade.EntryEstimatedParticipationPct);
        Assert.Equal(candidate.StrategyId, trade.StrategyId);
        Assert.Equal(candidate.CandidateId, trade.CandidateId);
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
    public void ResolveSecretForTesting_PrefersDeploymentEnvironment_OverLocalSettings()
    {
        var settingsPath = CreateTemporaryAlpacaSettings("local-test-key");
        try
        {
            var key = BacktestRunner.ResolveSecretForTesting(
                "Alpaca",
                "KeyId",
                settingsPath,
                "environment-test-key");

            Assert.Equal("environment-test-key", key);
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

        var snapshots = bars
            .Select(bar => Snapshot(bar.Timestamp.ToString("O")) with
            {
                CurrentPrice = bar.Close
            })
            .ToArray();
        var plan = new CompletedBarExecutionPlanner().Plan(
            new CompletedBarExecutionRequest(
                strategy,
                signal,
                PlannedOrderSide.Long,
                bars,
                snapshots,
                _ => true,
                RequireKnownFillBar: true));

        Assert.True(plan.IsReady);
        Assert.Equal(0, plan.ConfirmationIndex);
        Assert.Equal(0, plan.StopContextIndex);
        Assert.Equal(1, plan.EntryIndex);
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
    public void BuildPortfolioTrades_EqualTimestampCandidates_UsesStableTickerOrdering()
    {
        var method = typeof(BacktestRunner).GetMethod(
            "BuildPortfolioTrades",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var strategy = ConfirmedEntryStrategy();
        var portfolio = new PortfolioConfig(
            StartingCapital: 10_000m,
            AccountRiskBudgetPct: 1.0m,
            MaxPositionNotionalPct: 100m,
            MaxConcurrentPositions: 1,
            FixedBuyFee: 0m,
            FixedSellFee: 0m,
            MaxOpenTradesPerTicker: 1,
            PreventOverlappingTickerPositions: true);
        var candidates = new[]
        {
            Candidate("ZZZ", "2026-06-01T13:31:00Z", "2026-06-01T13:40:00Z", 10m, 9m, 12m, "take_profit"),
            Candidate("AAA", "2026-06-01T13:31:00Z", "2026-06-01T13:40:00Z", 10m, 9m, 12m, "take_profit")
        };

        var trades = (IReadOnlyList<BacktestTrade>)method.Invoke(
            null, [portfolio, strategy, candidates, null, null])!;

        var trade = Assert.Single(trades);
        Assert.Equal("AAA", trade.Ticker);
    }

    [Fact]
    public void BuildUnifiedPortfolioTrades_CompetingStrategiesShareSlotsAndUseSelectionScore()
    {
        var method = typeof(BacktestRunner).GetMethod(
            "BuildUnifiedPortfolioTrades",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var first = ConfirmedEntryStrategy() with
        {
            StrategyId = "strategy.first",
            StrategyName = "First"
        };
        var second = ConfirmedEntryStrategy() with
        {
            StrategyId = "strategy.second",
            StrategyName = "Second"
        };
        var portfolio = new PortfolioConfig(
            StartingCapital: 10_000m,
            AccountRiskBudgetPct: 1.0m,
            MaxPositionNotionalPct: 100m,
            MaxConcurrentPositions: 1,
            FixedBuyFee: 0m,
            FixedSellFee: 0m,
            MaxOpenTradesPerTicker: 1,
            PreventOverlappingTickerPositions: true);
        var candidates = new[]
        {
            Candidate("AAA", "2026-06-01T13:31:00Z", "2026-06-01T13:40:00Z", 10m, 9m, 12m, "take_profit", "First", 1m, first.StrategyId),
            Candidate("ZZZ", "2026-06-01T13:31:00Z", "2026-06-01T13:40:00Z", 10m, 9m, 12m, "take_profit", "Second", 2m, second.StrategyId)
        };

        var trades = (IReadOnlyList<BacktestTrade>)method.Invoke(
            null,
            [portfolio, new[] { first, second }, candidates, null, null])!;

        var trade = Assert.Single(trades);
        Assert.Equal("ZZZ", trade.Ticker);
        Assert.Equal("Second", trade.StrategyName);
    }

    [Fact]
    public void BuildUnifiedPortfolioTrades_SameBarEntryAndExit_FailsClosedAsAmbiguous()
    {
        var method = typeof(BacktestRunner).GetMethod(
            "BuildUnifiedPortfolioTrades",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var first = ConfirmedEntryStrategy() with
        {
            StrategyId = "strategy.first",
            StrategyName = "First"
        };
        var second = ConfirmedEntryStrategy() with
        {
            StrategyId = "strategy.second",
            StrategyName = "Second"
        };
        var portfolio = new PortfolioConfig(
            StartingCapital: 10_000m,
            AccountRiskBudgetPct: 1.0m,
            MaxPositionNotionalPct: 100m,
            MaxConcurrentPositions: 4,
            FixedBuyFee: 0m,
            FixedSellFee: 0m,
            MaxOpenTradesPerTicker: 1,
            PreventOverlappingTickerPositions: true);
        var candidates = new[]
        {
            Candidate("AAA", "2026-06-01T13:31:00Z", "2026-06-01T13:31:00Z", 10m, 9m, 12m, "stop_loss", "First", 2m, first.StrategyId),
            Candidate("AAA", "2026-06-01T13:31:00Z", "2026-06-01T13:31:00Z", 10m, 9m, 12m, "stop_loss", "Second", 1m, second.StrategyId)
        };

        var trades = (IReadOnlyList<BacktestTrade>)method.Invoke(
            null,
            [portfolio, new[] { first, second }, candidates, null, null])!;

        Assert.Empty(trades);
    }

    [Fact]
    public void BuildUnifiedPortfolioTrades_ResolvesVersionsByStrategyIdNotDisplayName()
    {
        var method = typeof(BacktestRunner).GetMethod(
            "BuildUnifiedPortfolioTrades",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var first = ConfirmedEntryStrategy() with
        {
            StrategyId = "strategy.shared.v1",
            StrategyName = "Shared Display Name"
        };
        var second = ConfirmedEntryStrategy() with
        {
            StrategyId = "strategy.shared.v2",
            StrategyName = "Shared Display Name"
        };
        var portfolio = new PortfolioConfig(
            StartingCapital: 10_000m,
            AccountRiskBudgetPct: 1m,
            MaxPositionNotionalPct: 100m,
            MaxConcurrentPositions: 1,
            FixedBuyFee: 0m,
            FixedSellFee: 0m,
            MaxOpenTradesPerTicker: 1,
            PreventOverlappingTickerPositions: true);
        var candidates = new[]
        {
            Candidate("AAA", "2026-06-01T13:31:00Z", "2026-06-01T13:40:00Z", 10m, 9m, 12m, "take_profit", first.StrategyName, 1m, first.StrategyId),
            Candidate("ZZZ", "2026-06-01T13:31:00Z", "2026-06-01T13:40:00Z", 10m, 9m, 12m, "take_profit", second.StrategyName, 2m, second.StrategyId)
        };

        var trades = (IReadOnlyList<BacktestTrade>)method.Invoke(
            null,
            [portfolio, new[] { first, second }, candidates, null, null])!;

        Assert.Equal("ZZZ", Assert.Single(trades).Ticker);
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

    [Fact]
    public void PublishedEconomics_IncompleteExecutionSuppressesTradeAndPerformanceClaims()
    {
        var trade = new BacktestTrade(
            "AAA",
            "Strategy",
            "long",
            DateTimeOffset.Parse("2026-06-01T13:30:00Z"),
            DateTimeOffset.Parse("2026-06-01T14:00:00Z"),
            10,
            10m,
            11m,
            9m,
            12m,
            "take_profit",
            10m,
            2m,
            8m,
            CandidateId: "candidate-1");
        var strategy = StrategyResult("winner", "Winner", 8m, 0.08m, 1m, 1, 1, 0) with
        {
            CompletedTrades = [trade]
        };
        var winner = new WinnerStrategySummary(
            strategy.StrategyId,
            strategy.StrategyName,
            strategy.EndingCapital,
            strategy.NetProfit,
            strategy.TotalReturnPct,
            strategy.AverageDailyReturnPct,
            strategy.MaxDrawdownPct,
            strategy.AcceptedTradeCount,
            strategy.RejectedTradeCount);

        var summary = BacktestRunner.BuildPublishedEconomicSummary(
            10_000m,
            strategy,
            winner,
            economicResultsComplete: false);

        Assert.Equal(10_000m, summary.EndingCapital);
        Assert.Equal(0m, summary.NetProfit);
        Assert.Equal(0m, summary.TotalReturnPct);
        Assert.Equal(0m, summary.MaxDrawdownPct);
        Assert.Null(summary.Winner);
        Assert.Equal(0, summary.WinningTradeCount);
        Assert.Empty(summary.CompletedTrades);
    }

    [Fact]
    public void IncompleteUnifiedExecution_InvalidatesNestedEconomicProjections()
    {
        var strategy = StrategyResult("winner", "Winner", 8m, 0.08m, 1m, 1, 1, 0) with
        {
            EconomicResultsComplete = true
        };
        var unified = new UnifiedPortfolioBacktestResult(
            10_000m,
            10_008m,
            8m,
            0.08m,
            1m,
            1,
            1,
            0,
            1,
            0,
            Array.Empty<BacktestTrade>(),
            EconomicResultsComplete: true);

        var suppressedStrategy = BacktestRunner.SuppressStrategyEconomicClaims(strategy);
        var suppressedUnified = BacktestRunner.SuppressUnifiedPortfolioEconomicClaims(unified);

        Assert.False(suppressedStrategy.EconomicResultsComplete);
        Assert.Equal(strategy.StartingCapital, suppressedStrategy.EndingCapital);
        Assert.Equal(0m, suppressedStrategy.NetProfit);
        Assert.Empty(suppressedStrategy.CompletedTrades);
        Assert.False(suppressedUnified.EconomicResultsComplete);
        Assert.Equal(unified.StartingCapital, suppressedUnified.EndingCapital);
        Assert.Equal(0m, suppressedUnified.NetProfit);
        Assert.Empty(suppressedUnified.CompletedTrades);
    }

    [Fact]
    public void ReplayRetentionLimit_AppliesAcrossPreparedMarketAndBenchmarkBars()
    {
        BacktestRunner.EnsureReplayRetentionLimit(
            retainedBars: 90,
            additionalBars: 10,
            maximumBars: 100);

        var exception = Assert.Throws<BacktestReplayRetentionLimitExceededException>(() =>
            BacktestRunner.EnsureReplayRetentionLimit(
                retainedBars: 90,
                additionalBars: 11,
                maximumBars: 100));

        Assert.Contains("max_retained_replay_bars", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StrategyWorkItemLimit_RejectsMatrixBeforeMaterialization()
    {
        BacktestRunner.EnsureStrategyWorkItemLimit(100, 10, 1_000);

        var exception = Assert.Throws<BacktestWorkItemLimitExceededException>(() =>
            BacktestRunner.EnsureStrategyWorkItemLimit(101, 10, 1_000));

        Assert.Contains("max_strategy_work_items", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RetainedCandidateBudget_FailsClosedAtConfiguredLimit()
    {
        var budget = new RetainedCandidateBudget(1);
        budget.Reserve();

        var exception = Assert.Throws<BacktestCandidateRetentionLimitExceededException>(budget.Reserve);

        Assert.Contains("max_retained_candidates", exception.Message, StringComparison.Ordinal);
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
                "swing_reclaim",
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
                EnableEntryBarConfirmation: true,
                MinEntryBarCloseLocationValue: 0.55m,
                RejectEntryBarCloseLocationBelowMinimum: true,
                RejectEntryBarBreaksSignalMidpoint: true),
            new ConfluenceRules(false, "15m", 50, "none"),
            new ExitRules(3m, 3.5m, 6.5m, true, 3m, 2m, false, false, false, 2),
            new ExecutionRules(
                "5m",
                10m,
                new ExecutionConfirmationRules(
                    true,
                    3,
                    "none",
                    "none",
                    "none",
                    MinCloseLocationValue: 0.55m)),
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
        string exitReason,
        string strategyName = "Confirmed Entry Test",
        decimal selectionScore = 0m,
        string strategyId = "")
    {
        return new BacktestCandidateTrade(
            ticker,
            strategyName,
            "long",
            DateTimeOffset.Parse(entryTimestamp, System.Globalization.CultureInfo.InvariantCulture),
            entry,
            stop,
            12m,
            DateTimeOffset.Parse(exitTimestamp, System.Globalization.CultureInfo.InvariantCulture),
            exit,
            exitReason,
            Math.Abs(entry - stop),
            SelectionScore: selectionScore,
            StrategyId: strategyId,
            CandidateId: $"{strategyId}:{ticker}:{entryTimestamp}:{exitTimestamp}");
    }

    private static DateOnly ToNewYorkDate(DateTimeOffset timestamp)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, timeZone).DateTime);
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
