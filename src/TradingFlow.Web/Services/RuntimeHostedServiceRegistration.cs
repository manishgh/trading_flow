using Microsoft.Extensions.DependencyInjection;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Services;

public sealed record RuntimeHostedServiceOptions(
    bool Enabled,
    bool EnableEarningsMonitor);

/// <summary>
/// Owns the complete background-service composition for the web host. Keeping the
/// registrations together makes autonomous runtime behavior reviewable and testable.
/// </summary>
public static class RuntimeHostedServiceRegistration
{
    public static IServiceCollection AddTradingFlowRuntimeHostedServices(
        this IServiceCollection services,
        RuntimeHostedServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return services;
        }

        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<NewsFeedService>());
        services.AddHostedService<AlpacaNewsStreamService>();
        if (options.EnableEarningsMonitor)
        {
            services.AddHostedService<EarningsMonitorHostedService>();
        }

        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<WishlistObserverService>());
        services.AddHostedService<DatabaseBackupHostedService>();
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<AlpacaSecurityTradingStatusService>());
        services.AddHostedService<AlpacaOrderSynchronizationHostedService>();
        return services;
    }
}
