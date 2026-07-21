using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TradingFlow.Data.Context;

/// <summary>
/// Gives EF tooling a side-effect-free model source instead of booting the web host and local services.
/// </summary>
public sealed class TradingFlowDesignTimeDbContextFactory : IDesignTimeDbContextFactory<TradingFlowDbContext>
{
    public TradingFlowDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
            .UseSqlite("Data Source=tradingflow.design.db")
            .Options;
        return new TradingFlowDbContext(options);
    }
}
