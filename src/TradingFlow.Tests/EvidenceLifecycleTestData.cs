using System.Security.Cryptography;
using System.Text;
using TradingFlow.Data.Evidence;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

internal static class EvidenceLifecycleTestData
{
    public static readonly DateTimeOffset Now =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    public static EvidenceCollectionPlan Plan(string runId = "lifecycle-run") =>
        new(
            Now,
            runId,
            Hash($"config-{runId}"),
            "commit-1",
            "collection-v1",
            "partition-v1",
            [
                new EvidenceCollectionRequest(
                    "bars",
                    "alpaca",
                    "/v2/stocks/bars",
                    ["AAPL"],
                    Now.AddDays(-2),
                    Now,
                    "sip",
                    "all",
                    "USD",
                    new DateOnly(2026, 7, 25))
            ]);

    public static EvidenceSourceObservation Observation(
        EvidenceCollectionPlan plan,
        EvidenceArtifactReference artifact,
        string observationId,
        DateTimeOffset receivedAtUtc) =>
        new(
            observationId,
            plan.JobId,
            plan.LogicalPlanHash,
            plan.CollectionAttemptHash,
            "bars",
            observationId,
            "alpaca",
            "/v2/stocks/bars",
            EvidenceTransportKind.Http,
            200,
            ["AAPL"],
            Now.AddDays(-2),
            Now,
            "sip",
            "all",
            "USD",
            new DateOnly(2026, 7, 25),
            null,
            null,
            null,
            receivedAtUtc,
            artifact,
            plan.RunId,
            plan.ConfigHash,
            plan.CodeVersion);

    public static EvidenceDatasetManifest Dataset(
        EvidenceCollectionPlan plan,
        EvidenceArtifactReference partition,
        EvidenceSourceReference source,
        DateTimeOffset createdAtUtc) =>
        new(
            EvidenceDatasetKind.MarketBarsAsTraded,
            1,
            createdAtUtc,
            plan.JobId,
            plan.LogicalPlanHash,
            plan.ConfigHash,
            plan.CodeVersion,
            "normalizer-v1",
            "sip",
            [
                new EvidenceDatasetPartitionManifest(
                    "AAPL-1m",
                    EvidenceDatasetKind.MarketBarsAsTraded,
                    1,
                    new EvidencePartitionProvenance(
                        "alpaca",
                        "/v2/stocks/bars",
                        "sip",
                        "all",
                        "USD",
                        new DateOnly(2026, 7, 25),
                        ["security-aapl"],
                        null,
                        ["AAPL"],
                        "1m",
                        Now.AddDays(-2),
                        Now),
                    Now.AddDays(-2),
                    Now.AddMinutes(-1),
                    10,
                    partition,
                    [source],
                    "normalizer-v1",
                    plan.CodeVersion,
                    new EvidenceQualityReport())
            ],
            new EvidenceQualityReport());

    public static async Task<EvidenceResearchRunManifest> ResearchRunAsync(
        FileSystemImmutableArtifactStore store,
        EvidenceDatasetManifest dataset,
        string runId = "research-lifecycle")
    {
        var universe = await PutAsync(store, "universe", "research/input", "application/json");
        var config = await PutAsync(store, "config", "research/input", "application/json");
        var assumptions = new EvidenceSimulationAssumptions(
            await PutAsync(store, "cost", "research/input", "application/json"),
            await PutAsync(store, "spread", "research/input", "application/json"),
            await PutAsync(store, "slippage", "research/input", "application/json"),
            await PutAsync(store, "borrow", "research/input", "application/json"),
            await PutAsync(store, "benchmark", "research/input", "application/json"));
        var output = await PutAsync(store, "report", "research/output", "application/json");
        return new EvidenceResearchRunManifest(
            runId,
            "momentum",
            Now.AddHours(2),
            [DatasetReference(dataset)],
            "universe-ledger",
            universe,
            config,
            "commit-1",
            new EvidenceStudyPartitions(
                new EvidenceStudyWindow("development", Now.AddDays(-90), Now.AddDays(-60)),
                new EvidenceStudyWindow("validation", Now.AddDays(-60), Now.AddDays(-30)),
                new EvidenceStudyWindow("holdout", Now.AddDays(-30), Now)),
            assumptions,
            null,
            evidenceReady: false,
            readinessFailures: ["crash-recovery-test"],
            outputs: [output]);
    }

    public static EvidenceDatasetReference DatasetReference(EvidenceDatasetManifest dataset)
    {
        var bytes = EvidenceCanonicalJson.SerializeToUtf8Bytes(dataset);
        return new EvidenceDatasetReference(
            dataset.DatasetId,
            Artifact(
                bytes,
                EvidenceObjectNamespaces.DatasetManifests.Value,
                "application/vnd.tradingflow.evidence-dataset+json"));
    }

    public static async Task<EvidenceArtifactReference> PutAsync(
        IImmutableArtifactStore store,
        string content,
        string objectNamespace,
        string mediaType)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var artifact = Artifact(bytes, objectNamespace, mediaType);
        await store.PutIfAbsentAsync(new ImmutableArtifactWriteRequest(artifact), bytes);
        return artifact;
    }

    public static EvidenceArtifactReference Artifact(
        ReadOnlySpan<byte> bytes,
        string objectNamespace,
        string mediaType) =>
        new(
            new EvidenceContentAddress(
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.Length,
                mediaType),
            new EvidenceObjectNamespace(objectNamespace));

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
