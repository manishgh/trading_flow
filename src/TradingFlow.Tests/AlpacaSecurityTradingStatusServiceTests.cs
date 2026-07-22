using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Engine.Execution;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class AlpacaSecurityTradingStatusServiceTests
{
    [Fact]
    public void ApplyMessage_TradeCannotClearObservedHalt_ButProviderResumeCan()
    {
        var service = CreateService();
        var now = new DateTimeOffset(2026, 7, 22, 14, 30, 0, TimeSpan.Zero);

        service.ApplyMessage(Parse("""{"T":"t","S":"MSFT","t":"2026-07-22T14:30:00Z"}"""), now);
        Assert.Equal(SecurityTradingState.TradingObserved, service.GetStatus("MSFT").State);

        service.ApplyMessage(Parse("""{"T":"s","S":"MSFT","sc":"H","rc":"M","t":"2026-07-22T14:30:01Z"}"""), now.AddSeconds(1));
        Assert.Equal(SecurityTradingState.Halted, service.GetStatus("MSFT").State);

        service.ApplyMessage(Parse("""{"T":"t","S":"MSFT","t":"2026-07-22T14:30:02Z"}"""), now.AddSeconds(2));
        Assert.Equal(SecurityTradingState.Halted, service.GetStatus("MSFT").State);

        service.ApplyMessage(Parse("""{"T":"s","S":"MSFT","sc":"T","t":"2026-07-22T14:30:03Z"}"""), now.AddSeconds(3));
        Assert.Equal(SecurityTradingState.TradingObserved, service.GetStatus("MSFT").State);
    }

    [Fact]
    public void ApplyMessage_QuotationResumption_DoesNotClaimTradingResumed()
    {
        var service = CreateService();
        var now = new DateTimeOffset(2026, 7, 22, 14, 30, 0, TimeSpan.Zero);

        service.ApplyMessage(Parse("""{"T":"s","S":"MSFT","sc":"Q","t":"2026-07-22T14:30:00Z"}"""), now);

        Assert.Equal(SecurityTradingState.Unknown, service.GetStatus("MSFT").State);
    }

    private static AlpacaSecurityTradingStatusService CreateService() =>
        new(
            new AlpacaCredentialProvider(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            NullLoggerFactory.Instance,
            NullLogger<AlpacaSecurityTradingStatusService>.Instance);

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
