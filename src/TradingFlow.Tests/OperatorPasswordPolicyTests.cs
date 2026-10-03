using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TradingFlow.Web;

namespace TradingFlow.Tests;

public sealed class OperatorPasswordPolicyTests
{
    [Theory]
    [InlineData(false, "10", 12)]
    [InlineData(false, "1", 12)]
    [InlineData(true, null, 12)]
    [InlineData(true, "invalid", 12)]
    [InlineData(true, "10", 10)]
    [InlineData(true, "1", 10)]
    [InlineData(true, "40", 12)]
    public void ReviewPasswordOverrideCannotLowerProductionMinimum(bool reviewMode, string? requestedMinimum, int expectedMinimum)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperatorAuthentication(reviewMode, requestedMinimum);
        using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<IOptions<IdentityOptions>>().Value;
        Assert.Equal(expectedMinimum, policy.Password.RequiredLength);
        Assert.True(policy.Password.RequireDigit);
        Assert.True(policy.Password.RequireLowercase);
        Assert.True(policy.Password.RequireUppercase);
        Assert.True(policy.Password.RequireNonAlphanumeric);
        Assert.Equal(5, policy.Lockout.MaxFailedAccessAttempts);
    }
}
