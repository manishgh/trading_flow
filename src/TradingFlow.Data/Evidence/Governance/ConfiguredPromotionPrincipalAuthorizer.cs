using TradingFlow.Engine.Research;

namespace TradingFlow.Data.Evidence.Governance;

/// <summary>
/// Exact allow-list authorization boundary. Authentication supplies the acting principal;
/// this component only decides whether that authenticated human may govern promotions.
/// </summary>
public sealed class ConfiguredPromotionPrincipalAuthorizer : IPromotionPrincipalAuthorizer
{
    private readonly HashSet<string> authorizedPrincipals;

    public ConfiguredPromotionPrincipalAuthorizer(IEnumerable<string> authorizedHumanPrincipals)
    {
        ArgumentNullException.ThrowIfNull(authorizedHumanPrincipals);
        authorizedPrincipals = authorizedHumanPrincipals
            .Select(Normalize)
            .ToHashSet(StringComparer.Ordinal);
        if (authorizedPrincipals.Count == 0)
        {
            throw new ArgumentException(
                "At least one authorized human principal is required.",
                nameof(authorizedHumanPrincipals));
        }
    }

    public bool IsAuthorizedHumanPrincipal(string principal) =>
        !String.IsNullOrWhiteSpace(principal) &&
        authorizedPrincipals.Contains(principal.Trim());

    private static string Normalize(string principal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        return principal.Trim();
    }
}
