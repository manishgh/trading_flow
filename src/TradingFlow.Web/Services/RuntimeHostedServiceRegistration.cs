using Microsoft.Extensions.DependencyInjection;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Services;

public sealed record RuntimeHostedServiceOptions(
    bool Enabled,
    bool EnableEarningsMonitor,
    bool EnableMarketFeeds = false);

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
        services.AddSingleton<OrderDispatchRecoveryHostedService>();
        services.AddSingleton<IOrderDispatchRecoveryHealth>(serviceProvider =>
            serviceProvider.GetRequiredService<OrderDispatchRecoveryHostedService>());
        if (!options.Enabled && !options.EnableMarketFeeds)
        {
            return services;
        }

        // Hosted services stop in reverse registration order. Register shutdown
        // first so all producers and journal writers stop before the final broker
        // drain and SQLite checkpoint execute.
        if (options.Enabled) services.AddHostedService<ExecutionShutdownHostedService>();
        if (options.Enabled) services.AddHostedService<BrokerAccountValidationHostedService>();
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<NewsFeedService>());
        services.AddHostedService<AlpacaNewsStreamService>();
        if (options.EnableEarningsMonitor)
        {
            services.AddHostedService<EarningsMonitorHostedService>();
        }

        // Read-only provider feeds may run while jobs and broker mutations stay paused.
        if (!options.Enabled) return services;

        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<WishlistObserverService>());
        services.AddHostedService<DatabaseBackupHostedService>();
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<AlpacaMarketStateStreamService>());
        services.AddHostedService<AlpacaOrderSynchronizationHostedService>();
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<OrderDispatchRecoveryHostedService>());
        // Register durable workers last so host shutdown stops and drains jobs
        // before their market-data, order, and persistence dependencies.
        services.AddHostedService<DurableJobWorkerHostedService>();
        services.AddHostedService<PreparationSchedulerHostedService>();
        return services;
    }
}
