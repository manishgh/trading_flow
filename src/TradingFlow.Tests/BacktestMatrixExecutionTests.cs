using System.Text.Json;
using TradingFlow.Alpaca;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingFlow.Application.Jobs;
using TradingFlow.Backtesting;
using TradingFlow.Contracts.Transport;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Web;
using TradingFlow.Web.Services;
using TradingFlow.Data.Evidence.Historical;

namespace TradingFlow.Tests;

public sealed class BacktestMatrixExecutionTests
{
    /// <summary>A real retained assessment provider preserves every synthetic runner decision, rejection, order and provenance value.</summary>
    [Fact]
    public async Task ScopedAndOrdinaryAssessmentVerification_PreserveCompleteRunnerEvidence()
    {
        using var news = new HistoricalDiagnosticNewsTests.Archive();
        var publication = await HistoricalDiagnosticNewsTests.PublishAssessmentDiagnosticAsync(news);
        var provider = HistoricalDiagnosticNewsProvider.LoadVerified(news.Root, publication.Path, publication.Sha256);
        var engine = new EngineConfig("batch", 1, 1, 0, 1, true, EvidenceResearchRoot: news.Root,
            EvidenceManifestPath: publication.Path, EvidenceManifestSha256: publication.Sha256,
            EvidenceDependencyGraphSha256: provider.DependencyGraphSha256);
        await using var fixture = await BacktestMatrixFixture.CreateAsync(6, 1, evidence: engine);
        var saved = await fixture.Plans.SaveAsync(new(fixture.Request, 6));
        var started = await fixture.Plans.StartAsync(saved.PlanId, new("1.0", "scoped-assessment-parity"));
        var job = Assert.Single(started.Jobs);
        var ordinary = await fixture.RunEvidenceParityAsync(job.JobId, null, "ordinary");
        using var scope = Assert.IsType<WorkflowEvidenceVerificationScope>(WorkflowEvidenceVerificationScope.Open(new object(), engine));
        var scoped = await fixture.RunEvidenceParityAsync(job.JobId, scope, "scoped");
        Assert.True(ordinary.CompletedTrades.Count > 0, JsonSerializer.Serialize(new { ordinary.CandidateTradeCount,
            ordinary.AcceptedTradeCount, ordinary.RejectedTradeCount, ordinary.ProcessedBarCount,
            ordinary.TickerResults, ordinary.Diagnostics, ordinary.HistoryEligibility, ordinary.Validation }));
        Assert.NotEmpty(ordinary.CompletedTrades);
        var ordinaryState = Assert.IsType<BacktestPortfolioState>(ordinary.PortfolioState);
        var scopedState = Assert.IsType<BacktestPortfolioState>(scoped.PortfolioState);
        Assert.NotEmpty(ordinaryState.AdmittedOrders);
        Assert.NotEmpty(ordinaryState.Fills);
        Assert.NotEmpty(scopedState.AdmittedOrders);
        Assert.NotEmpty(scopedState.Fills);
        Assert.Equal(JsonSerializer.Serialize(ordinaryState), JsonSerializer.Serialize(scopedState));
        Assert.True(ordinary.RejectedTradeCount > 0 || (ordinary.UnifiedPortfolio?.RejectedTradeCount ?? 0) > 0,
            "The fixture must exercise portfolio rejection as well as accepted orders.");
        Assert.Equal(JsonSerializer.Serialize(ordinary.CompletedTrades), JsonSerializer.Serialize(scoped.CompletedTrades));
        Assert.Equal(JsonSerializer.Serialize(ordinary.AcceptedOrders), JsonSerializer.Serialize(scoped.AcceptedOrders));
        Assert.Equal(JsonSerializer.Serialize(ordinary.Diagnostics), JsonSerializer.Serialize(scoped.Diagnostics));
        Assert.Equal(JsonSerializer.Serialize(ordinary.UnifiedPortfolio), JsonSerializer.Serialize(scoped.UnifiedPortfolio));
        Assert.Equal(JsonSerializer.Serialize(ordinary.DecisionContextEvidence), JsonSerializer.Serialize(scoped.DecisionContextEvidence));
        Assert.NotNull(scoped.DecisionContextEvidence);
        Assert.Equal(provider.DependencyGraphSha256, scoped.DecisionContextEvidence.DependencyGraphSha256);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var ordinaryDecisions = JsonSerializer.Deserialize<BacktestCandidateDecisionAudit[]>(await File.ReadAllTextAsync(ordinary.CandidateDecisionAuditPath!), options)!;
        var scopedDecisions = JsonSerializer.Deserialize<BacktestCandidateDecisionAudit[]>(await File.ReadAllTextAsync(scoped.CandidateDecisionAuditPath!), options)!;
        Assert.NotEmpty(ordinaryDecisions);
        Assert.Equal(JsonSerializer.Serialize(ordinaryDecisions.OrderBy(item => item.Candidate.CandidateId)),
            JsonSerializer.Serialize(scopedDecisions.OrderBy(item => item.Candidate.CandidateId)));
        scope.RequireLive(engine);
    }

