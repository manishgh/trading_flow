using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Context;
using TradingFlow.Data.Jobs;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Jobs;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

/// <summary>
/// Focused tests for Checkpoints UI-2, UI-3, and UI-4: plan-scoped SQLite lookup with fault isolation,
/// saved-plan monthly matrix builder (strictly monthly, non-pooled, batch/hash aware), and Razor/JS/CSS contracts.
/// </summary>
public sealed class Ui2RedesignTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 15, 0, 0, TimeSpan.Zero);

    // Creates a minimal saved plan fixture for testing monthly matrix projection without disk or database IO.
    private static BacktestPlanResponse CreatePlan(long planId, string runName, params StrategyReference[] strategies)
    {
        var universeId = Guid.NewGuid();
        var run = new StartBacktestRequest(
            ContractVersions.VersionOne,
            runName,
            universeId,
            strategies,
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 30, 21, 0, 0, TimeSpan.Zero),
            100_000m,
            0.5m,
            20m,
            10,
            "use_cache",
            "full",
            "idem-plan-test");
        var source = new UniversePreviewResponse(
            ContractVersions.VersionOne,
            universeId,
            "swing",
            Now.AddMinutes(-5),
            Now.AddMinutes(25),
            "sha-universe",
            [],
            []);
        var cohorts = new[] { new BacktestCohort("Group 1", ["AAPL", "MSFT"], 100_000m) };
        return new BacktestPlanResponse(planId, Now, run, cohorts, strategies.Length, 32, "Research screen cohort", source, "sha-cfg");
    }

    // Creates a BacktestJobSnapshot fixture with explicit creation timestamp, monthly rows, and diagnostics.
    private static BacktestJobSnapshot CreateJob(
        long planId,
        string cohortName,
        StrategyReference strategy,
        string strategyName,
        string status,
        IReadOnlyList<MonthlyRunResult> monthly,
        IReadOnlyDictionary<string, int>? rejections = null,
        DateTimeOffset? createdAtUtc = null,
        string? errorMessage = null,
        string? monthlyUnavailableReason = null)
    {
        var created = createdAtUtc ?? Now.AddMinutes(-10);
        BacktestResult? result = null;
        if (rejections is not null)
        {
            var dailyPnl = new DailyPnlSummary(20, 5, 3, 2, 1200m, 60m, 240m, 500m, new DateOnly(2026, 5, 10), -200m, new DateOnly(2026, 5, 12));
            var diagnostic = new StrategyDiagnosticReport(
                strategy.StrategyId,
                strategyName,
                100,
                20,
                5,
                60m,
                400m,
                -150m,
                2.6m,
                dailyPnl,
                [],
                new Dictionary<string, int>(),
                rejections,
                new Dictionary<string, IReadOnlyList<string>>(),
                []);
            result = new BacktestResult(
                $"run-{strategy.StrategyId}",
                "result.json",
                created,
                created.AddMinutes(5),
                100_000m,
                101_200m,
                1_200m,
                1.2m,
                0.06m,
                20,
                1.1m,
                null,
                100,
                20,
                5,
                15,
                3,
                2,
                [],
                [],
                null!,
                [],
                [diagnostic],
                [],
                []);
        }

        return new BacktestJobSnapshot(
            Guid.NewGuid(),
            $"run-{strategy.StrategyId}",
            "config.yaml",
            status,
            created,
            created.AddSeconds(5),
            created.AddMinutes(5),
            errorMessage,
            status,
            2,
            2,
            [],
            result,
            [],
            MonthlyResults: monthly,
            MonthlyUnavailableReason: monthlyUnavailableReason,
            Cohort: new BacktestCohortExecution(planId, cohortName, ["AAPL", "MSFT"], 100_000m, strategy));
    }

    [Fact]
    public void SavedPlanMatrixPreservesPerStrategyBatchHashValuationUnavailableReasonAndRejectionsWithoutPooling()
    {
        var longRef = new StrategyReference("swing_long_v1", "1.0.0", "aaaa1111bbbb2222cccc3333dddd4444");
        var shortRef = new StrategyReference("swing_short_v1", "1.0.0", "ffff9999eeee8888dddd7777cccc6666");
        var plan = CreatePlan(42, "may-oct-diagnostic", longRef, shortRef);

        var olderBatchTime = Now.AddHours(-3);
        var newerBatchTime = Now.AddHours(-1);

        var longOlderFailedJob = CreateJob(
            42,
            "Group 1",
            longRef,
            "Long Breakout V1",
            "failed",
            [],
            createdAtUtc: olderBatchTime,
            errorMessage: "Market data feed timed out during warmup.");

        var longNewerJob = CreateJob(
            42,
            "Group 1",
            longRef,
            "Long Breakout V1",
            "completed",
            [
                new MonthlyRunResult(longRef, "Group 1", "2026-06", 102_500m, 101_000m, -1.46m, 3, "marked_to_market_daily_close", PartialMonth: true, UnavailableReason: null, MaxDrawdownPct: 2.1m, StrategyName: "Long Breakout V1", OpenPositions: 1),
                new MonthlyRunResult(longRef, "Group 1", "2026-05", 100_000m, 102_500m, 2.50m, 4, "marked_to_market_daily_close", PartialMonth: false, UnavailableReason: null, MaxDrawdownPct: 0.9m, StrategyName: "Long Breakout V1", OpenPositions: 0)
            ],
            new Dictionary<string, int>
            {
                ["min_average_range_pct_not_met"] = 12,
                ["vix_above_long_limit"] = 28,
                ["earnings_blackout"] = 4
            },
            createdAtUtc: newerBatchTime);

        var shortJob = CreateJob(
            42,
            "Group 1",
            shortRef,
            "Short Breakdown V1",
            "completed",
            [
                new MonthlyRunResult(shortRef, "Group 1", "2026-05", 100_000m, null, null, 0, "realized_closed_trade_equity", PartialMonth: false, UnavailableReason: "Benchmark close missing", MaxDrawdownPct: null, StrategyName: "Short Breakdown V1", OpenPositions: 0)
            ],
            new Dictionary<string, int>
            {
                ["vix_below_short_limit"] = 19
            },
            createdAtUtc: newerBatchTime);

        // Unrelated plan job must never leak into plan 42's matrix.
        var otherPlanJob = CreateJob(
            999,
            "Group 1",
            longRef,
            "Other Plan Strategy",
            "completed",
            [new MonthlyRunResult(longRef, "Group 1", "2026-07", 100_000m, 110_000m, 10.0m, 5, "marked_to_market_daily_close", false, null)]);

        var matrix = SavedPlanMonthlyMatrixBuilder.Build(plan, [shortJob, otherPlanJob, longOlderFailedJob, longNewerJob]);

        Assert.Equal(ContractVersions.VersionOne, matrix.ContractVersion);
        Assert.Equal(42, matrix.PlanId);
        Assert.Equal("may-oct-diagnostic", matrix.RunName);
        Assert.Equal(["2026-05", "2026-06"], matrix.Months);
        Assert.Equal(3, matrix.Rows.Count);

        // Two batches of the same strategy/cohort stay separate and ordered newest CreatedAtUtc first.
        var longRows = matrix.Rows.Where(r => r.StrategyId == "swing_long_v1").ToArray();
        Assert.Equal(2, longRows.Length);
        Assert.Equal(newerBatchTime, longRows[0].CreatedAtUtc);
        Assert.Equal(olderBatchTime, longRows[1].CreatedAtUtc);
        Assert.Equal(longRef.ContentSha256, longRows[0].ContentSha256);
        Assert.Null(longRows[0].MonthlyUnavailableReason);
        Assert.Equal("Market data feed timed out during warmup.", longRows[1].MonthlyUnavailableReason);

        var longRow = longRows[0];
        Assert.Equal("Long Breakout V1", longRow.StrategyName);
        Assert.Equal(["2026-05", "2026-06"], longRow.Cells.Select(c => c.Month));
        Assert.Equal(2.50m, longRow.Cells[0].ReturnPct);
        Assert.False(longRow.Cells[0].PartialMonth);
        Assert.Equal("marked_to_market_daily_close", longRow.Cells[0].ValuationBasis);
        Assert.Equal(-1.46m, longRow.Cells[1].ReturnPct);
        Assert.True(longRow.Cells[1].PartialMonth);
        Assert.Equal(1, longRow.Cells[1].OpenPositions);
        Assert.Equal(["vix_above_long_limit", "min_average_range_pct_not_met", "earnings_blackout"], longRow.TopRejections.Select(x => x.Code));
        Assert.Equal([28, 12, 4], longRow.TopRejections.Select(x => x.Count));

        var shortRow = Assert.Single(matrix.Rows, r => r.StrategyId == "swing_short_v1");
        Assert.Equal(shortRef.ContentSha256, shortRow.ContentSha256);
        var unavailableCell = Assert.Single(shortRow.Cells);
        Assert.Null(unavailableCell.ReturnPct);
        Assert.Equal("realized_closed_trade_equity", unavailableCell.ValuationBasis);
        Assert.Equal("Benchmark close missing", unavailableCell.UnavailableReason);
        Assert.Equal("vix_below_short_limit", Assert.Single(shortRow.TopRejections).Code);

        // Oct 8 governance rule: matrix contracts must never expose accumulated or pooled return fields.
        var forbiddenTokens = new[] { "TotalReturn", "Cumulative", "Pooled", "AverageReturn", "CombinedReturn" };
        foreach (var type in new[] { typeof(SavedPlanMonthlyMatrixResponse), typeof(SavedPlanMonthlyMatrixRow), typeof(SavedPlanMonthlyMatrixCell) })
        {
            foreach (var prop in type.GetProperties())
            {
                Assert.DoesNotContain(forbiddenTokens, token => prop.Name.Contains(token, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public async Task SqlitePlanJobQueryFiltersByPlanIdAndIsolatesMalformedLegacyRequestJson()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tf-ui2-plan-query-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            var factory = new InlineContextFactory(options);
            await using (var setup = new TradingFlowDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
            }

            var repo = new SqliteJobRepository(factory);
            var olderPlan42Id = Guid.NewGuid();
            var newerPlan42Id = Guid.NewGuid();
            var otherPlanId = Guid.NewGuid();
            var malformedJsonId = Guid.NewGuid();
            var incompleteSchemaId = Guid.NewGuid();
            var nullJsonId = Guid.NewGuid();

            Assert.True(await repo.TryEnqueueBatchAsync(
                [
                    BuildPersistedJob(olderPlan42Id, "backtest", Now.AddMinutes(-20), """{"schemaVersion":1,"files":[{"path":"a.yaml","length":1,"sha256":"abc"}],"workflow":{"cohort":{"planId":42}}}"""),
                    BuildPersistedJob(newerPlan42Id, "backtest", Now.AddMinutes(-5), """{"schemaVersion":1,"files":[{"path":"b.yaml","length":1,"sha256":"def"}],"workflow":{"cohort":{"planId":42}}}"""),
                    BuildPersistedJob(otherPlanId, "backtest", Now.AddMinutes(-10), """{"schemaVersion":1,"files":[{"path":"c.yaml","length":1,"sha256":"ghi"}],"workflow":{"cohort":{"planId":999}}}"""),
                    BuildPersistedJob(malformedJsonId, "backtest", Now.AddMinutes(-15), "{not-valid-json"),
                    BuildPersistedJob(incompleteSchemaId, "backtest", Now.AddMinutes(-12), """{"schemaVersion":1,"files":[]}"""),
                    BuildPersistedJob(nullJsonId, "backtest", Now.AddMinutes(-11), "null")
                ],
                maximumNonTerminalJobs: 20,
                CancellationToken.None));

            var plan42Jobs = await repo.GetJobsForPlanAsync("backtest", 42, CancellationToken.None);
            Assert.Equal([newerPlan42Id, olderPlan42Id], plan42Jobs.Select(job => job.Id));

            // Light JsonDocument parser also isolates malformed / legacy / null payloads without throwing.
            Assert.Equal(42L, BacktestJobService.TryReadPlanId("""{"workflow":{"cohort":{"planId":42}}}"""));
            Assert.Null(BacktestJobService.TryReadPlanId("{not-valid-json"));
            Assert.Null(BacktestJobService.TryReadPlanId("null"));
            Assert.Null(BacktestJobService.TryReadPlanId("""{"schemaVersion":1,"files":[]}"""));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void DeskUi2ExposesAlwaysVisibleReadinessCompactPausedPreparationAndCascadeSafeLayout()
    {
        var layout = WebFile("Pages/Shared/_Layout.cshtml");
        var stylesSectionIdx = layout.IndexOf("@await RenderSectionAsync(\"Styles\", required: false)", StringComparison.Ordinal);
        var ui2CssIdx = layout.IndexOf("ui2-foundation.css", StringComparison.Ordinal);
        Assert.True(stylesSectionIdx >= 0 && stylesSectionIdx < ui2CssIdx,
            "_Layout.cshtml must render page Styles section strictly before ui2-foundation.css.");

        var desk = WebFile("Pages/Desk.cshtml");
        var focusStart = desk.IndexOf("id=\"watch-focus-content\"", StringComparison.Ordinal);
        var orderHelpStart = desk.IndexOf("id=\"watch-order-help\"", StringComparison.Ordinal);
        var exitHelpStart = desk.IndexOf("id=\"watch-exit-help\"", StringComparison.Ordinal);
        var holdingStart = desk.IndexOf("id=\"watch-holding-block\"", StringComparison.Ordinal);
        var strategyStart = desk.IndexOf("id=\"watch-strategy-block\"", StringComparison.Ordinal);
        var evidenceStart = desk.IndexOf("<details id=\"watch-evidence\"", StringComparison.Ordinal);

        Assert.True(focusStart >= 0 && focusStart < orderHelpStart && orderHelpStart < exitHelpStart &&
                    exitHelpStart < holdingStart && holdingStart < strategyStart && strategyStart < evidenceStart,
            "Order hints, holding, and strategy readiness blocks must sit inside #watch-focus-content and outside <details id=\"watch-evidence\">.");
        Assert.Contains("<summary>More technicals &amp; group tools</summary>", desk);
        Assert.Contains("@section Styles", desk);

        var prepJs = WebFile("wwwroot/js/preparation-summary.js");
        foreach (var token in new[]
                 {
                     "dataset.compact", "Saved preparation (paused)", "Previous preparation",
                     "Preparation not configured", "hasActiveOrFailedStep", "in progress", "failed",
                     "cancelled", "unavailable", "worker?.state !== 'available'", "12 * 60 * 60 * 1000", "data-preparation-"
                 })
        {
            Assert.Contains(token, prepJs);
        }

        var deskJs = WebFile("wwwroot/js/watch-desk.js");
        foreach (var token in new[]
                 {
                     "function humanizeCheck(", "(!passed && (checkLabels[normalizedCode] || checkLabels[normalizedGate]))",
                     "min_average_range_pct_not_met", "regime_price_below_sma",
                     "vix_above_long_limit", "sector_not_in_top_n", "earnings_blackout", "insufficient_history",
                     "check.gateName", "check.rejectionCode", "check.inputs", "ArrowDown", "ArrowUp", "selectStock("
                 })
        {
            Assert.Contains(token, deskJs);
        }

        var css = WebFile("wwwroot/css/ui2-foundation.css");
        foreach (var token in new[]
                 {
                     ".preparation-steps li", "white-space: normal", "[data-preparation-summary][data-compact=\"true\"]",
                     "@media (min-width: 1200px)", "html body.workspace-app .watch-desk .watch-grid",
                     "minmax(520px, 1.18fr) minmax(430px, 1fr)", "position: sticky", ".watch-strategy-check-item"
                 })
        {
            Assert.Contains(token, css);
        }
    }

    [Fact]
    public void StrategyLabUi3WiresDirectionPillsWindowPresetsResearchScreenAutoGroupAndMonthlyMatrix()
    {
        var lab = WebFile("Pages/Backtests.cshtml");
        foreach (var token in new[]
                 {
                     "data-direction=\"@strategy.Definition.Direction.ToString().ToLowerInvariant()\"",
                     "data-strategy-select=\"long\"", "data-strategy-select=\"short\"", "data-strategy-select=\"clear\"",
                     "2026-05-01T00:00", "2026-10-30T21:00", "Clear dates",
                     "id=\"saved-plan-matrix-panel\"", "data-current-plan-id=\"@(job?.Cohort?.PlanId)\"",
                     "@section Styles"
                 })
        {
            Assert.Contains(token, lab);
        }

        var workflowJs = WebFile("wwwroot/js/run-workflow.js");
        foreach (var token in new[]
                 {
                     "/monthly-matrix", "research_screen", "groupSize.value = '1000'",
                     "▲ +", "(partial)", "/Backtests?jobId=", "wireStrategyDirectionHelpers", "wireWindowPresetHelpers",
                     "hideSavedPlanMatrix", "row.contentSha256", "row.createdAtUtc", "row.monthlyUnavailableReason",
                     "lab-matrix-reason"
                 })
        {
            Assert.Contains(token, workflowJs);
        }

        var endpoints = WebFile("BacktestPlanEndpoints.cs");
        Assert.Contains("/backtests/plans/{id:long}/monthly-matrix", endpoints);
        Assert.Contains("Results.NotFound()", endpoints);
    }

    [Fact]
    public void CatalystsAndOptionsUi4HeadersExposeEyebrowAndContextualDeskLinks()
    {
        var news = WebFile("Pages/News.cshtml");
        Assert.Contains("<span class=\"eyebrow\">CATALYSTS &amp; OPTIONS</span>", news);
        Assert.Contains("asp-route-symbol=\"@Model.Ticker\">Open Desk ↗</a>", news);
        Assert.Contains("@section Styles", news);

        var earnings = WebFile("Pages/Earnings.cshtml");
        Assert.Contains("<span class=\"eyebrow\">CATALYSTS &amp; OPTIONS</span>", earnings);
        Assert.Contains("asp-page=\"/Desk\">Open Desk ↗</a>", earnings);
        Assert.Contains("@section Styles", earnings);
    }

    // Creates a minimal queued PersistedJob row for testing SQLite plan-scoped filtering.
    private static PersistedJob BuildPersistedJob(Guid id, string jobType, DateTimeOffset createdAt, string requestJson) => new()
    {
        Id = id,
        JobType = jobType,
        RunName = $"job-{id:N}",
        ConfigPath = "config.yaml",
        RequestJson = requestJson,
        SnapshotJson = "{}",
        Status = "queued",
        CreatedAt = createdAt,
        CreatedAtUtcTicks = createdAt.UtcTicks
    };

    // Reads a source file from src/TradingFlow.Web relative to the active worktree root.
    private static string WebFile(string relativePath) =>
        File.ReadAllText(Path.Combine(TestRepository.FindRoot(), "src", "TradingFlow.Web", relativePath));

    private sealed class InlineContextFactory(DbContextOptions<TradingFlowDbContext> options) : IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);
    }
}
