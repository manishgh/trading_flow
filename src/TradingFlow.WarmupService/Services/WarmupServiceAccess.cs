using System.Security.Cryptography;
using System.Text;
using TradingFlow.Contracts.Transport;

namespace TradingFlow.WarmupService.Services;

/// <summary>Authenticates private warm-up reads and commands before they can access provider credentials or mutable state.</summary>
public static class WarmupServiceAccess
{
    /// <summary>Allows health probes without a key and rejects unconfigured or unauthenticated API calls before endpoint execution.</summary>
    public static IApplicationBuilder UseWarmupServiceAccess(this IApplicationBuilder app, IConfiguration configuration) =>
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api/warmup")) { await next(context); return; }
            var key = ResolveKey(configuration);
            if (String.IsNullOrWhiteSpace(key))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new { message = "Configure WarmupService:ApiKey before accessing warm-up work." });
                return;
            }
            var supplied = context.Request.Headers[WarmupServiceAuthentication.HeaderName];
            if (supplied.Count != 1 || String.IsNullOrWhiteSpace(supplied[0]) ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key)),
                    SHA256.HashData(Encoding.UTF8.GetBytes(supplied[0]!))))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { message = "A valid private warm-up service key is required." });
                return;
            }
            await next(context);
        });

    /// <summary>Uses one explicit host source; a deliberately blank environment value must not fall back to another credential.</summary>
    public static string ResolveKey(IConfiguration configuration) =>
        configuration[WarmupServiceAuthentication.EnvironmentKey] ?? configuration[WarmupServiceAuthentication.ConfigurationKey] ?? String.Empty;
}
