using TradingFlow.Mobile.Services;

namespace TradingFlow.Tests;

public sealed class NativeExitReviewTests
{
    [Theory]
    [InlineData("de-DE", "1.5")]
    [InlineData("de-DE", "1,5")]
    [InlineData("en-US", "1,5")]
    [InlineData("en-US", "1.5")]
    [InlineData("en-US", "1,000")]
    [InlineData("de-DE", "1.000")]
    [InlineData("en-US", "0")]
    [InlineData("en-US", "-1")]
    [InlineData("en-US", "79228162514264337593543950336")]
    public void QuantityText_RejectsFractionalOrGroupedInput(string culture, string input) =>
        Assert.Throws<InvalidOperationException>(() => NativeExitReview.ParseQuantity(input, System.Globalization.CultureInfo.GetCultureInfo(culture)));

    [Theory]
    [InlineData("de-DE", "101,25")]
    [InlineData("en-US", "101.25")]
    public void PriceText_UsesOnlyTheLocalDecimalSeparator(string culture, string input)
    {
        var locale = System.Globalization.CultureInfo.GetCultureInfo(culture);
        Assert.Equal(101.25m, NativeExitReview.ParsePrice(input, locale, "price"));
        Assert.Equal(1m, NativeExitReview.ParseQuantity("1", locale));
        Assert.Throws<InvalidOperationException>(() => NativeExitReview.ParsePrice(input.Replace(',', '|').Replace('.', ',').Replace('|', '.'), locale, "price"));
    }

    [Theory]
    [InlineData("FILLED", true)]
    [InlineData("Canceled", true)]
    [InlineData("rejected", true)]
    [InlineData("Expired", true)]
    [InlineData("ACKED", false)]
    [InlineData("PartiallyFilled", false)]
    [InlineData("CancelPending", false)]
    [InlineData("confirmation_pending", false)]
    public void JournalStateSpelling_OnlyTerminalStatesReleaseRetainedCommands(string state, bool terminal) =>
        Assert.Equal(terminal, NativeExitReview.IsTerminal(state));

    [Theory]
    [InlineData("market", null, null)]
    [InlineData("limit", 101, null)]
    [InlineData("stop", null, 99)]
    [InlineData("stop_limit", 98, 99)]
    public void ExitShapes_PreserveReviewedIdentityAndDiscardIrrelevantPrices(string type, int? expectedLimit, int? expectedTrigger)
    {
        foreach (var tif in new[] { "day", "gtc", "ioc", "fok" }) {
            var request = NativeExitReview.Create("bound-account", "msft", 10m, 3m, type, tif, false,
                type == "stop_limit" ? 98m : 101m, 99m, "unchanged-key");
            Assert.Equal("MSFT", request.Symbol);
            Assert.Equal("bound-account", request.AccountId);
            Assert.Equal("unchanged-key", request.IdempotencyKey);
            Assert.Equal(3m, request.Quantity);
            Assert.Equal(type, request.OrderType);
            Assert.Equal(tif, request.TimeInForce);
            Assert.Equal(expectedLimit is { } limit ? (decimal?)limit : null, request.LimitPrice);
            Assert.Equal(expectedTrigger is { } trigger ? (decimal?)trigger : null, request.TriggerPrice);
        }
    }

    [Theory]
    [InlineData("limit", null, null)]
    [InlineData("stop", null, null)]
    [InlineData("stop_limit", 100, null)]
    [InlineData("stop_limit", null, 99)]
    public void MissingRequiredPrices_CannotCreateAReviewedCommand(string type, int? limit, int? trigger) =>
        Assert.Throws<InvalidOperationException>(() => NativeExitReview.Create("a", "MSFT", 10m, 3m, type, "day", false, limit, trigger, "key"));

    [Theory]
    [InlineData("market", "day")]
    [InlineData("limit", "gtc")]
    [InlineData("stop", "day")]
    [InlineData("stop_limit", "day")]
    public void ExtendedPermission_RequiresLimitDay(string type, string tif) =>
        Assert.Throws<InvalidOperationException>(() => NativeExitReview.Create("a", "MSFT", 10m, 3m, type, tif, true, 100m, 99m, "key"));

    [Fact]
    public void FractionalHolding_AllowsAnExplicitWholeSharePortionWithoutRounding()
    {
        var request = NativeExitReview.Create("a", "MSFT", 3.75m, 3m, "market", "day", false, null, null, "key");
        Assert.Equal(3m, request.Quantity);
        Assert.Throws<InvalidOperationException>(() => NativeExitReview.Create("a", "MSFT", 3.75m, 4m, "market", "day", false, null, null, "key"));
    }

    [Fact]
    public void ExtendedLimitDay_KeepsPermissionExplicit()
    {
        var request = NativeExitReview.Create("a", "MSFT", 10m, 3m, "limit", "day", true, 100m, 99m, "key");
        Assert.True(request.AllowExtendedHoursTrading);
        Assert.Null(request.TriggerPrice);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.5)]
    [InlineData(11)]
    public void InvalidQuantity_IsNeverRoundedOrReduced(double value) =>
        Assert.Throws<InvalidOperationException>(() => NativeExitReview.Create("a", "MSFT", 10m, (decimal)value, "market", "day", false, null, null, "key"));

    [Theory]
    [InlineData("confirmation_pending", "reconciliation_required", false)]
    [InlineData("working", null, false)]
    [InlineData("cancel_pending", null, false)]
    [InlineData("rejected", "unknown", false)]
    [InlineData("rejected", "command_expired", true)]
    [InlineData("rejected", "close_revalidation_failed", true)]
    [InlineData("rejected", "broker_identity_missing", true)]
    public void OnlyKnownUndispatchedRejection_AllowsEndingLocalReview(string state, string? code, bool allowed)
    {
        var response = new TradingFlow.Contracts.V1.OrderCommandResponse("1.0", Guid.NewGuid(), "", null, state,
            "a", "MSFT", "sell", 3m, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "key", code, null);
        Assert.Equal(allowed, NativeExitReview.CanEndUndispatched(response));
    }
}
