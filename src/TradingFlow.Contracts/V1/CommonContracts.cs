namespace TradingFlow.Contracts.V1;

public static class ContractVersions
{
    public const string VersionOne = "1.0";
}

public sealed record StrategyReference(
    string StrategyId,
    string SemanticVersion,
    string ContentSha256);

public sealed record RunProvenance(
    string Mode,
    Guid UniverseSnapshotId,
    Guid? DecisionRunId,
    IReadOnlyList<StrategyReference> Strategies,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    PaperMembershipReview? PaperMembership = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? UniverseContentSha256 = null);

public sealed record ApiErrorResponse(
    string ContractVersion,
    string Code,
    string Message,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? ValidationErrors = null,
    string? TraceId = null);
