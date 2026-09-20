using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class ProductionCompositionTests
{
    [Fact]
    public void Etoro_RemainsReferenceSource_AndIsExcludedFromProductionComposition()
    {
        var root = TestRepository.FindRoot();
        var etoroRoot = Path.Combine(root, "src", "TradingFlow.Etoro");
        Assert.True(Directory.Exists(etoroRoot), "The dormant eToro reference source must remain available.");

        AssertFileDoesNotContain(Path.Combine(root, "TradingFlow.sln"), "TradingFlow.Etoro");
        AssertFileDoesNotContain(Path.Combine(root, "TradingFlow.slnx"), "TradingFlow.Etoro");

        var productionProjects = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(etoroRoot, StringComparison.OrdinalIgnoreCase));
        foreach (var project in productionProjects)
        {
            AssertFileDoesNotContain(project, "TradingFlow.Etoro");
        }

        var productionEntryPoints = new[]
        {
            Path.Combine(root, "src", "TradingFlow.Web", "Program.cs"),
            Path.Combine(root, "src", "TradingFlow.Cli", "Program.cs"),
            Path.Combine(root, "src", "TradingFlow.WarmupService", "Program.cs")
        };
        foreach (var entryPoint in productionEntryPoints)
        {
            AssertFileDoesNotContain(entryPoint, "Etoro");
        }

        var deploymentFiles = Directory
            .EnumerateFiles(Path.Combine(root, "deploy"), "*", SearchOption.AllDirectories)
            .Where(path => new[] { ".yaml", ".yml", ".bicep", ".json" }
                .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
        foreach (var deploymentFile in deploymentFiles)
        {
            AssertFileDoesNotContain(deploymentFile, "ETORO_");
        }
    }

    [Fact]
    public void ResearchAssembly_IsCompilerIsolatedFromRuntimeProjects()
    {
        var root = TestRepository.FindRoot();
        var runtimeProjects = new[]
        {
            Path.Combine(root, "src", "TradingFlow.Web", "TradingFlow.Web.csproj"),
            Path.Combine(root, "src", "TradingFlow.Backtesting", "TradingFlow.Backtesting.csproj"),
            Path.Combine(root, "src", "TradingFlow.Engine", "TradingFlow.Engine.csproj")
        };
        foreach (var project in runtimeProjects)
        {
            AssertFileDoesNotContain(project, "TradingFlow.Research");
        }

        var cliProject = Path.Combine(root, "src", "TradingFlow.Cli", "TradingFlow.Cli.csproj");
        Assert.Contains(
            "TradingFlow.Research",
            File.ReadAllText(cliProject),
            StringComparison.OrdinalIgnoreCase);

        var researchProject = Path.Combine(
            root,
            "src",
            "TradingFlow.Research",
            "TradingFlow.Research.csproj");
        foreach (var forbiddenReference in new[]
                 {
                     "TradingFlow.Data",
                     "TradingFlow.Alpaca",
                     "TradingFlow.Finviz",
                     "TradingFlow.Backtesting",
                     "Microsoft.Extensions.Http"
                 })
        {
            AssertFileDoesNotContain(researchProject, forbiddenReference);
        }

        var researchSources = Directory.EnumerateFiles(
            Path.Combine(root, "src", "TradingFlow.Research"),
            "*.cs",
            SearchOption.AllDirectories);
        foreach (var source in researchSources)
        {
            AssertFileDoesNotContain(source, "HttpClient");
            AssertFileDoesNotContain(source, "AlpacaMarketDataProvider");
            AssertFileDoesNotContain(source, "Finviz");
        }
    }

    [Fact]
    public void RuntimeComposition_ContainsNoAutonomousPrototypeTradingOrAdvisoryServices()
    {
        var services = new ServiceCollection();
        services.AddTradingFlowRuntimeHostedServices(new RuntimeHostedServiceOptions(
            Enabled: true,
            EnableEarningsMonitor: true));

        var hostedTypesInRegistrationOrder = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(ResolveHostedServiceType)
            .ToArray();
        var actualTypes = hostedTypesInRegistrationOrder
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        var expectedTypes = new[]
        {
            typeof(NewsFeedService),
            typeof(AlpacaNewsStreamService),
            typeof(BrokerAccountValidationHostedService),
            typeof(DurableJobWorkerHostedService),
            typeof(EarningsMonitorHostedService),
            typeof(TradingFlow.Web.Services.Wishlists.WishlistObserverService),
            typeof(DatabaseBackupHostedService),
            typeof(AlpacaMarketStateStreamService),
            typeof(AlpacaOrderSynchronizationHostedService),
            typeof(OrderDispatchRecoveryHostedService),
            typeof(ExecutionShutdownHostedService)
        }.OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();

        Assert.Equal(expectedTypes, actualTypes);
        Assert.Equal(typeof(ExecutionShutdownHostedService), hostedTypesInRegistrationOrder[0]);
        Assert.True(
            Array.IndexOf(hostedTypesInRegistrationOrder, typeof(AlpacaOrderSynchronizationHostedService)) > 0,
            "Order synchronization must stop before execution shutdown performs the final journal checkpoint.");
        Assert.True(
            Array.IndexOf(hostedTypesInRegistrationOrder, typeof(OrderDispatchRecoveryHostedService)) > 0,
            "Dispatch recovery must stop before execution shutdown closes the broker mutation boundary.");
        Assert.True(
            Array.IndexOf(hostedTypesInRegistrationOrder, typeof(DurableJobWorkerHostedService)) > 0,
            "Durable run workers must drain before execution shutdown closes the broker mutation boundary.");
        Assert.Equal(typeof(DurableJobWorkerHostedService), hostedTypesInRegistrationOrder[^1]);
        Assert.DoesNotContain(actualTypes, type =>
            type.Name.Contains("CatalystExecution", StringComparison.Ordinal) ||
            type.Name.Contains("PortfolioAdvisor", StringComparison.Ordinal) ||
            type.Name.Contains("SectorNewsWatcher", StringComparison.Ordinal));

        var root = TestRepository.FindRoot();
        var webRoot = Path.Combine(root, "src", "TradingFlow.Web");
        var registryPath = Path.Combine(webRoot, "Services", "RuntimeHostedServiceRegistration.cs");
        var bypasses = Directory
            .EnumerateFiles(webRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Equals(registryPath, StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("AddHostedService", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(bypasses);

        var program = File.ReadAllText(Path.Combine(webRoot, "Program.cs"));
        Assert.Equal(1, CountOccurrences(program, "AddTradingFlowRuntimeHostedServices("));
        Assert.Contains(
            "ResolveProductionParameter<ProductionTimeWindow>(",
            program,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeComposition_TestModeStartsNoBackgroundServices()
    {
        var services = new ServiceCollection();
        services.AddTradingFlowRuntimeHostedServices(new RuntimeHostedServiceOptions(
            Enabled: false,
            EnableEarningsMonitor: true));

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void WebLogging_UsesPortableStructuredConsoleWithoutWindowsEventLog()
    {
        var root = TestRepository.FindRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "TradingFlow.Web", "Program.cs"));

        Assert.Contains("builder.Logging.ClearProviders()", program, StringComparison.Ordinal);
        Assert.Contains("builder.Logging.AddJsonConsole", program, StringComparison.Ordinal);
        Assert.DoesNotContain("AddEventLog", program, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionComposition_ContainsNoLegacyOrderStateRegistrationOrActiveModel()
    {
        var root = TestRepository.FindRoot();
        var legacyFiles = new[]
        {
            Path.Combine(root, "src", "TradingFlow.Domain", "Orders", "IOrderStateRepository.cs"),
            Path.Combine(root, "src", "TradingFlow.Domain", "Orders", "PersistedOrder.cs"),
            Path.Combine(root, "src", "TradingFlow.Data", "Orders", "SqliteOrderStateRepository.cs")
        };
        Assert.All(legacyFiles, path => Assert.False(File.Exists(path), $"Legacy source remains: {path}"));

        var activeCompositionFiles = new[]
        {
            Path.Combine(root, "src", "TradingFlow.Web", "Program.cs"),
            Path.Combine(root, "src", "TradingFlow.Data", "Context", "TradingFlowDbContext.cs"),
            Path.Combine(root, "src", "TradingFlow.Backtesting", "LiveRunner.cs"),
            Path.Combine(root, "src", "TradingFlow.Backtesting", "LiveRunner.ProcessTicker.cs"),
            Path.Combine(root, "src", "TradingFlow.Backtesting", "LiveRunner.Exits.cs"),
            Path.Combine(root, "src", "TradingFlow.Web", "Services", "PaperJobService.cs"),
            Path.Combine(root, "src", "TradingFlow.Web", "Services", "MobileAutomationService.cs")
        };
        foreach (var path in activeCompositionFiles)
        {
            AssertFileDoesNotContain(path, "IOrderStateRepository");
            AssertFileDoesNotContain(path, "PersistedOrder");
            AssertFileDoesNotContain(path, "SqliteOrderStateRepository");
        }
    }

    [Fact]
    public void StrategyDecisionSources_ContainNoVendorOrLegacyFullSessionRvolGate()
    {
        var root = TestRepository.FindRoot();
        var productionSources = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains("TradingFlow.Tests", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var source in productionSources)
        {
            var contents = File.ReadAllText(source);
            Assert.DoesNotContain("SessionRelativeVolume", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("AverageSessionVolume", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("MinSessionRelativeVolume", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("min_session_relative_volume", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("finviz_style", contents, StringComparison.Ordinal);
        }

        var decisionSources = new[]
        {
            Path.Combine(root, "src", "TradingFlow.Engine", "Strategies"),
            Path.Combine(root, "src", "TradingFlow.Backtesting")
        }.SelectMany(path => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories));
        foreach (var source in decisionSources)
        {
            Assert.DoesNotContain(
                "VendorReportedRvol",
                File.ReadAllText(source),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void WishlistObservation_ConsumesSharedStreamStateWithoutRestPolling()
    {
        var root = TestRepository.FindRoot();
        var observerPath = Path.Combine(
            root,
            "src",
            "TradingFlow.Web",
            "Services",
            "Wishlists",
            "WishlistObserverService.cs");
        var source = File.ReadAllText(observerPath);

        Assert.Contains("IDiscoverySubscriptionSink", source, StringComparison.Ordinal);
        Assert.Contains("IMarketStateSnapshotProvider", source, StringComparison.Ordinal);
        Assert.Contains("ReplaceScopeAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetTickerStateAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AlpacaMarketDataProvider", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetBarsAsync", source, StringComparison.Ordinal);
    }

    private static Type ResolveHostedServiceType(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationType is not null)
        {
            return descriptor.ImplementationType;
        }

        if (descriptor.ImplementationInstance is not null)
        {
            return descriptor.ImplementationInstance.GetType();
        }

        var instance = descriptor.ImplementationFactory?.Invoke(UninitializedServiceProvider.Instance)
            ?? throw new InvalidOperationException("Hosted service registration has no implementation.");
        return instance.GetType();
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    private sealed class UninitializedServiceProvider : IServiceProvider
    {
        public static UninitializedServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => RuntimeHelpers.GetUninitializedObject(serviceType);
    }

    private static void AssertFileDoesNotContain(string path, string value)
    {
        Assert.True(File.Exists(path), $"Required composition file is missing: {path}");
        Assert.DoesNotContain(value, File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }
}
