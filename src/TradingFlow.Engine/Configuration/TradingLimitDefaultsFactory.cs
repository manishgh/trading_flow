using TradingFlow.Domain.Execution;
using System.Globalization;

namespace TradingFlow.Engine.Configuration;

public static class TradingLimitDefaultsFactory
{
    /// <summary>Keep operational defaults in the checked parameter registry rather than repeating numbers in clients or repositories.</summary>
    public static TradingLimitDefaults FromRegistry() => new(
        (int)ProductionParameterRegistry.ByName["max_new_stocks_per_day"].DefaultValue!,
        (int)ProductionParameterRegistry.ByName["max_positions_swing"].DefaultValue!);

    /// <summary>Apply the existing registry ranges to saved choices as well as host settings, avoiding separate API validation rules.</summary>
    public static void Validate(string mode, int maxNewStocksPerDay, int maxOpenPositions)
    {
        var profile = OperationalTradingLimitProfile.Mode(mode) == "paper" ? ProductionProfile.Paper : ProductionProfile.Live;
        var loader = new ProductionConfigurationLoader();
        try
        {
            _ = loader.ResolveParameter<int>(profile, "max_new_stocks_per_day", maxNewStocksPerDay.ToString(CultureInfo.InvariantCulture));
            _ = loader.ResolveParameter<int>(profile, "max_positions_swing", maxOpenPositions.ToString(CultureInfo.InvariantCulture));
        }
        catch (ProductionConfigurationException error)
        {
            throw new InvalidOperationException(error.Message, error);
        }
    }
}
