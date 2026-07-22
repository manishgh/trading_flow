using Microsoft.AspNetCore.DataProtection;
using Moq;
using TradingFlow.Engine.Execution;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class ManualOrderTicketServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PreviewAsync_FreshRegularSession_ReturnsImmutableReviewEvidence()
    {
        var market = CreateMarket();
        var service = CreateService(market.Object, ManualEntryPolicy.OperatorDirect);

        var preview = await service.PreviewAsync(Draft(), CancellationToken.None);

        Assert.True(preview.CanSubmit);
        Assert.False(String.IsNullOrWhiteSpace(preview.TicketToken));
        Assert.Equal("PAPER", preview.Environment);
        Assert.Equal(100.10m, preview.BidPrice);
        Assert.Equal(100.20m, preview.AskPrice);
        Assert.Equal("regular", preview.Session);
        Assert.Equal("DAY", preview.TimeInForce);
        Assert.Empty(preview.Rejections);
    }

    [Fact]
    public async Task PreviewAsync_StrategyGatedPolicy_FailsClosed()
    {
        var market = CreateMarket();
        var service = CreateService(market.Object, ManualEntryPolicy.StrategyGated);

        var preview = await service.PreviewAsync(Draft(), CancellationToken.None);

        Assert.False(preview.CanSubmit);
        Assert.Null(preview.TicketToken);
        Assert.Contains(preview.Rejections, reason => reason.Contains("strategy-gated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfirmAsync_ChangedQuote_RequiresNewReview()
    {
        var market = CreateMarket();
        var service = CreateService(market.Object, ManualEntryPolicy.OperatorDirect);
        var preview = await service.PreviewAsync(Draft(), CancellationToken.None);
        market.Setup(gateway => gateway.GetLatestQuoteAsync("MSFT", "sip", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlpacaLatestQuote("MSFT", 101.10m, 101.20m, 10m, 10m, Now));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmAsync(preview.TicketToken!, CancellationToken.None));

        Assert.Contains("review the order again", error.Message, StringComparison.OrdinalIgnoreCase);
        market.Verify(gateway => gateway.SubmitAsync(It.IsAny<ManualOrderDraft>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmAsync_RepeatedConfirmation_ReusesStableTicketIdentity()
    {
        var market = CreateMarket();
        var submittedIds = new List<Guid>();
        market.Setup(gateway => gateway.SubmitAsync(It.IsAny<ManualOrderDraft>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<ManualOrderDraft, Guid, CancellationToken>((_, id, _) => submittedIds.Add(id))
            .ReturnsAsync((ManualOrderDraft draft, Guid id, CancellationToken _) =>
                new ManualOrderResult("broker-order-1", draft.Ticker, draft.Side, draft.Quantity, draft.LimitPrice));
        var service = CreateService(market.Object, ManualEntryPolicy.OperatorDirect);
        var preview = await service.PreviewAsync(Draft(), CancellationToken.None);

        await service.ConfirmAsync(preview.TicketToken!, CancellationToken.None);
        await service.ConfirmAsync(preview.TicketToken!, CancellationToken.None);

        Assert.Equal([preview.TicketId, preview.TicketId], submittedIds);
    }

    [Fact]
    public async Task PreviewAsync_ExtendedSessionWithoutPermission_FailsClosed()
    {
        var market = CreateMarket(EquityTradingSession.Premarket);
        var service = CreateService(market.Object, ManualEntryPolicy.OperatorDirect);

        var preview = await service.PreviewAsync(Draft(), CancellationToken.None);

        Assert.False(preview.CanSubmit);
        Assert.Contains(preview.Rejections, reason => reason.Contains("allow_extended_hours_trading is false", StringComparison.Ordinal));
    }

    private static ManualOrderTicketService CreateService(
        IManualOrderMarketGateway market,
        ManualEntryPolicy policy)
    {
        var admission = new Mock<IEntryAdmissionControl>(MockBehavior.Strict);
        admission.Setup(control => control.GetSnapshot()).Returns(new EntryAdmissionSnapshot(true, []));
        return new ManualOrderTicketService(
            market,
            new ManualEntryOptions(policy),
            new EntryGateOptions(20, 2_000, 100m, 15m, 25m, 100m, 100m, 20, 20),
            admission.Object,
            new FixedTimeProvider(Now),
            new EphemeralDataProtectionProvider());
    }

    private static Mock<IManualOrderMarketGateway> CreateMarket(
        EquityTradingSession session = EquityTradingSession.Regular)
    {
        var market = new Mock<IManualOrderMarketGateway>(MockBehavior.Strict);
        market.Setup(gateway => gateway.GetLatestQuoteAsync("MSFT", "sip", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlpacaLatestQuote("MSFT", 100.10m, 100.20m, 10m, 10m, Now));
        market.Setup(gateway => gateway.GetBrokerContextAsync("MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManualBrokerContext(
                session,
                new DateOnly(2026, 7, 22),
                100_000m,
                100_000m,
                0m,
                true,
                true,
                true));
        return market;
    }

    private static ManualOrderDraft Draft() => new(
        "MSFT", "buy", 10m, 100.20m, 99m, 104m, "intraday", false);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
