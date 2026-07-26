using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Governance;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public sealed class SqliteStrategyPromotionRegistryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-promotion-tests",
        Guid.NewGuid().ToString("N"));
    private FileSystemImmutableArtifactStore store = null!;
    private SqliteEvidenceCatalog catalog = null!;
    private SqliteStrategyPromotionRegistry registry = null!;
    private EvidenceResearchRunManifest run = null!;
    private StrategyPromotionEvidence promotionEvidence = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(rootPath);
        store = new FileSystemImmutableArtifactStore(
            new ImmutableArtifactStoreOptions(Path.Combine(rootPath, "objects")));
        catalog = new SqliteEvidenceCatalog(
            new EvidenceCatalogOptions(
                Path.Combine(rootPath, "evidence.db"),
                EvidenceCatalogOpenMode.BootstrapNew),
            store);
        run = await CreateReadyResearchRunAsync();
        await catalog.RegisterResearchRunAsync(run);
        promotionEvidence = new StrategyPromotionEvidence(
            run.Outputs[0],
            run.Outputs[1],
            run.Outputs[2],
            run.Assumptions.CostModel,
            run.Outputs[3]);
        registry = NewRegistry(StrategyPromotionRegistryOpenMode.BootstrapNew);
        Assert.Empty(await registry.GetDecisionHistoryAsync());
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
    public async Task RegisterDecision_RequiresIndependentAuthorizedHumanPrincipal()
    {
        var decision = Decision("accepted", StrategyPromotionDecisionStatus.Accepted);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            registry.RegisterDecisionAsync(decision, "intruder@example.test"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            registry.RegisterDecisionAsync(decision, "reviewer@example.test"));

        Assert.Empty(await registry.GetDecisionHistoryAsync());
    }

    [Fact]
    public async Task ActiveCatalog_IsAcceptedMinusRejectedRevokedAndSuperseded()
    {
        var accepted = Decision("accepted", StrategyPromotionDecisionStatus.Accepted);
        var rejected = Decision("rejected", StrategyPromotionDecisionStatus.Rejected);
        await registry.RegisterDecisionAsync(accepted, accepted.ApprovedBy);
        await registry.RegisterDecisionAsync(rejected, rejected.ApprovedBy);
        Assert.Equal(
            [accepted.DecisionId],
            (await registry.GetActiveDecisionsAsync()).Select(item => item.DecisionId));

        var revoked = Decision(
            "revoked",
            StrategyPromotionDecisionStatus.Revoked,
            accepted.DecisionId);
        await registry.RegisterDecisionAsync(revoked, revoked.ApprovedBy);

        Assert.Empty(await registry.GetActiveDecisionsAsync());
        Assert.Equal(
            [accepted.DecisionId, rejected.DecisionId, revoked.DecisionId],
            (await registry.GetDecisionHistoryAsync())
                .Select(item => item.DecisionId)
                .OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task RegisterDecision_IsIdempotentAndConcurrent()
    {
        var decision = Decision("accepted", StrategyPromotionDecisionStatus.Accepted);

        var results = await Task.WhenAll(
            registry.RegisterDecisionAsync(decision, decision.ApprovedBy),
            NewRegistry(StrategyPromotionRegistryOpenMode.OpenExisting)
                .RegisterDecisionAsync(decision, decision.ApprovedBy));

        Assert.Single(results, result => !result.AlreadyCommitted);
        Assert.Single(results, result => result.AlreadyCommitted);
        Assert.Single(await registry.GetDecisionHistoryAsync());
    }

    [Fact]
    public async Task ConcurrentTerminalDecisions_AllowOnlyOnePermanentOutcome()
    {
        var accepted = Decision("accepted", StrategyPromotionDecisionStatus.Accepted);
        await registry.RegisterDecisionAsync(accepted, accepted.ApprovedBy);
        var revoked = Decision(
            "revoked",
            StrategyPromotionDecisionStatus.Revoked,
            accepted.DecisionId);
        var superseded = Decision(
            "superseded",
            StrategyPromotionDecisionStatus.Superseded,
            accepted.DecisionId);

        var attempts = await Task.WhenAll(
            CaptureAsync(() => registry.RegisterDecisionAsync(revoked, revoked.ApprovedBy)),
            CaptureAsync(() => NewRegistry(StrategyPromotionRegistryOpenMode.OpenExisting)
                .RegisterDecisionAsync(superseded, superseded.ApprovedBy)));

        Assert.Single(attempts, attempt => attempt.Result is not null);
        Assert.Single(attempts, attempt => attempt.Error is InvalidOperationException);
        Assert.Empty(await registry.GetActiveDecisionsAsync());
        Assert.Equal(2, (await registry.GetDecisionHistoryAsync()).Count);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("config")]
    [InlineData("holdout")]
    [InlineData("universe")]
    public async Task RegisterDecision_RejectsForgedResearchIdentity(string forgery)
    {
        var decision = forgery switch
        {
            "manifest" => Decision(
                "forged",
                StrategyPromotionDecisionStatus.Accepted,
                manifestHash: Hash("forged-manifest")),
            "config" => Decision(
                "forged",
                StrategyPromotionDecisionStatus.Accepted,
                configHash: Hash("forged-config")),
            "holdout" => Decision(
                "forged",
                StrategyPromotionDecisionStatus.Accepted,
                holdoutId: Hash("forged-holdout")),
            "universe" => Decision(
                "forged",
                StrategyPromotionDecisionStatus.Accepted,
                universeLedgerId: "forged-ledger"),
            _ => throw new InvalidOperationException()
        };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            registry.RegisterDecisionAsync(decision, decision.ApprovedBy));
        Assert.Empty(await registry.GetDecisionHistoryAsync());
    }

    [Fact]
    public async Task DecisionHistory_FailsClosedWhenRegistryJsonIsModified()
    {
        var decision = Decision("accepted", StrategyPromotionDecisionStatus.Accepted);
        await registry.RegisterDecisionAsync(decision, decision.ApprovedBy);
        await using (var connection = new SqliteConnection(
                         $"Data Source={Path.Combine(rootPath, "promotions.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE promotion_decisions SET canonical_json = '{}' WHERE decision_id = $id;";
            command.Parameters.AddWithValue("$id", decision.DecisionId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            registry.GetDecisionHistoryAsync());
    }

    private SqliteStrategyPromotionRegistry NewRegistry(
        StrategyPromotionRegistryOpenMode mode) =>
        new(
            new StrategyPromotionRegistryOptions(
                Path.Combine(rootPath, "promotions.db"),
                mode),
            catalog,
            catalog,
            store,
            new ConfiguredPromotionPrincipalAuthorizer(["governor@example.test"]));

    private StrategyPromotionDecision Decision(
        string decisionId,
        StrategyPromotionDecisionStatus status,
        string? targetDecisionId = null,
        string? manifestHash = null,
        string? configHash = null,
        string? holdoutId = null,
        string? universeLedgerId = null) =>
        new(
            decisionId,
            status,
            new EvidenceResearchRunReference(
                run.ResearchRunId,
                manifestHash ?? ResearchManifestHash(run)),
            holdoutId ?? run.Holdout!.HoldoutId,
            run.StudyId,
            configHash ?? run.StudyConfigArtifact.Content.Sha256,
            run.CodeVersion,
            run.InputDatasets,
            universeLedgerId ?? run.UniverseLedgerId,
            run.UniverseLedgerArtifact,
            promotionEvidence,
            "governor@example.test",
            Now.AddMinutes(decisionId.Length),
            $"Governance decision {decisionId}.",
            targetDecisionId);

    private async Task<EvidenceResearchRunManifest> CreateReadyResearchRunAsync()
    {
        var plan = EvidenceLifecycleTestData.Plan("promotion-evidence");
        await catalog.RegisterCollectionPlanAsync(plan);
        var raw = await EvidenceLifecycleTestData.PutAsync(
            store,
            "raw",
            "raw/alpaca",
            "application/json");
        var observation = EvidenceLifecycleTestData.Observation(
            plan,
            raw,
            "promotion-observation",
            Now);
        await catalog.RegisterSourceObservationAsync(observation);
        var partition = await EvidenceLifecycleTestData.PutAsync(
            store,
            "partition",
            "partitions/bars",
            "application/x-parquet");
        var dataset = EvidenceLifecycleTestData.Dataset(
            plan,
            partition,
            observation.ToReference(),
            Now.AddMinutes(1));
        await catalog.CommitDatasetAsync(dataset);
        var datasetReference = EvidenceLifecycleTestData.DatasetReference(dataset);
        var universe = await PutAsync("universe");
        var config = await PutAsync("config");
        var partitionDefinition = await PutAsync("partition-definition");
        var holdout = new EvidenceHoldoutIdentity(
            "strategy-a",
            Now.AddDays(30),
            Now.AddDays(60),
            [datasetReference],
            "universe-a",
            universe,
            partitionDefinition);
        var assumptions = new EvidenceSimulationAssumptions(
            await PutAsync("cost"),
            await PutAsync("spread"),
            await PutAsync("slippage"),
            await PutAsync("borrow"),
            await PutAsync("benchmark"));
        var outputs = new[]
        {
            await PutAsync("development-report"),
            await PutAsync("validation-report"),
            await PutAsync("holdout-report"),
            await PutAsync("concentration-report")
        };
        return new EvidenceResearchRunManifest(
            "research-a",
            holdout.StudyFamily,
            Now.AddHours(1),
            holdout.Datasets,
            holdout.UniverseLedgerId,
            holdout.UniverseLedgerArtifact,
            config,
            "commit-1",
            new EvidenceStudyPartitions(
                new EvidenceStudyWindow("development", Now.AddDays(-30), Now),
                new EvidenceStudyWindow("validation", Now, holdout.StartUtc),
                new EvidenceStudyWindow("holdout", holdout.StartUtc, holdout.EndUtc)),
            assumptions,
            holdout,
            evidenceReady: true,
            readinessFailures: null,
            outputs);
    }

    private async Task<EvidenceArtifactReference> PutAsync(string value) =>
        await EvidenceLifecycleTestData.PutAsync(
            store,
            value,
            "research/governance-tests",
            "application/json");

    private static string ResearchManifestHash(EvidenceResearchRunManifest manifest) =>
        Convert.ToHexString(
                SHA256.HashData(EvidenceCanonicalJson.SerializeToUtf8Bytes(manifest)))
            .ToLowerInvariant();

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static async Task<RegistrationAttempt> CaptureAsync(
        Func<Task<EvidenceCatalogCommitResult>> operation)
    {
        try
        {
            return new RegistrationAttempt(await operation(), null);
        }
        catch (Exception error)
        {
            return new RegistrationAttempt(null, error);
        }
    }

    private sealed record RegistrationAttempt(
        EvidenceCatalogCommitResult? Result,
        Exception? Error);
}
