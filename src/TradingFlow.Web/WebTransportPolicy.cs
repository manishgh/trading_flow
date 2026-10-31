using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace TradingFlow.Web;

/// <summary>Accepts HTTPS forwarding only from explicitly trusted proxies and prevents production HTTP authentication.</summary>
public static class WebTransportPolicy
{
    /// <summary>Retains ASP.NET's loopback proxy defaults for local ngrok; deployed ingress networks must be supplied explicitly.</summary>
    public static IServiceCollection AddTrustedProxyTransport(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            foreach (var proxy in configuration.GetSection("Transport:TrustedProxies").Get<string[]>() ?? [])
            {
                if (!IPAddress.TryParse(proxy, out var address)) throw new ArgumentException("Transport:TrustedProxies contains an invalid IP address.");
                options.KnownProxies.Add(address);
            }
            foreach (var network in configuration.GetSection("Transport:TrustedProxyNetworks").Get<string[]>() ?? [])
            {
                if (!System.Net.IPNetwork.TryParse(network, out var parsed) || parsed.PrefixLength == 0)
                    throw new ArgumentException("Transport:TrustedProxyNetworks requires explicit proxy CIDRs; invalid networks and trust-all ranges are not allowed.");
                options.KnownIPNetworks.Add(parsed);
            }
        });
        return services;
    }

    /// <summary>Processes trusted proxy headers before HTTPS enforcement; health probes remain available on the private pod port.</summary>
    public static IApplicationBuilder UseTrustedProxyTransport(this IApplicationBuilder app, IHostEnvironment environment)
    {
        app.UseForwardedHeaders();
        if (!environment.IsDevelopment())
            app.Use(async (context, next) =>
            {
                if (context.Request.IsHttps || context.Request.Path == "/health") { await next(context); return; }
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { message = "HTTPS is required. Use the configured public HTTPS address." });
            });
        return app;
    }
}
