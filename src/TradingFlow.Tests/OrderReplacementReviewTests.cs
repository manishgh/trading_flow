using Moq;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Execution;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class OrderReplacementReviewTests
{
    private readonly Mock<IOrderWorkflowService> workflow = new();
    private readonly Mock<IOrderIntentRepository> intents = new();
    private readonly Mock<IOrderEventRepository> events = new();
    private readonly Mock<IReviewedOrderBroker> broker = new();
    private readonly OrderIntentRecord intent = new() { IntentId=Guid.NewGuid(),ClientOrderId="original",AccountId="paper",Symbol="AAA",Side="buy",Kind=OrderIntentKind.StrategyEntry };
    private OrderReplacementReviewService Service => new(workflow.Object,intents.Object,events.Object,broker.Object,new("paper"));
    public OrderReplacementReviewTests() {
        intents.Setup(x => x.GetByClientOrderIdAsync("original",It.IsAny<CancellationToken>())).ReturnsAsync(intent);
        broker.Setup(x => x.GetBrokerContextAsync("AAA",It.IsAny<CancellationToken>())).ReturnsAsync(new ManualBrokerContext(default,default,10000,10000,10,true,true,false,"paper"));
    }
    private OrderStateSnapshot State(OrderState state, decimal? filled = 0) => new(Guid.NewGuid(),"original","broker",state,DateTimeOffset.UtcNow,null,filled,null,1);
    [Theory]
    [InlineData(OrderState.Acked,0)] [InlineData(OrderState.CancelPending,0)] [InlineData(OrderState.Canceled,2)] [InlineData(OrderState.Filled,10)]
    public async Task ReviewDoesNotProceedUntilAcknowledgedCancellationWithZeroFills(OrderState state,decimal fills) {
        events.Setup(x => x.GetCurrentAsync("original",It.IsAny<CancellationToken>())).ReturnsAsync(State(state,fills));
        var result=await Service.ReviewAsync("original",default);
        Assert.False(result.CanReview); Assert.Null(result.ReviewUrl);
        broker.Verify(x => x.GetBrokerContextAsync(It.IsAny<string>(),It.IsAny<CancellationToken>()),Times.Never);
    }
    [Fact]
    public async Task CanceledEntryRefreshesAccountButOnlyOpensCandidateReview() {
        events.Setup(x => x.GetCurrentAsync("original",It.IsAny<CancellationToken>())).ReturnsAsync(State(OrderState.Canceled));
        var result=await Service.ReviewAsync("original",default);
        Assert.True(result.CanReview); Assert.StartsWith("/Desk?search=",result.ReviewUrl);
        Assert.Contains("consumed candidate cannot be reused",result.Detail);
        broker.Verify(x => x.GetBrokerContextAsync("AAA",It.IsAny<CancellationToken>()),Times.Once);
        Assert.Single(broker.Invocations); Assert.Empty(workflow.Invocations);
    }
    [Fact]
    public async Task FillRaceDuringRefreshStopsReview() {
        events.SetupSequence(x => x.GetCurrentAsync("original",It.IsAny<CancellationToken>())).ReturnsAsync(State(OrderState.Canceled)).ReturnsAsync(State(OrderState.Canceled,1));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service.ReviewAsync("original",default));
    }
    [Theory] [InlineData(OrderIntentKind.ProtectiveStop)] [InlineData((OrderIntentKind)999)]
    public async Task ProtectiveAndUnknownIntentsCannotBeCanceledThroughReplace(OrderIntentKind kind) {
        intent.Kind=kind;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service.CancelAsync(new("original","key"),default));
        Assert.Empty(workflow.Invocations); Assert.Empty(broker.Invocations);
    }
    [Fact]
    public async Task KnownUndispatchedRejectionAllowsExplicitEndAndNewReview() {
        events.Setup(x => x.GetCurrentAsync("original",It.IsAny<CancellationToken>())).ReturnsAsync(State(OrderState.Acked));
        workflow.Setup(x=>x.CancelAsync(It.IsAny<CancelOrderRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(
            new OrderCommandResponse("v1",intent.IntentId,"original",null,"rejected","paper","AAA","buy",1,0,null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,"key","command_expired","Undispatched cancellation expired."));
        var result=await Service.CancelAsync(new("original","key"),default);
        Assert.False(result.CanReview); Assert.True(result.CanRelease); Assert.Equal("rejected",result.State);
        Assert.Contains("expired",result.Detail);
        Assert.Empty(broker.Invocations);
    }
    [Fact]
    public async Task UncertainCancellationRetriesUseIdenticalDurableKey() {
        events.Setup(x => x.GetCurrentAsync("original",It.IsAny<CancellationToken>())).ReturnsAsync(State(OrderState.Acked));
        var request=new ReplacementCancelRequest("original","same-key");
        Assert.False((await Service.CancelAsync(request,default)).CanReview);
        Assert.False((await Service.CancelAsync(request,default)).CanReview);
        workflow.Verify(x=>x.CancelAsync(It.Is<CancelOrderRequest>(r=>r.IdempotencyKey=="same-key" && r.OrderIntentId==intent.IntentId),It.IsAny<CancellationToken>()),Times.Exactly(2));
    }
}
