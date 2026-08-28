namespace TradingFlow.Engine.Pipeline;

using TradingFlow.Engine.Indicators;

public sealed record StreamingMarketStateOptions(
    int SymbolPipelineCapacity,
    int RevisionAcceptanceMinutes,
    int RecoveryLookbackDays,
    IReadOnlyList<string> DerivedTimeframes,
    int ActiveSessionStalenessMinutes = 3)
{
    public static StreamingMarketStateOptions Default { get; } = new(
        256,
        MarketEvidenceOperationalRules.Production.RevisionAcceptanceMinutes,
        MarketEvidenceOperationalRules.Production.RecoveryLookbackDays,
        ["5m", "15m", "1h", "4h"],
        3);

    public TimeSpan RevisionAcceptanceWindow => TimeSpan.FromMinutes(RevisionAcceptanceMinutes);

    public void Validate()
    {
        if (SymbolPipelineCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SymbolPipelineCapacity));
        }

        if (RevisionAcceptanceMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RevisionAcceptanceMinutes));
        }

        if (RecoveryLookbackDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RecoveryLookbackDays));
        }

        if (ActiveSessionStalenessMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ActiveSessionStalenessMinutes));
        }
    }
}
