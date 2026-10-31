using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public sealed class AlpacaMarketDataArchiveTests
{
    private const string FirstPage = """{"bars":{"AMD":[{"t":"2026-06-08T20:00:00Z","o":100,"h":101,"l":99,"c":100,"v":1000}]},"next_page_token":"fixture-page-2"}""";
    private const string LastPage = """{"bars":{"AMD":[{"t":"2026-06-08T20:05:00Z","o":100,"h":101,"l":99,"c":100,"v":1000}]},"next_page_token":null}""";

    /// <summary>Malformed JSON remains byte-exact on disk with its receipt before either parser reports failure.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedJson_IsArchivedBeforeParsing(bool calendar)
    {
        using var archive = new OwnedAlpacaTestArchive();
        var writer = new RecordingWriter(archive.Writer);
        var payload = Encoding.UTF8.GetBytes(" \r\n{ malformed fixture ö\n");
        using var content = new TrackingContent(payload);
        using var http = new HttpClient(new ResponseQueueHandler(writer,
            Response(HttpStatusCode.OK, content)));
        var provider = CreateProvider(http, writer);

        var exception = await Record.ExceptionAsync(() => ReadAsync(provider, calendar));

        if (calendar) Assert.IsType<InvalidOperationException>(exception);
        else Assert.IsAssignableFrom<JsonException>(exception);
        await AssertCommittedAsync(Assert.Single(writer.Receipts), payload, calendar, 200);
        Assert.True(content.Disposed);
    }

    /// <summary>Error status handling reads the committed body, preserving original bytes and transport metadata.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpFailure_IsArchivedBeforeStatusHandling(bool calendar)
    {
        using var archive = new OwnedAlpacaTestArchive();
        var writer = new RecordingWriter(archive.Writer);
        var payload = Encoding.Unicode.GetBytes(" \r\nfixture provider unavailable ö\n ");
        using var content = new TrackingContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-16" };
        using var http = new HttpClient(new ResponseQueueHandler(writer,
            Response(HttpStatusCode.ServiceUnavailable, content)));
        var provider = CreateProvider(http, writer);

        var exception = await Record.ExceptionAsync(() => ReadAsync(provider, calendar));

        Assert.NotNull(exception);
        Assert.Contains("503", exception.Message);
        Assert.Contains("fixture provider unavailable ö", exception.Message);
        await AssertCommittedAsync(Assert.Single(writer.Receipts), payload, calendar, 503);
        Assert.True(content.Disposed);
    }

    /// <summary>A rate-limit response commits before disposal/retry and every paginated success commits before the next request.</summary>
    [Fact]
    public async Task RetryAndPagination_ArchiveEveryBodyBeforeContinuing()
    {
        using var archive = new OwnedAlpacaTestArchive();
        var writer = new RecordingWriter(archive.Writer);
        var payloads = new[] { " \r\nfixture rate limit ö", FirstPage, LastPage }.Select(Encoding.UTF8.GetBytes).ToArray();
        using var limited = new TrackingContent(payloads[0]);
        using var first = new TrackingContent(payloads[1]);
        using var last = new TrackingContent(payloads[2]);
        var retry = Response(HttpStatusCode.TooManyRequests, limited);
        retry.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        using var handler = new ResponseQueueHandler(writer, retry,
            Response(HttpStatusCode.OK, first), Response(HttpStatusCode.OK, last));
        using var http = new HttpClient(handler);

        var rows = await ReadAsync(CreateProvider(http, writer), calendar: false);

        Assert.Equal(2, rows);
        Assert.Equal(3, writer.Receipts.Count);
        Assert.DoesNotContain("page_token=", handler.Requests[1].Query);
        Assert.Contains("page_token=fixture-page-2", handler.Requests[2].Query);
        for (var index = 0; index < payloads.Length; index++)
        {
            await AssertCommittedAsync(writer.Receipts[index], payloads[index], calendar: false, index == 0 ? 429 : 200);
        }
        Assert.True(limited.Disposed && first.Disposed && last.Disposed);
    }

    /// <summary>A failed page commit emits no rows from the batch even when its first page parsed successfully.</summary>
    [Fact]
    public async Task ArchiveFailure_OnLaterPageEmitsNoBars()
    {
        using var archive = new OwnedAlpacaTestArchive();
        var writer = new RecordingWriter(archive.Writer, failOnAttempt: 2);
        using var first = new TrackingContent(Encoding.UTF8.GetBytes(FirstPage));
        using var last = new TrackingContent(Encoding.UTF8.GetBytes(LastPage));
        using var handler = new ResponseQueueHandler(writer,
            Response(HttpStatusCode.OK, first), Response(HttpStatusCode.OK, last));
        using var http = new HttpClient(handler);
        var provider = CreateProvider(http, writer);
        var emitted = 0;

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var bar in provider.GetBarsAsync(["AMD"], ["5m"],
                DateTimeOffset.Parse("2026-06-08T20:00:00Z"), DateTimeOffset.Parse("2026-06-08T21:00:00Z"), default))
            {
                emitted++;
            }
        });

        Assert.Equal(0, emitted);
        Assert.Equal(2, handler.Requests.Count);
        await AssertCommittedAsync(Assert.Single(writer.Receipts), Encoding.UTF8.GetBytes(FirstPage), calendar: false, 200);
        Assert.True(first.Disposed && last.Disposed);
    }

    /// <summary>Archive failure takes precedence over parsing, HTTP errors and retry handling for both endpoint families.</summary>
    [Theory]
    [InlineData(false, HttpStatusCode.OK)]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.TooManyRequests)]
    [InlineData(true, HttpStatusCode.TooManyRequests)]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    [InlineData(true, HttpStatusCode.ServiceUnavailable)]
    public async Task ArchiveFailure_StopsBeforeParsingStatusOrRetry(bool calendar, HttpStatusCode status)
    {
        using var archive = new OwnedAlpacaTestArchive();
        var writer = new RecordingWriter(archive.Writer, failOnAttempt: 1);
        using var content = new TrackingContent(Encoding.UTF8.GetBytes("malformed fixture"));
        using var handler = new ResponseQueueHandler(writer, Response(status, content));
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<IOException>(() => ReadAsync(CreateProvider(http, writer), calendar));

        Assert.Empty(writer.Receipts);
        Assert.Single(handler.Requests);
        Assert.True(content.Disposed);
    }

    /// <summary>A storage error mentioning invalid symbols cannot activate the provider's symbol fallback.</summary>
    [Fact]
    public async Task ArchiveFailure_MentioningInvalidSymbolDoesNotTriggerFallback()
    {
        using var archive = new OwnedAlpacaTestArchive();
        var writer = new RecordingWriter(archive.Writer, failOnAttempt: 1, failureMessage: "invalid symbol archive directory unavailable");
        using var content = new TrackingContent(Encoding.UTF8.GetBytes(LastPage));
        using var handler = new ResponseQueueHandler(writer, Response(HttpStatusCode.OK, content));
        using var http = new HttpClient(handler);
        var provider = CreateProvider(http, writer);

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var bar in provider.GetBarsAsync(["AMD", "INVALID"], ["5m"],
                DateTimeOffset.Parse("2026-06-08T20:00:00Z"), DateTimeOffset.Parse("2026-06-08T21:00:00Z"), default))
            {
                Assert.Fail("An unarchived batch must not emit bars.");
            }
        });

        Assert.Single(handler.Requests);
        Assert.Empty(writer.Receipts);
    }

    /// <summary>Provider symbol rejection still falls back individually, with the rejected batch and symbol responses retained.</summary>
    [Fact]
    public async Task InvalidSymbolFallback_ArchivesRejectedAndAcceptedResponses()
    {
        using var archive = new OwnedAlpacaTestArchive();
        var writer = new RecordingWriter(archive.Writer);
        var invalidBytes = Encoding.UTF8.GetBytes("invalid symbol fixture");
        using var batch = new TrackingContent(invalidBytes);
        using var accepted = new TrackingContent(Encoding.UTF8.GetBytes(LastPage));
        using var rejected = new TrackingContent(invalidBytes);
        using var handler = new ResponseQueueHandler(writer, Response(HttpStatusCode.BadRequest, batch),
            Response(HttpStatusCode.OK, accepted), Response(HttpStatusCode.BadRequest, rejected));
        using var http = new HttpClient(handler);
        var provider = CreateProvider(http, writer);
        var count = 0;

        await foreach (var bar in provider.GetBarsAsync(["AMD", "INVALID"], ["5m"],
            DateTimeOffset.Parse("2026-06-08T20:00:00Z"), DateTimeOffset.Parse("2026-06-08T21:00:00Z"), default)) count++;

        Assert.Equal(1, count);
        Assert.Equal(3, handler.Requests.Count);
        await AssertCommittedAsync(writer.Receipts[0], invalidBytes, calendar: false, 400);
        await AssertCommittedAsync(writer.Receipts[1], Encoding.UTF8.GetBytes(LastPage), calendar: false, 200);
        await AssertCommittedAsync(writer.Receipts[2], invalidBytes, calendar: false, 400);
    }

    /// <summary>Cancellation while waiting to retry leaves the already committed error body and receipt intact.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterRetryResponse_PreservesCommittedBody(bool calendar)
    {
        using var archive = new OwnedAlpacaTestArchive();
        using var cancellation = new CancellationTokenSource();
        var writer = new RecordingWriter(archive.Writer);
        var payload = Encoding.UTF8.GetBytes("fixture limited before cancellation");
        using var content = new TrackingContent(payload, cancellation.Cancel);
        var response = Response(HttpStatusCode.TooManyRequests, content);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
        using var handler = new ResponseQueueHandler(writer, response);
        using var http = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAsync(CreateProvider(http, writer), calendar, cancellation.Token));

        await AssertCommittedAsync(Assert.Single(writer.Receipts), payload, calendar, 429);
        Assert.Single(handler.Requests);
    }

    /// <summary>Cancellation after a success body commits prevents publication while retaining its original payload and manifest.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterSuccessfulCommit_EmitsNothingAndPreservesBody(bool calendar)
    {
        using var archive = new OwnedAlpacaTestArchive();
        using var cancellation = new CancellationTokenSource();
        var writer = new RecordingWriter(archive.Writer);
        var payload = Encoding.UTF8.GetBytes(calendar ? "[]" : LastPage);
        using var content = new TrackingContent(payload, cancellation.Cancel);
        using var handler = new ResponseQueueHandler(writer, Response(HttpStatusCode.OK, content));
        using var http = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAsync(CreateProvider(http, writer), calendar, cancellation.Token));

        await AssertCommittedAsync(Assert.Single(writer.Receipts), payload, calendar, 200);
        Assert.Single(handler.Requests);
    }

    /// <summary>Run cancellation during a completed body's commit cannot discard it or permit rows or retries afterward.</summary>
    [Theory]
    [InlineData(false, HttpStatusCode.OK)]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.TooManyRequests)]
    [InlineData(true, HttpStatusCode.TooManyRequests)]
    public async Task CancellationDuringArchiveCommit_FinishesReceiptWithoutRowsOrRetries(bool calendar, HttpStatusCode status)
    {
        using var archive = new OwnedAlpacaTestArchive();
        using var cancellation = new CancellationTokenSource();
        var commitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new RecordingWriter(archive.Writer, commitGate: releaseCommit.Task, commitStarted: commitStarted);
        var payload = Encoding.UTF8.GetBytes(calendar ? "[]" : LastPage);
        using var content = new TrackingContent(payload);
        using var handler = new ResponseQueueHandler(writer, Response(status, content));
        using var http = new HttpClient(handler);
        var provider = CreateProvider(http, writer);
        var emitted = 0;

        // Counts publications separately from the final task outcome to detect even a row emitted before cancellation escapes.
        async Task ConsumeAsync()
        {
            if (calendar)
            {
                emitted += (await provider.LoadMarketSessionSchedulesAsync(
                    new DateOnly(2026, 6, 8), new DateOnly(2026, 6, 8), cancellation.Token)).Count;
                return;
            }
            await foreach (var bar in provider.GetBarsAsync(["AMD"], ["5m"],
                DateTimeOffset.Parse("2026-06-08T20:00:00Z"), DateTimeOffset.Parse("2026-06-08T21:00:00Z"), cancellation.Token)) emitted++;
        }

        var consumption = ConsumeAsync();
        try
        {
            await commitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(writer.CommitCancellationToken.CanBeCanceled);
            cancellation.Cancel();
            releaseCommit.TrySetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumption);

            await AssertCommittedAsync(Assert.Single(writer.Receipts), payload, calendar, (int)status);
            Assert.Equal(0, emitted);
            Assert.Single(handler.Requests);
            Assert.True(content.Disposed);
        }
        finally
        {
            cancellation.Cancel();
            releaseCommit.TrySetResult();
            try { await consumption; }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>Provides explicit fake credentials and the test-owned writer without consulting host configuration.</summary>
    private static AlpacaMarketDataProvider CreateProvider(HttpClient http, IRawArchiveWriter writer) =>
        new(http, AlpacaOptions.Create(ProductionProfile.Paper) with { KeyId = "fixture", SecretKey = "fixture" }, writer);

    /// <summary>Consumes the requested endpoint entirely so exceptions and emitted row counts are observable.</summary>
    private static async Task<int> ReadAsync(AlpacaMarketDataProvider provider, bool calendar, CancellationToken cancellationToken = default)
    {
        if (calendar)
        {
            return (await provider.LoadMarketSessionSchedulesAsync(new DateOnly(2026, 6, 8), new DateOnly(2026, 6, 8), cancellationToken)).Count;
        }

        var count = 0;
        await foreach (var bar in provider.GetBarsAsync(["AMD"], ["5m"],
            DateTimeOffset.Parse("2026-06-08T20:00:00Z"), DateTimeOffset.Parse("2026-06-08T21:00:00Z"), cancellationToken)) count++;
        return count;
    }

    /// <summary>Creates a disposable fake response with a non-secret header that must survive in the receipt.</summary>
    private static HttpResponseMessage Response(HttpStatusCode status, HttpContent content)
    {
        var response = new HttpResponseMessage(status) { Content = content };
        response.Headers.Add("X-Fixture-Receipt", "captured");
        return response;
    }

    /// <summary>Checks persisted bytes and manifest metadata rather than trusting the writer's returned in-memory receipt.</summary>
    private static async Task AssertCommittedAsync(RawArchiveReceipt receipt, byte[] expected, bool calendar, int status)
    {
        Assert.Equal(expected, await File.ReadAllBytesAsync(receipt.PayloadPath));
        var manifest = JsonSerializer.Deserialize<RawArchiveManifest>(await File.ReadAllTextAsync(receipt.ManifestPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(calendar ? "calendar-rest-page" : "bars-rest-page", manifest.ArtifactType);
        Assert.Equal(status, manifest.Http!.StatusCode);
        Assert.Equal(calendar ? "/v2/calendar" : "/v2/stocks/bars", manifest.Http.RequestUri!.AbsolutePath);
        Assert.Equal("captured", Assert.Single(manifest.Http.Headers!["X-Fixture-Receipt"]));
        Assert.Equal(expected.LongLength, manifest.ByteLength);
    }

    private sealed class RecordingWriter(IRawArchiveWriter inner, int failOnAttempt = 0,
        string failureMessage = "simulated archive failure", Task? commitGate = null,
        TaskCompletionSource? commitStarted = null) : IRawArchiveWriter
    {
        private int attempts;
        public List<RawArchiveReceipt> Receipts { get; } = [];
        public CancellationToken CommitCancellationToken { get; private set; }

        /// <summary>Delegates synthetic persistence with an optional cancellable commit gate or exact-attempt failure.</summary>
        public async Task<RawArchiveReceipt> ArchiveAsync(RawArchiveRequest request, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            if (++attempts == failOnAttempt) throw new IOException(failureMessage);
            CommitCancellationToken = cancellationToken;
            commitStarted?.TrySetResult();
            if (commitGate is not null) await commitGate.WaitAsync(cancellationToken);
            var receipt = await inner.ArchiveAsync(request, payload, cancellationToken);
            Receipts.Add(receipt);
            return receipt;
        }

        /// <summary>Leaves retention behavior to the existing archive implementation.</summary>
        public Task<RawArchiveRetentionResult> EnforceRetentionAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            inner.EnforceRetentionAsync(nowUtc, cancellationToken);
    }

    private sealed class ResponseQueueHandler(RecordingWriter writer, params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        /// <summary>Requires the preceding response to be committed and disposed before allowing another fake HTTP request.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Requests.Count;
            Assert.Equal(index, writer.Receipts.Count);
            if (index > 0)
            {
                Assert.True(((TrackingContent)responses[index - 1].Content).Disposed);
                Assert.True(File.Exists(writer.Receipts[index - 1].ManifestPath));
            }
            Requests.Add(request.RequestUri!);
            var response = responses[index];
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class TrackingContent(byte[] payload, Action? onDispose = null) : ByteArrayContent(payload)
    {
        public bool Disposed { get; private set; }

        /// <summary>Records disposal and optionally cancels at the exact retry boundary after the archive commit.</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && !Disposed)
            {
                Disposed = true;
                onDispose?.Invoke();
            }
            base.Dispose(disposing);
        }
    }
}
