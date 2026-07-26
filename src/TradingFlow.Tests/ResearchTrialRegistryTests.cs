using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Data.Evidence.Governance;
using TradingFlow.Domain.Research;

namespace TradingFlow.Tests;

public sealed class ResearchTrialRegistryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-research-registry-tests",
        Guid.NewGuid().ToString("N"));
    private string databasePath = null!;
    private SqliteResearchTrialRegistry registry = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(rootPath);
        databasePath = Path.Combine(rootPath, "research-trials.db");
        registry = new SqliteResearchTrialRegistry(
            new ResearchTrialRegistryOptions(
                databasePath,
                ResearchTrialRegistryOpenMode.BootstrapNew));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task TrialRegistration_IsImmutableIdempotentAndCountsFamilyTrials()
    {
        var first = Trial("swing-momentum-001", 1);
        Assert.False((await registry.RegisterTrialAsync(first)).AlreadyCommitted);
        Assert.True((await registry.RegisterTrialAsync(first)).AlreadyCommitted);

        var mutation = Trial(
            "swing-momentum-001",
            1,
            hypothesis: "A changed hypothesis must use a new experiment ID.");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            registry.RegisterTrialAsync(mutation));

        var skippedCount = Trial(
            "swing-momentum-002",
            3,
            parentExperimentId: first.ExperimentId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.RegisterTrialAsync(skippedCount));

        var second = Trial(
            "swing-momentum-002",
            2,
            parentExperimentId: first.ExperimentId);
        Assert.False((await registry.RegisterTrialAsync(second)).AlreadyCommitted);
        var stored = await registry.GetTrialAsync(second.ExperimentId);
        Assert.NotNull(stored);
        Assert.Equal(
            EvidenceCanonicalJson.SerializeToUtf8Bytes(second),
            EvidenceCanonicalJson.SerializeToUtf8Bytes(stored!));
    }

    [Fact]
    public async Task ResultManifest_MustMatchFrozenTrialAndCannotBeMutated()
    {
        var trial = Trial("swing-momentum-001", 1);
        await registry.RegisterTrialAsync(trial);
        var manifest = Result(trial, "result-001", 0.012m);

        Assert.False((await registry.RegisterResultAsync(manifest)).AlreadyCommitted);
        Assert.True((await registry.RegisterResultAsync(manifest)).AlreadyCommitted);
        var stored = await registry.GetResultAsync(manifest.ResultId);
        Assert.NotNull(stored);
        Assert.Equal(
            EvidenceCanonicalJson.SerializeToUtf8Bytes(manifest),
            EvidenceCanonicalJson.SerializeToUtf8Bytes(stored!));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            registry.RegisterResultAsync(Result(trial, "result-001", -0.50m)));

        var wrongHash = new CanonicalResearchResultManifest(
            "result-002",
            trial.ExperimentId,
            Hash("not-the-trial"),
            "run-002",
            "commit-a",
            "validation",
            trial.PrimaryMetric,
            new Dictionary<string, decimal> { [trial.PrimaryMetric] = 0.01m },
            [Hash("artifact-2")],
            Now.AddHours(2));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            registry.RegisterResultAsync(wrongHash));
    }

    [Fact]
    public async Task HoldoutConsumption_IsAppendOnlyAndFailsClosedOnMismatch()
    {
        var trial = Trial("swing-momentum-001", 1);
        await registry.RegisterTrialAsync(trial);
        Assert.Equal(
            ResearchHoldoutState.Unopened,
            await registry.GetHoldoutStateAsync(trial.ExperimentId));

        var consumption = new ResearchHoldoutConsumptionRecord(
            trial.ExperimentId,
            "run-holdout-001",
            Now.AddDays(1));
        Assert.False((await registry.ConsumeHoldoutAsync(consumption)).AlreadyCommitted);
        Assert.True((await registry.ConsumeHoldoutAsync(consumption)).AlreadyCommitted);
        Assert.Equal(
            ResearchHoldoutState.Consumed,
            await registry.GetHoldoutStateAsync(trial.ExperimentId));

        var secondConsumption = new ResearchHoldoutConsumptionRecord(
            trial.ExperimentId,
            "run-holdout-002",
            Now.AddDays(2));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            registry.ConsumeHoldoutAsync(secondConsumption));
    }

    [Fact]
    public async Task ReadingTamperedCanonicalTrial_FailsClosed()
    {
        var trial = Trial("swing-momentum-001", 1);
        await registry.RegisterTrialAsync(trial);
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE research_trials SET canonical_json = '{}' WHERE experiment_id = $id;";
            command.Parameters.AddWithValue("$id", trial.ExperimentId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAnyAsync<Exception>(() =>
            registry.GetTrialAsync(trial.ExperimentId));
    }

    private static ResearchTrialDefinition Trial(
        string experimentId,
        int familyTrialCount,
        string? parentExperimentId = null,
        string hypothesis = "Twelve-minus-one momentum earns positive excess return.") =>
        new(
            experimentId,
            "swing-momentum",
            parentExperimentId,
            familyTrialCount,
            hypothesis,
            ["dataset-bars", "dataset-membership"],
            new Dictionary<string, string>
            {
                ["decision"] = "completed month-end bar",
                ["execution"] = "next regular-session open"
            },
            new Dictionary<string, string>
            {
                ["momentum_12_1"] = "adjusted_close[t-21]/adjusted_close[t-252]-1"
            },
            new Dictionary<string, decimal>
            {
                ["selection_fraction"] = 0.30m
            },
            new Dictionary<string, string>
            {
                ["round_trip_cost"] = "frozen quote-side model v1"
            },
            [
                new ResearchPartitionDefinition(
                    "development",
                    Now.AddYears(-2),
                    Now.AddYears(-1),
                    TimeSpan.FromDays(20)),
                new ResearchPartitionDefinition(
                    "validation",
                    Now.AddYears(-1),
                    Now,
                    TimeSpan.FromDays(20)),
                new ResearchPartitionDefinition(
                    "holdout",
                    Now,
                    Now.AddYears(1),
                    TimeSpan.FromDays(20))
            ],
            "net_excess_return",
            ["validation estimate is non-positive", "holdout lower confidence bound is non-positive"],
            ResearchHoldoutState.Unopened,
            Now);

    private static CanonicalResearchResultManifest Result(
        ResearchTrialDefinition trial,
        string resultId,
        decimal metric) =>
        new(
            resultId,
            trial.ExperimentId,
            EvidenceCanonicalJson.ComputeSha256(trial),
            "run-001",
            "commit-a",
            "validation",
            trial.PrimaryMetric,
            new Dictionary<string, decimal> { [trial.PrimaryMetric] = metric },
            [Hash("artifact-1")],
            Now.AddHours(1));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
