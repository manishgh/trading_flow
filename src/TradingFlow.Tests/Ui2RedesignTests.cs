using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Context;
using TradingFlow.Data.Jobs;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Wishlists;

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
        var chartWindowStart = desk.IndexOf("id=\"watch-chart-window\"", StringComparison.Ordinal);
        var technicalsStart = desk.IndexOf("id=\"watch-technicals-block\"", StringComparison.Ordinal);
        var evidenceStart = desk.IndexOf("<details id=\"watch-evidence\"", StringComparison.Ordinal);

        Assert.True(focusStart >= 0 && focusStart < chartWindowStart && chartWindowStart < technicalsStart &&
                    technicalsStart < orderHelpStart && orderHelpStart < exitHelpStart &&
                    exitHelpStart < holdingStart && holdingStart < strategyStart && strategyStart < evidenceStart,
            "Chart window (with embedded completed-1d indicators), order hints, holding, and strategy readiness blocks must sit inside #watch-focus-content and outside <details id=\"watch-evidence\">.");
        Assert.Contains("<summary>Group &amp; catalyst tools</summary>", desk);
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
                     "minmax(260px, 3fr) minmax(560px, 7fr)", "position: sticky", ".watch-strategy-check-item"
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

    [Fact]
    public async Task CatalystOptionsAdrComputesCompleted20SessionAdrAndFailsClosedOnMissingStaleOrFailedSchedule()
    {
        var session = new DateOnly(2026, 11, 19);
        var now = new DateTimeOffset(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);
        var schedules = BuildWeekdaySchedules(session.AddDays(-65), session.AddDays(2));

        // 22 completed trading-day 1d bars with trending closes (100 -> 121) and constant $4 range.
        // Canonical SignalGenerator formula: mean(High - Low over latest 20 sessions) / latestClose * 100 = 4 / 121 * 100 = 3.31%.
        var trendingBars = BuildTrendingDailyBars("NVDA", 22, session, startClose: 100m, step: 1m, range: 4m);
        var expectedCanonical = TradingFlow.Engine.Strategies.SignalGenerator.ComputeAverageRangeAndDollarVolume(trendingBars, trendingBars.Length - 1, 20).AverageRangePct;
        var computed = CatalystOptionsAdrService.ComputeAdr20Pct(trendingBars, schedules, now, scheduleSucceeded: true);
        Assert.Equal(Math.Round(expectedCanonical!.Value, 2), computed.Adr20Pct);
        Assert.Equal(3.31m, computed.Adr20Pct);
        Assert.Equal(session, computed.AsOfSessionDate);
        Assert.Null(computed.UnavailableReason);
        Assert.Equal(Math.Round(7.00m / 3.31m, 2), CatalystOptionsAdrService.ComputeImpliedMoveToAdrRatio(7.00m, computed.Adr20Pct));
        Assert.Null(CatalystOptionsAdrService.ComputeImpliedMoveToAdrRatio(null, computed.Adr20Pct));
        Assert.Null(CatalystOptionsAdrService.ComputeImpliedMoveToAdrRatio(7.00m, null));

        // Corrupt D-1 bar (Close <= 0) must fail closed rather than dropping D-1 and computing from stale D-21..D-2.
        var badLatestClose = trendingBars.ToArray();
        badLatestClose[^1] = badLatestClose[^1] with { Close = 0m };
        var badCloseResult = CatalystOptionsAdrService.ComputeAdr20Pct(badLatestClose, schedules, now, scheduleSucceeded: true);
        Assert.Null(badCloseResult.Adr20Pct);
        Assert.NotNull(badCloseResult.UnavailableReason);

        // Missing intermediate session inside the 20-session schedule window must fail closed rather than bridging with D-21.
        var withGap = trendingBars.Where((_, idx) => idx != trendingBars.Length - 5).ToArray();
        var gapResult = CatalystOptionsAdrService.ComputeAdr20Pct(withGap, schedules, now, scheduleSucceeded: true);
        Assert.Null(gapResult.Adr20Pct);
        Assert.NotNull(gapResult.UnavailableReason);

        // Fewer than 20 completed bars must fail closed.
        var nineteenBars = BuildConstantRangeDailyBars("NVDA", 19, session, close: 100m, range: 4m);
        var tooFew = CatalystOptionsAdrService.ComputeAdr20Pct(nineteenBars, schedules, now, scheduleSucceeded: true);
        Assert.Null(tooFew.Adr20Pct);
        Assert.NotNull(tooFew.UnavailableReason);

        // 19 completed bars + 1 uncompleted bar (today's 1d bar not yet closed at 15:00 UTC) must still fail closed.
        var withUncompleted = nineteenBars.Concat([
            new OhlcvBar("NVDA", new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero), "1d", 100m, 110m, 95m, 105m, 1_000_000m)
        ]).ToArray();
        var uncompletedResult = CatalystOptionsAdrService.ComputeAdr20Pct(withUncompleted, schedules, now, scheduleSucceeded: true);
        Assert.Null(uncompletedResult.Adr20Pct);

        // 19 completed bars + 1 future-known bar (KnownAtUtc > now) must also fail closed.
        var withFutureKnown = nineteenBars.Concat([
            new OhlcvBar("NVDA", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), "1d", 100m, 110m, 95m, 105m, 1_000_000m, KnownAtUtc: now.AddMinutes(5))
        ]).ToArray();
        Assert.Null(CatalystOptionsAdrService.ComputeAdr20Pct(withFutureKnown, schedules, now, scheduleSucceeded: true).Adr20Pct);

        // Failed schedule lookup must fail closed even when 22 valid bars are present.
        var failedSchedule = CatalystOptionsAdrService.ComputeAdr20Pct(trendingBars, schedules: null, now, scheduleSucceeded: false);
        Assert.Null(failedSchedule.Adr20Pct);
        Assert.Equal("Exchange schedule unavailable", failedSchedule.UnavailableReason);

        // Stale history ending D-2 against authoritative schedule expecting D-1 must fail closed.
        var staleBars = BuildConstantRangeDailyBars("NVDA", 22, session.AddDays(-1), close: 100m, range: 4m);
        var stale = CatalystOptionsAdrService.ComputeAdr20Pct(staleBars, schedules, now, scheduleSucceeded: true);
        Assert.Null(stale.Adr20Pct);

        // Schedule loader fails closed on empty or throwing IMarketSessionScheduleProvider.
        Assert.False((await CatalystOptionsAdrService.TryLoadAdrSchedulesAsync(new FakeAdrScheduleProvider(returnEmpty: true), now, CancellationToken.None)).Succeeded);
        Assert.False((await CatalystOptionsAdrService.TryLoadAdrSchedulesAsync(new FakeAdrScheduleProvider(throwOnLoad: true), now, CancellationToken.None)).Succeeded);

        // Symbol parser defaults to SPY,QQQ and enforces 1-6 valid US equity tokens.
        Assert.True(CatalystOptionsAdrService.TryParseSymbols(null, out var defaults, out _));
        Assert.Equal(["SPY", "QQQ"], defaults);
        Assert.True(CatalystOptionsAdrService.TryParseSymbols("nvda, spy, NVDA, aapl", out var deduped, out _));
        Assert.Equal(["NVDA", "SPY", "AAPL"], deduped);
        Assert.False(CatalystOptionsAdrService.TryParseSymbols("A,B,C,D,E,F,G", out _, out _));
        Assert.False(CatalystOptionsAdrService.TryParseSymbols("BAD$SYM", out _, out _));
    }

    [Fact]
    public void SavedPlanMatrixProjectsCappedSampleTradesWithNyExitMonthAndRMultiple()
    {
        var strategyRef = new StrategyReference("swing_long_v1", "1.0.0", "aaaa1111bbbb2222cccc3333dddd4444");
        var plan = CreatePlan(77, "drawer-sample-check", strategyRef);
        var trades = new List<BacktestTrade>();
        // Add 30 trades for swing_long_v1 plus 1 unrelated strategy trade to verify strategy filter, NY month tagging, and 25-trade cap.
        for (var i = 0; i < 30; i++)
        {
            var exitDate = new DateOnly(2026, 6, 1).AddDays(i % 20);
            var exitStamp = new DateTimeOffset(exitDate.ToDateTime(new TimeOnly(20, 0)), TimeSpan.Zero);
            trades.Add(new BacktestTrade(
                Ticker: $"T{i:00}",
                StrategyName: "Long Breakout V1",
                Direction: "long",
                EntryTimestamp: exitStamp.AddDays(-2),
                ExitTimestamp: exitStamp,
                ShareQuantity: 10,
                EntryPrice: 100m,
                ExitPrice: 106m,
                StopLossPrice: 97m,
                TakeProfitPrice: 109m,
                ExitReason: "target_hit",
                GrossProfit: 60m,
                Fees: 1m,
                NetProfit: 59m,
                StrategyId: strategyRef.StrategyId)
            {
                ExitFillTiming = new BacktestFillTiming(exitDate, exitStamp, "completed_bar", exitStamp, exitStamp)
            });
        }

        // Cross-month UTC vs NY boundary: 2026-06-01 01:30 UTC is 2026-05-31 21:30 NY -> must stamp "2026-05" on intraday valuation.
        var boundaryExitUtc = new DateTimeOffset(2026, 6, 1, 1, 30, 0, TimeSpan.Zero);
        trades.Add(new BacktestTrade(
            Ticker: "NVDA",
            StrategyName: "Long Breakout V1",
            Direction: "long",
            EntryTimestamp: boundaryExitUtc.AddHours(-4),
            ExitTimestamp: boundaryExitUtc,
            ShareQuantity: 5,
            EntryPrice: 200m,
            ExitPrice: 210m,
            StopLossPrice: 195m,
            TakeProfitPrice: 215m,
            ExitReason: "session_close",
            GrossProfit: 50m,
            Fees: 0.5m,
            NetProfit: 49.5m,
            StrategyId: strategyRef.StrategyId));

        // Unrelated strategy trade must never leak into swing_long_v1 sample trades.
        trades.Add(new BacktestTrade(
            Ticker: "OTHER",
            StrategyName: "Other Strategy",
            Direction: "short",
            EntryTimestamp: Now.AddDays(-2),
            ExitTimestamp: Now.AddDays(-1),
            ShareQuantity: 10,
            EntryPrice: 50m,
            ExitPrice: 48m,
            StopLossPrice: 52m,
            TakeProfitPrice: 46m,
            ExitReason: "stop",
            GrossProfit: 20m,
            Fees: 0m,
            NetProfit: 20m,
            StrategyId: "different_strategy"));

        var portfolioState = new BacktestPortfolioState(
            ValuationBasis: TradingFlow.Backtesting.IntradayPortfolioExecution.ValuationBasis,
            CutoffUtc: new DateTimeOffset(2026, 6, 30, 21, 0, 0, TimeSpan.Zero),
            AdmittedOrderCount: 31,
            FilledCandidateCount: 31,
            ClosedTradeCount: 31,
            Fills: [],
            OpenPositions: [],
            PendingOrders: [],
            PendingExits: [],
            Sessions: [])
        {
            StartUtc = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var strategyResult = new StrategyBacktestResult(
            strategyRef.StrategyId, "Long Breakout V1", "research", 100_000m, 101_000m, 1_000m, 1.0m, 0.05m, 20, 0.5m,
            31, 31, 0, 31, 0, trades)
        {
            PortfolioState = portfolioState
        };
        var result = new BacktestResult(
            "drawer-run", "result.json", Now.AddMinutes(-10), Now, 100_000m, 101_000m, 1_000m, 1.0m, 0.05m, 20, 0.5m,
            null, 100, 31, 31, 0, 31, 0, [strategyResult], [], null!, trades, [], [], []);
        var job = new BacktestJobSnapshot(
            Guid.NewGuid(), "drawer-run", "config.yaml", "completed", Now.AddMinutes(-10), Now.AddMinutes(-9), Now,
            null, "completed", 2, 2, [], result, [],
            MonthlyResults: [new MonthlyRunResult(strategyRef, "Group 1", "2026-06", 100_000m, 101_000m, 1.0m, 30, portfolioState.ValuationBasis, false, null)],
            Cohort: new BacktestCohortExecution(77, "Group 1", ["NVDA"], 100_000m, strategyRef));

        var matrix = SavedPlanMonthlyMatrixBuilder.Build(plan, [job]);
        var row = Assert.Single(matrix.Rows);
        Assert.NotNull(row.SampleTrades);
        Assert.Equal(25, row.SampleTrades!.Count);
        Assert.DoesNotContain(row.SampleTrades, t => t.Ticker == "OTHER");
        Assert.True(row.SampleTrades.Zip(row.SampleTrades.Skip(1)).All(pair => pair.First.ExitTimestampUtc >= pair.Second.ExitTimestampUtc));
        Assert.All(row.SampleTrades, t => Assert.Equal(2.0m, t.RMultiple));

        // Verify NY boundary trade stamps 2026-05 when included as newest trade.
        var boundaryOnlyResult = result with { CompletedTrades = [trades.Single(t => t.Ticker == "NVDA")] };
        var boundaryMatrix = SavedPlanMonthlyMatrixBuilder.Build(plan, [job with { Result = boundaryOnlyResult }]);
        var boundarySample = Assert.Single(boundaryMatrix.Rows[0].SampleTrades!);
        Assert.Equal("2026-05", boundarySample.Month);
        Assert.Equal(2.0m, boundarySample.RMultiple);

        // Governance check: SavedPlanMonthlyTradeSample never exposes pooled/accumulated return fields.
        var forbiddenTokens = new[] { "TotalReturn", "Cumulative", "Pooled", "AverageReturn", "CombinedReturn" };
        foreach (var prop in typeof(SavedPlanMonthlyTradeSample).GetProperties())
        {
            Assert.DoesNotContain(forbiddenTokens, token => prop.Name.Contains(token, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void DeskTicketPlanRefusesShortStrategiesInsteadOfInvertingToLong()
    {
        var shortStrategy = new TradingFlow.Domain.Strategies.StrategyDefinition(
            StrategyId: "short_breakdown_v1",
            StrategyName: "Short Breakdown V1",
            Source: "research",
            Version: 1,
            Timeframe: "1d",
            Direction: "short",
            EntryRules: null!,
            Confluence: null!,
            ExitRules: new TradingFlow.Domain.Strategies.ExitRules(1m, 2.5m, 120m, false, 1m, 1m, false, false, false, 0),
            Execution: null!,
            Session: null!);
        var plan = DeskTicketPlanService.Build(shortStrategy, 150m, [], [], [], [], 250m);
        Assert.Null(plan.Stop);
        Assert.Null(plan.Target);
        Assert.Equal("Order ticket planning supports long entries only.", plan.StopNote);
        Assert.Equal(250m, plan.RiskBudget);
    }

    [Fact]
    public void Ui5ContractsCoverCompactScannerChartLevelsRiskTicketLabDrawerAndCatalystsOpra()
    {
        // 1. 40% left compact 6-col Desk scanner (keeps News col 6!) + 60% right chart window with embedded basic indicators & pinned OPRA
        var desk = WebFile("Pages/Desk.cshtml");
        var optionsPartial = WebFile("Pages/Shared/_OptionsSentimentPanel.cshtml");
        var chartPartial = WebFile("Pages/Shared/_StockChart.cshtml");
        Assert.Contains("id=\"watch-density-toggle\"", desk);
        Assert.Contains("data-scanner-density=\"compact\"", desk);
        Assert.Contains("Mid · Day %", desk);
        Assert.Contains("RVOL · ATR", desk);
        Assert.Contains("class=\"watch-chart-window\"", desk);
        Assert.Contains("class=\"watch-strategy-group\"", desk);
        Assert.Contains("id=\"watch-strategy-refresh\"", desk);
        Assert.DoesNotContain("data-chart-inline-studies", chartPartial);
        Assert.Contains("class=\"stock-chart-more\"", chartPartial);
        Assert.DoesNotContain("id=\"desk-options-selected\"", optionsPartial);
        var chartIdx = desk.IndexOf("<partial name=\"_StockChart\"", StringComparison.Ordinal);
        var selectedOpraIdx = desk.IndexOf("id=\"desk-options-selected\"", StringComparison.Ordinal);
        var orderHelpIdx = desk.IndexOf("id=\"watch-order-help\"", StringComparison.Ordinal);
        Assert.True(chartIdx >= 0 && chartIdx < selectedOpraIdx && selectedOpraIdx < orderHelpIdx,
            "#desk-options-selected must sit directly below _StockChart inside #watch-focus-content.");

        var deskOptionsJs = WebFile("wwwroot/js/desk-options.js");
        Assert.Contains("document.querySelector('[data-options-slot=\"' + slotName + '\"]')", deskOptionsJs);

        // 2. Chart Entry / Stop / Target reference lines + Desk defaultStudies
        var plotJs = WebFile("wwwroot/js/stock-chart-plot.js");
        foreach (var token in new[] { "function setTradePlanLevels(", "createPriceLine(", "removePriceLine(", "title: 'Entry'", "title: 'Stop'", "title: 'Target'", "paintTradePlanLines()" })
        {
            Assert.Contains(token, plotJs);
        }

        var chartJs = WebFile("wwwroot/js/stock-chart.js");
        Assert.Contains("function setTradePlanLevels(", chartJs);
        Assert.Contains("setTradePlanLevels,", chartJs);
        Assert.Contains("defaultStudies", chartJs);
        Assert.DoesNotContain("get('inline-studies')", chartJs);

        var watchDeskJs = WebFile("wwwroot/js/watch-desk.js");
        foreach (var token in new[] { "function setScannerDensity(", "function formatRowDayChange(", "scannerDensityV2", "Compact 5-col", "Full 10-col", "deskDefaultChartStudies", "defaultStudies: deskDefaultChartStudies", "ATR ", "function clearChartTradePlan(", "async function syncChartTradePlan(", "/api/v1/orders/plan", "plan?.stop == null && plan?.target == null", "setTradePlanLevels(" })
        {
            Assert.Contains(token, watchDeskJs);
        }

        // 3. Risk-first bracket order ticket
        var orderPartial = WebFile("Pages/Shared/_OrderWorkflow.cshtml");
        foreach (var token in new[] { "data-order-risk-strip", "data-risk-budget", "data-risk-stop-dist", "data-risk-dollar", "data-risk-rr", "<details class=\"order-options\" data-entry-only open>" })
        {
            Assert.Contains(token, orderPartial);
        }

        var orderJs = WebFile("wwwroot/js/order-workflow.js");
        Assert.Contains("function refreshRiskStrip()", orderJs);
        Assert.Contains("lastRiskBudget", orderJs);

        // 4. Strategy Lab slide-over drawer + Provenance popover
        var lab = WebFile("Pages/Backtests.cshtml");
        foreach (var token in new[] { "lab-provenance-popover", "ⓘ Provenance", "<dialog id=\"lab-matrix-drawer\"", "Open full job report ↗" })
        {
            Assert.Contains(token, lab);
        }

        var workflowJs = WebFile("wwwroot/js/run-workflow.js");
        foreach (var token in new[] { "function openMatrixDrawer(", "function renderDrawerRejections(", "function renderDrawerSampleTrades(", "lab-drawer-progress", "MonthlyRunResult", "lab-matrix-inspect-btn" })
        {
            Assert.Contains(token, workflowJs);
        }

        // 5. Catalysts OPRA Implied Move vs 20d ADR%
        var endpoints = WebFile("DeskOptionsEndpoints.cs");
        Assert.Contains("/catalysts/options-adr", endpoints);

        var news = WebFile("Pages/News.cshtml");
        var earnings = WebFile("Pages/Earnings.cshtml");
        Assert.Contains("data-catalysts-opra", news);
        Assert.Contains("catalysts-opra.js", news);
        Assert.Contains("data-catalysts-opra", earnings);
        Assert.Contains("catalysts-opra.js", earnings);

        var earningsJs = WebFile("wwwroot/js/earnings.js");
        Assert.Contains("tradingflow:earnings-symbols-changed", earningsJs);

        var catalystsJs = WebFile("wwwroot/js/catalysts-opra.js");
        foreach (var token in new[] { "/api/v1/catalysts/options-adr?symbols=", "AbortController", "pagehide", "'✓ '", "'◐ '", "'▲ '", "textContent" })
        {
            Assert.Contains(token, catalystsJs);
        }
        Assert.DoesNotContain("innerHTML", catalystsJs);
        Assert.DoesNotContain("setTimeout(", catalystsJs);

        // CSS rules for all 5 UI-5 features (30% scanner / 70% chart window; compact 5-col scanner must NEVER hide News col 6)
        var css = WebFile("wwwroot/css/ui2-foundation.css");
        foreach (var token in new[] { "minmax(260px, 3fr) minmax(560px, 7fr)", "[data-scanner-density=\"compact\"]", ".watch-chart-window", ".stock-chart-bar", ".order-risk-strip", ".lab-provenance-popover", ".lab-matrix-drawer", ".catalysts-opra-strip", ".catalysts-opra-ratio-pill" })
        {
            Assert.Contains(token, css);
        }
        Assert.DoesNotContain("nth-child(6)", css);
    }

    // Verifies completed-bar 1D / 5-session week / 21-session month volatility percentages and short/invalid bar null guards.
    [Fact]
    public void DailyObservationComputeVolatilityCoversOneDayWeekMonthAndShortHistoryNullGuards()
    {
        // Build 21 completed daily bars with distinct highs/lows in the 21d, 5d, and 1d windows ending on Close = 100.
        var session = new DateOnly(2026, 11, 20);
        var bars21 = Enumerable.Range(0, 21).Select(index =>
        {
            var date = session.AddDays(index - 20);
            var low = index == 2 ? 80m : index == 17 ? 92m : 98m;
            var high = index == 5 ? 115m : index == 19 ? 104m : 101m;
            if (index == 20) { low = 97.5m; high = 102.5m; }
            return new OhlcvBar("AAPL", new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), "1d",
                Open: 99m, High: high, Low: low, Close: 100m, Volume: 1_000_000m);
        }).ToArray();

        var full = DailyObservationService.ComputeVolatility(bars21);
        Assert.NotNull(full);
        Assert.Equal(5.00m, full!.DailyRangePct);    // (102.5 - 97.5) / 100 * 100
        Assert.Equal(12.00m, full.WeekRangePct);     // (104 - 92) / 100 * 100 over trailing 5 bars (indices 16..20)
        Assert.Equal(35.00m, full.MonthRangePct);    // (115 - 80) / 100 * 100 over trailing 21 bars

        // Fewer than 21 bars (e.g., 10 bars): 1D and 1W populated, 1M stays null.
        var tenBars = bars21[^10..];
        var partialMonth = DailyObservationService.ComputeVolatility(tenBars);
        Assert.NotNull(partialMonth);
        Assert.Equal(5.00m, partialMonth!.DailyRangePct);
        Assert.Equal(12.00m, partialMonth.WeekRangePct);
        Assert.Null(partialMonth.MonthRangePct);

        // Fewer than 5 bars (e.g., 4 bars): only 1D populated, 1W and 1M stay null.
        var fourBars = bars21[^4..];
        var shortHistory = DailyObservationService.ComputeVolatility(fourBars);
        Assert.NotNull(shortHistory);
        Assert.Equal(5.00m, shortHistory!.DailyRangePct);
        Assert.Null(shortHistory.WeekRangePct);
        Assert.Null(shortHistory.MonthRangePct);

        // Empty history and invalid latest close fail closed to null.
        Assert.Null(DailyObservationService.ComputeVolatility([]));
        var zeroClose = bars21.ToArray();
        zeroClose[^1] = zeroClose[^1] with { Close = 0m };
        Assert.Null(DailyObservationService.ComputeVolatility(zeroClose));
    }

    // Verifies the Desk chart minimal-words polish, 30/70 split, no-Inspect compact 5-col table, zero More button,
    // zero duplicate inline MA readout, and single-line SMA20/50/200 + Today support + ATR + 1D/1W/1M Volatility strip.
    [Fact]
    public void DeskChartMinimalWordsPolishHidesVerboseDiagnosticsAndExposesCompactSmaAndTodaySupportStrip()
    {
        var chartPartial = WebFile("Pages/Shared/_StockChart.cshtml");
        var moreStart = chartPartial.IndexOf("<details class=\"stock-chart-more\">", StringComparison.Ordinal);
        var announcementIdx = chartPartial.IndexOf("data-chart-settings-announcement", StringComparison.Ordinal);
        var studiesIdx = chartPartial.IndexOf("data-chart-studies", StringComparison.Ordinal);
        var contextIdx = chartPartial.IndexOf("data-chart-context-label", StringComparison.Ordinal);
        var sourceIdx = chartPartial.IndexOf("data-chart-source", StringComparison.Ordinal);
        Assert.True(moreStart >= 0 && moreStart < studiesIdx && studiesIdx < contextIdx && contextIdx < sourceIdx && sourceIdx < announcementIdx,
            "Selected studies, context label, and source text must sit inside <details class=\"stock-chart-more\"> above the chart stage.");
        Assert.DoesNotContain("data-chart-inline-studies", chartPartial);

        var indicatorsJs = WebFile("wwwroot/js/stock-chart-indicators.js");
        Assert.Contains("const preferenceKey = 'tradingflow.stock-chart.v3';", indicatorsJs);
        Assert.Contains("row.title = item.reason;", indicatorsJs);
        Assert.DoesNotContain("[reason, item.reason]", indicatorsJs);

        var chartJs = WebFile("wwwroot/js/stock-chart.js");
        Assert.Contains("defaultInterval, defaultStudies, onZones", chartJs);
        Assert.Contains("minimumFractionDigits: 2, maximumFractionDigits: 2", chartJs);
        Assert.DoesNotContain("inline-studies", chartJs);
        var zoneKeyStart = chartJs.IndexOf("function renderZoneKey(", StringComparison.Ordinal);
        var renderZonesStart = chartJs.IndexOf("function renderZones(", StringComparison.Ordinal);
        Assert.True(zoneKeyStart >= 0 && zoneKeyStart < renderZonesStart);
        var renderZoneKeyBody = chartJs[zoneKeyStart..renderZonesStart];
        Assert.DoesNotContain("element('small'", renderZoneKeyBody);
        Assert.DoesNotContain("Inputs available ' + zone.effectiveAtUtc", renderZoneKeyBody);
        Assert.Contains("item.title =", renderZoneKeyBody);
        Assert.Contains("onZones?.(visible,", renderZoneKeyBody);

        var desk = WebFile("Pages/Desk.cshtml");
        var stripStart = desk.IndexOf("class=\"watch-chart-indicators-strip\"", StringComparison.Ordinal);
        var sma20Idx = desk.IndexOf("id=\"watch-sma20\"", StringComparison.Ordinal);
        var sma50Idx = desk.IndexOf("id=\"watch-sma50\"", StringComparison.Ordinal);
        var sma200Idx = desk.IndexOf("id=\"watch-sma200\"", StringComparison.Ordinal);
        var todaySupportIdx = desk.IndexOf("id=\"watch-today-support\"", StringComparison.Ordinal);
        var atrIdx = desk.IndexOf("id=\"watch-atr\"", StringComparison.Ordinal);
        var volIdx = desk.IndexOf("id=\"watch-volatility\"", StringComparison.Ordinal);
        Assert.True(stripStart >= 0 && stripStart < sma20Idx && sma20Idx < sma50Idx && sma50Idx < sma200Idx &&
                    sma200Idx < todaySupportIdx && todaySupportIdx < atrIdx && atrIdx < volIdx,
            "Desk chart strip must present SMA20, SMA50, SMA200, Today support, ATR, and Vol 1D/1W/1M on one unified line.");
        Assert.DoesNotContain("watch-chart-ind-more", desk);
        Assert.DoesNotContain("watch-tech-more", desk);

        var watchDeskJs = WebFile("wwwroot/js/watch-desk.js");
        foreach (var token in new[]
                 {
                     "const deskDefaultChartStudies = ['sma20', 'sma50', 'sma200'];",
                     "defaultInterval: '1d'",
                     "onZones: handleDeskChartZones",
                     "function deskReferencePrice(",
                     "function renderSmaPill(",
                     "function selectTodaySupportZone(",
                     "function renderTodaySupportPill(",
                     "function renderAtrPill(",
                     "function renderVolatilityPill(",
                     "row?.observation?.volatility",
                     "function handleDeskChartZones(",
                     "renderTodaySupportPill(null);"
                 })
        {
            Assert.Contains(token, watchDeskJs);
        }

        var css = WebFile("wwwroot/css/ui2-foundation.css");
        foreach (var token in new[]
                 {
                     "minmax(260px, 3fr) minmax(560px, 7fr)",
                     "col:nth-child(10)",
                     "th:nth-child(10)",
                     "td:nth-child(10)",
                     ".watch-stock-col { width: 17%; }",
                     ".watch-price-col { width: 23%; }",
                     ".watch-strategy-col { width: 22%; }",
                     ".watch-news-col { width: 20%; }",
                     ".watch-rvol-col { width: 18%; }",
                     ".watch-chart-window [data-chart-zone-key]",
                     ".watch-chart-indicators-strip",
                     ".watch-ind-pill",
                     ".watch-ind-pill[data-role=\"support\"]",
                     ".watch-ind-pill[data-role=\"resistance\"]"
                 })
        {
            Assert.Contains(token, css);
        }
        Assert.DoesNotContain("nth-child(6)", css);
    }

    // Builds consecutive weekday 1d bars ending on lastDate with deterministic High-Low range for ADR% unit tests.
    private static OhlcvBar[] BuildConstantRangeDailyBars(string symbol, int count, DateOnly lastDate, decimal close, decimal range) =>
        BuildTrendingDailyBars(symbol, count, lastDate, startClose: close, step: 0m, range: range);

    // Builds consecutive weekday 1d bars with trending closes so ADR% parity against SignalGenerator is distinguishable from per-bar averages.
    private static OhlcvBar[] BuildTrendingDailyBars(string symbol, int count, DateOnly lastDate, decimal startClose, decimal step, decimal range)
    {
        var dates = new List<DateOnly>(count);
        for (var cursor = lastDate; dates.Count < count; cursor = cursor.AddDays(-1))
        {
            if (cursor.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            dates.Add(cursor);
        }

        dates.Reverse();
        return dates.Select((date, idx) =>
        {
            var close = startClose + (idx * step);
            return new OhlcvBar(
                symbol,
                new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                "1d",
                Open: close,
                High: close + range,
                Low: close,
                Close: close,
                Volume: 1_000_000m);
        }).ToArray();
    }

    // Builds a weekday-only authoritative exchange schedule map across the requested date range.
    private static IReadOnlyDictionary<DateOnly, MarketSessionSchedule> BuildWeekdaySchedules(DateOnly start, DateOnly end)
    {
        var map = new Dictionary<DateOnly, MarketSessionSchedule>();
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            map[d] = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                ? MarketSessionSchedule.Closed(d)
                : MarketSessionSchedule.RegularDay(d);
        }

        return map;
    }

    private sealed class FakeAdrScheduleProvider(bool returnEmpty = false, bool throwOnLoad = false)
        : IMarketDataProvider, IMarketSessionScheduleProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(
            DateOnly startDateInclusive, DateOnly endDateInclusive, CancellationToken cancellationToken)
        {
            if (throwOnLoad) throw new InvalidOperationException("Simulated schedule outage.");
            IReadOnlyDictionary<DateOnly, MarketSessionSchedule> result = returnEmpty
                ? new Dictionary<DateOnly, MarketSessionSchedule>()
                : BuildWeekdaySchedules(startDateInclusive, endDateInclusive);
            return Task.FromResult(result);
        }
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
