using System.Text;
using TradingFlow.Data.Evidence.Governance;
using TradingFlow.Data.Evidence.Research;
using TradingFlow.Domain.Research;
using TradingFlow.Research.Momentum;

namespace TradingFlow.Research.Workflows;

public sealed partial class CatalogResearchWorkflow
{
    private const string RegistrationOutput = "research-registration";

    /// <summary>Freezes the registered workflow, inputs, costs, and chronological windows without opening any observations.</summary>
    private static FrozenResearchStart FreezeCatalogStudy<T>(string researchRunId, string family, string hypothesis, T request,
        IReadOnlyList<string> datasets, EvidenceStudyPartitions windows, CatalogResearchAssumptionSet assumptions,
        string primaryMetric, string? momentumRequestHash = null)
    {
        var requestHash = EvidenceCanonicalJson.ComputeSha256(request);
        var codeHash = ResearchStartIdentity.CodeSha256(typeof(CatalogResearchWorkflow).Assembly,
            typeof(CrossSectionalMomentumResearchAnalyzer).Assembly, typeof(ResearchTrialDefinition).Assembly,
            typeof(EvidenceResearchRunArtifactPackager).Assembly, typeof(TradingFlow.Engine.Research.IEvidencePartitionDataReader).Assembly);
        var formulas = new Dictionary<string, string>
        {
            ["frozen_config_sha256"] = requestHash,
            ["price_basis"] = family == "catalog.daily-news" ? "as-traded daily bars" : "research-adjusted returns and as-traded liquidity",
            ["time_horizon_and_rules"] = Encoding.UTF8.GetString(EvidenceCanonicalJson.SerializeToUtf8Bytes(request))
        };
        if (momentumRequestHash is not null) formulas.Add(CatalogResearchTrialBinding.RequestBindingFormulaKey, momentumRequestHash);
        var partitions = new[] { windows.Development, windows.Validation, windows.Holdout }
            .Select(window => new ResearchPartitionDefinition(window.Name, window.StartUtc, window.EndUtc, TimeSpan.Zero)).ToArray();
        var variant = new FrozenResearchVariant(requestHash, family, hypothesis, requestHash, datasets,
            new Dictionary<string, string> { ["market_clock"] = "America/New_York", ["stored_clock"] = "UTC" }, formulas,
            new Dictionary<string, decimal> { ["minimum_required_evidence_partitions"] = 1m },
            new Dictionary<string, string> { ["assumptions"] = Encoding.UTF8.GetString(EvidenceCanonicalJson.SerializeToUtf8Bytes(assumptions)) },
            partitions, primaryMetric,
            ["Missing or out-of-scope observations, failed exact dataset or benchmark checks, or a failed frozen study criterion"]);
        return new(ResearchStartIdentity.StartId("catalog-study", researchRunId), ResearchStartKind.CatalogStudy, requestHash, codeHash,
            [variant], new Dictionary<string, string[]> { [researchRunId] = [variant.VariantKey] });
    }

    /// <summary>Reconciles the exact catalog and trial registry publications without rerunning already stored results.</summary>
    private async Task<T> RunRegisteredCatalogAsync<T>(FrozenResearchStart frozen, string runId,
        Func<ResearchStart, CancellationToken, Task<T>> compute,
        Func<EvidenceResearchRunManifest, CancellationToken, Task<T>> load,
        Func<T, EvidenceResearchRunManifest> manifestFor,
        Func<T, EvidenceStudyWindow, decimal> metric,
        CancellationToken token)
    {
        return await trialRegistry.RunDirectAsync(frozen, runId, async (start, ct) =>
        {
            var published = await catalog.GetResearchRunAsync(runId, ct);
            var result = published is null ? await compute(start, ct) :
                await LoadMatchingPublicationAsync(published, start.Binding, load, ct);
            var manifest = manifestFor(result);
            var trial = start.Binding.Trials.Single();
            var primaryMetric = frozen.Variants.Single().PrimaryMetric;
            var sha = EvidenceCanonicalJson.ComputeSha256(manifest);
            var hashes = manifest.Outputs.Select(output => output.Content.Sha256).Append(sha).ToArray();
            var results = new[] { manifest.StudyPartitions.Development, manifest.StudyPartitions.Validation }.Select(window =>
                new CanonicalResearchResultManifest(trial.ExperimentId + "-" + window.Name, trial.ExperimentId, trial.DefinitionSha256,
                    frozen.StartId, frozen.CodeSha256, window.Name, primaryMetric,
                    new Dictionary<string, decimal> { [primaryMetric] = metric(result, window) },
                    hashes, start.RegisteredAtUtc)).ToArray();
            return new DirectResearchCompletion<T>(result, runId, sha, results);
        }, async (prior, ct) =>
        {
            var manifest = await catalog.GetResearchRunAsync(runId, ct);
            if (manifest is null || prior.ArtifactReference != runId || prior.ArtifactSha256 != EvidenceCanonicalJson.ComputeSha256(manifest))
                throw new InvalidDataException("The published catalog study differs from its recorded immutable result.");
            return await load(manifest, ct);
        }, token);
    }

    /// <summary>Checks a cross-store crash left the exact registration in the already published catalog manifest.</summary>
    private async Task<T> LoadMatchingPublicationAsync<T>(EvidenceResearchRunManifest manifest, ResearchStartBinding binding,
        Func<EvidenceResearchRunManifest, CancellationToken, Task<T>> load, CancellationToken token)
    {
        var actual = await packager.ReadOutputAsync<ResearchStartBinding>(manifest, RegistrationOutput, token);
        if (EvidenceCanonicalJson.ComputeSha256(actual) != EvidenceCanonicalJson.ComputeSha256(binding))
            throw new InvalidDataException("This catalog research run ID belongs to a different or previously unregistered study.");
        return await load(manifest, token);
    }

    /// <summary>Carries the exact trial fingerprint into catalog publication before releasing the outcome to readers.</summary>
    private static EvidenceResearchNamedOutput RegistrationArtifact(ResearchStart start) => new(RegistrationOutput, Json(start.Binding));

    /// <summary>Reconstructs the results already in the content-addressed store without running a momentum analyzer.</summary>
    private async Task<(CrossSectionalMomentumReport Report, MomentumResearchAuditReport Audit)> ReadMomentumResultsAsync(
        EvidenceResearchRunManifest manifest, CancellationToken token) =>
        (await packager.ReadOutputAsync<CrossSectionalMomentumReport>(manifest, MomentumCanonicalArtifactName.Report, token),
         await packager.ReadOutputAsync<MomentumResearchAuditReport>(manifest, MomentumCanonicalArtifactName.AuditReport, token));
}
