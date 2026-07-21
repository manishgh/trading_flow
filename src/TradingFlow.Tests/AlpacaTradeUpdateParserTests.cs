using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Domain.Orders;

namespace TradingFlow.Tests;

public sealed class AlpacaTradeUpdateParserTests
{
    [Fact]
    public void Protocol_AcceptsDocumentedAuthorizationAndSubscriptionAcknowledgements()
    {
        AlpacaTradeStreamProtocol.RequireAuthorization(
            """{"stream":"authorization","data":{"status":"authorized","action":"authenticate"}}""");
        AlpacaTradeStreamProtocol.RequireTradeUpdateSubscription(
            """{"stream":"listening","data":{"streams":["trade_updates"]}}""");
    }

    [Fact]
    public void Protocol_CreatesDocumentedAuthenticationAndSubscriptionMessages()
    {
        using var authentication = JsonDocument.Parse(
            AlpacaTradeStreamProtocol.CreateAuthenticationMessage("paper-key", "paper-secret"));
        Assert.Equal("auth", authentication.RootElement.GetProperty("action").GetString());
        Assert.Equal("paper-key", authentication.RootElement.GetProperty("key").GetString());
        Assert.Equal("paper-secret", authentication.RootElement.GetProperty("secret").GetString());
        Assert.False(authentication.RootElement.TryGetProperty("data", out _));

        using var subscription = JsonDocument.Parse(
            AlpacaTradeStreamProtocol.CreateTradeUpdateSubscriptionMessage());
        Assert.Equal("listen", subscription.RootElement.GetProperty("action").GetString());
        Assert.Equal(
            "trade_updates",
            subscription.RootElement.GetProperty("data").GetProperty("streams")[0].GetString());
    }

    [Theory]
    [InlineData("{\"stream\":\"authorization\",\"data\":{\"status\":\"unauthorized\"}}")]
    [InlineData("{\"stream\":\"listening\",\"data\":{\"streams\":[\"account_updates\"]}}")]
    public void Protocol_RejectsUnusableAcknowledgements(string payload)
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (payload.Contains("authorization", StringComparison.Ordinal))
            {
                AlpacaTradeStreamProtocol.RequireAuthorization(payload);
            }
            else
            {
                AlpacaTradeStreamProtocol.RequireTradeUpdateSubscription(payload);
            }
        });
    }

    [Fact]
    public void Parse_Fill_ProducesUtcSourceAwareUpdate()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "stream": "trade_updates",
              "data": {
                "event": "fill",
                "order": {
                  "id": "broker-1",
                  "client_order_id": "SWGA-B-MSFT-20260721-001-12345678",
                  "symbol": "msft",
                  "filled_qty": "10",
                  "filled_avg_price": "100.50",
                  "updated_at": "2026-07-21T11:00:00-04:00"
                }
              }
            }
            """);

        var update = AlpacaTradeUpdateParser.Parse(document.RootElement);

        Assert.NotNull(update);
        Assert.Equal(OrderStatus.Filled, update.Status);
        Assert.Equal(BrokerUpdateSource.TradeStream, update.Source);
        Assert.Equal("MSFT", update.Ticker);
        Assert.Equal(new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero), update.Timestamp);
    }

    [Fact]
    public void Parse_NonTradeUpdate_ReturnsNull()
    {
        using var document = JsonDocument.Parse("""{"stream":"listening","data":{"streams":["trade_updates"]}}""");

        Assert.Null(AlpacaTradeUpdateParser.Parse(document.RootElement));
    }

    [Fact]
    public void Parse_FillWithoutPrice_FailsClosed()
    {
        using var document = JsonDocument.Parse(
            """
            {"stream":"trade_updates","data":{"event":"fill","order":{
              "id":"broker-1","client_order_id":"SWGA-B-MSFT-20260721-001-12345678",
              "symbol":"MSFT","filled_qty":"10","filled_avg_price":null,
              "updated_at":"2026-07-21T15:00:00Z"}}}
            """);

        Assert.Throws<InvalidOperationException>(() => AlpacaTradeUpdateParser.Parse(document.RootElement));
    }
}
