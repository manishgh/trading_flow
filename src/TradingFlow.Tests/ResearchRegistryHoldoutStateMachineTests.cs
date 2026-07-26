using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Data.Evidence.Governance;
using TradingFlow.Domain.Research;

namespace TradingFlow.Tests;

public sealed class ResearchRegistryHoldoutStateMachineTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-research-holdout-tests",
        Guid.NewGuid().ToString("N"));
    private string databasePath = null!;
    private SqliteResearchTrialRegistry registry = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(rootPath);
        databasePath = Path.Combine(rootPath, "research-trials.db");
        registry = Registry(ResearchTrialRegistryOpenMode.BootstrapNew);
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
    public async Task BeginHoldoutEvaluation_IsIdempotentOnlyForIdenticalReservation()
    {
        var trial = Trial();
        await registry.RegisterTrialAsync(trial);
        var reservation = Consumption(trial, "run-holdout-001");

        Assert.False(
            (await registry.BeginHoldoutEvaluationAsync(reservation)).AlreadyCommitted);
        Assert.True(
            (await registry.BeginHoldoutEvaluationAsync(reservation)).AlreadyCommitted);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            registry.BeginHoldoutEvaluationAsync(
                Consumption(trial, "run-holdout-002")));
        Assert.Equal(
            ResearchHoldoutState.Consumed,
            await registry.GetHoldoutStateAsync(trial.ExperimentId));
        Assert.Equal(1, await CountAsync("holdout_consumptions"));
    }

    [Fact]
    public async Task BeginHoldoutEvaluation_RollsBackWhenTrialDoesNotExist()
    {
        var reservation = new ResearchHoldoutConsumptionRecord(
            "missing-experiment",
            "run-holdout-001",
            Now);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.BeginHoldoutEvaluationAsync(reservation));

        Assert.Equal(0, await CountAsync("holdout_consumptions"));
    }

    [Fact]
    public async Task ConcurrentBegin_AllowsExactlyOneRunToAcquireHoldout()
    {
        var trial = Trial();
        await registry.RegisterTrialAsync(trial);
        var firstRegistry = Registry(ResearchTrialRegistryOpenMode.OpenExisting);
        var secondRegistry = Registry(ResearchTrialRegistryOpenMode.OpenExisting);

        var attempts = await Task.WhenAll(
            CaptureAsync(() => firstRegistry.BeginHoldoutEvaluationAsync(
                Consumption(trial, "run-holdout-001"))),
            CaptureAsync(() => secondRegistry.BeginHoldoutEvaluationAsync(
                Consumption(trial, "run-holdout-002"))));

        Assert.Single(attempts, attempt => attempt.Result is not null);
        Assert.Single(attempts, attempt => attempt.Error is InvalidDataException);
        Assert.Equal(1, await CountAsync("holdout_consumptions"));
    }

    [Fact]
    public async Task ConcurrentIdenticalBegin_IsOneCommitAndOneIdempotentReplay()
    {
        var trial = Trial();
        await registry.RegisterTrialAsync(trial);
        var reservation = Consumption(trial, "run-holdout-001");
        var firstRegistry = Registry(ResearchTrialRegistryOpenMode.OpenExisting);
        var secondRegistry = Registry(ResearchTrialRegistryOpenMode.OpenExisting);

        var results = await Task.WhenAll(
            firstRegistry.BeginHoldoutEvaluationAsync(reservation),
            secondRegistry.BeginHoldoutEvaluationAsync(reservation));

        Assert.Contains(results, result => !result.AlreadyCommitted);
        Assert.Contains(results, result => result.AlreadyCommitted);
        Assert.Equal(1, await CountAsync("holdout_consumptions"));
    }

    [Fact]
    public async Task CompleteHoldoutEvaluation_RequiresPriorMatchingBegin()
    {
        var trial = Trial();
        await registry.RegisterTrialAsync(trial);
        var manifest = HoldoutResult(trial, "result-holdout-001", "run-holdout-001");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.CompleteHoldoutEvaluationAsync(manifest));

        Assert.Equal(0, await CountAsync("research_results"));
        Assert.Equal(
            ResearchHoldoutState.Unopened,
            await registry.GetHoldoutStateAsync(trial.ExperimentId));
    }

    [Fact]
    public async Task CompleteHoldoutEvaluation_WrongRunRollsBackButConsumptionRemainsFinal()
    {
        var trial = Trial();
        await registry.RegisterTrialAsync(trial);
        await registry.BeginHoldoutEvaluationAsync(
            Consumption(trial, "run-holdout-001"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            registry.CompleteHoldoutEvaluationAsync(
                HoldoutResult(trial, "result-holdout-001", "run-holdout-002")));

        Assert.Equal(0, await CountAsync("research_results"));
        Assert.Equal(1, await CountAsync("holdout_consumptions"));
        Assert.Equal(
            ResearchHoldoutState.Consumed,
            await registry.GetHoldoutStateAsync(trial.ExperimentId));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            registry.BeginHoldoutEvaluationAsync(
                Consumption(trial, "run-holdout-002")));
    }

    [Fact]
    public async Task CompleteHoldoutEvaluation_IsIdempotentOnlyForIdenticalResult()
    {
        var trial = Trial();
        await registry.RegisterTrialAsync(trial);
        await registry.BeginHoldoutEvaluationAsync(
            Consumption(trial, "run-holdout-001"));
        var manifest = HoldoutResult(
            trial,
            "result-holdout-001",
            "run-holdout-001");

        Assert.False(
            (await registry.CompleteHoldoutEvaluationAsync(manifest)).AlreadyCommitted);
        Assert.True(
            (await registry.CompleteHoldoutEvaluationAsync(manifest)).AlreadyCommitted);
        Assert.Equal(
            ResearchHoldoutState.Completed,
            await registry.GetHoldoutStateAsync(trial.ExperimentId));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            registry.CompleteHoldoutEvaluationAsync(
                HoldoutResult(
                    trial,
                    "result-holdout-002",
                    "run-holdout-001",
                    metric: -0.50m)));

        Assert.Equal(1, await CountAsync("research_results"));
    }

    [Fact]
    public async Task ConcurrentComplete_AllowsOnlyOneCanonicalHoldoutResult()
    {
        var trial = Trial();
        await registry.RegisterTrialAsync(trial);
        await registry.BeginHoldoutEvaluationAsync(
            Consumption(trial, "run-holdout-001"));
        var firstRegistry = Registry(ResearchTrialRegistryOpenMode.OpenExisting);
        var secondRegistry = Registry(ResearchTrialRegistryOpenMode.OpenExisting);

        var attempts = await Task.WhenAll(
            CaptureAsync(() => firstRegistry.CompleteHoldoutEvaluationAsync(
                HoldoutResult(
                    trial,
                    "result-holdout-001",
                    "run-holdout-001",
                    metric: 0.01m))),
            CaptureAsync(() => secondRegistry.CompleteHoldoutEvaluationAsync(
                HoldoutResult(
                    trial,
                    "result-holdout-002",
                    "run-holdout-001",
                    metric: 0.02m))));

        Assert.Single(attempts, attempt => attempt.Result is not null);
        Assert.Single(attempts, attempt => attempt.Error is InvalidDataException);
        Assert.Equal(1, await CountAsync("research_results"));
    }

    [Fact]
    public async Task ConcurrentIdenticalComplete_IsOneCommitAndOneIdempotentReplay()
    {
        var trial = Trial();
        await registry.RegisterTrialAsync(trial);
        await registry.BeginHoldoutEvaluationAsync(
            Consumption(trial, "run-holdout-001"));
        var manifest = HoldoutResult(
            trial,
            "result-holdout-001",
            "run-holdout-001");
        var firstRegistry = Registry(ResearchTrialRegistryOpenMode.OpenExisting);
        var secondRegistry = Registry(ResearchTrialRegistryOpenMode.OpenExisting);

        var results = await Task.WhenAll(
            firstRegistry.CompleteHoldoutEvaluationAsync(manifest),
            secondRegistry.CompleteHoldoutEvaluationAsync(manifest));

        Assert.Contains(results, result => !result.AlreadyCommitted);
        Assert.Contains(results, result => result.AlreadyCommitted);
        Assert.Equal(1, await CountAsync("research_results"));
    }

    [Fact]
    public async Task RegisterResult_RejectsHoldoutBeforeAndAfterReservation()
    {
        var trial = Trial();
        await registry.RegisterTrialAsync(trial);
        var manifest = HoldoutResult(
            trial,
            "result-holdout-001",
            "run-holdout-001");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.RegisterResultAsync(manifest));
        await registry.BeginHoldoutEvaluationAsync(
            Consumption(trial, "run-holdout-001"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.RegisterResultAsync(manifest));

        Assert.Equal(0, await CountAsync("research_results"));
    }

    [Fact]
    public async Task SchemaRejectsDirectHoldoutResultWithoutMatchingReservation()
    {
        var trial = Trial();
        await registry.RegisterTrialAsync(trial);
        var manifest = HoldoutResult(
            trial,
            "result-holdout-001",
            "run-holdout-001");
        var json = Encoding.UTF8.GetString(
            EvidenceCanonicalJson.SerializeToUtf8Bytes(manifest));

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO research_results(
                result_id, experiment_id, research_run_id, partition,
                created_at_utc, sha256, canonical_json)
            VALUES($id, $experiment, $run, $partition, $created, $hash, $json);
            """;
        command.Parameters.AddWithValue("$id", manifest.ResultId);
        command.Parameters.AddWithValue("$experiment", manifest.ExperimentId);
        command.Parameters.AddWithValue("$run", manifest.ResearchRunId);
        command.Parameters.AddWithValue("$partition", manifest.Partition);
        command.Parameters.AddWithValue("$created", manifest.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue(
            "$hash",
            EvidenceCanonicalJson.ComputeSha256(manifest));
        command.Parameters.AddWithValue("$json", json);

        await Assert.ThrowsAsync<SqliteException>(() =>
            command.ExecuteNonQueryAsync());
        Assert.Equal(0, await CountAsync("research_results"));
    }

    [Fact]
    public async Task DevelopmentAndValidationEachAllowOneCanonicalResult()
    {
        var trial = Trial();
        await registry.RegisterTrialAsync(trial);
        var validation = Result(
            trial,
            "result-validation-001",
            "run-validation-001",
            "validation",
            0.01m);

        Assert.False((await registry.RegisterResultAsync(validation)).AlreadyCommitted);
        Assert.True((await registry.RegisterResultAsync(validation)).AlreadyCommitted);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            registry.RegisterResultAsync(
                Result(
                    trial,
                    "result-validation-002",
                    "run-validation-002",
                    "validation",
                    0.02m)));

        Assert.False(
            (await registry.RegisterResultAsync(
                Result(
                    trial,
                    "result-development-001",
                    "run-development-001",
                    "development",
                    0.03m))).AlreadyCommitted);
        Assert.Equal(2, await CountAsync("research_results"));
    }

    private SqliteResearchTrialRegistry Registry(ResearchTrialRegistryOpenMode mode) =>
        new(new ResearchTrialRegistryOptions(databasePath, mode));

    private async Task<long> CountAsync(string table)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<Attempt> CaptureAsync(
        Func<Task<ResearchRegistryCommitResult>> action)
    {
        try
        {
            return new Attempt(await action(), null);
        }
        catch (Exception exception)
        {
            return new Attempt(null, exception);
        }
    }

    private static ResearchTrialDefinition Trial() =>
        new(
            "swing-momentum-001",
            "swing-momentum",
            null,
            1,
            "Twelve-minus-one momentum earns positive excess return.",
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
            ["validation estimate is non-positive"],
            ResearchHoldoutState.Unopened,
            Now);

    private static ResearchHoldoutConsumptionRecord Consumption(
        ResearchTrialDefinition trial,
        string researchRunId) =>
        new(trial.ExperimentId, researchRunId, Now.AddHours(1));

    private static CanonicalResearchResultManifest HoldoutResult(
        ResearchTrialDefinition trial,
        string resultId,
        string researchRunId,
        decimal metric = 0.01m) =>
        Result(trial, resultId, researchRunId, "holdout", metric);

    private static CanonicalResearchResultManifest Result(
        ResearchTrialDefinition trial,
        string resultId,
        string researchRunId,
        string partition,
        decimal metric) =>
        new(
            resultId,
            trial.ExperimentId,
            EvidenceCanonicalJson.ComputeSha256(trial),
            researchRunId,
            "commit-a",
            partition,
            trial.PrimaryMetric,
            new Dictionary<string, decimal> { [trial.PrimaryMetric] = metric },
            [Hash($"{resultId}-artifact")],
            Now.AddHours(2));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed record Attempt(
        ResearchRegistryCommitResult? Result,
        Exception? Error);
}
