using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public sealed class ManualEntryOptionsTests
{
    [Theory]
    [InlineData("strategy_gated", ManualEntryPolicy.StrategyGated)]
    [InlineData("operator_direct", ManualEntryPolicy.OperatorDirect)]
    public void Parse_KnownPolicy_ReturnsTypedValue(string value, ManualEntryPolicy expected)
    {
        Assert.Equal(expected, ManualEntryOptions.Parse(value).Policy);
    }

    [Fact]
    public void Parse_UnknownPolicy_FailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() => ManualEntryOptions.Parse("bypass"));
    }
}
