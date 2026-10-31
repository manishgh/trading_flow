using TradingFlow.Backtesting;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

public sealed partial class ConfigCatalogService
{
    /// <summary>Lists server profile bindings and published immutable diagnostics without scanning operator output folders.</summary>
    public IReadOnlyList<DecisionContextEvidenceChoice> GetDecisionContextEvidenceChoices()
    {
        var choices = GetBacktestConfigs().Where(item => item.Config.Engine.EvidenceManifestPath is not null ||
                item.Config.Engine.EvidenceResearchRoot is not null).Select(profile =>
        {
            try
            {
                var binding = ResolveEvidence(profile);
                var reason = binding.Summary.UnknownReasons.Count == 0 ? null : String.Join(" ", binding.Summary.UnknownReasons);
                return new DecisionContextEvidenceChoice(binding.Summary.Reference, EvidenceId(profile), reason is null, reason, binding.Summary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
            {
                return new DecisionContextEvidenceChoice(null, EvidenceId(profile), false,
                    "Configured decision-context evidence cannot be read and verified: " + exception.Message, null);
            }
        }).Concat(GetHistoricalNewsDiagnosticChoices()).ToArray();
        return choices.Length > 0 ? choices : [new(null, "Decision-context evidence", false,
            "No server backtest profile currently binds a frozen decision-context manifest. Collected articles do not establish independent classifier evaluation, original receipt times or company/sector/global coverage.", null)];
    }

    /// <summary>Resolves a stable relative profile identity and expected bytes; copies only evidence fields onto the chosen execution engine.</summary>
    public (EngineConfig Engine, DecisionContextEvidenceSummary? Summary) BindDecisionContextEvidence(
        StartBacktestRequest request, EngineConfig executionEngine, IReadOnlyList<StrategyOption> selected) =>
        BindDecisionContextEvidence(request, executionEngine, selected, null);

    /// <summary>Keeps ordinary recipe and evidence fences while permitting one operation's leased assessment replay.</summary>
    internal (EngineConfig Engine, DecisionContextEvidenceSummary? Summary) BindDecisionContextEvidence(
        StartBacktestRequest request, EngineConfig executionEngine, IReadOnlyList<StrategyOption> selected,
        WorkflowEvidenceVerificationScope? scope)
    {
        scope?.RequireOwner(this);
        var required = selected.Any(item => item.Definition.EntryRules.DrawdownQualification is { Mode: "gate" } rule &&
            (rule.UsesEventContext || rule.UsesSectorContext));
        var diagnosticRequired = selected.Any(item => item.Definition.EntryRules.HistoricalNewsDiagnostic is not null);
        if (request.DecisionContextEvidence is not { } reference)
        {
            if (required || diagnosticRequired) throw new InvalidOperationException("This recipe requires an explicit verified decision-context evidence selection before a plan can be saved or started.");
            return (executionEngine, null);
        }
        if (reference.Id.StartsWith(HistoricalNewsEvidencePrefix, StringComparison.Ordinal))
            return BindHistoricalNewsDiagnostic(reference, executionEngine, selected, required, scope);
        if (scope is not null) throw new InvalidDataException("Diagnostic verification scope cannot bind another evidence selection.");
        if (diagnosticRequired)
            throw new InvalidOperationException("Historical news recipes require a historical news diagnostic package; strict evidence cannot substitute for it.");
        var profile = GetBacktestConfigs().SingleOrDefault(item => EvidenceId(item) == reference.Id)
            ?? throw new InvalidOperationException("Selected decision-context evidence is no longer in the server profile catalog.");
        var binding = ResolveEvidence(profile);
        if (!binding.Summary.Reference.ManifestSha256.Equals(reference.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Decision-context manifest changed after review. Select and save its new identity before starting.");
        if (binding.Summary.UnknownReasons.Count > 0)
            throw new InvalidOperationException(String.Join(" ", binding.Summary.UnknownReasons));
        var rules = selected.Select(item => item.Definition.EntryRules.DrawdownQualification).Where(item => item?.UsesEventContext == true).ToArray();
        var summary = binding.Summary with {
            EventLookbackDays = rules.Select(item => item!.EventLookbackDays ?? 5).Distinct().Count() == 1 ? rules[0]!.EventLookbackDays ?? 5 : null,
            MaxCoverageAgeHours = rules.Select(item => item!.EventMaxCoverageAgeHours ?? 24).Distinct().Count() == 1 ? rules[0]!.EventMaxCoverageAgeHours ?? 24 : null };
        return (executionEngine with { EvidenceManifestPath = binding.Engine.EvidenceManifestPath,
            EvidenceResearchRoot = binding.Engine.EvidenceResearchRoot, EvidenceManifestSha256 = summary.Reference.ManifestSha256,
            EvidenceDependencyGraphSha256 = summary.DependencyGraphSha256 }, summary);
    }

    /// <summary>Uses the profile's complete relative path so nested profiles with the same filename cannot collide.</summary>
    private string EvidenceId(TradingFlow.Web.Models.RunConfigSummary profile) =>
        Path.GetRelativePath(paths.BacktestConfigsRoot, profile.Path).Replace('\\', '/');

    /// <summary>Separates structural file verification from the existing per-decision clock, identity and coverage eligibility checks.</summary>
    private (EngineConfig Engine, DecisionContextEvidenceSummary Summary) ResolveEvidence(TradingFlow.Web.Models.RunConfigSummary profile)
    {
        var provider = FrozenManifestDecisionContextProvider.FromConfig(profile.Config.Engine) as FrozenManifestDecisionContextProvider
            ?? throw new InvalidDataException("The selected profile has no frozen decision-context manifest.");
        var documents = provider.ContextDocuments;
        var events = documents.Select(item => item.Events).OfType<DrawdownEventContext>().ToArray();
        var unknown = new List<string>();
        if (documents.Count == 0) unknown.Add("The manifest contains no actual decision contexts.");
        if (documents.Any(item => item.History is null)) unknown.Add("At least one context lacks independent history evidence; required recipes must retain the normal unknown-input rejection.");
        if (events.Length != documents.Count) unknown.Add("At least one context lacks classified-event evidence.");
        if (events.Any(item => !item.Evaluation.Passed)) unknown.Add("At least one classifier evaluation has not passed independent evaluation.");
        if (events.Any(item => Enum.GetValues<DrawdownEventScope>().Any(scope => !item.ScopeCoverages.Any(coverage => coverage.Scope == scope))))
            unknown.Add("At least one context lacks independently bounded company, sector or global coverage.");
        var summary = new DecisionContextEvidenceSummary(new(EvidenceId(profile), provider.ManifestSha256), provider.Manifest.Provider,
            provider.Manifest.Version, events.Select(item => item.Evaluation.TaxonomyVersion).Distinct().Order().ToArray(),
            events.Select(item => item.Evaluation.ClassifierVersion).Distinct().Order().ToArray(),
            events.Select(item => item.Evaluation.EvaluationSha256).Distinct().Order().ToArray(),
            ["Original publication, provider update, actual receipt, classification, evaluation and known-at clocks; eligibility is checked at each decision."],
            events.SelectMany(item => item.ScopeCoverages).Select(item => item.Scope.ToString().ToLowerInvariant()).Distinct().Order().ToArray(),
            null, null, unknown, provider.DependencyGraphSha256);
        return (profile.Config.Engine, summary);
    }
}
