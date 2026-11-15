using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Application.Preparation;
using TradingFlow.Contracts.Preparation;
using TradingFlow.Data.Context;
using TradingFlow.Data.Market;
using TradingFlow.Data.ScreenCaptures;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.ScreenCaptures;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Engine.Storage;
using TradingFlow.Finviz;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.ScreenCaptures;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Tests;

public sealed class FinvizScheduledCaptureTests
{
    private const string Header = "No.,Ticker,Company,Sector,Industry,Country,Market Cap,P/E,Price,Change,Volume";
    private const string Row = "1,AAA,Alpha,Technology,Software,USA,200,10,20,1%,1000";
    private static readonly DateOnly SessionDate = new(2026, 11, 16);
    private static readonly DateTimeOffset PlannedAt = new(2026, 11, 16, 14, 0, 0, TimeSpan.Zero);
    private static readonly FinvizScreenCaptureOptions Enabled = new(true, true, true, false);
    private static FinvizPlannedSlot Plan => new(SessionDate, FinvizSlotKind.BeforeOpen, PlannedAt);

    // A spring DST change moves UTC clocks, and an early close moves only that day's after-close slot.
    [Theory]
    [InlineData(2026, 3, 6, 16, 14, 21)]
    [InlineData(2026, 3, 9, 16, 13, 20)]
    [InlineData(2026, 11, 27, 13, 14, 18)]
    public void UsesActualNewYorkSessionClocks(int year, int month, int day, int closeHour, int openSlotUtc, int closeSlotUtc)
    {
        var date = new DateOnly(year, month, day);
        var session = new PreparationSession(date, true, PreparationCalendarAdapter.Utc(date, new TimeOnly(9, 30)),
            PreparationCalendarAdapter.Utc(date, new TimeOnly(closeHour, 0)));
        var slots = FinvizScreenCaptureScheduler.PlansFor(session);
        Assert.Collection(slots,
            before => Assert.Equal(new DateTimeOffset(year, month, day, openSlotUtc, 0, 0, TimeSpan.Zero), before.PlannedAtUtc),
            after => Assert.Equal(new DateTimeOffset(year, month, day, closeSlotUtc, 15, 0, TimeSpan.Zero), after.PlannedAtUtc));
        Assert.Empty(FinvizScreenCaptureScheduler.PlansFor(session with { TradingDay = false }));
    }

    // The capture worker can register under a paused trading runtime and requires both options and credentials.
    [Fact]
    public void CaptureRegistrationIsExplicitAndReadOnlyWhenTradingIsPaused()
    {
        var paused = new RuntimeHostedServiceOptions(Enabled: false, EnableEarningsMonitor: false,
            EnableMarketFeeds: true, MutationPause: true);
        var services = new ServiceCollection();
        services.AddTradingFlowRuntimeHostedServices(paused).AddFinvizScreenCapture(Enabled);
        Assert.Contains(services, service => service.ImplementationType == typeof(FinvizScreenCaptureHostedService));
        Assert.DoesNotContain(services, service => service.ImplementationType == typeof(AlpacaOrderSynchronizationHostedService));
        foreach (var disabled in new[]
                 {
                     Enabled with { Requested = false }, Enabled with { MarketFeedsEnabled = false },
                     Enabled with { CredentialAvailable = false }, Enabled with { FixtureHost = true }
                 })
        {
            var off = new ServiceCollection().AddFinvizScreenCapture(disabled);
            Assert.DoesNotContain(off, service => service.ServiceType == typeof(IHostedService));
            Assert.Contains(off, service => service.ServiceType == typeof(FinvizScreenCaptureStatusReader));
        }
    }

