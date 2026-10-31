using System.Text.Json;
using Moq;
using TradingFlow.Application.Candidates;
using TradingFlow.Application.Jobs;
using TradingFlow.Backtesting;
using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Tests;

public sealed class NativeDailyResultIntegrationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // A partly exited candidate remains open; monthly realized profit includes costs when charged, rather than only closed trades.
    [Fact]
    public void MonthlyState_UsesMonthOpeningEquityAndCumulativeProfitAndFeeDifferences()
    {
        var fixture = MonthlyRunResultWriterTests.CreateFixture();
        var state = State(fixture.Identity.StrategyId, fixture.Config.TimeWindow.End!.Value);
        var result = WithState(fixture.Result, state);
        var report = fixture.Writer.Create(fixture.Config, result, state.CutoffUtc, "group", [fixture.Identity]);

        Assert.Null(report.UnavailableReason);
        var january = report.Results[0];
        var february = report.Results[1];
        Assert.Equal(NativeDailySessionLedger.ValuationBasis, january.ValuationBasis);
        Assert.Equal(1000m, january.OpeningEquity);
        Assert.Equal(1009m, january.ClosingEquity);
        Assert.Equal(.9m, january.ReturnPct);
        Assert.Equal(-1m, january.RealizedNetProfit);
        Assert.Equal(10m, january.UnrealizedProfit);
        Assert.Equal(1m, january.Fees);
        Assert.Equal(1009m, february.OpeningEquity);
        Assert.Equal(1022m, february.ClosingEquity);
        Assert.Equal((1022m / 1009m - 1m) * 100m, february.ReturnPct);
        Assert.Equal(11m, february.RealizedNetProfit);
        Assert.Equal(12m, february.UnrealizedProfit);
        Assert.Equal(1m, february.Fees);
        Assert.Equal(950m, february.ClosingCash);
        Assert.Equal(72m, february.ClosingPositionValue);
        Assert.Equal(0, february.ClosedTrades);
        Assert.Equal(1, february.OpenPositions);
        Assert.Equal(1, january.FilledCandidates);
        Assert.Equal(0, february.FilledCandidates);
        Assert.Equal(1, february.FillCount);
    }

    // Missing marks withhold that month and later valuations while preserving supported earlier months and known cash/fees.
    [Fact]
    public void MissingMark_RetainsJanuaryAndExplicitlyWithholdsFebruary()
    {
        var fixture = MonthlyRunResultWriterTests.CreateFixture();
        var state = MissingMark(State(fixture.Identity.StrategyId, fixture.Config.TimeWindow.End!.Value));
        var result = WithState(fixture.Result, state) with { EconomicResultsComplete = false };
        var report = fixture.Writer.Create(fixture.Config, result, state.CutoffUtc, "group", [fixture.Identity]);

        Assert.Equal(.9m, report.Results[0].ReturnPct);
        var february = report.Results[1];
        Assert.Null(february.ReturnPct);
        Assert.Null(february.ClosingEquity);
        Assert.Null(february.UnrealizedProfit);
        Assert.Null(february.MaxDrawdownPct);
        Assert.Equal(950m, february.ClosingCash);
        Assert.Equal(11m, february.RealizedNetProfit);
        Assert.Equal(1m, february.Fees);
        Assert.Contains("Missing aligned", february.UnavailableReason);
        Assert.Equal(february.UnavailableReason, report.UnavailableReason);
    }

    // A failed selected symbol can hide candidates throughout the study and cannot be treated as an empty, profitable book.
    [Fact]
    public void FailedSelectedTicker_WithholdsEveryMonthFromTheDeclaredEvidenceBoundary()
    {
        var fixture = MonthlyRunResultWriterTests.CreateFixture();
        var state = State(fixture.Identity.StrategyId, fixture.Config.TimeWindow.End!.Value) with {
            UnavailableFromSession = new(2026, 1, 7), UnavailableReason = "Selected ticker SIVE has no verified history."
        };
        var result = WithState(fixture.Result, state) with { EconomicResultsComplete = false };
        var report = fixture.Writer.Create(fixture.Config, result, state.CutoffUtc, "group", [fixture.Identity]);

        Assert.All(report.Results, month => {
            Assert.Null(month.ReturnPct);
            Assert.Null(month.OpeningEquity);
            Assert.Null(month.ClosingEquity);
            Assert.Contains("SIVE", month.UnavailableReason);
        });
    }

    // Trimmed presentation collections must not remove the immutable ledger or alter older realized-only evidence.
    [Fact]
    public void SummaryRetention_KeepsStateAndEligibilityAndLeavesLegacyBasisUnchanged()
    {
        var fixture = MonthlyRunResultWriterTests.CreateFixture();
        var state = State(fixture.Identity.StrategyId, fixture.Config.TimeWindow.End!.Value);
        var eligibility = new[] { new BacktestHistoryEligibility("X", fixture.Identity.StrategyId, 260, 220, 239, null, "still_warming_at_study_end") };
        var originalJson = JsonSerializer.Serialize(fixture.Result, Json);
        var current = WithState(fixture.Result, state) with { HistoryEligibility = eligibility };
        var summary = BacktestArtifactProjector.Project(current, new("summary"));

        Assert.Same(state, summary.PortfolioState);
        Assert.Same(state, summary.StrategyResults.Single().PortfolioState);
        Assert.Same(eligibility, summary.HistoryEligibility);
        Assert.Empty(summary.CompletedTrades);
        Assert.Equal(2, summary.PortfolioState!.Fills.Count);
        var oldReport = fixture.Writer.Create(fixture.Config, fixture.Result, state.CutoffUtc, "legacy", [fixture.Identity]);
        Assert.All(oldReport.Results, month => Assert.Equal("realized_closed_trade_equity", month.ValuationBasis));
        Assert.All(oldReport.Results, month => Assert.Null(month.RealizedNetProfit));
        Assert.Equal(originalJson, JsonSerializer.Serialize(fixture.Result, Json));
    }

    // Source-session attribution shares the engine clock at month ends and Friday/Saturday availability boundaries.
    [Theory]
    [InlineData("2026-07-01T04:00:00Z", "2026-06-30")]
    [InlineData("2026-09-01T04:00:00Z", "2026-08-31")]
    [InlineData("2026-10-17T04:00:00Z", "2026-10-16")]
    public void PresentationSourceDate_UsesSharedNativeSessionPolicy(string receipt, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), NativeDailySessionLedger.SourceSessionDate(DateTimeOffset.Parse(receipt)));

    // The normal details endpoint copies terminal state and eligibility, preserving unknown marks and separate admission/closed counts.
    [Fact]
    public async Task JobDetails_RoundTripsOpenPendingEligibilityAndUnavailableValuations()
    {
        var fixture = MonthlyRunResultWriterTests.CreateFixture();
        var state = MissingMark(State(fixture.Identity.StrategyId, fixture.Config.TimeWindow.End!.Value));
        var result = WithState(fixture.Result, state) with { HistoryEligibility =
            [new("X", fixture.Identity.StrategyId, 260, 220, 239, null, "still_warming_at_study_end")] };
        var job = new PersistedJob { Id = Guid.NewGuid(), JobType = "backtest", Status = "completed",
            SnapshotJson = JsonSerializer.Serialize(new { result }, Json) };
        var jobs = new Mock<IDurableJobRepository>();
        jobs.Setup(repository => repository.GetJobAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        var service = new RunWorkflowService(jobs.Object, Mock.Of<IUniversePreviewRepository>(),
            Mock.Of<IStrategyIdentityResolver>(), Mock.Of<IWorkflowRunConfiguration>(), TimeProvider.System, new(32, 16));
        var details = await service.DetailsAsync(job.Id);
        var roundTrip = JsonSerializer.Deserialize<RunJobDetailsResponse>(JsonSerializer.Serialize(details, Json), Json)!;

        var portfolio = Assert.IsType<RunPortfolioStateResponse>(roundTrip.PortfolioState);
        Assert.Equal(1, portfolio.AdmittedOrderCount);
        Assert.Equal(0, portfolio.ClosedTradeCount);
        Assert.Equal(1, portfolio.FilledCandidateCount);
        Assert.Equal(950m, portfolio.Cash);
        Assert.Null(portfolio.Equity);
        Assert.Null(roundTrip.Result!.EndingCapital);
        Assert.Contains("Missing aligned", portfolio.UnavailableReason);
        Assert.Null(Assert.Single(portfolio.OpenPositions).MarkPrice);
        Assert.Equal(6, Assert.Single(portfolio.PendingOrders).RemainingQuantity);
        Assert.Equal("technical_exit", Assert.Single(portfolio.PendingExits).Reason);
        Assert.Equal("still_warming_at_study_end", Assert.Single(roundTrip.HistoryEligibility!).Status);
        Assert.Null(roundTrip.HistoryEligibility![0].FirstEligibleDecisionUtc);
    }

    // Synthetic observations include a partial exit, with actual fees charged once and a six-share remainder.
    private static BacktestPortfolioState State(string strategy, DateTimeOffset cutoff)
    {
        var january = new DateOnly(2026, 1, 30);
        var february = new DateOnly(2026, 2, 2);
        var known = DateTimeOffset.Parse("2026-02-03T05:00:00Z");
        return new(NativeDailySessionLedger.ValuationBasis, cutoff, 1, 1, 0,
            [new("candidate", strategy, "X", true, true, 10, 10, 1, DateTimeOffset.Parse("2026-01-31T05:00:00Z"), january),
             new("candidate", strategy, "X", false, false, 4, 13, 1, known, february)],
            [new("candidate", strategy, "X", 6, 10, 12, known, 12)],
            [new("candidate", strategy, "X", "exit", "Sell", "Market", 6, "PartiallyFilled", 0, known)],
            [new("candidate", strategy, "X", known, "technical_exit")],
            [new(january, 899, -1, 1, 110, 10, 1009, 110m / 1009m * 100m, 1, null),
             new(february, 950, 10, 2, 72, 12, 1022, 72m / 1022m * 100m, 1, null)])
            { StartUtc = DateTimeOffset.Parse("2026-01-07T05:00:00Z") };
    }

    // Only mark-dependent quantities become unknown; cash, realized profit and actual costs remain observed.
    private static BacktestPortfolioState MissingMark(BacktestPortfolioState state) => state with {
        OpenPositions = state.OpenPositions.Select(position => position with { MarkPrice = null, MarkTimestampUtc = null, UnrealizedProfit = null }).ToArray(),
        Sessions = [state.Sessions[0], state.Sessions[1] with { Equity = null, UnrealizedProfit = null,
            PositionValue = null, GrossExposurePct = null, UnavailableReason = "Missing aligned completed daily mark for X on 2026-02-02." }],
        UnavailableFromSession = new(2026, 2, 2), UnavailableReason = "Missing aligned completed daily mark for X on 2026-02-02."
    };

    // New results may admit a candidate that has no completed trade; the old fixture remains untouched.
    private static BacktestResult WithState(BacktestResult result, BacktestPortfolioState state) => result with {
        PortfolioState = state, AcceptedTradeCount = 1, CompletedTrades = [],
        StrategyResults = [result.StrategyResults.Single() with { AcceptedTradeCount = 1, CompletedTrades = [], PortfolioState = state }]
    };
}
