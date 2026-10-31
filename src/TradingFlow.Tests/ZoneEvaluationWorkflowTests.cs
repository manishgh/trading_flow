using System.Text.Json;
using Moq;
using TradingFlow.Application.Candidates;
using TradingFlow.Application.Jobs;
using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Contracts.Presentation;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Tests;

public sealed class ZoneEvaluationWorkflowTests
{
    // Cost estimates retain their target origin without manufacturing a target zone or promising a realized ratio.
    [Fact]
    public void RiskTarget_RoundTripsSeparateSizingAndFullCostEstimates()
    {
        var at = DateTimeOffset.Parse("2026-10-27T14:00:00Z");
        var detail = new BacktestZoneDecisionDetail("candidate", "strategy", "SYNTHETIC", "long", "python_bounce",
            1, 2, "Support", 98, 100, 2, at.AddHours(-1), at.AddMinutes(-45), at.AddMinutes(-5), at.AddMinutes(-5),
            null, at, at.AddMinutes(5), 101, null, 0.5m, 200, 20, 97, at.AddHours(5), "not_filled", "risk_rejected",
            null, null, "python-zone-volume-spread-v1", at.AddHours(4), 112, 1, TargetPlan:
                new("risk_derived", 2, 2, 8, 100, 101.15m, 96.85m, 1, 1, 1, 10.6m, 21.2m, 2, 15, 112),
            LongBounceVolumeMode: "diagnostic_only");
        var wire = Assert.Single(RunPortfolioStateProjection.ProjectZones([detail]));
        var roundTrip = JsonSerializer.Deserialize<RunZoneDecisionDetail>(JsonSerializer.Serialize(wire))!;
        Assert.Null(roundTrip.TargetZoneId);
        Assert.Equal(8, roundTrip.TargetPlan!.PlannerLossDollars);
        Assert.Equal(10.6m, roundTrip.TargetPlan.EstimatedNetStopRisk);
        var lines = IntradayZoneDecisionPresentation.Lines(roundTrip);
        Assert.Contains(lines, line => line.Contains("Target origin: risk_derived"));
        Assert.Contains(lines, line => line.Contains("long volume diagnostic only; short minimum 1.2"));
        Assert.Contains(lines, line => line.Contains("realized reward/risk can differ"));
        Assert.DoesNotContain(lines, line => line.Contains("admission ratio omits"));
    }

