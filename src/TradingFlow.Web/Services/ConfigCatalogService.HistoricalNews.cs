using TradingFlow.Backtesting;
using TradingFlow.Application.Jobs;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Evidence.Historical;
using TradingFlow.Data.Evidence.Sentiment;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Strategies;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

public sealed partial class ConfigCatalogService
{
    private const string HistoricalNewsEvidencePrefix = "historical-news:";
    private string HistoricalNewsPublicationRoot => Path.Combine(paths.DataRoot, "research", "evidence", "historical-news-diagnostics");

    /// <summary>Shows only content-addressed publications; expensive original-source verification happens during binding and capture.</summary>
    private IReadOnlyList<DecisionContextEvidenceChoice> GetHistoricalNewsDiagnosticChoices()
    {
        return HistoricalDiagnosticNewsManifestStore.FindPublished(HistoricalNewsPublicationRoot).Select(publication =>
        {
            var reference = new DecisionContextEvidenceReference(HistoricalNewsEvidencePrefix + publication.Id, publication.Sha256);
            try
            {
                var manifest = HistoricalDiagnosticNewsManifestStore.ReadPublished(publication);
                using var scope = OpenHistoricalNewsScope(reference, manifest, publication);
                var provider = scope?.Resolve(scope.Bind(new EngineConfig(String.Empty, 0, 0, 0, 0, false) { EvidenceResearchRoot = manifest.ResearchRoot,
                    EvidenceManifestPath = publication.Path, EvidenceManifestSha256 = publication.Sha256 }))
                    .HistoricalNewsProvider as HistoricalDiagnosticNewsProvider;
                return new DecisionContextEvidenceChoice(reference,
                    $"Historical company news · {manifest.BuiltAtUtc:yyyy-MM-dd HH:mm} UTC · {publication.Id[..8]}",
                    true, null, HistoricalNewsSummary(reference, provider?.Manifest ?? manifest,
                        provider?.DependencyGraphSha256 ?? HistoricalDiagnosticNewsManifestStore.DeclaredDependencyGraphSha256(publication, manifest),
                        scope?.AssessmentSummary));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
            {
                return new DecisionContextEvidenceChoice(reference, "Historical company news", false,
                    "Published diagnostic cannot be read and verified: " + exception.Message, null);
            }
        }).ToArray();
    }

    /// <summary>Resolves a server publication ID and captures its complete verified graph while leaving execution settings unchanged.</summary>
    private (EngineConfig Engine, DecisionContextEvidenceSummary? Summary) BindHistoricalNewsDiagnostic(
        DecisionContextEvidenceReference reference, EngineConfig executionEngine, IReadOnlyList<StrategyOption> selected,
        bool strictContextRequired, WorkflowEvidenceVerificationScope? scope)
    {
        if (strictContextRequired || selected.Any(item => HistoricalNewsPresenceEvaluation.HasStrictNewsRequirements(item.Definition.EntryRules)))
            throw new InvalidOperationException("Historical news diagnostics cannot replace the selected recipe's strict news, sector or event evidence.");
        var publication = HistoricalDiagnosticNewsManifestStore.FindPublished(HistoricalNewsPublicationRoot)
            .SingleOrDefault(item => HistoricalNewsEvidencePrefix + item.Id == reference.Id)
            ?? throw new InvalidOperationException("Selected historical news package is no longer in the server catalog.");
        if (!String.Equals(publication.Sha256, reference.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Historical news manifest differs from the reviewed selection.");
        var manifest = HistoricalDiagnosticNewsManifestStore.ReadPublished(publication);
        if (selected.Any(item => HistoricalCompanyNewsDiagnosticPolicy.IsTreatment(item.Definition.EntryRules.HistoricalNewsDiagnostic)) &&
            manifest.CompanyAssessmentManifest is null)
            throw new InvalidOperationException("This strategy requires a frozen company-assessment package. Unclassified articles cannot satisfy its news rule.");
        var candidate = executionEngine with { EvidenceResearchRoot = manifest.ResearchRoot,
            EvidenceManifestPath = publication.Path, EvidenceManifestSha256 = publication.Sha256,
            EvidenceDependencyGraphSha256 = null };
        using var localScope = scope is null ? OpenHistoricalNewsScope(reference, manifest, publication) : null;
        var activeScope = scope ?? localScope;
        if (activeScope is not null) candidate = activeScope.Bind(candidate);
        var verified = (activeScope?.Resolve(candidate) ?? FrozenBacktestEvidence.FromConfig(candidate))
            ?? throw new InvalidDataException("Historical news publication has no verified evidence graph.");
        if (verified.HistoricalNewsProvider is not HistoricalDiagnosticNewsProvider provider)
            throw new InvalidDataException("Selected publication is not a historical news diagnostic.");
        var engine = candidate with { EvidenceDependencyGraphSha256 = verified.DependencyGraphSha256 };
        return (engine, HistoricalNewsSummary(reference, provider.Manifest, verified.DependencyGraphSha256,
            activeScope?.AssessmentSummary));
    }

    /// <summary>Creates a fresh disposable token for this request; prior catalog reads never supply Start evidence.</summary>
    internal IWorkflowEvidenceVerificationScope? OpenEvidenceVerificationScope(StartBacktestRequest request)
    {
        if (request.DecisionContextEvidence is not { } reference || !reference.Id.StartsWith(HistoricalNewsEvidencePrefix, StringComparison.Ordinal)) return null;
        var publication = HistoricalDiagnosticNewsManifestStore.FindPublished(HistoricalNewsPublicationRoot)
            .SingleOrDefault(item => HistoricalNewsEvidencePrefix + item.Id == reference.Id)
            ?? throw new InvalidOperationException("Selected historical news package is no longer in the server catalog.");
        var manifest = HistoricalDiagnosticNewsManifestStore.ReadPublished(publication);
        return OpenHistoricalNewsScope(reference, manifest, publication);
    }

    /// <summary>Confirms the reviewed server identity before leasing its declared graph and performing the complete replay.</summary>
    private WorkflowEvidenceVerificationScope? OpenHistoricalNewsScope(DecisionContextEvidenceReference reference,
        HistoricalDiagnosticNewsManifest manifest, HistoricalDiagnosticNewsPublication publication)
    {
        if (!publication.Sha256.Equals(reference.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Historical news manifest differs from the reviewed selection.");
        return WorkflowEvidenceVerificationScope.Open(this, new EngineConfig(String.Empty, 0, 0, 0, 0, false) { EvidenceResearchRoot = manifest.ResearchRoot,
            EvidenceManifestPath = publication.Path, EvidenceManifestSha256 = publication.Sha256 });
    }

    /// <summary>Reports real receipts and query limitations without turning partial coverage into an absence claim.</summary>
    private static DecisionContextEvidenceSummary HistoricalNewsSummary(DecisionContextEvidenceReference reference,
        HistoricalDiagnosticNewsManifest manifest, string dependencyGraphSha256, CompanyNewsAssessmentSummary? companyAssessments = null)
    {
        var observations = manifest.Archives.SelectMany(item => item.Evidence.Observations).ToArray();
        if (observations.Length == 0) throw new InvalidDataException("Historical news package has no source observations.");
        var unknown = manifest.QueryCoverage.Where(item => !item.Complete)
            .Select(item => item.UnknownReason ?? "Query evidence is incomplete.")
            .Concat(manifest.Revisions.Where(item => item.UnknownReason is not null)
                .Select(item => $"Revision {item.FactId}: {item.UnknownReason}"))
            .Distinct().ToArray();
        var notes = manifest.QueryCoverage.Select(item =>
            $"{(item.Symbols.IsEmpty ? "All provider symbols" : String.Join(",", item.Symbols))}: {item.StartUtc:O} through {item.EndUtc:O}; " +
            (item.Complete ? "retained response sequence complete" : "unknown: " + item.UnknownReason)).ToList();
        notes.Add("Query completeness does not prove complete historical revisions or all published news.");
        notes.Add("Query-time basis: " + (manifest.QuerySemantics?.Basis ?? "Unproved; decisions remain unknown."));
        if (manifest.CompanyAssessmentManifest is not null && companyAssessments is null)
            throw new InvalidDataException("Company assessment summary requires this operation's verified assessment.");
        return new(reference, String.Join(",", observations.Select(item => item.Provider).Distinct().Order()),
            manifest.SchemaVersion, [], [], [], ["Provider update + 24 hours, then the next captured market-session open."],
            ["company-linked"], HistoricalNewsDiagnosticPolicy.FreshnessHours / 24, null, unknown, dependencyGraphSha256,
            new(manifest.PolicyId, manifest.PolicySha256, HistoricalNewsDiagnosticPolicy.FreshnessHours,
                observations.Min(item => item.CapturedAtUtc), observations.Max(item => item.CapturedAtUtc), notes, companyAssessments));
    }
}
