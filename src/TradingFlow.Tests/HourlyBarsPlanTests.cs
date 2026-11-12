using TradingFlow.Cli;

namespace TradingFlow.Tests;

/// <summary>Planning hourly bars for the sessions a research-screen position may be held on but the stock is not eligible.</summary>
public sealed class HourlyBarsPlanTests
{
    // The complement covers each listed stock's traded sessions after its first eligible day that are not eligible days, up to
    // the window end, so eligible plus complement requests cover every session a position can be open, once.
    [Fact]
    public void NonEligibleSecurities_CoverTradedSessionsAfterFirstEligibilityThatAreNotEligible()
    {
        var days = Enumerable.Range(0, 6).Select(index => new DateOnly(2026, 1, 5).AddDays(index)).ToArray();
        var eligible = new Dictionary<DateOnly, IReadOnlyCollection<string>> { [days[1]] = ["ABC"], [days[3]] = ["ABC"] };
        var traded = new Dictionary<string, (string SecurityId, DateOnly[] Sessions)> { ["ABC"] = ("sec-abc", days) };

        var plan = IntradayBarsCollectionPlanCommand.NonEligibleSecurities(eligible, traded, days[4]);

        Assert.Equal(new[] { days[2], days[4] }, plan.Keys.Order());
        Assert.All(plan.Values, list => Assert.Equal("sec-abc", Assert.Single(list).SecurityId));
    }
}
