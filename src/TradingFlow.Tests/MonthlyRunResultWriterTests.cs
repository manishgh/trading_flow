using System.Text.Json;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class MonthlyRunResultWriterTests
{
    // Monthly percentages must carry the preceding close as the next month's opening, not reuse initial capital.
    [Fact]
    public void FullTrades_ProduceIndependentMonthlyRowsBeforeTrimming()
    {
        var fixture = CreateFixture();
        var report = fixture.Writer.Create(fixture.Config, fixture.Result, fixture.Config.TimeWindow.End!.Value,
            "Volatile group 1", [fixture.Identity]);

        Assert.Null(report.UnavailableReason);
        Assert.Equal(2, report.Results.Count);
        var january = report.Results[0];
        var february = report.Results[1];
        Assert.Equal(fixture.Identity, january.Strategy);
        Assert.Equal("Volatile group 1", january.CohortName);
        Assert.Equal("2026-01", january.Month);
        Assert.Equal(1000m, january.OpeningEquity);
        Assert.Equal(1100m, january.ClosingEquity);
        Assert.Equal(10m, january.ReturnPct);
        Assert.Equal(1100m, february.OpeningEquity);
        Assert.Equal(990m, february.ClosingEquity);
        Assert.Equal(-10m, february.ReturnPct);
        Assert.All(report.Results, row =>
        {
            Assert.Equal("realized_closed_trade_equity", row.ValuationBasis);
            Assert.True(row.PartialMonth);
            Assert.Equal(1, row.ClosedTrades);
        });
    }

    // Event fills cannot be mislabeled as native-daily realized returns when their month-end marks are missing.
    [Theory]
    [InlineData(ExecutionReplayPolicy.DailyRthSipPortfolioV1)]
    [InlineData(ExecutionReplayPolicy.DailyRthFiveMinuteV1)]
    public void EventPolicy_WithholdsFiguresAndRetainsExactIdentity(string policy)
    {
        var fixture = CreateFixture();
        var config = fixture.Config with { Engine = fixture.Config.Engine with { ExecutionReplayPolicyId = policy } };
        var report = fixture.Writer.Create(config, fixture.Result, config.TimeWindow.End!.Value,
            "Volatile group 2", [fixture.Identity]);

        Assert.NotNull(report.UnavailableReason);
        Assert.Equal(2, report.Results.Count);
        Assert.All(report.Results, row =>
        {
            Assert.Equal(fixture.Identity, row.Strategy);
            Assert.Null(row.OpeningEquity);
            Assert.Null(row.ClosingEquity);
            Assert.Null(row.ReturnPct);
            Assert.Null(row.ClosedTrades);
            Assert.NotNull(row.UnavailableReason);
        });
    }

    // Removing trades must produce an explicit missing-evidence result, never a fabricated flat month.
    [Fact]
    public void TrimmedTrades_WithholdMonthlyResults()
    {
        var fixture = CreateFixture();
        var trimmed = fixture.Result with
        {
            StrategyResults = [fixture.Result.StrategyResults[0] with { CompletedTrades = [] }]
        };
        var report = fixture.Writer.Create(fixture.Config, trimmed, fixture.Config.TimeWindow.End!.Value,
            "Volatile group 1", [fixture.Identity]);

        Assert.Contains("trimmed UI summary", report.UnavailableReason);
        Assert.All(report.Results, row => Assert.Null(row.ReturnPct));
    }

    // A different requested hash must not silently label results with the wrong strategy version.
    [Fact]
    public void IdentityMismatch_WithholdsFigures()
    {
        var fixture = CreateFixture();
        var requested = fixture.Identity with { ContentSha256 = new string('a', 64) };
        var report = fixture.Writer.Create(fixture.Config, fixture.Result, fixture.Config.TimeWindow.End!.Value,
            "Volatile group 1", [requested]);
        Assert.Contains("identity differs", report.UnavailableReason);
        Assert.All(report.Results, row =>
        {
            Assert.Equal(requested, row.Strategy);
            Assert.Null(row.ReturnPct);
        });
    }

    // Persist the compact report, original capital and saved cohort independently of the UI summary; never replace an existing artifact.
    [Fact]
    public async Task Report_IsWrittenOnceInsideTheAttemptDirectory()
    {
        var fixture = CreateFixture();
        var root = Path.Combine(Path.GetTempPath(), "tradingflow-monthly-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = fixture.Result with { ResultPath = Path.Combine(root, "result.json") };
            var cohort = new BacktestCohortExecution(47, "Volatile group 1", fixture.Config.Tickers,
                fixture.Config.Portfolio.StartingCapital, fixture.Identity);
            var report = await fixture.Writer.WriteAsync(fixture.Config, result, fixture.Config.TimeWindow.End!.Value,
                "Volatile group 1", [fixture.Identity], default, cohort);
            var path = Path.Combine(root, "result.monthly.json");
            var stored = JsonSerializer.Deserialize<MonthlyRunResultReport>(await File.ReadAllTextAsync(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(stored);
            Assert.Equal(report.Results, stored.Results);
            Assert.Equal(fixture.Config.Portfolio.StartingCapital, stored.StartingCapital);
            Assert.NotNull(stored.Cohort);
            Assert.Equal(cohort.PlanId, stored.Cohort.PlanId);
            Assert.Equal(cohort.CohortName, stored.Cohort.CohortName);
            Assert.Equal(cohort.StartingCapital, stored.Cohort.StartingCapital);
            Assert.Equal(cohort.Strategy, stored.Cohort.Strategy);
            Assert.Equal(cohort.Symbols, stored.Cohort.Symbols);
            await Assert.ThrowsAnyAsync<IOException>(() => fixture.Writer.WriteAsync(fixture.Config, result,
                fixture.Config.TimeWindow.End!.Value, "Volatile group 1", [fixture.Identity], default));
            Assert.Equal(2, JsonSerializer.Deserialize<MonthlyRunResultReport>(await File.ReadAllTextAsync(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Results.Count);
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetDirectoryName(root), ignoreCase: true);
            Directory.Delete(root, recursive: true);
        }
    }

    // Use the same catalog/runtime metadata and completed-source-date convention as the existing daily accounting tests.
    internal static (MonthlyRunResultWriter Writer, BacktestRunConfig Config, BacktestResult Result, StrategyReference Identity) CreateFixture()
    {
        var repo = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var artifacts = new StrategyArtifactCatalog(repo, Path.Combine(repo, "configs", "strategy-catalog.json"), reader);
        var validator = new StrategyRunArtifactValidator(reader, artifacts);
        var strategyPath = Path.Combine(repo, "configs", "backtest", "strategies", "swing-connors-rsi2-sma5-risk-managed.bt-v6.yaml");
        var runtime = validator.ReadAndValidateRuntimeArtifact(strategyPath, StrategySelectionMode.Backtest);
        var start = DateTimeOffset.Parse("2026-01-07T05:00:00Z");
        var end = DateTimeOffset.Parse("2026-02-03T05:00:00Z");
        var firstExit = DateTimeOffset.Parse("2026-02-01T05:00:00Z");
        var trades = new[] { Trade(firstExit, 100m), Trade(firstExit.AddDays(1), -110m) };
        var strategy = new StrategyBacktestResult(runtime.Definition.StrategyId, "RSI", "fixture", 1000, 990,
            -10, -1, 0, 2, 1, 2, 2, 0, 1, 1, trades);
        var config = reader.ReadBacktestRun(Path.Combine(repo, "configs", "backtest", "swing-backtest-profile.yaml"));
        config = config with
        {
            Engine = config.Engine with { ExecutionReplayPolicyId = null },
            Strategies = [strategyPath],
            Portfolio = config.Portfolio with { StartingCapital = 1000 },
            TimeWindow = new("fixed", 0, start, end)
        };
        var validation = new BacktestValidationReport(new(false, 0, null, []), [], new(false, "", null, []),
            new(0, 0, 0, 0, 0, []), new("fixture", null, "none", false, false, []));
        var result = new BacktestResult("fixture", "result.json", start, end, 1000, 990, -10, -1, 0, 2, 1, null,
            0, 2, 2, 0, 1, 1, [strategy], [], validation, trades, [], [], []);
        return (new(validator), config, result,
            new(runtime.Identity.StrategyId, runtime.Identity.SemanticVersion, runtime.Identity.ContentSha256));
    }

    // Profit values are already net of costs; receipt time maps to the previous New York daily source session.
    private static BacktestTrade Trade(DateTimeOffset exit, decimal profit) =>
        new("MSFT", "RSI", "long", exit.AddDays(-2), exit, 10, 10, 11, 9, 14, "technical_exit", profit, 0, profit);
}
