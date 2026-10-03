using Microsoft.AspNetCore.Identity;
using TradingFlow.Data.Context;
using TradingFlow.Data.Identity;

namespace TradingFlow.Web;

public static class OperatorAuthentication
{
    public static void AddOperatorAuthentication(this IServiceCollection services, bool uiTestMode = false, string? reviewMinimumPasswordLength = null)
    {
services
    .AddIdentityCore<TradingFlowUser>(identity =>
    {
        identity.User.RequireUniqueEmail = false;
        identity.Password.RequiredLength = uiTestMode && Int32.TryParse(reviewMinimumPasswordLength, out var reviewMinimum) ? Math.Clamp(reviewMinimum, 10, 12) : 12;
        identity.Password.RequireDigit = true;
        identity.Password.RequireLowercase = true;
        identity.Password.RequireUppercase = true;
        identity.Password.RequireNonAlphanumeric = true;
        // A trading surface locks out rather than throttles: an attacker who can
        // keep guessing eventually reaches an account that can move money.
        identity.Lockout.MaxFailedAccessAttempts = 5;
        identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        identity.Lockout.AllowedForNewUsers = true;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<TradingFlowDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();
services
    .AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies();
services.ConfigureApplicationCookie(cookie =>
{
    cookie.LoginPath = "/Login";
    cookie.LogoutPath = "/Login";
    cookie.AccessDeniedPath = "/AccessDenied";
    cookie.Cookie.HttpOnly = true;
    cookie.Cookie.SameSite = SameSiteMode.Lax;
    cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    cookie.ExpireTimeSpan = TimeSpan.FromHours(12);
    cookie.SlidingExpiration = true;
    cookie.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
    cookie.Events.OnRedirectToAccessDenied = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = StatusCodes.Status403Forbidden;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});
// Fail closed. Without a fallback policy, only endpoints that opt in are
// protected, so forgetting one leaves it open - which is exactly what happened
// to /api/v1, a group carrying order preview, order confirm and paper-run
// start. With it, every endpoint requires a signed-in operator unless it
// explicitly says otherwise, and the failure mode of forgetting is a locked
// endpoint rather than an exposed one.
//
// The deliberate exceptions are marked AllowAnonymous at their definition:
// the Login and AccessDenied pages, the Earnings page and its API, the auth
// login endpoint, and the health probes.
services.AddAuthorization(authorization =>
{
    authorization.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
    }
}