    // Preserve every column and row from the archive without inferring total result membership.
    [Theory]
    [InlineData("\n" + Row, "", FinvizCaptureState.Captured, FinvizColumnCoverage.Baseline, 1)]
    [InlineData("\n" + Row + ",extra", ",New Metric", FinvizCaptureState.Captured, FinvizColumnCoverage.Unexpected, 1)]
    [InlineData("\n", "", FinvizCaptureState.Captured, FinvizColumnCoverage.Baseline, 0)]
    [InlineData("\n1,AAA,Alpha", "", FinvizCaptureState.Partial, FinvizColumnCoverage.Unverified, 0)]
    [InlineData("\n1,AAA,\"Alpha", "", FinvizCaptureState.Partial, FinvizColumnCoverage.Unverified, 0)]
    public async Task ExactCsvShapeAndMembershipAreSeparate(string body, string extraHeaders, FinvizCaptureState state,
        FinvizColumnCoverage coverage, int rows)
    {
        using var archive = new OwnedCaptureDirectory();
        var text = Header + extraHeaders + body;
        using var handler = new TestHandler(_ => Task.FromResult(Respond(HttpStatusCode.OK, text)));
        using var client = NewClient(handler, archive.Options);
        var attempt = Request();
        var captured = await client.CaptureScreenerExportAsync(attempt, new ManualClock(PlannedAt), default);
        Assert.Equal(state, captured.State);
        Assert.Equal(coverage, captured.ColumnCoverage);
        Assert.Equal(rows, captured.RowCount);
        Assert.False(captured.MembershipVerified);
        Assert.Equal(text.Length, captured.ArchivedBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), captured.Sha256);
        Assert.Equal(PlannedAt, captured.ReceivedAtUtc);
        Assert.Equal(PlannedAt, captured.HeadersReceivedAtUtc);
        Assert.Equal(PlannedAt, captured.DispatchedAtUtc);
        Assert.Equal("?f=sh_price_o5&s=ta_p_channelup&auth=capture-test", handler.LastRequestUri!.Query);
        var manifest = await File.ReadAllTextAsync(Assert.Single(Directory.EnumerateFiles(archive.Root, "*.manifest.json", SearchOption.AllDirectories)));
        Assert.DoesNotContain("capture-test", manifest);
        Assert.Contains(attempt.CorrelationId.ToString("N"), manifest);
    }

    // Reduced CSV and HTML are retained as actual partial/failure receipts, even when HTTP returned 200.
    [Fact]
    public async Task IncompleteCoverageAndLoginPagesNeverPublishAsSuccessfulCsv()
    {
        using var archive = new OwnedCaptureDirectory();
        using var html = new TestHandler(_ => Task.FromResult(Respond(HttpStatusCode.OK, "<html>Sign in</html>", "text/html")));
        using var htmlClient = NewClient(html, archive.Options);
        var login = await htmlClient.CaptureScreenerExportAsync(Request(), new ManualClock(PlannedAt), default);
        Assert.Equal(FinvizCaptureState.Failed, login.State);
        Assert.Equal(200, login.HttpStatus);
        Assert.True(login.CompletePayload);
        Assert.NotNull(login.ArchiveId);

        using var reduced = new TestHandler(_ => Task.FromResult(Respond(HttpStatusCode.OK, "Ticker,Company\nAAA,Alpha")));
        using var reducedClient = NewClient(reduced, archive.Options);
        var result = await reducedClient.CaptureScreenerExportAsync(Request(), new ManualClock(PlannedAt), default);
        Assert.Equal(FinvizCaptureState.Partial, result.State);
        Assert.Equal(FinvizColumnCoverage.Reduced, result.ColumnCoverage);
        Assert.NotNull(result.Headers);
        Assert.Equal(["Ticker", "Company"], result.Headers);
        Assert.Equal(1, result.RowCount);
    }

    // Known lengths are rejected before reading; streaming lengths read only through the first byte past the cap.
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, FinvizClient.ScheduledPayloadLimit + 1)]
    public async Task CeilingDoesNotBufferOversizedResponse(bool declared, int expectedRead)
    {
        using var archive = new OwnedCaptureDirectory();
        using var stream = new GeneratedBody(FinvizClient.ScheduledPayloadLimit + 1L);
        using var handler = new TestHandler(_ =>
        {
            var content = new StreamContent(stream);
            if (declared)
                content.Headers.ContentLength = FinvizClient.ScheduledPayloadLimit + 1L;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var client = NewClient(handler, archive.Options);
        var result = await client.CaptureScreenerExportAsync(Request(), new ManualClock(PlannedAt), default);
        Assert.Equal(FinvizCaptureState.Failed, result.State);
        Assert.False(result.CompletePayload);
        Assert.Equal(0, result.ArchivedBytes);
        Assert.Equal(expectedRead, stream.BytesRead);
        Assert.Contains("16 MiB", result.Reason);
    }

    // A same-date receipt with another correlation cannot satisfy the attempt; byte tampering also prevents recovery.
    [Fact]
    public async Task RecoveryRequiresAnExactUntamperedReceipt()
    {
        using var archive = new OwnedCaptureDirectory();
        using var handler = new TestHandler(_ => Task.FromResult(Respond(HttpStatusCode.OK, Header + "\n" + Row)));
        using var client = NewClient(handler, archive.Options);
        var request = Request();
        var captured = await client.CaptureScreenerExportAsync(request, new ManualClock(PlannedAt), default);
        Assert.Null(await client.RecoverScreenerExportAsync(Request(), archive.Options, default));
        var recovered = await client.RecoverScreenerExportAsync(request, archive.Options, default);
        Assert.NotNull(recovered);
        Assert.Equal(FinvizCaptureState.Captured, recovered.State);
        Assert.Equal(captured.ArchiveId, recovered.ArchiveId);
        Assert.Equal(captured.Sha256, recovered.Sha256);

        var payload = Assert.Single(Directory.EnumerateFiles(archive.Root, "*.csv", SearchOption.AllDirectories));
        await File.WriteAllTextAsync(payload, new string('x', Header.Length + Row.Length + 1));
        var tampered = await client.RecoverScreenerExportAsync(request, archive.Options, default);
        Assert.NotNull(tampered);
        Assert.Equal(FinvizCaptureState.Failed, tampered.State);
        Assert.Contains("verification", tampered.Reason);
        Assert.Equal(1, handler.Requests);
    }

    // Revisions freeze in the winning first-claim transaction, with undone slots keeping their unknown query.
    [Fact]
    public async Task FirstAttemptsPrecedeRetriesAndFreezeOrMissAcrossPresetChanges()
    {
        await using var db = await CaptureDatabase.CreateAsync();
        var lease = await db.AcquireAsync("one");
        var first = await db.CreatePresetAsync("First");
        var second = await db.CreatePresetAsync("Second");
        var deleted = await db.CreatePresetAsync("Deleted");
        await db.Catalog.StageAsync(lease, Plan, await db.Presets.ListAsync(null, default), default);
        db.Clock.Advance(TimeSpan.FromSeconds(1));
        await db.ReplaceQueryAsync(first.Id, "cap_smallover");
        var one = await db.BeginAsync(lease);
        Assert.Equal(first.Id, one.PresetId);
        Assert.Equal("f=cap_smallover", one.Query);
        await db.Catalog.FinishAsync(lease, one, new(FinvizCaptureState.Failed, "HTTP response missing"), default);

        db.Clock.Advance(TimeSpan.FromSeconds(16));
        await db.Presets.DeleteAsync(first.Id, default);
        await db.Presets.DeleteAsync(deleted.Id, default);
        var two = await db.BeginAsync(lease);
        Assert.Equal(second.Id, two.PresetId);
        await db.Catalog.FinishAsync(lease, two, new(FinvizCaptureState.Failed, "HTTP response missing"), default);

        var retry = await db.BeginAsync(lease);
        Assert.Equal(first.Id, retry.PresetId);
        Assert.Equal(one.Query, retry.Query);
        Assert.Equal(one.QuerySha256, retry.QuerySha256);
        var firstHistory = await db.Catalog.GetHistoryAsync(first.Id, default);
        Assert.False(firstHistory.Latest!.PlannedRevisionKnown);
        var removedHistory = await db.Catalog.GetHistoryAsync(deleted.Id, default);
        Assert.Equal(FinvizCaptureState.Missed, removedHistory.Latest!.State);
        Assert.Null(removedHistory.Latest.Query);
        Assert.Null(removedHistory.Latest.ClaimedAtUtc);
    }

    // A rate limit extending past its preset's window is terminal, while ordinary failures stop after three starts.
    [Fact]
    public async Task NoRetryCanExceedTheAttemptCountOrWindow()
    {
        await using var db = await CaptureDatabase.CreateAsync();
        var lease = await db.AcquireAsync("one");
        var first = await db.CreatePresetAsync("Rate limited");
        var second = await db.CreatePresetAsync("Three failures");
        await db.Catalog.StageAsync(lease, Plan, await db.Presets.ListAsync(null, default), default);
        var rateLimited = await db.BeginAsync(lease);
        await db.Catalog.FinishAsync(lease, rateLimited,
            new(FinvizCaptureState.Failed, "Finviz returned HTTP 429.", HttpStatus: 429, RetryAfterUtc: Plan.DeadlineUtc), default);

        for (var i = 0; i < SqliteFinvizCaptureCatalog.MaximumAttempts; i++)
        {
            var attempt = await db.BeginAsync(lease);
            Assert.Equal(second.Id, attempt.PresetId);
            await db.Catalog.FinishAsync(lease, attempt, new(FinvizCaptureState.Failed, "No response"), default);
            db.Clock.Advance(TimeSpan.FromSeconds(15));
        }
        var none = await db.Catalog.TryBeginAsync(lease, Plan, FinvizClient.ScheduledExportFormat,
            FinvizClient.CanonicalizeScheduledSelection, default);
        Assert.Null(none);
        var limited = (await db.Catalog.GetHistoryAsync(first.Id, default)).Latest;
        var exhausted = (await db.Catalog.GetHistoryAsync(second.Id, default)).Latest;
        Assert.Equal(1, limited!.AttemptCount);
        Assert.Contains("retry delay", limited.Reason);
        Assert.Equal(3, exhausted!.AttemptCount);
        Assert.Contains("budget was exhausted", exhausted.Reason);
    }

    // Read the provider delay from the archived response clock so restart preserves the original retry eligibility.
    [Fact]
    public async Task RateLimitDelaySurvivesReceiptRecovery()
    {
        using var archive = new OwnedCaptureDirectory();
        using var handler = new TestHandler(_ =>
        {
            var response = Respond(HttpStatusCode.TooManyRequests, "Try again later");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return Task.FromResult(response);
        });
        using var client = NewClient(handler, archive.Options);
        var request = Request();
        var captured = await client.CaptureScreenerExportAsync(request, new ManualClock(PlannedAt), default);
        Assert.Equal(429, captured.HttpStatus);
        Assert.Equal(PlannedAt.AddSeconds(90), captured.RetryAfterUtc);
        var recovered = await client.RecoverScreenerExportAsync(request, archive.Options, default);
        Assert.Equal(captured.RetryAfterUtc, recovered!.RetryAfterUtc);
        Assert.Equal(captured.ArchiveId, recovered.ArchiveId);
        Assert.Equal(1, handler.Requests);
    }

    // A dead owner cannot publish, and the next host recovers the committed archive without issuing another GET.
    [Fact]
    public async Task RestartRecoversPublishedReceiptWithExactlyOneRequest()
    {
        await using var db = await CaptureDatabase.CreateAsync();
        var lease = await db.AcquireAsync("old", TimeSpan.FromSeconds(5));
        var preset = await db.CreatePresetAsync("Recover");
        await db.Catalog.StageAsync(lease, Plan, await db.Presets.ListAsync(null, default), default);
        var started = await db.BeginAsync(lease);
        using var handler = new TestHandler(_ => Task.FromResult(Respond(HttpStatusCode.OK, Header + "\n" + Row)));
        using var client = NewClient(handler, db.Archive);
        var receipt = await client.CaptureScreenerExportAsync(started, db.Clock, default);
        db.Clock.Advance(TimeSpan.FromSeconds(6));
        await Assert.ThrowsAsync<FinvizCaptureOwnershipLostException>(() => db.Catalog.FinishAsync(lease, started, receipt, default));

        await db.CreateScheduler(client).RunOnceAsync(default);
        var history = await db.Catalog.GetHistoryAsync(preset.Id, default);
        Assert.Equal(FinvizCaptureState.Captured, history.Latest!.State);
        Assert.Equal(started.CorrelationId, history.Latest.PublishedAttemptId);
        Assert.Equal(receipt.ArchiveId, history.LastPublishedEvidence!.ArchiveId);
        Assert.Equal(PlannedAt, history.LastPublishedEvidence.ReceivedAtUtc);
        Assert.Equal(db.Clock.GetUtcNow(), history.LastPublished!.PublishedAtUtc);
        Assert.Equal(1, handler.Requests);
    }

    // Two workers using the same database cannot reach Finviz simultaneously.
    [Fact]
    public async Task ConcurrentHostsShareOneCollectorOwner()
    {
        await using var db = await CaptureDatabase.CreateAsync();
        var preset = await db.CreatePresetAsync("One screen");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new TestHandler(async token =>
        {
            entered.TrySetResult();
            await allowResponse.Task.WaitAsync(token);
            return Respond(HttpStatusCode.OK, Header + "\n" + Row);
        });
        using var client = NewClient(handler, db.Archive);
        var first = db.CreateScheduler(client).RunOnceAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await db.CreateScheduler(client).RunOnceAsync(default);
            Assert.Equal(1, handler.Requests);
        }
        finally
        {
            allowResponse.TrySetResult();
            await first;
        }
        Assert.Equal(FinvizCaptureState.Captured, (await db.Catalog.GetHistoryAsync(preset.Id, default)).Latest!.State);
    }

    // A late collector reports a missed window without a historical GET or an invented earlier revision.
    [Fact]
    public async Task LateStartRecordsMissedAndLeavesOtherWorkflowsUntouched()
    {
        await using var db = await CaptureDatabase.CreateAsync();
        var preset = await db.CreatePresetAsync("Missed");
        db.Clock.Advance(TimeSpan.FromMinutes(11));
        await db.ReplaceQueryAsync(preset.Id, "cap_mega");
        using var handler = new TestHandler(_ => throw new InvalidOperationException("A late collector must not fetch the screen."));
        using var client = NewClient(handler, db.Archive);
        var scheduler = db.CreateScheduler(client);
        await scheduler.RunOnceAsync(default);

        var status = await new FinvizScreenCaptureStatusReader(Enabled, db.Presets, db.Catalog, db.Clock).ReadAsync(default);
        var screen = Assert.Single(status.Screens);
        Assert.Equal("Missed", screen.State);
        Assert.Null(screen.LastCaptureAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 21, 15, 0, TimeSpan.Zero), status.NextSlotAtUtc);
        var row = (await db.Catalog.GetHistoryAsync(preset.Id, default)).Latest;
        Assert.Null(row!.Query);
        Assert.Null(row.PlannedRevisionKnown);
        Assert.Equal(0, handler.Requests);

        await using var context = db.CreateDbContext();
        Assert.Empty(await context.Jobs.ToListAsync());
        Assert.Empty(await context.WishlistItems.ToListAsync());
        Assert.Empty(await context.Candidates.ToListAsync());
        Assert.Empty(await context.OrderIntents.ToListAsync());
    }

    // Multiple offline dates are reconciled in contiguous batches without skipping when today's slots are already closed.
    [Fact]
    public async Task LongDowntimeKeepsOlderMissedSlotsInTheCursor()
    {
        await using var db = await CaptureDatabase.CreateAsync();
        await db.CreatePresetAsync("Offline");
        var firstLease = await db.AcquireAsync("old", TimeSpan.FromSeconds(1));
        await db.Catalog.OpenScheduleAsync(firstLease, SessionDate, default);
        db.Clock.Advance(TimeSpan.FromDays(11) + TimeSpan.FromHours(8));
        using var handler = new TestHandler(_ => throw new InvalidOperationException("Historical slots cannot make Finviz requests."));
        using var client = NewClient(handler, db.Archive);
        var scheduler = db.CreateScheduler(client);

        await scheduler.RunOnceAsync(default);
        Assert.Equal(SessionDate.AddDays(7), (await db.Catalog.ReadScheduleAsync(default))!.NextUnclosedSessionDate);
        await scheduler.RunOnceAsync(default);
        Assert.Equal(SessionDate.AddDays(12), (await db.Catalog.ReadScheduleAsync(default))!.NextUnclosedSessionDate);

        await using var context = db.CreateDbContext();
        var slots = await context.FinvizCaptureSlots.AsNoTracking().ToListAsync();
        Assert.Equal(18, slots.Count);
        Assert.Equal(9, slots.Select(slot => slot.SessionDate).Distinct().Count());
        Assert.DoesNotContain(slots, slot => slot.SessionDate == new DateOnly(2026, 11, 26));
        Assert.All(slots, slot => Assert.Equal(FinvizCaptureState.Missed, slot.State));
        Assert.Equal(0, handler.Requests);
    }

    // Verify the effective schema also works after upgrading an existing preset database.
    [Fact]
    public async Task MigrationPreservesExistingSavedScreen()
    {
        await using var db = await CaptureDatabase.CreateAsync(migrate: true);
        var preset = await db.CreatePresetAsync("Existing screen");
        await using (var context = db.CreateDbContext())
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        var lease = await db.AcquireAsync("one");
        await db.Catalog.StageAsync(lease, Plan, await db.Presets.ListAsync(null, default), default);
        Assert.Equal(preset.Id, (await db.BeginAsync(lease)).PresetId);
        Assert.Equal("sh_price_o5", Assert.Single(await db.Presets.ListAsync(null, default)).FilterQuery);
    }

    // Pin the same canonical selection and hash the catalog supplies in production.
    private static FinvizExportRequest Request()
    {
        var query = FinvizClient.CanonicalizeScheduledSelection("f=sh_price_o5&s=ta_p_channelup&v=111&auth=previous");
        return new(Guid.NewGuid(), Guid.NewGuid(), query, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(query))),
            FinvizClient.ScheduledExportFormat, PlannedAt, Plan.DeadlineUtc);
    }

    // Use the real archive writer and a fake HTTP transport with no reachable provider URL.
    private static FinvizClient NewClient(HttpMessageHandler handler, RawArchiveOptions archive) => new(new HttpClient(handler),
        new FinvizOptions(new Uri("https://finviz.example"), "capture-test"), new FileSystemRawArchiveWriter(archive));

    // Build a fixture HTTP response with the actual body and media type.
    private static HttpResponseMessage Respond(HttpStatusCode status, string body, string mediaType = "text/csv") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        // Test clocks advance explicitly, without any delay in SQLite or transport tests.
        public override DateTimeOffset GetUtcNow() => now;

        // Move to the next known UTC boundary.
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }

    private sealed class TestHandler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int requests;
        public int Requests => requests;
        public Uri? LastRequestUri { get; private set; }

        // Count real transport invocations while substituting deterministic provider bytes.
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            LastRequestUri = request.RequestUri;
            Interlocked.Increment(ref requests);
            return send(token);
        }
    }

    private sealed class OwnedCaptureDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"tradingflow-finviz-schedule-{Guid.NewGuid():N}");
        public RawArchiveOptions Options => new(Root);

        // Only this synthetic test directory is removed.
        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class CaptureDatabase : IDbContextFactory<TradingFlowDbContext>, IAsyncDisposable
    {
        private readonly OwnedCaptureDirectory root = new();
        private readonly DbContextOptions<TradingFlowDbContext> options;
        private int presetNumber;
        public ManualClock Clock { get; } = new(PlannedAt);
        public RawArchiveOptions Archive { get; }
        public SqliteFinvizCaptureCatalog Catalog { get; }
        public ScreenerPresetService Presets { get; }

        // Share one synthetic file-backed database across scheduler hosts and keep its archive separate.
        private CaptureDatabase()
        {
            Directory.CreateDirectory(root.Root);
            options = new DbContextOptionsBuilder<TradingFlowDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root.Root, "fixture.db")};Pooling=False").Options;
            Archive = new RawArchiveOptions(Path.Combine(root.Root, "raw"));
            Catalog = new SqliteFinvizCaptureCatalog(this, Clock);
            Presets = new ScreenerPresetService(this, Clock);
        }

        // Create the database via its current model or migrate to the exact version preceding this checkpoint.
        public static async Task<CaptureDatabase> CreateAsync(bool migrate = false)
        {
            var database = new CaptureDatabase();
            await using var context = database.CreateDbContext();
            if (migrate)
                await context.Database.MigrateAsync("20261114120000_AddProductionHostRunLease");
            else
                await context.Database.EnsureCreatedAsync();
            return database;
        }

        // Open the same database using an independent connection and context.
        public TradingFlowDbContext CreateDbContext() => new(options);

        // Build an independent collector with no provider dependencies besides the synthetic Finviz transport.
        public FinvizScreenCaptureScheduler CreateScheduler(FinvizClient client) => new(Enabled,
            new SqliteMarketStreamLeaseRepository(this, Clock), new TestCalendar(Clock), Presets, Catalog,
            client, Archive, Clock, NullLogger<FinvizScreenCaptureScheduler>.Instance);

        // Reserve a deliberately ordered saved preset that existed before the scheduled observation.
        public async Task<ScreenerPreset> CreatePresetAsync(string name)
        {
            await using var context = CreateDbContext();
            var preset = new ScreenerPreset
            {
                Id = Guid.Parse($"00000000-0000-0000-0000-{++presetNumber:000000000000}"), Name = name,
                Category = ScreenerScope.Swing.ToString(), FilterQuery = "sh_price_o5",
                CreatedAtUtc = PlannedAt.AddDays(-3), UpdatedAtUtc = PlannedAt.AddDays(-3)
            };
            context.ScreenerPresets.Add(preset);
            await context.SaveChangesAsync();
            return preset;
        }

        // Simulate a concurrent preset edit without mutating the frozen capture record.
        public async Task ReplaceQueryAsync(Guid presetId, string query)
        {
            await using var context = CreateDbContext();
            var preset = await context.ScreenerPresets.SingleAsync(preset => preset.Id == presetId);
            preset.FilterQuery = query;
            preset.UpdatedAtUtc = Clock.GetUtcNow();
            await context.SaveChangesAsync();
        }

        // Take the exact production shared lease with a deterministic clock.
        public async Task<MarketStreamLease> AcquireAsync(string owner, TimeSpan? duration = null) =>
            await new SqliteMarketStreamLeaseRepository(this, Clock).TryAcquireAsync(
                SqliteFinvizCaptureCatalog.LeaseResource, owner, duration ?? TimeSpan.FromMinutes(12))
            ?? throw new InvalidOperationException("The test did not acquire collector ownership.");

        // Require one reserved attempt for a test that already established due work.
        public async Task<FinvizExportRequest> BeginAsync(MarketStreamLease lease) =>
            await Catalog.TryBeginAsync(lease, Plan, FinvizClient.ScheduledExportFormat, FinvizClient.CanonicalizeScheduledSelection, default)
            ?? throw new InvalidOperationException("The test did not reserve a scheduled attempt.");

        // Close the disposable test root without touching the operator database or archive.
        public ValueTask DisposeAsync()
        {
            root.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestCalendar(TimeProvider clock) : IPreparationCalendar
    {
        // Return a contiguous local exchange calendar without invoking a market provider.
        public Task<PreparationCalendar> LoadAsync(DateOnly start, DateOnly end, CancellationToken token) =>
            Task.FromResult(new PreparationCalendar(clock.GetUtcNow(), "fixture",
                Enumerable.Range(0, end.DayNumber - start.DayNumber + 1).Select(index => start.AddDays(index))
                    .Select(date => new PreparationSession(date, date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
                        && date != new DateOnly(2026, 11, 26),
                        PreparationCalendarAdapter.Utc(date, new TimeOnly(9, 30)),
                        PreparationCalendarAdapter.Utc(date, new TimeOnly(date == new DateOnly(2026, 11, 27) ? 13 : 16, 0))))
                    .ToArray()));
    }

    private sealed class GeneratedBody(long length) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        // Simulate a provider body without allocating a real oversized input fixture.
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var read = (int)Math.Min(length - BytesRead, buffer.Length);
            buffer.Span[..read].Fill((byte)'x');
            BytesRead += read;
            return ValueTask.FromResult(read);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
