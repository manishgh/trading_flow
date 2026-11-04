using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Web.Services;

/// <summary>Shared web/mobile projection of exact strategy prices, without broker writes.</summary>
public sealed class OrderTicketPlanService(ConfigCatalogService catalog, AlpacaQuoteService quotes,
    OperationalStatusService status, DeskTicketPlanService planner)
{
    public async Task<OrderTicketPlanResponse> PlanAsync(OrderTicketPlanRequest request, CancellationToken ct)
    {
        if (request.ContractVersion != ContractVersions.VersionOne ||
            String.IsNullOrWhiteSpace(request.Symbol) ||
            DeskStreamTickers.Parse(request.Symbol).Count != 1 ||
            request.Symbol.Contains(','))
            throw new InvalidOperationException("A supported contract and one equity symbol are required.");
        var symbol = request.Symbol.Trim().ToUpperInvariant();
        StrategyDefinition? definition = null;
        if (request.Strategy is { } identity)
        {
            if (String.IsNullOrWhiteSpace(identity.StrategyId) || String.IsNullOrWhiteSpace(identity.SemanticVersion) ||
                identity.ContentSha256 is null || identity.ContentSha256.Length != 64 || !identity.ContentSha256.All(Uri.IsHexDigit))
                throw new InvalidOperationException("An exact strategy ID, version and SHA-256 are required.");
            var options = new List<TradingFlow.Web.Models.StrategyOption>();
            foreach (var mode in new[] { StrategySelectionMode.Backtest, StrategySelectionMode.RunPaperExperiment, StrategySelectionMode.RunPaperShadow })
                options.AddRange(await catalog.GetStrategiesAsync(mode, ct));
            definition = options.FirstOrDefault(x => x.Identity.StrategyId == identity.StrategyId &&
                x.Identity.SemanticVersion == identity.SemanticVersion &&
                x.Identity.ContentSha256.Equals(identity.ContentSha256, StringComparison.OrdinalIgnoreCase))?.Definition
                ?? throw new InvalidOperationException("The exact strategy is no longer available. Select it again.");
        }
        var quote = (await quotes.GetLatestQuotesAsync([symbol], "sip", ct, allowStreamed: false)).GetValueOrDefault(symbol);
        var profile = catalog.DefaultPaperConfig();
        var operational = await status.GetAsync("sip", [quote?.Timestamp], ct);
        var plan = await planner.PlanAsync(definition, symbol, quote?.AskPrice, quote?.MidPrice,
            request.LimitPrice, profile?.Config.Engine.IndicatorWarmupBars ?? 0,
            DeskTicketPlanService.RiskBudget(operational.Equity, profile?.Config.Portfolio.AccountRiskBudgetPct), ct);
        return new(ContractVersions.VersionOne, symbol, request.Strategy, plan.Limit, plan.Stop,
            plan.Target, plan.StopNote, plan.TargetNote, plan.RiskBudget);
    }
}