    // The 101st rejected candle remains addressable, independent of the latest-hypothesis display limit.
    [Fact]
    public async Task Pages_PreserveEveryRejectedSourceRowAndDetectMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), "tf-zone-trace-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var resultPath = Path.Combine(root, "synthetic.json");
            var tracePath = Path.ChangeExtension(resultPath, ".zone-evaluations.ndjson");
            var source = Enumerable.Range(0, 205).Select(index => JsonSerializer.Serialize(new RunZoneEvaluationDetail(
                "strategy", "SYNTHETIC", null, JsonSerializer.SerializeToElement(new { Sequence = index,
                    VolumeMode = "diagnostic_only", VolumeThresholdRequired = false, TargetZone = (object?)null,
                    ZoneKnownAtUtc = "2026-10-27T13:30:00Z",
                    Retest = new { ArmTransition = new { After = new { ZoneId = 7, Version = 2 } },
                        ActiveSourceAtConfirmationStart = new { ZoneId = 7, Version = 4 },
                        ConfirmationSnapshotAsOfUtc = "2026-10-27T14:00:00Z" },
                    RetestConsumptions = new[] { new { EmittedAtUtc = "2026-10-27T14:05:00Z",
                        ArmedRetest = new { Transition = new { After = new { ZoneId = 7, Version = 2 } } } } }
                }), "target_does_not_cover_costs")));
            await File.WriteAllLinesAsync(tracePath, source);
            var reference = await BacktestArtifactIntegrity.CreateReferenceAsync(root, "zone_evaluations", tracePath, 205);
            var job = new PersistedJob { Id = Guid.NewGuid(), JobType = "backtest", RunName = "synthetic",
                Status = "completed", ResultReference = resultPath };
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new { RunName = "synthetic", ResultPath = resultPath }));
            var resultReference = await BacktestArtifactIntegrity.CreateReferenceAsync(root, "backtest_result", resultPath, 1);
            var manifest = new BacktestArtifactManifest(1, "fixture", "synthetic", DateTimeOffset.UtcNow, true,
                new(1, 1, 0, [], 0, []), [reference, resultReference]);
            await File.WriteAllTextAsync(Path.ChangeExtension(resultPath, ".manifest.json"), JsonSerializer.Serialize(manifest));
            await File.WriteAllTextAsync(Path.Combine(root, DurableJobResultPublication.MarkerFileName),
                JsonSerializer.Serialize(new DurableJobPublication(job.Id, "backtest", resultPath, DateTimeOffset.UtcNow)));
            var service = Service(job);
            var first = (await service.ZoneEvaluationsAsync(job.Id, 0, 100))!;
            var second = (await service.ZoneEvaluationsAsync(job.Id, first.NextOffset!.Value, 100))!;
            var last = (await service.ZoneEvaluationsAsync(job.Id, second.NextOffset!.Value, 100))!;
            Assert.Equal(205, first.TotalCount);
            Assert.Equal(100, second.Rows[0].Evidence.GetProperty("Sequence").GetInt32());
            Assert.Equal(5, last.Rows.Count);
            Assert.Null(last.NextOffset);
            Assert.Equal(205, first.Rows.Count + second.Rows.Count + last.Rows.Count);
            var roundTrip = JsonSerializer.Deserialize<RunZoneEvaluationPage>(JsonSerializer.Serialize(last))!;
            var lines = ZoneEvaluationPresentation.Lines(roundTrip.Rows[0]);
            Assert.Contains(lines, line => line.Contains("VolumeThresholdRequired: False"));
            Assert.Contains(lines, line => line.Contains("TargetZone: unavailable"));
            Assert.Contains(lines, line => line.Contains("ZoneKnownAtUtc: 2026-10-27T13:30:00Z"));
            Assert.Contains(lines, line => line.Contains("Retest.ArmTransition.After.Version: 2"));
            Assert.Contains(lines, line => line.Contains("Retest.ActiveSourceAtConfirmationStart.Version: 4"));
            Assert.Contains(lines, line => line.Contains("RetestConsumptions[0].EmittedAtUtc: 2026-10-27T14:05:00Z"));
            Assert.Contains(lines, line => line.Contains("RetestConsumptions[0].ArmedRetest.Transition.After.Version: 2"));
            Assert.Equal("target_does_not_cover_costs", roundTrip.Rows[0].ResolutionReason);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ZoneEvaluationsAsync(job.Id, 0, 201));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ZoneEvaluationsAsync(job.Id, -1, 50));
            await File.AppendAllTextAsync(tracePath, "{}\n");
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ZoneEvaluationsAsync(job.Id));
        }
        finally
        {
            // This unique directory contains synthetic fixture files only.
            if (Path.GetFileName(root).StartsWith("tf-zone-trace-tests-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }
    }

    // Saved-plan display names survive independently from the unique generated run name in immutable result artifacts.
    [Fact]
    public async Task GeneratedRunIdentity_AcceptsDifferentDisplayNameAndRejectsSubstitutedPublication()
    {
        var root = Path.Combine(Path.GetTempPath(), "tf-zone-trace-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var resultPath = Path.Combine(root, "result.json");
            var tracePath = Path.ChangeExtension(resultPath, ".zone-evaluations.ndjson");
            var manifestPath = Path.ChangeExtension(resultPath, ".manifest.json");
            const string generatedName = "saved-plan-unique-generated-config-name";
            var job = new PersistedJob { Id = Guid.NewGuid(), JobType = "backtest", RunName = "Saved_Plan_Display_Name",
                Status = "completed", ResultReference = resultPath };
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new { RunName = generatedName, ResultPath = resultPath }));
            await File.WriteAllTextAsync(tracePath, JsonSerializer.Serialize(new RunZoneEvaluationDetail("strategy", "SYNTHETIC",
                null, JsonSerializer.SerializeToElement(new { VolumeMode = "diagnostic_only" }), "no_pattern")) + "\n");
            var resultReference = await BacktestArtifactIntegrity.CreateReferenceAsync(root, "backtest_result", resultPath, 1);
            var traceReference = await BacktestArtifactIntegrity.CreateReferenceAsync(root, "zone_evaluations", tracePath, 1);
            var manifest = new BacktestArtifactManifest(1, "fixture", generatedName, DateTimeOffset.UtcNow, true,
                new(1, 1, 0, [], 0, []), [resultReference, traceReference]);
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));
            var markerPath = Path.Combine(root, DurableJobResultPublication.MarkerFileName);
            var marker = new DurableJobPublication(job.Id, "backtest", resultPath, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(markerPath, JsonSerializer.Serialize(marker));
            var service = Service(job);

            var page = await service.ZoneEvaluationsAsync(job.Id);
            Assert.Single(page!.Rows);
            Assert.NotEqual(generatedName, job.RunName);

            // Matching the display name cannot legitimize a substituted manifest.
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest with { RunName = job.RunName }));
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ZoneEvaluationsAsync(job.Id));
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));

            await File.WriteAllTextAsync(markerPath, JsonSerializer.Serialize(marker with { JobId = Guid.NewGuid() }));
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ZoneEvaluationsAsync(job.Id));
            await File.WriteAllTextAsync(markerPath, JsonSerializer.Serialize(marker with { ResultReference = Path.Combine(root, "other.json") }));
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ZoneEvaluationsAsync(job.Id));
            await File.WriteAllTextAsync(markerPath, JsonSerializer.Serialize(marker));

            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest with { Artifacts =
                [resultReference with { Path = "other.json" }, traceReference] }));
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ZoneEvaluationsAsync(job.Id));
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));
            await File.AppendAllTextAsync(resultPath, " ");
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ZoneEvaluationsAsync(job.Id));
        }
        finally
        {
            // Only this test's uniquely named synthetic publication is disposable.
            if (Path.GetFileName(root).StartsWith("tf-zone-trace-tests-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }
    }

    // An older result without trace evidence must explicitly say so, including when the job had zero candidates.
    [Fact]
    public async Task UnpublishedEvidence_IsNamedUnavailable()
    {
        var job = new PersistedJob { Id = Guid.NewGuid(), JobType = "backtest", Status = "running" };
        var page = await Service(job).ZoneEvaluationsAsync(job.Id);
        Assert.Empty(page!.Rows);
        Assert.Contains("after result publication", page.UnavailableReason);
    }

    private static RunWorkflowService Service(PersistedJob job)
    {
        var jobs = new Mock<IDurableJobRepository>();
        jobs.Setup(repository => repository.GetJobAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        return new(jobs.Object, Mock.Of<IUniversePreviewRepository>(), Mock.Of<IStrategyIdentityResolver>(),
            Mock.Of<IWorkflowRunConfiguration>(), TimeProvider.System, new(32, 16));
    }
}
