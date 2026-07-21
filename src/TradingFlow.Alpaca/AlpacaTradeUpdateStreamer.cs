using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TradingFlow.Domain.Orders;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Alpaca;

public sealed class AlpacaTradeUpdateStreamer : ITradeUpdateStreamer, IDisposable
{
    private readonly AlpacaTradeStreamClient _client;

    public AlpacaTradeUpdateStreamer(AlpacaOptions options)
    {
        _client = new AlpacaTradeStreamClient(options);
    }

    public async IAsyncEnumerable<OrderUpdate> SubscribeTradeUpdatesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await _client.ConnectAsync(cancellationToken);
        await _client.SubscribeTradeUpdatesAsync(cancellationToken);

        await foreach (var element in _client.ReadMessagesAsync(cancellationToken))
        {
            if (element.TryGetProperty("stream", out var streamProp) && streamProp.GetString() == "trade_updates")
            {
                if (element.TryGetProperty("data", out var dataNode))
                {
                    if (dataNode.TryGetProperty("event", out var eventProp) && dataNode.TryGetProperty("order", out var orderNode))
                    {
                        var eventType = eventProp.GetString();
                        var orderId = orderNode.GetProperty("id").GetString() ?? string.Empty;
                        var clientOrderId = orderNode.GetProperty("client_order_id").GetString() ?? string.Empty;
                        var ticker = orderNode.GetProperty("symbol").GetString() ?? string.Empty;
                        var status = OrderStatusCodec.ParseBrokerValue(eventType ?? String.Empty);
                        
                        var filledQtyString = orderNode.GetProperty("filled_qty").GetString();
                        if (!decimal.TryParse(
                                filledQtyString,
                                NumberStyles.Number,
                                CultureInfo.InvariantCulture,
                                out var filledQty))
                        {
                            throw new InvalidOperationException("Alpaca trade update has invalid filled_qty.");
                        }
                        
                        var filledPriceString = orderNode.TryGetProperty("filled_avg_price", out var priceProp) &&
                            priceProp.ValueKind == System.Text.Json.JsonValueKind.String
                                ? priceProp.GetString()
                                : null;
                        var filledPrice = 0m;
                        if (!String.IsNullOrWhiteSpace(filledPriceString) &&
                            !decimal.TryParse(
                                filledPriceString,
                                NumberStyles.Number,
                                CultureInfo.InvariantCulture,
                                out filledPrice))
                        {
                            throw new InvalidOperationException("Alpaca trade update has invalid filled_avg_price.");
                        }

                        if (status is (OrderStatus.PartiallyFilled or OrderStatus.Filled) && filledPrice <= 0m)
                        {
                            throw new InvalidOperationException("Alpaca fill update is missing filled_avg_price.");
                        }

                        var timestampString = orderNode.GetProperty("updated_at").GetString();
                        if (!DateTimeOffset.TryParse(
                                timestampString,
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal,
                                out var timestamp))
                        {
                            throw new InvalidOperationException("Alpaca trade update has invalid updated_at.");
                        }

                        yield return new OrderUpdate(orderId, clientOrderId, ticker, status, filledQty, filledPrice, timestamp);
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
