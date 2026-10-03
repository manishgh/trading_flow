using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using TradingFlow.Application.Candidates;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Orders;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class OrderWorkflowIntegrityTests
{
    [Fact]
    public async Task StrategyConfirmation_PreservesBindingAndReturnsDurableOutcomeOnExpiredRetry()
    {
        await using var fixture = await Fixture.CreateAsync();
        var preview = await fixture.Service.PreviewAsync(fixture.Request);
        var result = await fixture.Service.ConfirmAsync(new(ContractVersions.VersionOne, preview.PreviewToken, fixture.Request.IdempotencyKey));
        Assert.Equal("ACKED", result.State);
        Assert.Equal("actual-client-id", result.ClientOrderId);
        Assert.Equal(preview.PreviewId, result.OrderIntentId);
        Assert.Equal(fixture.Request.CandidateId, fixture.SubmittedCandidate!.CandidateId);
        Assert.Equal(fixture.Request.Strategy.ContentSha256, fixture.SubmittedCandidate.StrategyContentSha256);
        Assert.Equal(fixture.Request.UniverseSnapshotId, fixture.SubmittedRun!.UniverseSnapshotId);
        fixture.Clock.Now = fixture.Clock.Now.AddHours(1);
        Assert.Equal(result, await fixture.Service.ConfirmAsync(new(ContractVersions.VersionOne, preview.PreviewToken, fixture.Request.IdempotencyKey)));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(preview), System.Text.Json.JsonSerializer.Serialize(await fixture.Service.PreviewAsync(fixture.Request)));
        Assert.Equal(1, fixture.Submissions);
        fixture.Market.Verify(item => item.SubmitAsync(It.IsAny<ManualOrderDraft>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("semantic")]
    [InlineData("universe")]
    [InlineData("risk")]
    [InlineData("quote")]
    [InlineData("expired")]
    public async Task Confirmation_RejectsChangedEvidenceWithoutSubmission(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        var preview = await fixture.Service.PreviewAsync(fixture.Request);
        await using (var context = fixture.Database.CreateDbContext())
        {
            var candidate = await context.Candidates.SingleAsync();
            if (change == "version") candidate.Version++;
            if (change == "semantic") candidate.StrategySemanticVersion = "different";
            if (change == "universe") (await context.UniversePreviews.SingleAsync()).ExpiresAtUtc = fixture.Clock.Now.AddDays(1);
            await context.SaveChangesAsync();
        }
        if (change == "risk") fixture.Equity = 999m;
        if (change == "quote") fixture.Ask = 110m;
        if (change == "expired") fixture.Clock.Now = fixture.Clock.Now.AddMinutes(3);
        var confirm = new ConfirmOrderRequest(ContractVersions.VersionOne, preview.PreviewToken, fixture.Request.IdempotencyKey);
        var result = await fixture.Service.ConfirmAsync(confirm);
        Assert.Equal("rejected", result.State);
        Assert.Equal(0, fixture.Submissions);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(1);
        Assert.Equal(result, await fixture.Service.ConfirmAsync(confirm));
    }

    [Fact]
    public async Task ConcurrentRetry_AfterExpiryDoesNotRejectOrResubmitAnActiveConfirmation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var preview = await fixture.Service.PreviewAsync(fixture.Request);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforePersist = async () => { entered.SetResult(); await release.Task; };
        var confirm = new ConfirmOrderRequest(ContractVersions.VersionOne, preview.PreviewToken, fixture.Request.IdempotencyKey);
        var first = fixture.Service.ConfirmAsync(confirm);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(5);
        Assert.Equal("confirmation_pending", (await fixture.Service.ConfirmAsync(confirm)).State);
        release.SetResult();
        var result = await first;
        Assert.Equal("ACKED", result.State);
        Assert.Equal(result, await fixture.Service.ConfirmAsync(confirm));
        Assert.Equal(1, fixture.Submissions);
    }

    [Fact]
    public async Task LostAcknowledgement_ReconcilesDurableIntentWithoutASecondBrokerCall()
    {
        await using var fixture = await Fixture.CreateAsync();
        var preview = await fixture.Service.PreviewAsync(fixture.Request);
        fixture.LoseAcknowledgement = true;
        var confirm = new ConfirmOrderRequest(ContractVersions.VersionOne, preview.PreviewToken, fixture.Request.IdempotencyKey);
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Service.ConfirmAsync(confirm));
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(5);
        Assert.Equal("ACKED", (await fixture.Service.ConfirmAsync(confirm)).State);
        Assert.Equal(1, fixture.Submissions);
    }

    [Fact]
    public async Task OverrideConfirmation_RechecksAdministratorAndWorkflowTokenCannotUseManualConfirm()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request with { ExecutionPolicy = "operator_override", CandidateId = null, CandidateVersion = null, CandidateRunId = null };
        var preview = await fixture.Service.PreviewAsync(request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Tickets.ConfirmAsync(preview.PreviewToken, CancellationToken.None));
        fixture.Principal.HttpContext!.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator")], "test"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ConfirmAsync(new(ContractVersions.VersionOne, preview.PreviewToken, request.IdempotencyKey)));
        Assert.Equal(0, fixture.Submissions);
    }

    [Fact]
    public async Task Preview_RejectsUnknownPolicyWrongAccountAndConflictingRetry()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PreviewAsync(fixture.Request with { ExecutionPolicy = "unknown" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PreviewAsync(fixture.Request with { AccountId = "other" }));
        await fixture.Service.PreviewAsync(fixture.Request);
        await Assert.ThrowsAsync<IdempotencyKeyConflictException>(() => fixture.Service.PreviewAsync(fixture.Request with { Quantity = 2 }));
        Assert.Equal(0, fixture.Submissions);
    }

    [Fact]
    public async Task InterruptedValidation_RecoversWithANewFenceBeforeAnySubmission()
    {
        await using var fixture = await Fixture.CreateAsync();
        var preview = await fixture.Service.PreviewAsync(fixture.Request);
        var stored = (await fixture.Database.Workflows.FindByKeyAsync(fixture.Request.IdempotencyKey))!;
        var interrupted = await fixture.Database.Workflows.ClaimConfirmationAsync(stored.TokenSha256, stored.IdempotencyKey, fixture.Clock.Now, TimeSpan.FromSeconds(20));
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(21);
        var result = await fixture.Service.ConfirmAsync(new(ContractVersions.VersionOne, preview.PreviewToken, stored.IdempotencyKey));
        Assert.Equal("ACKED", result.State);
        Assert.False(await fixture.Database.Workflows.TryBeginSubmissionAsync(stored.PreviewId, interrupted.LeaseToken!.Value, fixture.Clock.Now));
        Assert.Equal(1, fixture.Submissions);
    }

    [Fact]
    public async Task Cancel_RetryReusesOutcomeAndChangedIntentIsRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var preview = await fixture.Service.PreviewAsync(fixture.Request);
        var entry = await fixture.Service.ConfirmAsync(new(ContractVersions.VersionOne, preview.PreviewToken, fixture.Request.IdempotencyKey));
        var calls = 0;
        fixture.Broker.Setup(item => item.CancelOrderAsync("broker-order", It.IsAny<CancellationToken>())).Returns(async () =>
        {
            calls++;
            await using var context = fixture.Database.CreateDbContext();
            var previous = await context.OrderEvents.SingleAsync();
            context.OrderEvents.Add(new OrderEventRecord { RunId = previous.RunId, ConfigHash = previous.ConfigHash, CodeVersion = previous.CodeVersion,
                ClientOrderId = previous.ClientOrderId, BrokerOrderId = previous.BrokerOrderId, PreviousState = "ACKED", NewState = "CANCELED",
                LocalTimestampUtc = fixture.Clock.Now, Source = "broker", PayloadJson = "{}" });
            await context.SaveChangesAsync();
            return true;
        });
        var request = new CancelOrderRequest(ContractVersions.VersionOne, "account", entry.OrderIntentId, "operator cancel", "cancel-order");
        var result = await fixture.Service.CancelAsync(request);
        Assert.Equal("CANCELED", result.State);
        Assert.Equal(result, await fixture.Service.CancelAsync(request));
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<IdempotencyKeyConflictException>(() => fixture.Service.CancelAsync(request with { Reason = "different" }));
    }

    [Fact]
    public async Task Close_RetryDoesNotRereadPositionOrCreateAnotherExit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var calls = 0;
        fixture.Broker.Setup(item => item.GetBrokerContextAsync("MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManualBrokerContext(EquityTradingSession.Regular, new DateOnly(2026, 9, 27), 10000, 10000, 10, true, true, false, "account"));
        fixture.Broker.Setup(item => item.SubmitLimitOrderAsync("MSFT", "sell", 10m, 100m, null, null, "swing", It.IsAny<CancellationToken>(), false, It.IsAny<Guid?>(), "limit", null, "day"))
            .Returns<string, string, decimal, decimal, decimal?, decimal?, string?, CancellationToken, bool, Guid?, string, decimal?, string>(async (_, _, quantity, price, _, _, _, token, _, runId, _, _, _) =>
            {
                calls++;
                await using var context = fixture.Database.CreateDbContext();
                var original = await context.ProductionRuns.SingleAsync();
                var id = Guid.NewGuid();
                context.ProductionRuns.Add(new ProductionRun { RunId = runId!.Value, Profile = "paper", Status = "running", StartedAtUtc = fixture.Clock.Now,
                    ConfigHash = original.ConfigHash, CodeVersion = original.CodeVersion });
                context.OrderIntents.Add(new OrderIntentRecord { IntentId = id, RunId = runId.Value, Kind = OrderIntentKind.PositionExit,
                    ConfigHash = original.ConfigHash, CodeVersion = original.CodeVersion, AccountId = "account", ClientOrderId = "exit-client", Symbol = "MSFT",
                    StrategyId = "exit", Side = "sell", OrderType = "limit", TimeInForce = "day", RequestedQuantity = quantity,
                    CreatedAtUtc = fixture.Clock.Now, SessionDate = new DateOnly(2026, 9, 27), SequenceNumber = 1, RequestJson = "{}" });
                context.OrderEvents.Add(new OrderEventRecord { RunId = runId.Value, ConfigHash = original.ConfigHash, CodeVersion = original.CodeVersion,
                    ClientOrderId = "exit-client", BrokerOrderId = "exit-broker", NewState = "ACKED", LocalTimestampUtc = fixture.Clock.Now, Source = "broker", PayloadJson = "{}" });
                await context.SaveChangesAsync(token);
                return new ManualOrderResult("exit-broker", "MSFT", "sell", quantity, price, id, "exit-client");
            });
        var request = new ClosePositionRequest(ContractVersions.VersionOne, "account", "MSFT", null, "close position", false, 100m, "close-order");
        var result = await fixture.Service.CloseAsync(request);
        Assert.Equal("ACKED", result.State);
        Assert.Equal(10m, result.RequestedQuantity);
        Assert.Equal("exit-client", result.ClientOrderId);
        Assert.Equal(result, await fixture.Service.CloseAsync(request));
        fixture.Broker.Verify(item => item.GetBrokerContextAsync("MSFT", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<IdempotencyKeyConflictException>(() => fixture.Service.CloseAsync(request with { Quantity = 2 }));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedUndispatchedCommand_ExpiresToAStableOutcome(bool close)
    {
        await using var fixture = await Fixture.CreateAsync();
        var preview = await fixture.Service.PreviewAsync(fixture.Request);
        var entry = await fixture.Service.ConfirmAsync(new(ContractVersions.VersionOne, preview.PreviewToken, fixture.Request.IdempotencyKey));
        var cancelRequest = new CancelOrderRequest(ContractVersions.VersionOne, "account", entry.OrderIntentId, "cancel", "interrupted");
        var closeRequest = new ClosePositionRequest(ContractVersions.VersionOne, "account", "MSFT", null, "close", false, 100m, "interrupted");
        var json = close ? System.Text.Json.JsonSerializer.Serialize(closeRequest) : System.Text.Json.JsonSerializer.Serialize(cancelRequest);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
        var stored = await fixture.Database.Workflows.SaveAsync(new OrderPreviewRecord {
            PreviewId = Guid.NewGuid(), IdempotencyKey = "interrupted", TokenSha256 = new string('e', 64),
            RequestSha256 = hash, RequestJson = json, PreviewJson = "{}", Status = "previewed",
            CreatedAtUtc = fixture.Clock.Now, ExpiresAtUtc = fixture.Clock.Now.AddMinutes(2)
        });
        await fixture.Database.Workflows.ClaimConfirmationAsync(stored.TokenSha256, stored.IdempotencyKey, fixture.Clock.Now, TimeSpan.FromSeconds(20));
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(3);
        Task<OrderCommandResponse> Send() => close ? fixture.Service.CloseAsync(closeRequest) : fixture.Service.CancelAsync(cancelRequest);
        var result = await Send();
        Assert.Equal("rejected", result.State);
        Assert.Equal("command_expired", result.RejectionCode);
        Assert.Equal(result, await Send());
        fixture.Broker.Verify(item => item.CancelOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Broker.Verify(item => item.GetBrokerContextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    internal sealed class TestHttpContextAccessor : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } }

    internal sealed class Fixture : IAsyncDisposable
    {
        internal CandidateWorkflowTestDatabase Database { get; } = new();
        internal CandidateWorkflowTestClock Clock { get; } = new(CandidateAuditIntegrityTests.Now);
        internal Mock<IManualOrderMarketGateway> Market { get; } = new(MockBehavior.Strict);
        internal Mock<IReviewedOrderBroker> Broker { get; } = new(MockBehavior.Strict);
        internal TestHttpContextAccessor Principal { get; } = new() { HttpContext = new DefaultHttpContext
        { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator"), new Claim(ClaimTypes.Role, "Administrator")], "test")) } };
        internal decimal Equity = 100_000m;
        internal decimal Ask = 100.20m;
        internal int Submissions;
        internal bool LoseAcknowledgement;
        internal Func<Task>? BeforePersist;
        internal CandidateRecord? SubmittedCandidate;
        internal ProductionRun? SubmittedRun;
        internal PreviewOrderRequest Request = null!;
        internal ManualOrderTicketService Tickets = null!;
        internal OrderWorkflowService Service = null!;

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await fixture.Database.InitializeAsync();
            var preview = await UniverseWorkflowIntegrityTests.Preview(fixture.Database, fixture.Clock);
            var run = await UniverseWorkflowIntegrityTests.Workflow(fixture.Database, fixture.Clock).StartAsync(UniverseWorkflowIntegrityTests.Request(preview));
            await using (var context = fixture.Database.CreateDbContext())
            {
                var candidate = await context.Candidates.SingleAsync();
                candidate.State = StrategyCandidateState.Triggered;
                candidate.Version = 3;
                candidate.SemanticDecisionSha256 = new string('b', 64);
                (await context.ProductionRuns.SingleAsync()).Profile = "paper_shadow";
                await context.SaveChangesAsync();
            }
            fixture.Request = new(ContractVersions.VersionOne, "account", run.Candidates[0].CandidateId, 3, run.CandidateRunId,
                preview.UniverseSnapshotId, run.Candidates[0].Strategy, "MSFT", "buy", 1, "limit", "day", 100.2m, 98m, 106m,
                false, "strategy_validated", "review-order");
            fixture.Market.Setup(item => item.GetLatestQuoteAsync("MSFT", "sip", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new AlpacaLatestQuote("MSFT", 100.1m, fixture.Ask, 10, 10, fixture.Clock.Now));
            fixture.Market.Setup(item => item.GetBrokerContextAsync("MSFT", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ManualBrokerContext(EquityTradingSession.Regular, new DateOnly(2026, 9, 27), 100_000m, fixture.Equity, 0, true, true, false, "account"));
            var admission = new Mock<IEntryAdmissionControl>();
            admission.Setup(item => item.GetSnapshot()).Returns(new EntryAdmissionSnapshot(true, []));
            fixture.Tickets = new(fixture.Market.Object, new ManualEntryOptions(ManualEntryPolicy.OperatorDirect),
                new EntryGateOptions(20, 2000, 100, 15, 25, 100, 20), admission.Object, new TradingEnvironmentService(), fixture.Clock,
                new EphemeralDataProtectionProvider(), fixture.Principal);
            fixture.Broker.Setup(item => item.SubmitStrategyEntryAsync(It.IsAny<PreviewOrderRequest>(), It.IsAny<Guid>(),
                It.IsAny<CandidateRecord>(), It.IsAny<ProductionRun>(), It.IsAny<CancellationToken>()))
                .Returns<PreviewOrderRequest, Guid, CandidateRecord, ProductionRun, CancellationToken>(async (request, id, candidate, persistedRun, cancellationToken) =>
                {
                    fixture.Submissions++;
                    fixture.SubmittedCandidate = candidate;
                    fixture.SubmittedRun = persistedRun;
                    if (fixture.BeforePersist is not null) await fixture.BeforePersist();
                    await using var context = fixture.Database.CreateDbContext();
                    context.OrderIntents.Add(new OrderIntentRecord { IntentId = id, RunId = candidate.RunId, CandidateId = candidate.CandidateId,
                        ConfigHash = candidate.ConfigHash, CodeVersion = candidate.CodeVersion, Kind = OrderIntentKind.StrategyEntry,
                        ClientOrderId = "actual-client-id", AccountId = "account", StrategyId = candidate.SelectedStrategy!, Symbol = "MSFT",
                        Side = "buy", OrderType = "limit", TimeInForce = "day", RequestedQuantity = request.Quantity, CreatedAtUtc = fixture.Clock.Now,
                        SessionDate = new DateOnly(2026, 9, 27), SequenceNumber = 1, RequestJson = "{}" });
                    context.OrderEvents.Add(new OrderEventRecord { RunId = candidate.RunId, ConfigHash = candidate.ConfigHash, CodeVersion = candidate.CodeVersion,
                        ClientOrderId = "actual-client-id", BrokerOrderId = "broker-order", NewState = "ACKED", LocalTimestampUtc = fixture.Clock.Now,
                        Source = "broker", PayloadJson = "{}" });
                    await context.SaveChangesAsync(cancellationToken);
                    if (fixture.LoseAcknowledgement) throw new HttpRequestException("Lost response after durable acknowledgement");
                    return new ManualOrderResult("broker-order", "MSFT", "buy", request.Quantity, 100.2m, id, "actual-client-id");
                });
            fixture.Service = new(fixture.Tickets, fixture.Broker.Object, fixture.Database.Candidates, fixture.Database.Workflows,
                Mock.Of<IStrategyIdentityResolver>(), fixture.Database.Workflows, new SqliteOrderIntentRepository(fixture.Database),
                new SqliteOrderEventRepository(fixture.Database), new BrokerAccountBindingOptions("account"), fixture.Principal, fixture.Clock);
            return fixture;
        }
        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
