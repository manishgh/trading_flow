using TradingFlow.Contracts.V1;
using TradingFlow.Web.Services;
namespace TradingFlow.Tests;
public sealed class OrderTicketPlanApiTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AAPL,MSFT")]
    [InlineData("<script>")]
    public async Task InvalidSymbol_RejectsBeforeProviderRead(string? symbol)
    {
        var planner = new OrderTicketPlanService(null!, null!, null!, null!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => planner.PlanAsync(new("1.0", symbol!, null), default));
    }
    [Fact]
    public async Task IncompleteIdentity_RejectsBeforeProviderRead()
    {
        var planner = new OrderTicketPlanService(null!, null!, null!, null!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => planner.PlanAsync(new("1.0", "AAPL", new("id", "1.0.0", null!)), default));
    }
}
