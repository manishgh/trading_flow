using TradingFlow.Domain.Research;
using TradingFlow.Research;
using TradingFlow.Research.Catalysts;
using TradingFlow.Research.Momentum;

namespace TradingFlow.Research.Workflows;

public sealed record CatalogCostAssumptions(
    decimal CommissionBps,
    decimal RegulatoryFeesBps);

public sealed record CatalogSpreadAssumptions(
    string Source,
    decimal EstimatedFullSpreadBps);

public sealed record CatalogSlippageAssumptions(
    string Model,
    decimal MaximumSlippageBps);

public sealed record CatalogBorrowAssumptions(
    bool AvailabilityRequired,
    decimal AnnualBorrowBps);

public sealed record CatalogBenchmarkAssumptions(
    string Ticker,
    string ReturnDefinition);

public sealed class CatalogResearchAssumptionSet
{
    public CatalogResearchAssumptionSet(
        CatalogCostAssumptions cost,
        CatalogSpreadAssumptions spread,
        CatalogSlippageAssumptions slippage,
        CatalogBorrowAssumptions borrow,
        CatalogBenchmarkAssumptions benchmark)
    {
        Cost = cost ?? throw new ArgumentNullException(nameof(cost));
        Spread = spread ?? throw new ArgumentNullException(nameof(spread));
        Slippage = slippage ?? throw new ArgumentNullException(nameof(slippage));
        Borrow = borrow ?? throw new ArgumentNullException(nameof(borrow));
        Benchmark = benchmark ?? throw new ArgumentNullException(nameof(benchmark));

        if (Cost.CommissionBps < 0m ||
            Cost.RegulatoryFeesBps < 0m ||
            Spread.EstimatedFullSpreadBps < 0m ||
            Slippage.MaximumSlippageBps < 0m ||
            Borrow.AnnualBorrowBps < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cost),
                "Research cost assumptions cannot be negative.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Spread.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(Slippage.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(Benchmark.Ticker);
        ArgumentException.ThrowIfNullOrWhiteSpace(Benchmark.ReturnDefinition);
    }

    public CatalogCostAssumptions Cost { get; }

    public CatalogSpreadAssumptions Spread { get; }

    public CatalogSlippageAssumptions Slippage { get; }

    public CatalogBorrowAssumptions Borrow { get; }

    public CatalogBenchmarkAssumptions Benchmark { get; }
}

public enum CatalogResearchPhase
{
    DevelopmentValidation = 1,
    Holdout = 2
}

public sealed record CatalogFrozenTrialIdentity(
    string ExperimentId,
    string TrialDefinitionSha256);

public sealed record CatalogResearchPhaseState(
    CatalogResearchPhase Phase,
    string? ExperimentId,
    string? TrialDefinitionSha256,
    ResearchHoldoutState HoldoutState,
    bool HoldoutReserved,
    bool HoldoutConsumed);

public sealed record CatalogMomentumWorkflowRequest(
    string ResearchRunId,
    DateTimeOffset CreatedAtUtc,
    string CodeVersion,
    CatalogMomentumStudyRequest Study,
    EvidenceStudyPartitions StudyPartitions,
    CatalogResearchAssumptionSet Assumptions)
{
    public CatalogResearchPhase Phase { get; init; } =
        CatalogResearchPhase.DevelopmentValidation;

    public CatalogFrozenTrialIdentity? FrozenTrial { get; init; }
}

public sealed record CatalogMomentumWorkflowResult(
    CatalogMomentumStudyResult Study,
    EvidenceResearchRunManifest Manifest,
    bool AlreadyRegistered)
{
    public required CatalogResearchPhaseState PhaseState { get; init; }
}

public sealed record CatalogStaticUniverseMomentumRequest(
    string ResearchRunId,
    DateTimeOffset CreatedAtUtc,
    string CodeVersion,
    string ResearchAdjustedBarsDatasetId,
    string AsTradedBarsDatasetId,
    IReadOnlyList<string> FrozenSymbols,
    string SurvivorshipSelectionBiasWarning,
    CrossSectionalMomentumStudyDefinition Definition,
    EvidenceStudyPartitions StudyPartitions,
    CatalogResearchAssumptionSet Assumptions);

public sealed record CatalogStaticUniverseMomentumResult(
    string ResearchAdjustedBarsDatasetId,
    string AsTradedBarsDatasetId,
    IReadOnlyList<string> FrozenSymbols,
    string SurvivorshipSelectionBiasWarning,
    CrossSectionalMomentumReport Report,
    MomentumResearchAuditReport Audit,
    EvidenceResearchRunManifest Manifest,
    bool AlreadyRegistered);

public sealed record CatalogProviderUpdatedDailyNewsDiagnosticRequest(
    string ResearchRunId,
    string StudyName,
    DateTimeOffset CreatedAtUtc,
    string CodeVersion,
    string NewsDatasetId,
    string AsTradedBarsDatasetId,
    string ExchangeSessionsDatasetId,
    IReadOnlyList<string> FrozenSymbols,
    string SurvivorshipSelectionBiasWarning,
    ProviderUpdatedDailyNewsStudyOptions Options,
    EvidenceStudyPartitions StudyPartitions,
    CatalogResearchAssumptionSet Assumptions);

public sealed record CatalogProviderUpdatedDailyNewsDiagnosticResult(
    string NewsDatasetId,
    string AsTradedBarsDatasetId,
    string ExchangeSessionsDatasetId,
    IReadOnlyList<string> FrozenSymbols,
    ProviderUpdatedDailyNewsStudyReport Report,
    EvidenceResearchRunManifest Manifest,
    bool AlreadyRegistered);

public sealed record CatalogCatalystWorkflowRequest(
    string ResearchRunId,
    DateTimeOffset CreatedAtUtc,
    string CodeVersion,
    CatalogCatalystStudyRequest Study,
    string ClassifierGroundTruthDatasetId,
    string ExchangeSessionsDatasetId,
    EvidenceStudyPartitions StudyPartitions,
    CatalogResearchAssumptionSet Assumptions)
{
    public CatalogResearchPhase Phase { get; init; } =
        CatalogResearchPhase.DevelopmentValidation;

    public CatalogFrozenTrialIdentity? FrozenTrial { get; init; }
}

public sealed record CatalogCatalystWorkflowResult(
    CatalogCatalystStudyResult Study,
    EvidenceResearchRunManifest Manifest,
    bool ClassifierGroundTruthReady,
    bool AlreadyRegistered)
{
    public required CatalogResearchPhaseState PhaseState { get; init; }
}

public static class CatalogResearchTrialBinding
{
    public const string RequestBindingFormulaKey = "catalog_request_sha256";

    public static string ComputeMomentumRequestSha256(
        CatalogMomentumWorkflowRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return EvidenceCanonicalJson.ComputeSha256(new
        {
            Kind = "momentum",
            request.CodeVersion,
            request.Study,
            request.StudyPartitions,
            request.Assumptions
        });
    }

    public static string ComputeCatalystRequestSha256(
        CatalogCatalystWorkflowRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return EvidenceCanonicalJson.ComputeSha256(new
        {
            Kind = "catalyst",
            request.CodeVersion,
            request.Study,
            request.ClassifierGroundTruthDatasetId,
            request.ExchangeSessionsDatasetId,
            request.StudyPartitions,
            request.Assumptions
        });
    }
}
