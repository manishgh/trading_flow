using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public sealed class ExtendedHoursOrderPolicyTests
{
    [Fact]
    public void ValidateConfiguration_ExtendedHoursDisabled_AllowsRegularMarketDefaults()
    {
        ExtendedHoursOrderPolicy.ValidateConfiguration(
            "market",
            "gtc",
            allowExtendedHoursTrading: false);
    }

    [Theory]
    [InlineData("market", "day")]
    [InlineData("limit", "gtc")]
    [InlineData(null, "day")]
    [InlineData("limit", null)]
    public void ValidateConfiguration_InvalidExtendedHoursContract_IsRejected(
        string? orderType,
        string? timeInForce)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ExtendedHoursOrderPolicy.ValidateConfiguration(
                orderType,
                timeInForce,
                allowExtendedHoursTrading: true));

        Assert.Equal(
            "Extended-hours execution requires an explicit limit entry and DAY expiration.",
            error.Message);
    }

    [Fact]
    public void ValidateConfiguration_DayLimitExtendedHoursContract_IsAccepted()
    {
        ExtendedHoursOrderPolicy.ValidateConfiguration(
            "limit",
            "day",
            allowExtendedHoursTrading: true);
    }

    [Fact]
    public void Validate_RegularMarketOrder_IsAllowedWhenExtendedHoursTradingIsDisabled()
    {
        ExtendedHoursOrderPolicy.Validate(
            Session(EquityTradingSession.Regular),
            "market",
            "day",
            allowExtendedHoursTrading: false);
    }

    [Fact]
    public void Validate_PremarketOrder_FailsWhenExtendedHoursTradingIsDisabled()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ExtendedHoursOrderPolicy.Validate(
                Session(EquityTradingSession.Premarket),
                "limit",
                "day",
                allowExtendedHoursTrading: false));

        Assert.Contains("allow_extended_hours_trading is false", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("market", "day", "explicit limit order")]
    [InlineData("limit", "gtc", "time_in_force=day")]
    public void Validate_ExtendedHoursContract_FailsClosed(
        string orderType,
        string timeInForce,
        string expectedMessage)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ExtendedHoursOrderPolicy.Validate(
                Session(EquityTradingSession.AfterHours),
                orderType,
                timeInForce,
                allowExtendedHoursTrading: true));

        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EquityTradingSession.Premarket)]
    [InlineData(EquityTradingSession.AfterHours)]
    [InlineData(EquityTradingSession.Overnight)]
    public void Validate_EnabledDayLimit_FailsWhenBrokerProtectionIsUnavailable(EquityTradingSession session)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ExtendedHoursOrderPolicy.Validate(
                Session(session),
                "limit",
                "day",
                allowExtendedHoursTrading: true));

        Assert.Contains("does not support broker-protected bracket orders", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ProviderReportedClosed_FailsRegardlessOfToggle()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ExtendedHoursOrderPolicy.Validate(
                Session(EquityTradingSession.Closed),
                "limit",
                "day",
                allowExtendedHoursTrading: true));

        Assert.Contains("reports trade date", error.Message, StringComparison.Ordinal);
    }

    private static TradingSessionSnapshot Session(EquityTradingSession session) =>
        new(
            new DateOnly(2026, 7, 21),
            session,
            DateTimeOffset.Parse("2026-07-21T14:00:00Z"),
            DateTimeOffset.Parse("2026-07-21T13:30:00Z"),
            DateTimeOffset.Parse("2026-07-21T20:00:00Z"));
}
