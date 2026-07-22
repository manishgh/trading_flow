using Microsoft.Extensions.Configuration;
using Moq;
using TradingFlow.Domain.Orders;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class AlpacaManualOrderServiceTests
{
    [Fact]
    public async Task SubmitLimitOrderAsync_StrategyGatedPolicy_RejectsManualBuy()
    {
        var submissions = new Mock<IOrderSubmissionService>(MockBehavior.Strict);
        using var service = CreateService(ManualEntryPolicy.StrategyGated, submissions.Object);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitLimitOrderAsync(
                "MSFT", "buy", 10m, 400m, 396m, 412m, "intraday",
                CancellationToken.None));

        Assert.Contains("strategy-gated", error.Message, StringComparison.Ordinal);
        submissions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SubmitLimitOrderAsync_OperatorDirect_UsesCommonProtectedSubmissionPath()
    {
        BracketOrderSubmission? captured = null;
        var submissions = new Mock<IOrderSubmissionService>(MockBehavior.Strict);
        submissions
            .Setup(service => service.SubmitBracketOrderAsync(
                It.IsAny<BracketOrderSubmission>(),
                It.IsAny<IBrokerClient>(),
                It.IsAny<CancellationToken>()))
            .Callback<BracketOrderSubmission, IBrokerClient, CancellationToken>(
                (submission, _, _) => captured = submission)
            .ReturnsAsync((BracketOrderSubmission submission, IBrokerClient _, CancellationToken _) =>
                new OrderSubmissionResult(
                    "broker-order-1",
                    submission.Order.ClientOrderId,
                    submission.IntentId,
                    DateTimeOffset.UtcNow));
        using var service = CreateService(ManualEntryPolicy.OperatorDirect, submissions.Object);

        var result = await service.SubmitLimitOrderAsync(
            "msft", "buy", 10m, 400m, 396m, 412m, "swing",
            CancellationToken.None);

        Assert.Equal("broker-order-1", result.OrderId);
        Assert.NotNull(captured);
        Assert.Equal("MSFT", captured.Order.Ticker);
        Assert.Equal("operator_direct", captured.Candidate.DiscoverySource);
        Assert.Equal("swing", captured.Candidate.Horizon);
        Assert.Equal(396m, captured.Order.StopLossPrice);
        Assert.Equal(412m, captured.Order.TakeProfitPrice);
        Assert.False(captured.AllowExtendedHoursTrading);
        submissions.VerifyAll();
    }

    [Fact]
    public async Task SubmitLimitOrderAsync_OperatorDirect_RequiresExplicitProtection()
    {
        var submissions = new Mock<IOrderSubmissionService>(MockBehavior.Strict);
        using var service = CreateService(ManualEntryPolicy.OperatorDirect, submissions.Object);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitLimitOrderAsync(
                "MSFT", "buy", 10m, 400m, null, null, "intraday",
                CancellationToken.None));

        Assert.Contains("requires a stop price", error.Message, StringComparison.Ordinal);
        submissions.VerifyNoOtherCalls();
    }

    private static AlpacaManualOrderService CreateService(
        ManualEntryPolicy policy,
        IOrderSubmissionService submissions)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alpaca:KeyId"] = "test-key",
                ["Alpaca:SecretKey"] = "test-secret"
            })
            .Build();
        return new AlpacaManualOrderService(
            new AlpacaCredentialProvider(configuration),
            Mock.Of<IRawArchiveWriter>(),
            submissions,
            new ManualEntryOptions(policy));
    }
}
