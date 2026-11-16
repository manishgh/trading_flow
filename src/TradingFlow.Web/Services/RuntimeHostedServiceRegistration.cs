using Microsoft.Extensions.DependencyInjection;
using TradingFlow.Application.Preparation;
using TradingFlow.Data.ScreenCaptures;
using TradingFlow.Engine.Execution;
using TradingFlow.Web.Services.Charts;
using TradingFlow.Web.Services.ScreenCaptures;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Services;

public sealed record RuntimeHostedServiceOptions(
    bool Enabled,
    bool EnableEarningsMonitor,
    bool EnableMarketFeeds = false,
    bool EnableStrategyAssessments = false,
    bool? MutationPause = null,
    bool EnableChartAcceptance = false)
{
    public bool TradingMutationsPaused => MutationPause ?? !Enabled;
}

/// <summary>
/// Owns the complete background-service composition for the web host. Keeping the
/// registrations together makes autonomous runtime behavior reviewable and testable.
/// </summary>
public static class RuntimeHostedServiceRegistration
{
    // A paused trading host may collect only if market feeds and capture were explicitly enabled.
    public static IServiceCollection AddFinvizScreenCapture(this IServiceCollection services, FinvizScreenCaptureOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<SqliteFinvizCaptureCatalog>();
        services.AddSingleton<FinvizScreenCaptureStatusReader>();
        if (options.Enabled)
        {
            services.AddSingleton<FinvizScreenCaptureScheduler>();
            services.AddHostedService<FinvizScreenCaptureHostedService>();
        }
        return services;
    }

    // Register runtime ownership ahead of the services that create orders or journal broker state.
    public static IServiceCollection AddTradingFlowRuntimeHostedServices(
        this IServiceCollection services,
        RuntimeHostedServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        RegisterChartAcceptance(services, options);
        services.AddSingleton<PreparationWorkerCapability>();
        services.AddSingleton<IPreparationCollectionCapability>(sp => sp.GetRequiredService<PreparationWorkerCapability>());
        services.AddSingleton<OrderDispatchRecoveryHostedService>();
        services.AddSingleton<IOrderDispatchRecoveryHealth>(serviceProvider =>
            serviceProvider.GetRequiredService<OrderDispatchRecoveryHostedService>());
        if (!options.Enabled && !options.EnableMarketFeeds)
        {
            if (options.EnableStrategyAssessments) services.AddHostedService<DurableJobWorkerHostedService>();
            return services;
        }

        // Reverse shutdown order keeps the host run owned through the final broker drain.
        if (options.Enabled)
        {
            services.AddSingleton<TradingFlow.Data.Orders.SqliteProductionHostRunRepository>();
            services.AddHostedService<ProductionHostRunService>();
            services.AddHostedService<ExecutionShutdownHostedService>();
        }
        if (options.Enabled) services.AddHostedService<BrokerAccountValidationHostedService>();
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<NewsFeedService>());
        services.AddHostedService<AlpacaNewsStreamService>();
        if (options.EnableEarningsMonitor)
        {
            services.AddHostedService<EarningsMonitorHostedService>();
        }

        // Read-only provider feeds may run while jobs and broker mutations stay paused.
        if (!options.Enabled)
        {
            if (options.EnableStrategyAssessments) services.AddHostedService<DurableJobWorkerHostedService>();
            return services;
        }

        services.AddHostedService<DatabaseBackupHostedService>();
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<AlpacaMarketStateStreamService>());
        services.AddHostedService<AlpacaOrderSynchronizationHostedService>();
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<OrderDispatchRecoveryHostedService>());
        // Drain an enabled diagnostic before stopping its shared SIP connection.
        if (options.EnableChartAcceptance)
            services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ChartAcceptanceCoordinator>());
        // Register durable workers last so host shutdown stops and drains jobs
        // before their market-data, order, and persistence dependencies.
        services.AddHostedService<DurableJobWorkerHostedService>();
        services.AddHostedService<PreparationSchedulerHostedService>();
        return services;
    }

    // Keep the option available for Operations even when service activation or the paper runtime is disabled.
    private static void RegisterChartAcceptance(IServiceCollection services, RuntimeHostedServiceOptions options)
    {
        services.AddSingleton(new ChartAcceptanceOptions(options.EnableChartAcceptance, options.Enabled));
        if (!options.EnableChartAcceptance) return;

        services.AddSingleton<IAccountScopedBrokerOrderReader>(services => services.GetRequiredService<IBrokerClient>());
        services.AddSingleton<IBrokerPositionReader>(services => services.GetRequiredService<IBrokerClient>());
        services.AddSingleton<IChartAcceptanceAdmission, ChartAcceptanceAdmission>();
        services.AddSingleton<ChartAcceptanceCoordinator>();
    }
}
