using Microsoft.AspNetCore.Antiforgery;

namespace TradingFlow.Web;

/// <summary>Cookie-authenticated API mutations require the same CSRF proof as forms.</summary>
public static class ApiMutationSecurity
{
    public const string HeaderName = "X-CSRF-TOKEN";

    public static IServiceCollection AddApiMutationSecurity(this IServiceCollection services)
    {
        services.AddAntiforgery(options => options.HeaderName = HeaderName);
        return services;
    }

    public static IApplicationBuilder UseApiMutationSecurity(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api") &&
            !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) &&
            !HttpMethods.IsOptions(context.Request.Method))
        {
            try
            {
                await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { code = "csrf_validation_failed", message = "Refresh the session security token and retry." });
                return;
            }
        }
        await next(context);
    });
}
