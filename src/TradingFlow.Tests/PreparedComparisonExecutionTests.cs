using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingFlow.Application.Jobs;
using TradingFlow.Contracts.Transport;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Storage;
using TradingFlow.Web;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class PreparedComparisonExecutionTests
{
    /// <summary>Exercises API admission, durable preparation and every real book against archived synthetic sources, then proves cache reuse.</summary>
    [Fact]
    public async Task ApiComparison_RefreshCapturesEachQueryOnceAndNextComparisonReusesEveryVersion()
    {
        await using var fixture = await BacktestMatrixFixture.CreateAsync(2, 2);
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
            var refreshRequest = fixture.Request with { CachePolicy = "refresh", IdempotencyKey = "synthetic-refresh" };
            var reviewed = await client.PreviewBacktestPlanAsync(new(refreshRequest, 1));
            var plan = await client.SaveBacktestPlanAsync(new(reviewed.Run, 1));
            var started = await client.StartBacktestPlanAsync(plan.PlanId, new("1.0", "synthetic-refresh-start"));
            Assert.Equal(4, started.Jobs.Count);
            var firstJob = (await fixture.Jobs.GetJobAsync(started.Jobs[0].JobId, default))!;
            var normalizedRoot = new TradingFlow.Engine.Configuration.SimpleYamlReader().ReadBacktestRun(firstJob.ConfigPath).NormalizedRoot;
            Assert.All(started.Jobs, job => Assert.Null(job.PreparedMarketData));
            Assert.Equal(0, fixture.HistoricalBarsCaptureCount);
            Assert.Equal(0, fixture.HistoricalCalendarCaptureCount);

            var refresh = await ExecuteComparisonAsync(fixture, started);
            Assert.Equal(2, fixture.HistoricalBarsCaptureCount);
            Assert.Equal(2, fixture.HistoricalCalendarCaptureCount);
            Assert.Equal(2, fixture.HistoricalBarCaptures.Count);
            Assert.All(fixture.HistoricalBarCaptures.Values, captures => Assert.Equal(1, captures));
            Assert.All(refresh, summary => { Assert.Equal(0, summary.ReusedQueryCount); Assert.Equal(4, summary.DownloadedQueryCount); });
            Assert.Single(refresh.Select(summary => summary.SnapshotId).Distinct(StringComparer.Ordinal));
            Assert.Single(refresh.Select(summary => summary.SnapshotSha256).Distinct(StringComparer.Ordinal));

            var versions = Directory.EnumerateFiles(Path.Combine(normalizedRoot, "_historical"), "version-*.json", SearchOption.AllDirectories).Order().ToArray();
            Assert.Equal(4, versions.Length);
            var receipts = Directory.EnumerateFiles(Path.Combine(fixture.Root, "raw"), "*.manifest.json", SearchOption.AllDirectories).Order().ToArray();
            Assert.Equal(4, receipts.Length);
            foreach (var path in receipts)
            {
                var receipt = JsonSerializer.Deserialize<RawArchiveManifest>(await File.ReadAllTextAsync(path),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Assert.Equal("true", receipt.Attributes["syntheticFixture"]);
                Assert.Equal("false", receipt.Attributes["originalHistoricalReceiptClaim"]);
                Assert.Equal("true", receipt.Attributes["paginationCompleted"]);
                Assert.Null(receipt.Http);
                Assert.NotEqual(default, receipt.ReceivedAtUtc);
                Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, receipt.PayloadFileName)));
            }
            var replay = await client.StartBacktestPlanAsync(plan.PlanId, new("1.0", "synthetic-refresh-start"));
            Assert.Equal(started.Jobs.Select(job => job.JobId), replay.Jobs.Select(job => job.JobId));
            Assert.Equal(2, fixture.HistoricalBarsCaptureCount);

            var reuseRequest = fixture.Request with { CachePolicy = "use_cache", IdempotencyKey = "synthetic-reuse" };
            var reuseReviewed = await client.PreviewBacktestPlanAsync(new(reuseRequest, 1));
            var reusePlan = await client.SaveBacktestPlanAsync(new(reuseReviewed.Run, 1));
            var reuseStarted = await client.StartBacktestPlanAsync(reusePlan.PlanId, new("1.0", "synthetic-reuse-start"));
            Assert.Equal(4, reuseStarted.Jobs.Count);
            var reused = await ExecuteComparisonAsync(fixture, reuseStarted);
            Assert.All(reused, summary => { Assert.Equal(4, summary.ReusedQueryCount); Assert.Equal(0, summary.DownloadedQueryCount); });
            Assert.Single(reused.Select(summary => summary.SnapshotId).Distinct(StringComparer.Ordinal));
            Assert.Single(reused.Select(summary => summary.SnapshotSha256).Distinct(StringComparer.Ordinal));
            Assert.NotEqual(refresh[0].SnapshotId, reused[0].SnapshotId);
            Assert.Equal(2, fixture.HistoricalBarsCaptureCount);
            Assert.Equal(2, fixture.HistoricalCalendarCaptureCount);
            Assert.All(fixture.HistoricalBarCaptures.Values, captures => Assert.Equal(1, captures));
            Assert.Equal(versions, Directory.EnumerateFiles(Path.Combine(normalizedRoot, "_historical"), "version-*.json", SearchOption.AllDirectories).Order().ToArray());
            Assert.Equal(receipts, Directory.EnumerateFiles(Path.Combine(fixture.Root, "raw"), "*.manifest.json", SearchOption.AllDirectories).Order().ToArray());
            Assert.NotEmpty(Directory.EnumerateDirectories(fixture.Root, ".run-inputs-*", SearchOption.AllDirectories));
            await using var database = fixture.Database.CreateDbContext();
            Assert.Empty(await database.Wishlists.ToArrayAsync());
            Assert.Empty(await database.ScreenerPresets.ToArrayAsync());
            Assert.Empty(await database.Candidates.ToArrayAsync());
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>Proves the prepared source path preserves candidate decisions, execution timestamps, fills, costs and independently labeled monthly records.</summary>
    [Fact]
    public async Task PreparedAndDirectReads_PreserveDecisionsTradesAndMonthlyResults()
    {
        await using var fixture = await BacktestMatrixFixture.CreateAsync(1, 1);
        var plan = await fixture.Plans.SaveAsync(new(fixture.Request, 1));
        var started = await fixture.Plans.StartAsync(plan.PlanId, new("1.0", "synthetic-parity"));
        var accepted = Assert.Single(started.Jobs);
        await ExecuteComparisonAsync(fixture, started);
        var stored = (await fixture.Jobs.GetJobAsync(accepted.JobId, default))!;
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var prepared = JsonSerializer.Deserialize<BacktestResult>(await File.ReadAllTextAsync(stored.ResultReference!), json)!;
        var publishedMonthly = JsonSerializer.Deserialize<MonthlyRunResultReport>(await File.ReadAllTextAsync(
            Path.ChangeExtension(stored.ResultReference!, ".monthly.json")), json)!;
        var direct = await fixture.RunDirectAsync(accepted.JobId);
        Assert.NotNull(prepared.PreparedMarketData);
        Assert.Null(direct.Result.PreparedMarketData);
        Assert.True(prepared.EconomicResultsComplete);
        Assert.True(direct.Result.EconomicResultsComplete);
        Assert.Equal(prepared.CandidateTradeCount, direct.Result.CandidateTradeCount);
        Assert.Equal(prepared.AcceptedTradeCount, direct.Result.AcceptedTradeCount);
        Assert.Equal(prepared.RejectedTradeCount, direct.Result.RejectedTradeCount);
        Assert.Equal(prepared.ProcessedBarCount, direct.Result.ProcessedBarCount);
        Assert.Equal(prepared.CompletedTrades.OrderBy(trade => trade.CandidateId), direct.Result.CompletedTrades.OrderBy(trade => trade.CandidateId));
        Assert.Equal(prepared.StartingCapital, direct.Result.StartingCapital);
        Assert.Equal(prepared.EndingCapital, direct.Result.EndingCapital);
        Assert.NotEmpty(publishedMonthly.Results);
        Assert.Null(publishedMonthly.UnavailableReason);
        Assert.Null(direct.Monthly.UnavailableReason);
        Assert.Equal(publishedMonthly.Results, direct.Monthly.Results);
        Assert.All(direct.Monthly.Results, month => {
            Assert.Equal(accepted.Cohort!.Strategy, month.Strategy);
            Assert.Equal(accepted.Cohort.CohortName, month.CohortName);
        });
        var preparedDecisions = JsonSerializer.Deserialize<BacktestCandidateDecisionAudit[]>(await File.ReadAllTextAsync(prepared.CandidateDecisionAuditPath!), json)!;
        var directDecisions = JsonSerializer.Deserialize<BacktestCandidateDecisionAudit[]>(await File.ReadAllTextAsync(direct.Result.CandidateDecisionAuditPath!), json)!;
        Assert.NotEmpty(preparedDecisions);
        Assert.Equal(preparedDecisions.OrderBy(item => item.Candidate.CandidateId).Select(item =>
            (item.Candidate.CandidateId, item.Candidate.DiscoveredAtUtc, item.Candidate.RevalidatedAtUtc, item.Candidate.State, item.Candidate.SemanticDecisionSha256)),
            directDecisions.OrderBy(item => item.Candidate.CandidateId).Select(item =>
            (item.Candidate.CandidateId, item.Candidate.DiscoveredAtUtc, item.Candidate.RevalidatedAtUtc, item.Candidate.State, item.Candidate.SemanticDecisionSha256)));
        Assert.Equal(preparedDecisions.OrderBy(item => item.Candidate.CandidateId).SelectMany(item => item.Transitions.OrderBy(change => change.Sequence).Select(change =>
            (change.CandidateId, change.Sequence, change.OccurredAtUtc, change.PreviousState, change.NewState, change.ReasonCode, change.SemanticDecisionSha256))),
            directDecisions.OrderBy(item => item.Candidate.CandidateId).SelectMany(item => item.Transitions.OrderBy(change => change.Sequence).Select(change =>
            (change.CandidateId, change.Sequence, change.OccurredAtUtc, change.PreviousState, change.NewState, change.ReasonCode, change.SemanticDecisionSha256))));
    }

    /// <summary>Runs only accepted jobs, verifies every persisted result carries its common selection and checks reload evidence survives.</summary>
    private static async Task<IReadOnlyList<PreparedMarketDataSummary>> ExecuteComparisonAsync(BacktestMatrixFixture fixture,
        BacktestPlanStartResponse started)
    {
        var handler = fixture.CreateHandler();
        await handler.InitializeAsync(default);
        var processor = new DurableJobProcessor(fixture.Jobs);
        for (var index = 0; index < started.Jobs.Count; index++)
            Assert.True(await processor.ProcessNextAsync("synthetic-prepared-worker", handler, BacktestMatrixFixture.WorkerOptions, default));
        Assert.False(await processor.ProcessNextAsync("synthetic-prepared-worker", handler, BacktestMatrixFixture.WorkerOptions, default));
        var summaries = new List<PreparedMarketDataSummary>();
        foreach (var accepted in started.Jobs)
        {
            var stored = await fixture.Jobs.GetJobAsync(accepted.JobId, default);
            Assert.NotNull(stored);
            Assert.True(stored.Status == "completed", stored.ErrorMessage ?? stored.Status);
            Assert.NotNull(stored.ResultReference);
            var result = JsonSerializer.Deserialize<BacktestResult>(await File.ReadAllTextAsync(stored.ResultReference),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var summary = Assert.IsType<PreparedMarketDataSummary>(result.PreparedMarketData);
            Assert.Equal(64, summary.SnapshotSha256.Length);
            Assert.Equal(new[] { "synthetic-matrix-v1" }, summary.Providers);
            Assert.Equal(new[] { "sip" }, summary.DataFeeds);
            Assert.Equal(new[] { "all" }, summary.PriceAdjustments);
            Assert.NotEmpty(summary.HistoricalEvidenceNote);
            Assert.NotEmpty(result.CompletedTrades);
            Assert.Equal(accepted.Cohort!.StartingCapital, result.StartingCapital);
            Assert.All(result.CompletedTrades, trade => Assert.Contains(trade.Ticker, accepted.Cohort.Symbols));
            var snapshot = await handler.GetSnapshotAsync(accepted.JobId);
            Assert.Equal(summary.SnapshotSha256, snapshot!.PreparedMarketData?.SnapshotSha256);
            summaries.Add(summary);
        }
        var restored = fixture.CreateHandler();
        await restored.InitializeAsync(default);
        foreach (var accepted in started.Jobs)
            Assert.Equal(summaries[0].SnapshotSha256, (await restored.GetSnapshotAsync(accepted.JobId))!.PreparedMarketData?.SnapshotSha256);
        return summaries;
    }
}