    /// <summary>Runs every API-admitted portfolio through the real durable handler, shared engine and monthly publication.</summary>
    [Theory]
    [InlineData(50, 10, 4, 20, false)]
    [InlineData(30, 5, 2, 12, false)]
    [InlineData(11, 5, 2, 6, false)]
    [InlineData(1, 1, 1, 1, false)]
    [InlineData(1, 1, 1, 1, true)]
    public async Task SavedComparison_ExecutesIndependentBooksAndPublishesMonthlyResults(int stockCount, int groupSize, int strategyCount, int executionCount, bool sip)
    {
        await using var fixture = await BacktestMatrixFixture.CreateAsync(stockCount, strategyCount, sip);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(fixture.Plans);
        await using var app = builder.Build();
        app.MapGroup("/api/v1").MapBacktestPlanEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient();
            var client = new TradingWorkflowClient(http, () => address);
            var reviewed = await client.PreviewBacktestPlanAsync(new(fixture.Request, groupSize));
            var plan = await client.SaveBacktestPlanAsync(new(reviewed.Run, groupSize));
            Assert.Empty(await fixture.Jobs.GetAllJobsAsync(default));
            var started = await client.StartBacktestPlanAsync(plan.PlanId, new("1.0", "matrix-start"));
            Assert.Equal(executionCount, started.Jobs.Count);
            var handler = fixture.CreateHandler();
            await handler.InitializeAsync(default);
            var processor = new DurableJobProcessor(fixture.Jobs);
            for (var index = 0; index < executionCount; index++)
            {
                Assert.True(await processor.ProcessNextAsync("matrix-worker", handler, BacktestMatrixFixture.WorkerOptions, default));
                var failed = (await fixture.Jobs.GetJobsAsync("backtest", default)).Where(job => job.Status == "failed").ToArray();
                Assert.True(failed.Length == 0, String.Join(Environment.NewLine, failed.Select(job => job.ErrorMessage)));
            }
            Assert.False(await processor.ProcessNextAsync("matrix-worker", handler, BacktestMatrixFixture.WorkerOptions, default));
            foreach (var accepted in started.Jobs)
            {
                var stored = await fixture.Jobs.GetJobAsync(accepted.JobId, default);
                Assert.Equal("completed", stored!.Status);
                Assert.True(String.IsNullOrWhiteSpace(stored.ErrorMessage), stored.ErrorMessage);
                var snapshot = await handler.GetSnapshotAsync(accepted.JobId);
                Assert.NotNull(snapshot!.Result);
                Assert.NotNull(stored.ResultReference);
                var result = JsonSerializer.Deserialize<BacktestResult>(await File.ReadAllTextAsync(stored.ResultReference),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                Assert.Equal(accepted.Cohort!.StartingCapital, result.StartingCapital);
                Assert.True(result.EconomicResultsComplete);
                Assert.NotEmpty(result.CompletedTrades);
                Assert.NotEmpty(snapshot.MonthlyResults!);
                Assert.Null(snapshot.MonthlyUnavailableReason);
                Assert.All(snapshot.MonthlyResults!, month => {
                    Assert.Equal(accepted.Cohort.Strategy, month.Strategy);
                    Assert.Equal(accepted.Cohort.CohortName, month.CohortName);
                    Assert.NotNull(month.ReturnPct);
                    Assert.Equal((month.ClosingEquity!.Value / month.OpeningEquity!.Value - 1m) * 100m, month.ReturnPct);
                });
                Assert.Equal(accepted.Cohort.StartingCapital, snapshot.MonthlyResults![0].OpeningEquity);
                AssertMonthlyBalances(result, snapshot.MonthlyResults!, sip);
                Assert.All(result.CompletedTrades, trade => Assert.Contains(trade.Ticker, accepted.Cohort.Symbols));
                AssertCapitalBound(result, accepted.Cohort.StartingCapital);
                Assert.True(File.Exists(stored.ResultReference));
                Assert.True(File.Exists(Path.ChangeExtension(stored.ResultReference, ".monthly.json")));
                var monthlyReport = JsonSerializer.Deserialize<TradingFlow.Web.Services.MonthlyRunResultReport>(
                    await File.ReadAllTextAsync(Path.ChangeExtension(stored.ResultReference, ".monthly.json")),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Assert.Equal(accepted.Cohort.StartingCapital, monthlyReport.StartingCapital);
                Assert.NotNull(monthlyReport.Cohort);
                Assert.Equal(accepted.Cohort.PlanId, monthlyReport.Cohort.PlanId);
                Assert.Equal(accepted.Cohort.CohortName, monthlyReport.Cohort.CohortName);
                Assert.Equal(accepted.Cohort.Strategy, monthlyReport.Cohort.Strategy);
                Assert.Equal(accepted.Cohort.Symbols, monthlyReport.Cohort.Symbols);
                Assert.NotNull(snapshot.Cohort);
                Assert.Equal(accepted.Cohort.PlanId, snapshot.Cohort.PlanId);
                Assert.Equal(accepted.Cohort.StartingCapital, snapshot.Cohort.StartingCapital);
                if (sip) await AssertPhysicalSipResultAsync(result, fixture.SipSourceSha256!);
            }
            var reloaded = fixture.CreateHandler();
            await reloaded.InitializeAsync(default);
            Assert.All(started.Jobs, accepted => Assert.NotEmpty(reloaded.Get(accepted.JobId)!.MonthlyResults!));
            Assert.Equal(started.Jobs.Select(job => job.JobId),
                (await client.StartBacktestPlanAsync(plan.PlanId, new("1.0", "matrix-start"))).Jobs.Select(job => job.JobId));
            Assert.NotEmpty(Directory.EnumerateDirectories(fixture.Root, ".run-inputs-*", SearchOption.AllDirectories));
            await using var database = fixture.Database.CreateDbContext();
            Assert.Empty(await database.Wishlists.ToArrayAsync());
            Assert.Empty(await database.ScreenerPresets.ToArrayAsync());
            Assert.Empty(await database.Candidates.ToArrayAsync());
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>Reconciles native months to the shared source-session book, retaining the existing SIP/legacy closed-ledger expectations.</summary>
    private static void AssertMonthlyBalances(BacktestResult result, IReadOnlyList<MonthlyRunResult> months, bool sip)
    {
        if (result.PortfolioState is { } native)
        {
            AssertNativeMonthlyBalances(result, native, months);
            return;
        }
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var profitByMonth = result.CompletedTrades.GroupBy(trade =>
            TimeZoneInfo.ConvertTime(sip ? trade.ExitTimestamp : trade.ExitTimestamp.AddDays(-1), zone).ToString("yyyy-MM"))
            .ToDictionary(group => group.Key, group => group.Sum(trade => trade.NetProfit));
        Assert.Equal(sip ? 1 : 2, months.Count);
        var opening = result.StartingCapital;
        foreach (var month in months.OrderBy(row => row.Month, StringComparer.Ordinal))
        {
            Assert.Equal(opening, month.OpeningEquity);
            var closing = opening + profitByMonth.GetValueOrDefault(month.Month);
            Assert.Equal(closing, month.ClosingEquity);
            Assert.Equal((closing / opening - 1m) * 100m, month.ReturnPct);
            opening = closing;
        }
        Assert.Equal(result.EndingCapital, opening);
    }

    /// <summary>Checks every month against actual engine observations, separating monthly realized/cost changes from remaining unrealized value.</summary>
    private static void AssertNativeMonthlyBalances(BacktestResult result, BacktestPortfolioState state, IReadOnlyList<MonthlyRunResult> months)
    {
        var opening = result.StartingCapital;
        var previousRealized = 0m;
        var previousFees = 0m;
        var sessions = state.Sessions.GroupBy(session => session.SessionDate.ToString("yyyy-MM"))
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        Assert.Equal(2, months.Count);
        Assert.Equal(sessions.Count, months.Count);
        foreach (var month in months.OrderBy(row => row.Month, StringComparer.Ordinal))
        {
            var close = sessions[month.Month][^1];
            Assert.Null(close.UnavailableReason);
            Assert.NotNull(close.Equity);
            Assert.Equal(NativeDailySessionLedger.ValuationBasis, month.ValuationBasis);
            Assert.Equal(opening, month.OpeningEquity);
            Assert.Equal(close.Equity, month.ClosingEquity);
            Assert.Equal((close.Equity.Value / opening - 1m) * 100m, month.ReturnPct);
            Assert.Equal(close.RealizedNetProfit - previousRealized, month.RealizedNetProfit);
            Assert.Equal(close.Fees - previousFees, month.Fees);
            Assert.Equal(close.UnrealizedProfit, month.UnrealizedProfit);
            Assert.Equal(close.Cash, month.ClosingCash);
            Assert.Equal(close.PositionValue, month.ClosingPositionValue);
            Assert.Equal(close.GrossExposurePct, month.GrossExposurePct);
            Assert.Equal(close.OpenPositionCount, month.OpenPositions);
            Assert.Equal(result.CompletedTrades.Count(trade => NativeDailySessionLedger.SourceSessionDate(trade.ExitTimestamp).ToString("yyyy-MM") == month.Month), month.ClosedTrades);
            var fills = state.Fills.Where(fill => fill.SourceSessionDate.ToString("yyyy-MM") == month.Month).ToArray();
            Assert.Equal(fills.Length, month.FillCount);
            Assert.Equal(fills.Where(fill => fill.IsEntry).Select(fill => fill.CandidateId).Distinct(StringComparer.Ordinal).Count(), month.FilledCandidates);
            opening = close.Equity.Value;
            previousRealized = close.RealizedNetProfit;
            previousFees = close.Fees;
        }
        Assert.Equal(result.EndingCapital, Decimal.Round(opening, 4));
    }

    /// <summary>Proves the normal job generated candidates and consumed the exact physical SIP source instead of a candle fill fallback.</summary>
    private static async Task AssertPhysicalSipResultAsync(BacktestResult result, string expectedSourceHash)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var audit = JsonSerializer.Deserialize<SipPortfolioSourceAudit>(await File.ReadAllTextAsync(
            Path.ChangeExtension(result.ResultPath, ".event-portfolio-source-audit.json")), json)!;
        Assert.True(audit.FullyConsumedAndVerified);
        Assert.Equal(6, audit.QuoteRows);
        Assert.Equal(6, audit.TradeRows);
        using var request = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.ChangeExtension(result.ResultPath, ".event-portfolio-request.json")));
        Assert.Equal(expectedSourceHash, request.RootElement.GetProperty("sourceManifestSha256").GetString());
        var candidates = request.RootElement.GetProperty("request").GetProperty("candidates");
        Assert.Equal(1, candidates.GetArrayLength());
        Assert.Equal(candidates[0].GetProperty("candidateId").GetString(), Assert.Single(result.CompletedTrades).CandidateId);
        Assert.Equal(expectedSourceHash, Assert.Single(result.ArtifactReferences!, item => item.Kind == "event_source_manifest").Sha256);
    }

