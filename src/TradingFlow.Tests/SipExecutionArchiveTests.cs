using System.Net;
using System.Text;
using TradingFlow.Alpaca;
using Xunit;

namespace TradingFlow.Tests;

public sealed class SipExecutionArchiveTests
{
    [Fact]
    public void NanosecondsRoundTripWithoutDotnetTickTruncation()
    {
        const string value = "2026-05-21T16:00:00.123456789Z";
        Assert.Equal(value, SipNanosecondTime.Format(SipNanosecondTime.Parse(value)));
        Assert.Equal(1, SipNanosecondTime.Parse(value) - SipNanosecondTime.Parse("2026-05-21T16:00:00.123456788Z"));
        Assert.Throws<InvalidDataException>(() => SipNanosecondTime.Parse("2026-05-21T16:00:00.1234567890Z"));
    }

    [Fact]
    public async Task PagedArchiveRetainsSameTimestampRowsAndVerifiesRawCache()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "tf-sip-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var handler = new StubHandler();
            using var client = new HttpClient(handler);
            var collector = new SipExecutionArchiveCollector(client);
            var request = new SipArchiveRequest("DIOD", "trades", "2026-05-21T16:00:00Z", "2026-05-21T16:05:00Z", "2026-10-06");
            var manifest = await collector.CollectAsync(request, directory, minimumFreeBytes: 0);
            Assert.True(manifest.PaginationComplete);
            Assert.Equal(2, manifest.Rows);
            Assert.Equal(manifest.Pages[0].LastNanoseconds, manifest.Pages[1].FirstNanoseconds);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Contains("16%3A04%3A59.999999999Z", handler.Requests[0]);
            Assert.Contains("page_token=next", handler.Requests[1]);
            var resumed = await collector.CollectAsync(request, directory, minimumFreeBytes: 0);
            Assert.Equal(manifest.Rows, resumed.Rows);
            Assert.Equal(2, handler.Requests.Count);
            await File.AppendAllTextAsync(Path.Combine(directory, manifest.Pages[0].Payload), "corrupt");
            await Assert.ThrowsAsync<InvalidDataException>(() => collector.CollectAsync(request, directory, minimumFreeBytes: 0));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FutureIntervalIsRejectedBeforeAnyProviderCallOrArchiveWrite()
    {
        var handler=new StubHandler();
        using var client=new HttpClient(handler);
        var collector=new SipExecutionArchiveCollector(client);
        var tomorrow=DateTimeOffset.UtcNow.AddDays(1);
        var request=new SipArchiveRequest("DIOD","trades",tomorrow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            tomorrow.AddMinutes(5).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),"2026-10-06");
        var path=Path.Combine(Directory.GetCurrentDirectory(),"must-not-exist-"+Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<ArgumentException>(()=>collector.CollectAsync(request,path));
        Assert.Empty(handler.Requests);
        Assert.False(Directory.Exists(path));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            var token = Requests.Count == 1 ? "\"next\"" : "null";
            var body = "{\"trades\":{\"DIOD\":[{\"t\":\"2026-05-21T16:00:00.000000001Z\",\"p\":95,\"s\":1,\"i\":" + Requests.Count + "}]},\"next_page_token\":" + token + "}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
