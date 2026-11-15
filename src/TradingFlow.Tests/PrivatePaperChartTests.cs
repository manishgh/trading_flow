using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingFlow.Application.Jobs;
using TradingFlow.Data.Context;
using TradingFlow.Data.Identity;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;
using TradingFlow.Web;
using TradingFlow.Web.Services.Charts;

namespace TradingFlow.Tests;

[Collection("Chart transport measurements")]
public sealed class PrivatePaperChartTests
{
    private const string Account = "paper-fixture-account";
    private const string Symbol = "AAPL";
    private const string Code = "fixture-policy";
    private static readonly string Hash = new('a', 64);
    private static readonly Guid LiveIntent = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 16, 0, 0, TimeSpan.Zero);

    // Read the frozen plan, exact partial unit fills, cross-run close and verified replacement from a real SQLite journal.
    [Fact]
    public async Task PositionEvidenceJoinsExactGenerationAndProtectedOwnerWithoutPriceCoordinates()
    {
        await using var fixture = await Fixture.StartAsync();
        var selection = await fixture.IssueAsync();
        Assert.Equal(1L, selection.GenerationId);
        Assert.Equal("paper", selection.Profile);
        await fixture.CloseGenerationAsync();
        var windowStart = Now.AddDays(-1);
        var response = await fixture.ReadAsync(selection, windowStart, Now);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var events = page.RootElement.GetProperty("events").EnumerateArray().ToArray();
        var decision = events.Single(row => row.GetProperty("kind").GetString() == "decision");
        Assert.Equal("Frozen candidate triggered", decision.GetProperty("reason").GetString());
        Assert.Equal(new[] { "fill-first", "fill-second", "turn-to-flat" },
            events.Where(row => row.GetProperty("executionId").ValueKind == JsonValueKind.String)
                .Select(row => row.GetProperty("executionId").GetString()));
        var unit = events.Single(row => row.GetProperty("executionId").GetString() == "fill-second");
        Assert.Equal(100.125m, Decimal.Parse(unit.GetProperty("rawPrice").GetString()!, CultureInfo.InvariantCulture));
        Assert.Equal(3m, Decimal.Parse(unit.GetProperty("quantity").GetString()!, CultureInfo.InvariantCulture));
        Assert.Equal(Now.AddHours(-3), unit.GetProperty("eventAtUtc").GetDateTimeOffset());
        Assert.False(unit.TryGetProperty("chartPrice", out _));
        Assert.False(unit.TryGetProperty("price", out _));
        var openingStop = events.Single(row => row.GetProperty("label").GetString() == "Broker-effective stop");
        Assert.Equal(Now.AddHours(-2), openingStop.GetProperty("endAtUtc").GetDateTimeOffset());
        var replacement = events.Single(row => row.GetProperty("label").GetString() == "Broker-effective replacement stop");
        Assert.Equal(95m, Decimal.Parse(replacement.GetProperty("rawPrice").GetString()!, CultureInfo.InvariantCulture));
        Assert.DoesNotContain(events, row => row.GetProperty("clientOrderId").GetString() == "other-generation");
        Assert.DoesNotContain(events, row => row.GetProperty("kind").GetString() == "effective_target");

        await fixture.AssertIndexedQueryAsync("FROM \"position_events\"", "position_generation_event_id", "LIMIT", "position_event_id\" <>");
        await fixture.AssertIndexedQueryAsync("FROM \"order_intents\"", "position_generation_event_id", "LIMIT", "position_generation_event_id\" =");
        await fixture.AssertIndexedQueryAsync("FROM \"order_events\"", "client_order_id", "LIMIT");
        await fixture.AssertIndexedQueryAsync("FROM \"protective_stop_replacements\"", "root_broker_order_id", "LIMIT");
    }

    // An exact protective order follows its verified successor without reading other orders from that generation.
    [Fact]
    public async Task ExactProtectiveOrderShowsVerifiedSuccessorAndEndsOldStop()
    {
        await using var fixture = await Fixture.StartAsync(grantProtectiveOrderJob: true);
        var selection = await fixture.IssueAsync("stop");
        Assert.Null(selection.GenerationId);

        var page = await fixture.ReadPageAsync(selection, Now.AddDays(-1), Now);
        var former = Assert.Single(page.Events, item => item.Label == "Broker-effective stop");
        Assert.Equal(Now.AddHours(-2), former.EndAtUtc);
        var active = Assert.Single(page.ActiveLevels);
        Assert.Equal("Broker-effective replacement stop", active.Label);
        Assert.Equal(95m, active.RawPrice);
        Assert.All(page.Events, item => Assert.Equal("stop", item.ClientOrderId));
        await fixture.AssertIndexedQueryAsync("FROM \"protective_stop_replacements\"", "root_broker_order_id", "LIMIT");

        await fixture.InvalidateOpeningRunProvenanceAsync();
        var unresolved = await fixture.ReadPageAsync(selection, Now.AddDays(-1), Now);
        Assert.Empty(unresolved.ActiveLevels);
        Assert.Contains(unresolved.Events, item => item.Label == "Broker stop acknowledged");
        Assert.Contains("position generation and replacement history", unresolved.Detail);
    }

    // Exact manual grants, current role rows and active account grants are checked independently of the browser's role claim.
    [Fact]
    public async Task RevocationExpiryAndExactIdentityDenyPreviouslyIssuedContext()
    {
        await using var fixture = await Fixture.StartAsync();
        var original = fixture.Options.CurrentValue;
        var selection = await fixture.IssueAsync("entry", fixture.Job);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.SelectAsync(Symbol, "entry", Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.SelectAsync("MSFT", "entry", fixture.Job)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.SelectAsync(Symbol, "foreign", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.SelectAsync(Symbol, "live", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.ReadAsync(selection with { Symbol = "MSFT" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.ReadAsync(selection, user: Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.ReadAsync(selection, origin: "https://elsewhere.example")).StatusCode);

        fixture.Options.CurrentValue = new();
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.ReadAsync(selection)).StatusCode);
        fixture.Options.CurrentValue = original;
        var manual = await fixture.IssueAsync("manual");
        var manualResponse = await fixture.ReadAsync(manual);
        var body = await manualResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, manualResponse.StatusCode);
        Assert.Contains("manual", body);
        Assert.DoesNotContain("fill-first", body);
        Assert.DoesNotContain("reconciliation-exit", body);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.SelectAsync(Symbol, "ungranted-manual", null)).StatusCode);

        fixture.Clock.Now = Now.AddMinutes(6);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.ReadAsync(selection)).StatusCode);
        var renewed = await fixture.IssueAsync("entry", fixture.Job);
        await fixture.RemoveRoleAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.ReadAsync(renewed)).StatusCode);
    }

    // Bad windows perform no private journal acquisition. A sentinel stops the generation before decoding or following links.
    [Fact]
    public async Task WindowValidationAndOverflowStopBeforeAdditionalJournalReads()
    {
        await using var fixture = await Fixture.StartAsync(extraPositions: 400);
        var selection = await fixture.IssueAsync();
        fixture.Commands.Clear();
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.ReadAsync(selection, Now.AddDays(-31).AddTicks(-1), Now)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.ReadAsync(selection, Now, Now)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.ReadAsync(selection, Now.AddDays(-1), Now.AddTicks(1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.ReadAsync(selection, Now.AddDays(-1).ToOffset(TimeSpan.FromHours(1)), Now)).StatusCode);
        Assert.Empty(fixture.Commands.Snapshot());
        var page = await fixture.ReadPageAsync(selection, Now.AddDays(-31), Now);
        Assert.True(page.Incomplete);
        Assert.Empty(page.Events);
        Assert.Empty(page.ActiveLevels);
        Assert.DoesNotContain(fixture.Commands.Snapshot(), command => command.Text.Contains("FROM \"order_events\"", StringComparison.Ordinal));
        await fixture.AssertIndexedQueryAsync("FROM \"position_events\"", "position_generation_event_id", "LIMIT", "position_event_id\" <>");
    }

    // Half-open UTC windows and opaque continuations retain the full execution order across pages.
    [Fact]
    public async Task ExactOrderPagingUsesStableExecutionIdsAndRejectsChangedWindow()
    {
        await using var fixture = await Fixture.StartAsync(extraPositions: 110);
        var selected = await fixture.IssueAsync("entry", fixture.Job);
        var first = await fixture.ReadPageAsync(selected, Now.AddDays(-1), Now);
        Assert.True(first.Truncated);
        Assert.Equal(100, first.Events.Count);
        Assert.NotNull(first.Continuation);
        var second = await fixture.ReadPageAsync(selected, Now.AddDays(-1), Now, first.Continuation);
        Assert.False(second.Truncated);
        Assert.NotEmpty(second.Events);
        Assert.Empty(first.Events.Select(row => row.Id).Intersect(second.Events.Select(row => row.Id)));
        Assert.DoesNotContain(second.Events, row => row.ExecutionId is "outside-exclusive" or "turn-to-flat");
        Assert.Contains(first.Events.Concat(second.Events), item => item.ExecutionId == "clock-missing" && item.EventAtUtc is null);
        Assert.Equal(HttpStatusCode.NotFound,
            (await fixture.ReadAsync(selected, Now.AddDays(-2), Now, first.Continuation)).StatusCode);
        await fixture.AssertIndexedQueryAsync("FROM \"position_events\"", "broker_order_id", "LIMIT", "account_id\" =");
    }

    // The production mapper rejects a forged read POST before invoking the endpoint and stores no response in a browser cache.
    [Fact]
    public async Task AuthAndAntiforgeryProtectThePrivateEndpoint()
    {
        await using var fixture = await Fixture.StartAsync();
        var selection = await fixture.IssueAsync("entry", fixture.Job);
        var noAuth = await fixture.ReadAsync(selection, user: Guid.Empty, sendProof: false);
        Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);
        Assert.True(noAuth.Headers.CacheControl!.NoStore);
        var noProof = await fixture.ReadAsync(selection, sendProof: false);
        Assert.Equal(HttpStatusCode.BadRequest, noProof.StatusCode);
        Assert.True(noProof.Headers.CacheControl!.NoStore);
    }

    private sealed class Fixture(WebApplication app, SqliteConnection connection, Factory database, StoreOptions settings,
        CandidateWorkflowTestClock clock, CaptureCommands commands, Guid operatorId, Guid roleId, Guid jobId, HttpClient client)
        : IAsyncDisposable
    {
        public StoreOptions Options => settings;
        public CandidateWorkflowTestClock Clock => clock;
        public CaptureCommands Commands => commands;
        public Guid Job => jobId;

        // Start the production chart endpoint and antiforgery middleware with one synthetic SQLite and Identity store.
        public static async Task<Fixture> StartAsync(int extraPositions = 0, bool grantProtectiveOrderJob = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var commands = new CaptureCommands();
            var database = new Factory(new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(connection)
                .AddInterceptors(commands).Options);
            var user = Guid.NewGuid();
            var role = Guid.NewGuid();
            var job = Guid.NewGuid();
            var manual = Guid.NewGuid();
            var reconciliation = Guid.NewGuid();
            await SeedAsync(database, user, role, job, manual, reconciliation, extraPositions, grantProtectiveOrderJob);
            var settings = new StoreOptions(new()
            {
                Grants = [new()
                {
                    UserId = user, PaperAccountId = Account,
                    PaperJobIds = grantProtectiveOrderJob ? [reconciliation] : [job],
                    ManualOrderIntentIds = [manual, LiveIntent]
                }]
            });
            var clock = new CandidateWorkflowTestClock(Now);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddControllers();
            builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuth>("fixture", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddApiMutationSecurity();
            builder.Services.AddSingleton<IDbContextFactory<TradingFlowDbContext>>(database);
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddSingleton(new BrokerAccountBindingOptions(Account));
            builder.Services.AddSingleton<ChartAccessPolicy>();
            builder.Services.AddSingleton<ChartTransportLimits>();
            builder.Services.AddPrivatePaperChart(builder.Configuration);
            builder.Services.AddSingleton<IOptionsMonitor<PrivatePaperChartOptions>>(settings);
            var app = builder.Build();
            app.UseRouting();
            app.UsePrivatePaperChartNoStore();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseApiMutationSecurity();
            app.MapPrivatePaperChart();
            app.MapGet("/fixture/page", async (HttpContext http, Guid? job, string? client, string symbol, CancellationToken token) =>
            {
                var result = await PrivatePaperChartEndpoints.IssueAsync(http, job, client, symbol, paper: true, token);
                await result.ExecuteResultAsync(new ActionContext(http, new RouteData(), new ActionDescriptor()));
            }).RequireAuthorization();
            app.MapGet("/fixture/csrf", (HttpContext http, IAntiforgery csrf) => csrf.GetAndStoreTokens(http).RequestToken!)
                .RequireAuthorization();
            await app.StartAsync();
            var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            commands.Clear();
            return new(app, connection, database, settings, clock, commands, user, role, job, client);
        }

        // Seed retained journal facts, including a reconciler owned exit and a similarly named unauthorized order.
        private static async Task SeedAsync(Factory factory, Guid user, Guid role, Guid job, Guid manual, Guid reconciliation,
            int additional, bool grantProtectiveOrderJob)
        {
            await using var db = factory.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(new TradingFlowUser { Id = user, UserName = "chart-fixture", NormalizedUserName = "CHART-FIXTURE" });
            db.Roles.Add(new IdentityRole<Guid> { Id = role, Name = TradingFlowRoles.Administrator, NormalizedName = "ADMINISTRATOR" });
            db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user, RoleId = role });
            var other = Guid.NewGuid();
            var standalone = Guid.NewGuid();
            var ungranted = Guid.NewGuid();
            var live = Guid.NewGuid();
            db.Jobs.Add(new PersistedJob
            {
                Id = job, RunName = "Fixture paper run", JobType = "paper",
                RequestJson = new DurableFileJobRequest(1, Now.AddDays(-1), [new("fixture.yml", 1, Hash)]).Serialize()
            });
            if (grantProtectiveOrderJob)
                db.Jobs.Add(new PersistedJob
                {
                    Id = reconciliation, RunName = "Fixture protective owner", JobType = "paper",
                    RequestJson = new DurableFileJobRequest(1, Now.AddDays(-1), [new("fixture.yml", 1, Hash)]).Serialize()
                });
            db.ProductionRuns.AddRange(new[] { job, reconciliation, other, standalone, ungranted, live }.Select(id =>
                new ProductionRun { RunId = id, Profile = id == live ? "live" : "paper", ConfigHash = Hash,
                    CodeVersion = Code, StartedAtUtc = Now.AddDays(-1) }));
            var entry = Order(job, "entry", 1);
            const string triggerEvidence = """{"fixture":true}""";
            var candidate = Guid.NewGuid();
            entry.CandidateId = candidate;
            entry.CandidateTriggeredVersion = 1;
            entry.CandidateSemanticDecisionSha256 = Hash;
            entry.CandidateTriggeredEvidenceSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(triggerEvidence)));
            db.Candidates.Add(new CandidateRecord
            {
                CandidateId = candidate, RunId = job, Symbol = Symbol, ConfigHash = Hash, CodeVersion = Code
            });
            db.CandidateTransitions.Add(new CandidateTransitionRecord
            {
                CandidateId = candidate, RunId = job, Sequence = 1, NewState = StrategyCandidateState.Triggered,
                OccurredAtUtc = Now.AddHours(-7), ReasonCode = "Frozen candidate triggered", SemanticDecisionSha256 = Hash,
                EvidenceJson = triggerEvidence, ConfigHash = Hash, CodeVersion = Code
            });
            var stop = Order(reconciliation, "stop", 2, OrderIntentKind.ProtectiveStop, 1,
                """{"PositionGenerationIdentity":"ledger:1:entry"}""");
            var exit = Order(reconciliation, "reconciliation-exit", 3, OrderIntentKind.PositionExit, 1,
                """{"reason":"reconciliation_close","expectedPositionGenerationEventId":1,"expectedPositionGenerationClientOrderId":"entry"}""");
            var manualPlan = JsonSerializer.Serialize(new
            {
                operatorOverride = true, operatorOverrideActor = "wishlist_operator", operatorOverrideReason = "Manual fixture",
                operatorOverrideIssuedAtUtc = Now.AddHours(-6)
            });
            var manualOrder = (Order(standalone, "manual", 4, OrderIntentKind.OperatorEntry, request: manualPlan));
            manualOrder.IntentId = manual;
            manualOrder.StrategyId = "MANUAL-OPERATOR-DIRECT";
            var ungrantedOrder = Order(ungranted, "ungranted-manual", 5, OrderIntentKind.OperatorEntry, request: manualPlan);
            ungrantedOrder.StrategyId = manualOrder.StrategyId;
            var foreign = Order(job, "foreign", 6);
            foreign.AccountId = "foreign-paper";
            var liveOrder = Order(live, "live", 7, OrderIntentKind.OperatorEntry, request: manualPlan);
            liveOrder.IntentId = LiveIntent;
            liveOrder.StrategyId = manualOrder.StrategyId;
            db.OrderIntents.AddRange(entry, stop, exit, manualOrder, ungrantedOrder, foreign, liveOrder,
                Order(other, "other-generation", 8, OrderIntentKind.PositionExit, 2));
            db.Set<OrderAdmissionTelemetryRecord>().Add(new OrderAdmissionTelemetryRecord
                { IntentId = entry.IntentId, DecisionAtUtc = Now.AddHours(-7), DecisionPrice = 100m });
            var command = Guid.NewGuid();
            db.OrderEvents.AddRange(Event(entry, "entry-broker", 1, Now.AddHours(-5)),
                Event(stop, "root-stop", 2, Now.AddHours(-4)),
                Event(stop, "replacement-stop", 3, Now.AddHours(-2), payload: JsonSerializer.Serialize(new
                {
                    action = "protective_stop_replaced", commandId = command, predecessorBrokerOrderId = "root-stop",
                    successorBrokerOrderId = "replacement-stop", replacementClientOrderId = "replacement-client", StopPrice = 95m
                })),
                Event(exit, "exit-broker", 4, Now.AddHours(-1)),
                Event(manualOrder, "manual-broker", 5, Now.AddHours(-1)));
            db.ProtectiveStopReplacements.Add(new ProtectiveStopReplacementRecord
            {
                CommandId = command, RunId = reconciliation, ConfigHash = Hash, CodeVersion = Code, AccountId = Account,
                OwnerClientOrderId = stop.ClientOrderId, RootBrokerOrderId = "root-stop", BrokerOrderId = "root-stop",
                VerifiedBrokerOrderId = "replacement-stop", ReplacementClientOrderId = "replacement-client",
                Symbol = Symbol, State = ProtectiveStopReplacementState.Verified, StopPrice = 95,
                VerifiedAtUtc = Now.AddHours(-2).AddSeconds(1), Reason = "Fixture replacement"
            });
            db.PositionEvents.AddRange(
                Fill(entry, "entry-broker", "fill-first", 1, Now.AddHours(-4), 2, 2, 99.75m),
                Fill(entry, "entry-broker", "fill-second", 2, Now.AddHours(-3), 3, 5, 100.125m));
            for (var i = 0; i < additional; i++)
                db.PositionEvents.Add(Fill(entry, "entry-broker", "append-" + i, i + 10, Now.AddMinutes(-80).AddSeconds(i), 1, 5, 100.25m));
            if (additional == 110)
            {
                db.PositionEvents.Add(Fill(entry, "entry-broker", "outside-exclusive", 400, Now, 1, 5, 100m));
                db.PositionEvents.Add(Fill(entry, "entry-broker", "clock-missing", 401, null, 1, 5, 100m));
            }
            await db.SaveChangesAsync();
        }

        // Give each intent the exact durable provenance that the chart verifies.
        private static OrderIntentRecord Order(Guid run, string client, int sequence, OrderIntentKind kind = OrderIntentKind.StrategyEntry,
            long? generation = null, string request = """{"takeProfitPrice":110}""") => new()
            {
                IntentId = Guid.NewGuid(), RunId = run, ConfigHash = Hash, CodeVersion = Code, ClientOrderId = client,
                AccountId = Account, Symbol = Symbol, StrategyId = "fixture-strategy", Kind = kind,
                PositionGenerationEventId = generation, RequestedQuantity = 5, SequenceNumber = sequence, OrderType = "limit",
                LimitPrice = 100m, StopPrice = 90m, CreatedAtUtc = Now.AddHours(-6), RequestJson = request,
                Side = kind == OrderIntentKind.StrategyEntry ? "buy" : "sell", TimeInForce = "day"
            };

        // Lifecycle clocks and broker order ownership are kept separate from raw fills.
        private static OrderEventRecord Event(OrderIntentRecord order, string broker, long id, DateTimeOffset at,
            string payload = "{}") => new()
            {
                EventId = id, RunId = order.RunId, ConfigHash = Hash, CodeVersion = Code, ClientOrderId = order.ClientOrderId,
                BrokerOrderId = broker, NewState = "ACKED", Source = "broker_rest", BrokerTimestampUtc = at,
                LocalTimestampUtc = at.AddSeconds(1), PayloadJson = payload
            };

        // Persist an independent execution clock in the actual stream payload shape.
        private static PositionEventRecord Fill(OrderIntentRecord order, string broker, string execution, long id,
            DateTimeOffset? at, decimal qty, decimal remaining, decimal price, string source = "broker_stream") => new()
            {
                PositionEventId = id, PositionGenerationEventId = 1, PositionGenerationClientOrderId = "entry",
                RunId = order.RunId, ConfigHash = Hash, CodeVersion = Code, AccountId = Account, Symbol = Symbol,
                ClientOrderId = order.ClientOrderId, BrokerOrderId = broker, ExecutionId = execution, Source = source,
                FillQuantity = qty, FillPrice = price, QuantityAfter = remaining, Side = order.Side,
                PayloadJson = JsonSerializer.Serialize(new { ExecutionTimestampUtc = at, OrderId = broker })
            };

        // Exercise the production page selection method without its ordinary broker UI loads.
        public async Task<PaperChartSelection> IssueAsync(string? order = null, Guid? job = null)
        {
            using var response = await SelectAsync(Symbol, order, job);
            response.EnsureSuccessStatusCode();
            Assert.True(response.Headers.CacheControl!.NoStore);
            return (await response.Content.ReadFromJsonAsync<PaperChartSelection>())!;
        }

        // The selected owner is supplied only to the fixture's stand-in for the Razor handler.
        public async Task<HttpResponseMessage> SelectAsync(string symbol, string? order, Guid? job)
        {
            var query = $"?symbol={Uri.EscapeDataString(symbol)}";
            if (order is not null) query += $"&client={Uri.EscapeDataString(order)}";
            if (job is not null) query += $"&job={job}";
            using var request = new HttpRequestMessage(HttpMethod.Get, "/fixture/page" + query);
            request.Headers.Add("X-Test-User", operatorId.ToString("D"));
            request.Headers.Add("X-TradingFlow-Chart", "1");
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
            return await client.SendAsync(request);
        }

        // Supply browser-compatible same-origin and CSRF proof to the real HTTP endpoint.
        public async Task<HttpResponseMessage> ReadAsync(PaperChartSelection selection, DateTimeOffset? from = null,
            DateTimeOffset? to = null, string? continuation = null, Guid? user = null, string? origin = null, bool sendProof = true)
        {
            var caller = user ?? operatorId;
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/charts/paper-evidence");
            if (caller != Guid.Empty) request.Headers.Add("X-Test-User", caller.ToString("D"));
            request.Headers.Add("Origin", origin ?? client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            if (sendProof)
            {
                using var csrf = new HttpRequestMessage(HttpMethod.Get, "/fixture/csrf");
                csrf.Headers.Add("X-Test-User", caller.ToString("D"));
                using var proof = await client.SendAsync(csrf);
                request.Headers.Add(ApiMutationSecurity.HeaderName, await proof.Content.ReadAsStringAsync());
            }
            request.Content = JsonContent.Create(new { selection.Token, selection.Symbol,
                FromUtc = from ?? Now.AddDays(-1), ToUtc = to ?? Now, Continuation = continuation });
            return await client.SendAsync(request);
        }

        // Deserialize raw numeric strings only for typed test assertions.
        public async Task<PaperChartEvidencePage> ReadPageAsync(PaperChartSelection context, DateTimeOffset from,
            DateTimeOffset to, string? cursor = null)
        {
            using var response = await ReadAsync(context, from, to, cursor);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<PaperChartEvidencePage>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString }))!;
        }

        // Change only the real role journal while the fixture's client continues sending its previous Administrator claim.
        public async Task RemoveRoleAsync()
        {
            await using var db = database.CreateDbContext();
            db.UserRoles.Remove(new IdentityUserRole<Guid> { UserId = operatorId, RoleId = roleId });
            await db.SaveChangesAsync();
        }

        // A previously authorized generation remains bound to its opener after another paper run closes it.
        public async Task CloseGenerationAsync()
        {
            await using var db = database.CreateDbContext();
            var order = await db.OrderIntents.SingleAsync(item => item.ClientOrderId == "reconciliation-exit");
            db.PositionEvents.Add(Fill(order, "exit-broker", "turn-to-flat", 2000, Now.AddMinutes(-25), 5, 0, 104m));
            var closed = Event(order, "exit-broker", 2000, Now.AddMinutes(-25));
            closed.NewState = "FILLED";
            db.OrderEvents.Add(closed);
            await db.SaveChangesAsync();
        }

        // Retain the selected stop and its old replacement facts while making the opening execution lineage unknown.
        public async Task InvalidateOpeningRunProvenanceAsync()
        {
            await using var db = database.CreateDbContext();
            var run = await db.ProductionRuns.SingleAsync(item => item.RunId == jobId);
            run.CodeVersion = "";
            await db.SaveChangesAsync();
        }

        // Verify the executed, parameterized SQL uses a selective index and no temporary sort.
        public async Task AssertIndexedQueryAsync(string table, string indexKey, params string[] terms)
        {
            var query = Commands.Snapshot().Last(entry => entry.Text.Contains(table, StringComparison.Ordinal) &&
                terms.All(term => entry.Text.Contains(term, StringComparison.OrdinalIgnoreCase)) &&
                entry.Text.Contains(indexKey, StringComparison.Ordinal));
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + query.Text;
            foreach (var (name, value) in query.Parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            var steps = new List<string>();
            await using var rows = await command.ExecuteReaderAsync();
            while (await rows.ReadAsync()) steps.Add(rows.GetString(3));
            Assert.Contains(steps, step => step.Contains("SEARCH ", StringComparison.Ordinal) &&
                step.Contains("USING INDEX", StringComparison.Ordinal) && step.Contains(indexKey, StringComparison.Ordinal));
            Assert.DoesNotContain(steps, step => step.Contains("TEMP B-TREE", StringComparison.OrdinalIgnoreCase));
        }

        // Dispose only the synthetic loopback listener, client and in-memory database owned by this fixture.
        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class Factory(DbContextOptions<TradingFlowDbContext> options) : IDbContextFactory<TradingFlowDbContext>
    {
        // One fixture holds its private in-memory SQLite connection open.
        public TradingFlowDbContext CreateDbContext() => new(options);
    }

    private sealed class StoreOptions(PrivatePaperChartOptions current) : IOptionsMonitor<PrivatePaperChartOptions>
    {
        public PrivatePaperChartOptions CurrentValue { get; set; } = current;
        // Return the exact protected grant snapshot used by each request.
        public PrivatePaperChartOptions Get(string? name) => CurrentValue;
        // The fixture changes the active view directly between requests.
        public IDisposable? OnChange(Action<PrivatePaperChartOptions, string?> listener) => null;
    }

    private sealed record ExecutedQuery(string Text, IReadOnlyList<(string Name, object? Value)> Parameters);

    private sealed class CaptureCommands : DbCommandInterceptor
    {
        private readonly List<ExecutedQuery> executed = [];
        // Record only synthetic queries for plan verification.
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            lock (executed) executed.Add(new(command.CommandText, command.Parameters.Cast<DbParameter>()
                .Select(parameter => (parameter.ParameterName, parameter.Value)).ToArray()));
            return ValueTask.FromResult(result);
        }

        // Reset fixture command observations between HTTP requests.
        public void Clear() { lock (executed) executed.Clear(); }

        // Copy the completed reader invocations for test assertions.
        public ExecutedQuery[] Snapshot() { lock (executed) return executed.ToArray(); }
    }

    private sealed class FixtureAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        // Give the caller an intentionally long-lived role claim. Production uses the current Identity join separately.
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Guid.TryParse(Request.Headers["X-Test-User"].ToString(), out var id))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, id.ToString("D")),
                new(ClaimTypes.Role, TradingFlowRoles.Administrator)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
