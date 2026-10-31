using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class PublicContextNewsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-18T12:00:00Z");

    [Fact]
    public void PublicFeed_PreservesPublicationAndReceipt_WithoutFabricatingTickerLinks()
    {
        var items = OfficialMarketNewsProvider.ParsePublicRss(Rss("Sun, 18 Oct 2026 10:00:00 +0000"), "un_news", "UN News", Now);
        var item = Assert.Single(items);
        Assert.Equal("MARKET", item.Ticker);
        Assert.Equal(Now.AddHours(-2), item.Timestamp);
        Assert.Equal(Now, item.ReceivedAt);
        Assert.Equal("Conflict disrupts oil supply", item.Headline);
        Assert.Equal("Policy and energy", item.Summary);
    }

    [Theory]
    [InlineData("Sun, 18 Oct 2026 13:00:00 +0000", "https://example.org/article")]
    [InlineData("2026-10-18", "https://example.org/article")]
    [InlineData("invalid", "https://example.org/article")]
    [InlineData("Sun, 18 Oct 2026 10:00:00 +0000", "javascript:alert(1)")]
    public void PublicFeed_RejectsUnknownOrFuturePublicationAndUnsafeLinks(string published, string link)
    {
        Assert.Empty(OfficialMarketNewsProvider.ParsePublicRss(Rss(published, link), "un_news", "UN News", Now));
    }

    [Fact]
    public void PublicFeed_DoesNotTreatUpdatedTimeAsPublication()
    {
        const string xml = """
            <feed xmlns="http://www.w3.org/2005/Atom"><entry><title>Policy update</title>
            <updated>2026-10-18T10:00:00Z</updated><link href="https://example.org/article" /></entry></feed>
            """;
        Assert.Empty(OfficialMarketNewsProvider.ParsePublicRss(xml, "us_government", "Government", Now));
    }

    [Fact]
    public void PublicFeed_RejectsExternalEntities()
    {
        const string xml = "<!DOCTYPE rss [<!ENTITY external SYSTEM 'file:///not-readable'>]><rss><channel><item><title>&external;</title></item></channel></rss>";
        Assert.Throws<System.Xml.XmlException>(() => OfficialMarketNewsProvider.ParsePublicRss(xml, "un_news", "UN News", Now));
    }

    [Fact]
    public async Task PublicFeeds_AreCachedAndOldPublicationsDoNotReenterRollingWindow()
    {
        var handler = new FeedHandler();
        using var client = new HttpClient(handler);
        var provider = new OfficialMarketNewsProvider(client, NullLogger<OfficialMarketNewsProvider>.Instance, new Clock(Now));
        var first = await provider.GetEventsAsync([], Now.AddHours(-4), default);
        var second = await provider.GetEventsAsync([], Now.AddHours(-1), default);
        Assert.Equal(2, first.Count);
        Assert.All(first, item => Assert.Equal(Now, item.ReceivedAt));
        Assert.Empty(second);
        Assert.Equal(1, handler.Requests.Count(url => url == OfficialMarketNewsProvider.UnitedNationsFeedUrl));
        Assert.Equal(1, handler.Requests.Count(url => url == OfficialMarketNewsProvider.GovernmentDocumentsFeedUrl));
    }

    [Fact]
    public async Task OneSourceFailure_DoesNotDiscardOtherPublicContext()
    {
        var handler = new FeedHandler(failUn: true);
        using var client = new HttpClient(handler);
        var provider = new OfficialMarketNewsProvider(client, NullLogger<OfficialMarketNewsProvider>.Instance, new Clock(Now));
        var rows = await provider.GetEventsAsync([], Now.AddHours(-4), default);
        Assert.Equal("us_government", Assert.Single(rows).Provider);
    }

    [Fact]
    public async Task StalledBodyIsTimedOutAndHealthySourceStillReturns()
    {
        var handler = new FeedHandler(stallUn: true);
        using var client = new HttpClient(handler);
        var provider = new OfficialMarketNewsProvider(client, NullLogger<OfficialMarketNewsProvider>.Instance, new Clock(Now))
        { PublicContextRequestTimeout = TimeSpan.FromMilliseconds(100) };
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var rows = await provider.GetEventsAsync([], Now.AddHours(-4), watchdog.Token);
        Assert.False(watchdog.IsCancellationRequested);
        Assert.Equal("us_government", Assert.Single(rows).Provider);
        Assert.True(handler.StalledBody.ReadWasCanceled);
    }

    [Fact]
    public async Task ReceiptTimestampIsSampledAfterBodyCompletion()
    {
        var clock = new Clock(Now);
        using var client = new HttpClient(new FeedHandler(bodyRead: () => clock.Now = Now.AddSeconds(30)));
        var provider = new OfficialMarketNewsProvider(client, NullLogger<OfficialMarketNewsProvider>.Instance, clock);
        var rows = await provider.GetEventsAsync([], Now.AddHours(-4), default);
        Assert.Equal(Now.AddSeconds(30), Assert.Single(rows, item => item.Provider == "un_news").ReceivedAt);
    }

    private static string Rss(string publication, string link = "https://example.org/article") => $"""
        <rss><channel><item><title>Conflict disrupts oil supply</title>
        <description>&lt;b&gt;Policy&lt;/b&gt; and energy</description><pubDate>{publication}</pubDate>
        <link>{link}</link><guid>context-one</guid></item></channel></rss>
        """;

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FeedHandler(bool failUn = false, bool stallUn = false, Action? bodyRead = null) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public StalledStream StalledBody { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            if (failUn && url == OfficialMarketNewsProvider.UnitedNationsFeedUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (stallUn && url == OfficialMarketNewsProvider.UnitedNationsFeedUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(StalledBody) });
            var body = url == OfficialMarketNewsProvider.UnitedNationsFeedUrl || url == OfficialMarketNewsProvider.GovernmentDocumentsFeedUrl
                ? Rss("Sun, 18 Oct 2026 10:00:00 +0000")
                : url == OfficialMarketNewsProvider.SecCompanyTickersUrl ? "{}" : "<rss><channel /></rss>";
            if (bodyRead is not null && url == OfficialMarketNewsProvider.UnitedNationsFeedUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StreamContent(new ReceiptStream(System.Text.Encoding.UTF8.GetBytes(body), bodyRead)) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class ReceiptStream(byte[] buffer, Action onRead) : MemoryStream(buffer)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onRead();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class StalledStream : Stream
    {
        public bool ReadWasCanceled { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { ReadWasCanceled = true; throw; }
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
