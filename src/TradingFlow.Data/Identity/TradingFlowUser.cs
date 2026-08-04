using Microsoft.AspNetCore.Identity;

namespace TradingFlow.Data.Identity;

/// <summary>
/// Local TradingFlow operator identity. Password material is owned by ASP.NET
/// Core Identity and persisted only in the inherited <see cref="IdentityUser{TKey}.PasswordHash"/>.
/// </summary>
public sealed class TradingFlowUser : IdentityUser<Guid>
{
    public string? DisplayName { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public static class TradingFlowRoles
{
    public const string Administrator = "Administrator";
}
