using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Application.Candidates;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Application;
using TradingFlow.Data.Context;
using TradingFlow.Data.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Tests;

public sealed class CandidateApplicationProjectionTests
{
    [Fact]
    public async Task DurableCandidateProjection_PreservesSourcesAndDoesNotInventExposureOrMissingEvidence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(connection).Options;
        await using var context = new TradingFlowDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var factory = new SharedContextFactory(options);
        var now = DateTimeOffset.Parse("2026-09-16T14:00:00Z");
        var runId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        context.ProductionRuns.Add(new ProductionRun
        {
            RunId = runId,
            UniverseSnapshotId = snapshotId,
            DecisionRunId = runId,
            Profile = "paper_shadow",
            Status = "running",
            StartedAtUtc = now,
            ConfigHash = new string('a', 64),
            CodeVersion = "test"
        });
        context.Candidates.Add(new CandidateRecord
        {
            CandidateId = candidateId,
            RunId = runId,
            ConfigHash = new string('a', 64),
            CodeVersion = "test",
            Symbol = "MSFT",
            DiscoveredAtUtc = now.AddMinutes(-1),
            RevalidatedAtUtc = now,
            DiscoverySource = "wishlist+finviz",
            FinvizPreset = "swing-quality",
            Horizon = "swing",
            SelectedStrategy = "swing.momentum",
            StrategySemanticVersion = "1.0.0",
            StrategyContentSha256 = new string('b', 64),
            AdmissionProfileId = "swing.momentum",
            AdmissionProfileVersion = "1.0.0",
            SetupKey = "setup-msft",
            DiscoveryWindowStartUtc = now.AddMinutes(-1),
            DiscoveryWindowEndUtc = now,
            State = StrategyCandidateState.Consumed,
            Version = 2,
            ExpiresAtUtc = now.AddMinutes(2),
            SemanticDecisionSha256 = new string('c', 64),
            SetupScoresJson = JsonSerializer.Serialize(new
            {
                sources = new object[]
                {
                    new { sourceKind = "wishlist", sourceKey = "long", observedAtUtc = now.AddMinutes(-1), expiresAtUtc = now.AddMinutes(2), contentSha256 = new string('d', 64), providerReference = (string?)null },
                    new { sourceKind = "finviz", sourceKey = "swing-quality", observedAtUtc = now.AddSeconds(-30), expiresAtUtc = now.AddMinutes(2), contentSha256 = new string('e', 64), providerReference = "finviz:swing-quality" }
                }
            }),
            RejectReasonsJson = "[]"
        });
        context.ApplicationEvents.Add(new ApplicationEventRecord
        {
            StreamName = $"candidate-runs/{runId:N}",
            EventType = "candidate.changed",
            OccurredAtUtc = now,
            PayloadJson = "{}"
        });
        await context.SaveChangesAsync();

        var service = new CandidateApplicationService(
            new SqliteCandidateRepository(factory),
            new SqliteGateEvaluationRepository(factory),
            new SqliteOrderIntentRepository(factory),
            new SqliteCandidateAuditEvidenceRepository(factory),
            new SqliteApplicationWorkflowRepository(factory),
            new FixedTimeProvider(now));

        var run = await service.GetRunAsync(runId);
        var candidate = Assert.Single(Assert.IsType<CandidateRunResponse>(run).Candidates);
        Assert.Equal("consumed", candidate.Readiness);
        Assert.Equal(2, candidate.Sources?.Count);
        Assert.Null(candidate.LatestCompletedCandleAtUtc);
        Assert.Null(candidate.SameTimeRelativeVolume);
        Assert.Null(candidate.RelativeVolumeSampleCount);

        var audit = await service.GetAsync(candidateId);
        Assert.NotNull(audit);
        Assert.Null(audit.MarketEvidence);
        Assert.Null(audit.CatalystEvidence);
        Assert.Null(audit.ExecutionEvidence);
        Assert.True(run!.LatestEventSequence > 0);
    }

    private sealed class SharedContextFactory(DbContextOptions<TradingFlowDbContext> options) :
        IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
