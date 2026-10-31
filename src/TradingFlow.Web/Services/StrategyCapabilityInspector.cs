using TradingFlow.Domain.Strategies;

namespace TradingFlow.Web.Services;

internal static class StrategyCapabilityInspector
{
    /// <summary>Detects declared news entry requirements, including a veto that needs proof that recent news is absent.</summary>
    public static bool UsesNews(StrategyDefinition strategy)
    {
        var entryRules = strategy.EntryRules;
        var longSideUsesNews =
            entryRules.RequirePositiveNews ||
            entryRules.MinNewsSentiment is not null ||
            entryRules.VetoNewsSentimentBelow is not null ||
            entryRules.VetoFreshNewsHours is not null ||
            entryRules.MinCatalystPriceMovePct is not null ||
            entryRules.MaxCatalystPriceMovePct is not null ||
            entryRules.SetupType.Contains("catalyst", StringComparison.OrdinalIgnoreCase);
        var shortSideUsesNews =
            entryRules.EnableShort &&
            (entryRules.MaxShortNewsSentiment is not null ||
             entryRules.MinShortCatalystDropPct is not null ||
             entryRules.ShortSetupType.Contains("catalyst", StringComparison.OrdinalIgnoreCase));

        return longSideUsesNews || shortSideUsesNews;
    }

    /// <summary>Includes the entire fresh-news veto interval even when it exceeds the positive-news age setting.</summary>
    public static decimal RequiredNewsLookbackHours(StrategyDefinition strategy) =>
        Math.Max(strategy.EntryRules.MaxNewsAgeHours, strategy.EntryRules.VetoFreshNewsHours ?? 0m);
}
