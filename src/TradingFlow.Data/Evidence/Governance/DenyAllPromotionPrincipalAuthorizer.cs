using TradingFlow.Engine.Research;

namespace TradingFlow.Data.Evidence.Governance;

/// <summary>
/// Keeps the operational Web host read-only with respect to human promotion
/// decisions. Promotion commands are composed only by the dedicated operator CLI.
/// </summary>
public sealed class DenyAllPromotionPrincipalAuthorizer : IPromotionPrincipalAuthorizer
{
    public bool IsAuthorizedHumanPrincipal(string principal) => false;
}
