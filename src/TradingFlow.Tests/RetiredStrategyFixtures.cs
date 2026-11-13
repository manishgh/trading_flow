using TradingFlow.Engine.Configuration;

namespace TradingFlow.Tests;

/// <summary>
/// The test-owned copy of the strategy artifacts retired on 13 November 2026 (Connors RSI(2) long and short, snapback,
/// news-avoidance recovery; docs/research/retired-strategy-artifacts-2026-11-13.md). It is a small repository with the same
/// layout as the real one (configs/strategy-catalog.json, configs/backtest/strategies, configs/backtest run profiles), so the
/// files keep their catalog identities and tests keep exercising the generic engine with them. Checked-in fixtures are source.
/// </summary>
internal static class RetiredStrategyFixtures
{
    /// <summary>The fixture repository root.</summary>
    public static string Root => Path.Combine(TestRepository.FindRoot(), "src", "TradingFlow.Tests", "Fixtures", "RetiredStrategies");

    /// <summary>The fixture's lifecycle catalog of the retired artifacts and their admission profiles.</summary>
    public static string CatalogPath => Path.Combine(Root, "configs", "strategy-catalog.json");

    /// <summary>A retired strategy file by name.</summary>
    public static string Strategy(string fileName) => Path.Combine(Root, "configs", "backtest", "strategies", fileName);

    /// <summary>A run profile that served only retired strategies, by name.</summary>
    public static string Profile(string fileName) => Path.Combine(Root, "configs", "backtest", fileName);

    /// <summary>The fixture catalog, which validates and identifies the retired files exactly as the real catalog did.</summary>
    public static StrategyArtifactCatalog Catalog(SimpleYamlReader? reader = null) =>
        new(Root, CatalogPath, reader ?? new SimpleYamlReader());
}
