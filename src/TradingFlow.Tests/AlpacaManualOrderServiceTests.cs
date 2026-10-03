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
                "MSFT", "buy", 10m, 400m, 396m, 412m, "swing",
                CancellationToken.None));

        Assert.Contains("strategy-gated", error.Message, StringComparison.Ordinal);
        submissions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SubmitLimitOrderAsync_OperatorDirect_UsesCommonProtectedSubmissionPath()
    {
        EntryOrderSubmission? captured = null;
        var submissions = new Mock<IOrderSubmissionService>(MockBehavior.Strict);
        submissions
            .Setup(service => service.SubmitEntryOrderAsync(
                It.IsAny<EntryOrderSubmission>(),
                It.IsAny<IBrokerClient>(),
                It.IsAny<CancellationToken>()))
            .Callback<EntryOrderSubmission, IBrokerClient, CancellationToken>(
                (submission, _, _) => captured = submission)
            .ReturnsAsync((EntryOrderSubmission submission, IBrokerClient _, CancellationToken _) =>
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
                "MSFT", "buy", 10m, 400m, null, null, "swing",
                CancellationToken.None));

        Assert.Contains("requires a stop price", error.Message, StringComparison.Ordinal);
        submissions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StrategyEntry_UsesBoundCandidateAndSharedGatesWithoutOperatorOverride()
    {
        await using var fixture = await OrderWorkflowIntegrityTests.Fixture.CreateAsync();
        var candidate = (await fixture.Database.Candidates.GetAsync(fixture.Request.CandidateId!.Value))!;
        var run = (await new TradingFlow.Data.Orders.SqliteOrderIntentRepository(fixture.Database).GetRunAsync(candidate.RunId))!;
        EntryOrderSubmission? captured = null;
        var submissions = new Mock<IOrderSubmissionService>(MockBehavior.Strict);
        submissions.Setup(item => item.SubmitEntryOrderAsync(It.IsAny<EntryOrderSubmission>(), It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .Callback<EntryOrderSubmission, IBrokerClient, CancellationToken>((item, _, _) => captured = item)
            .ReturnsAsync(new OrderSubmissionResult("broker", "actual-client", Guid.Parse("11111111-1111-1111-1111-111111111111"), fixture.Clock.Now));
        using var service = CreateService(ManualEntryPolicy.StrategyGated, submissions.Object);
        candidate.StrategySemanticVersion = "1.0.0";
        await service.SubmitStrategyEntryAsync(fixture.Request with { Strategy = fixture.Request.Strategy with { SemanticVersion = "1.0.0" } }, Guid.NewGuid(), candidate, run, CancellationToken.None);
        Assert.NotNull(captured);
        Assert.Null(captured.OperatorOverride);
        Assert.Equal(candidate.CandidateId, captured.Candidate.CandidateId);
        Assert.Equal(candidate.Version, captured.Candidate.CandidateVersion);
        Assert.Equal(candidate.SemanticDecisionSha256, captured.Candidate.SemanticDecisionSha256);
        Assert.Equal(run.RunId, captured.RunContext.RunId);
        Assert.Equal(run.UniverseSnapshotId, captured.RunContext.UniverseSnapshotId);
        Assert.Equal(run.DecisionRunId, captured.RunContext.DecisionRunId);
        Assert.Equal(fixture.Request.Strategy.ContentSha256, captured.StrategyIdentity!.ContentSha256);
        Assert.Equal(TradingFlow.Domain.Strategies.StrategySelectionMode.RunPaperShadow, captured.StrategySelectionMode);
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
