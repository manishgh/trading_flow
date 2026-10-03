using TradingFlow.Application.Jobs;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

public sealed class WorkflowRunConfiguration(ConfigCatalogService catalog, RunConfigWriter writer, SimpleYamlReader reader) : IWorkflowRunConfiguration
{
    public async Task<IReadOnlyList<string>> WriteAsync(StartBacktestRequest request, UniversePreviewResponse universe, string uniqueName, CancellationToken token)
    {
        var config = SelectConfig(catalog.GetBacktestConfigs(), request.ConfigurationKey);
        var options = await catalog.GetStrategiesAsync(StrategySelectionMode.Backtest, token);
        var selected = request.Strategies.Select(identity => SelectStrategy(options, identity)).ToArray();
        var path = writer.WriteBacktestConfig(new BacktestRunRequest(config.Path, uniqueName, 0, null, null,
            universe.Members.Select(item => item.Symbol).ToArray(), selected.Select(item => item.Path).ToArray(),
            request.StartingCapital, request.AccountRiskBudgetPct, request.MaxPositionNotionalPct, request.MaxConcurrentPositions,
            request.CachePolicy, [], request.TestWindowStartUtc, request.TestWindowEndUtc, universe.UniverseSnapshotId, universe.ResolvedAtUtc,
            String.Join("+", universe.Members.SelectMany(item => item.Sources).Select(item => item.SourceKind).Distinct().Order())));
        return [path, .. reader.ReadBacktestRun(path).Strategies];
    }

    public async Task<IReadOnlyList<string>> WriteAsync(StartPaperRunRequest request, UniversePreviewResponse universe, string uniqueName, CancellationToken token)
    {
        var config = SelectConfig(catalog.GetPaperConfigs(), request.ConfigurationKey);
        var mode = request.Mode == "paper_experiment" ? StrategySelectionMode.RunPaperExperiment : StrategySelectionMode.RunPaperShadow;
        var selected = SelectStrategy(await catalog.GetStrategiesAsync(mode, token), request.Strategy);
        var wishlist = universe.Members.Where(item => item.Sources.Any(source => source.SourceKind == "wishlist")).Select(item => item.Symbol).ToArray();
        var screener = universe.Members.Where(item => item.Sources.Any(source => source.SourceKind is "screener" or "finviz")).Select(item => item.Symbol).ToArray();
        var path = writer.SaveTempConfig(config.Path, universe.Members.Select(item => item.Symbol), selected.Artifact, mode,
            request.TimeInForce, request.EntryOrderType, request.AllowExtendedHoursTrading, null, uniqueName, request.NewsEnabled,
            universeSource: screener.Length > 0 ? (wishlist.Length > 0 ? "wishlist+finviz" : "screener") : "wishlist",
            universeResolvedAtUtc: universe.ResolvedAtUtc, selectedSourceTickers: wishlist, resolvedScreenerTickers: screener,
            universeSnapshotId: universe.UniverseSnapshotId);
        return [path, .. reader.ReadBacktestRun(path).Strategies];
    }

    private static RunConfigSummary SelectConfig(IReadOnlyList<RunConfigSummary> configs, string? key) =>
        (String.IsNullOrWhiteSpace(key) ? configs.FirstOrDefault() : configs.SingleOrDefault(item => item.FileName == key))
        ?? throw new InvalidOperationException("Select a server catalog configuration.");
    private static StrategyOption SelectStrategy(IReadOnlyList<StrategyOption> options, StrategyReference identity) =>
        options.SingleOrDefault(item => item.Identity.StrategyId == identity.StrategyId && item.Identity.SemanticVersion == identity.SemanticVersion &&
            item.Identity.ContentSha256.Equals(identity.ContentSha256, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("Selected exact strategy is no longer authorized.");
}