    /// <summary>Every book spends its own actual fill cash, with open holdings retained separately; SIP/legacy closed books keep their original check.</summary>
    private static void AssertCapitalBound(BacktestResult result, decimal startingCapital)
    {
        if (result.PortfolioState is { } native)
        {
            AssertNativeCapitalBound(result, native, startingCapital);
            return;
        }
        decimal cash = startingCapital;
        Assert.All(result.CompletedTrades, trade => Assert.Equal(0m, trade.Fees));
        var clock = result.CompletedTrades.SelectMany(trade => new[] { trade.EntryTimestamp, trade.ExitTimestamp }).Distinct().Order();
        foreach (var time in clock)
        {
            foreach (var trade in result.CompletedTrades.Where(trade => trade.ExitTimestamp == time))
                cash += trade.ShareQuantity * trade.ExitPrice - trade.Fees;
            foreach (var trade in result.CompletedTrades.Where(trade => trade.EntryTimestamp == time))
                cash -= trade.ShareQuantity * trade.EntryPrice;
            Assert.True(cash >= 0, "A cohort spent cash belonging to another portfolio.");
        }
        Assert.Equal(result.EndingCapital, cash);
    }

    /// <summary>Reconciles fills to terminal cash and signed holdings without assuming every admitted entry has closed.</summary>
    private static void AssertNativeCapitalBound(BacktestResult result, BacktestPortfolioState state, decimal startingCapital)
    {
        var cash = startingCapital;
        Assert.All(state.Fills, fill => Assert.Equal(0m, fill.Fees));
        foreach (var fill in state.Fills.OrderBy(fill => fill.TimestampUtc))
        {
            cash -= fill.Quantity * fill.Price * (fill.IsBuy ? 1 : -1) + fill.Fees;
            Assert.True(cash >= 0, "A cohort spent cash belonging to another portfolio.");
        }
        var close = state.Sessions[^1];
        Assert.Equal(cash, close.Cash);
        Assert.Equal(state.Fills.Sum(fill => fill.Fees), close.Fees);
        Assert.Equal(cash + close.PositionValue, close.Equity);
        Assert.Equal(startingCapital + close.RealizedNetProfit + close.UnrealizedProfit, close.Equity);
        Assert.Equal(result.EndingCapital, Decimal.Round(close.Equity!.Value, 4));
        var remaining = state.Fills.GroupBy(fill => fill.CandidateId).ToDictionary(group => group.Key,
            group => group.Sum(fill => fill.Quantity * (fill.IsBuy ? 1 : -1)), StringComparer.Ordinal);
        Assert.Equal(remaining.Count(pair => pair.Value != 0), state.OpenPositions.Count);
        Assert.All(state.OpenPositions, position => Assert.Equal(remaining[position.CandidateId], position.SignedQuantity));
    }

