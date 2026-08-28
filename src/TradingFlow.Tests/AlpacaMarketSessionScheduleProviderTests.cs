using System.Net;
using TradingFlow.Alpaca;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Tests;

public sealed class AlpacaMarketSessionScheduleProviderTests
{
    [Fact]
    public async Task LoadMarketSessionSchedulesAsync_UsesTradingEndpointAndReturnsCompleteRange()
    {
        using var handler = new CalendarHandler(HttpStatusCode.OK, """
            [
              { "date": "2026-11-27", "open": "09:30", "close": "13:00" },
              { "date": "2026-11-30", "open": "09:30", "close": "16:00" }
            ]
            """);
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, ProductionProfile.Paper);

        var schedules = await provider.LoadMarketSessionSchedulesAsync(
            new DateOnly(2026, 11, 27),
            new DateOnly(2026, 11, 30),
            CancellationToken.None);

        Assert.Equal(4, schedules.Count);
        Assert.Equal(new TimeOnly(13, 0), schedules[new DateOnly(2026, 11, 27)].RegularClose);
        Assert.False(schedules[new DateOnly(2026, 11, 28)].IsTradingDay);
        Assert.False(schedules[new DateOnly(2026, 11, 29)].IsTradingDay);
        Assert.Equal(new TimeOnly(16, 0), schedules[new DateOnly(2026, 11, 30)].RegularClose);

        Assert.Equal("paper-api.alpaca.markets", handler.RequestUri.Host);
        Assert.Equal("/v2/calendar", handler.RequestUri.AbsolutePath);
        Assert.Contains("start=2026-11-27", handler.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("end=2026-11-30", handler.RequestUri.Query, StringComparison.Ordinal);
        Assert.Equal("test-key", handler.KeyId);
        Assert.Equal("test-secret", handler.SecretKey);
    }

    [Fact]
    public async Task LoadMarketSessionSchedulesAsync_UsesLiveTradingEndpointForLiveProfile()
    {
        using var handler = new CalendarHandler(HttpStatusCode.OK, "[]");
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, ProductionProfile.Live);

        await provider.LoadMarketSessionSchedulesAsync(
            new DateOnly(2026, 7, 4),
            new DateOnly(2026, 7, 4),
            CancellationToken.None);

        Assert.Equal("api.alpaca.markets", handler.RequestUri.Host);
    }

    [Theory]
    [InlineData("{}", "must be a JSON array")]
    [InlineData("[{\"date\":\"2026-11-27\",\"open\":\"bad\",\"close\":\"13:00\"}]", "valid open time")]
    [InlineData("[{\"date\":\"2026-11-27\",\"open\":\"16:00\",\"close\":\"09:30\"}]", "close after it opens")]
    [InlineData("[{\"date\":\"2026-11-26\",\"open\":\"09:30\",\"close\":\"16:00\"}]", "outside the requested")]
    [InlineData("[{\"date\":\"2026-11-27\",\"open\":\"09:30\",\"close\":\"13:00\"},{\"date\":\"2026-11-27\",\"open\":\"09:30\",\"close\":\"13:00\"}]", "duplicate date")]
    public async Task LoadMarketSessionSchedulesAsync_FailsClosedOnMalformedProviderData(
        string payload,
        string expectedMessage)
    {
        using var handler = new CalendarHandler(HttpStatusCode.OK, payload);
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, ProductionProfile.Paper);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.LoadMarketSessionSchedulesAsync(
                new DateOnly(2026, 11, 27),
                new DateOnly(2026, 11, 30),
                CancellationToken.None));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadMarketSessionSchedulesAsync_FailsExplicitlyOnProviderFailure()
    {
        using var handler = new CalendarHandler(HttpStatusCode.ServiceUnavailable, "provider unavailable");
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, ProductionProfile.Paper);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.LoadMarketSessionSchedulesAsync(
                new DateOnly(2026, 11, 27),
                new DateOnly(2026, 11, 30),
                CancellationToken.None));

        Assert.Contains("HTTP 503", exception.Message, StringComparison.Ordinal);
        Assert.Contains("provider unavailable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadMarketSessionSchedulesAsync_RejectsReversedRangeBeforeCallingProvider()
    {
        using var handler = new CalendarHandler(HttpStatusCode.OK, "[]");
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, ProductionProfile.Paper);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.LoadMarketSessionSchedulesAsync(
                new DateOnly(2026, 11, 30),
                new DateOnly(2026, 11, 27),
                CancellationToken.None));

        Assert.Equal(0, handler.CallCount);
    }

    private static AlpacaMarketDataProvider CreateProvider(HttpClient httpClient, ProductionProfile profile) =>
        new(
            httpClient,
            AlpacaOptions.Create(profile) with
            {
                KeyId = "test-key",
                SecretKey = "test-secret"
            });

    private sealed class CalendarHandler(HttpStatusCode statusCode, string payload) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public Uri RequestUri { get; private set; } = null!;

        public string? KeyId { get; private set; }

        public string? SecretKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required.");
            KeyId = ReadHeader(request, "APCA-API-KEY-ID");
            SecretKey = ReadHeader(request, "APCA-API-SECRET-KEY");
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(payload)
            });
        }

        private static string? ReadHeader(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;
    }

}