    /// <summary>Stops the host after real SIP consumption, then replays the same frozen request in a new worker and publishes its monthly results once.</summary>
    [Fact]
    public async Task InterruptedSipRunner_ReplaysFrozenRequestAndPublishesAfterRestart()
    {
        await using var fixture = await BacktestMatrixFixture.CreateAsync(1, 1, true);
        var saved = await fixture.Plans.SaveAsync(new(fixture.Request, 1));
        var accepted = Assert.Single((await fixture.Plans.StartAsync(saved.PlanId, new("1.0", "restart-sip"))).Jobs);
        var before = (await fixture.Jobs.GetJobAsync(accepted.JobId, default))!;
        using var host = new CancellationTokenSource();
        var stoppedAfterReplay = false;
        string[] firstCandidates = [];
        var first = fixture.CreateHandler(observe: update => {
            if (update.Stage != "writing_result") return;
            var auditPath = Assert.Single(Directory.EnumerateFiles(fixture.Root, "*.event-portfolio-source-audit.json", SearchOption.AllDirectories));
            var audit = JsonSerializer.Deserialize<SipPortfolioSourceAudit>(File.ReadAllText(auditPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.True(audit.FullyConsumedAndVerified);
            Assert.Equal(6, audit.TradeRows);
            var requestPath = Assert.Single(Directory.EnumerateFiles(fixture.Root, "*.event-portfolio-request.json", SearchOption.AllDirectories));
            using var request = JsonDocument.Parse(File.ReadAllText(requestPath));
            firstCandidates = request.RootElement.GetProperty("request").GetProperty("candidates").EnumerateArray()
                .Select(item => item.GetProperty("candidateId").GetString()!).ToArray();
            Assert.NotEmpty(firstCandidates);
            stoppedAfterReplay = true;
            host.Cancel();
        });
        await first.InitializeAsync(default);
        var processor = new DurableJobProcessor(fixture.Jobs);
        Assert.True(await processor.ProcessNextAsync("first-owner", first, BacktestMatrixFixture.WorkerOptions, host.Token));
        Assert.True(stoppedAfterReplay, (await fixture.Jobs.GetJobAsync(accepted.JobId, default))?.ErrorMessage ?? "The runner did not reach result publication.");
        var interrupted = (await fixture.Jobs.GetJobAsync(accepted.JobId, default))!;
        Assert.Equal("running", interrupted.Status);
        Assert.Null(interrupted.LeaseOwner);
        Assert.Null(interrupted.LeaseToken);
        Assert.Null(interrupted.LeaseExpiresAtUtc);
        Assert.Null(interrupted.CancellationRequestedAtUtc);
        Assert.Null(interrupted.ResultReference);
        Assert.Equal(before.RequestJson, interrupted.RequestJson);
        Assert.NotEmpty(Directory.EnumerateDirectories(fixture.Root, ".run-inputs-*", SearchOption.AllDirectories));
        var resumed = fixture.CreateHandler();
        await resumed.InitializeAsync(default);
        Assert.True(await processor.ProcessNextAsync("new-owner", resumed, BacktestMatrixFixture.WorkerOptions, default));
        var complete = (await fixture.Jobs.GetJobAsync(accepted.JobId, default))!;
        Assert.Equal("completed", complete.Status);
        Assert.Equal(2, complete.AttemptCount);
        Assert.Equal(before.RequestJson, complete.RequestJson);
        var result = JsonSerializer.Deserialize<BacktestResult>(await File.ReadAllTextAsync(complete.ResultReference!),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.NotEmpty(result.CompletedTrades);
        await AssertPhysicalSipResultAsync(result, fixture.SipSourceSha256!);
        using var resumedRequest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(result.ResultPath, ".event-portfolio-request.json")));
        Assert.Equal(firstCandidates, resumedRequest.RootElement.GetProperty("request").GetProperty("candidates").EnumerateArray()
            .Select(item => item.GetProperty("candidateId").GetString()!).ToArray());
        AssertMonthlyBalances(result, (await resumed.GetSnapshotAsync(accepted.JobId))!.MonthlyResults!, true);
        Assert.False(await processor.ProcessNextAsync("new-owner", resumed, BacktestMatrixFixture.WorkerOptions, default));
    }

    /// <summary>Cancels the actual runner during provider collection and retains its private spool for quant review beside existing publications.</summary>
    [Fact]
    public async Task CancelledRealRunner_RetainsOwnedInputsForReviewAndPreservesPublishedArtifacts()
    {
        await using var fixture = await BacktestMatrixFixture.CreateAsync(5, 1);
        var saved = await fixture.Plans.SaveAsync(new(fixture.Request, 5));
        var started = await fixture.Plans.StartAsync(saved.PlanId, new("1.0", "cancel-on-provider"));
        var provider = new BacktestMatrixFixture.BlockingProvider();
        var handler = fixture.CreateHandler(provider);
        await handler.InitializeAsync(default);
        var sentinel = Path.Combine(fixture.Root, "results", "existing-published-result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
        await File.WriteAllTextAsync(sentinel, "published fixture evidence");
        var processor = new DurableJobProcessor(fixture.Jobs);
        var task = processor.ProcessNextAsync("cancel-worker", handler, BacktestMatrixFixture.WorkerOptions, default);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(Directory.EnumerateDirectories(fixture.Root, ".run-inputs-*", SearchOption.AllDirectories));
        await fixture.Jobs.RequestCancellationAsync(started.Jobs[0].JobId, DateTimeOffset.UtcNow, default);
        Assert.True(await task.WaitAsync(TimeSpan.FromSeconds(10)));
        var stored = await fixture.Jobs.GetJobAsync(started.Jobs[0].JobId, default);
        Assert.Equal("cancelled", stored!.Status);
        Assert.NotEmpty(Directory.EnumerateDirectories(fixture.Root, ".run-inputs-*", SearchOption.AllDirectories));
        Assert.Equal("published fixture evidence", await File.ReadAllTextAsync(sentinel));
    }
}
