using System;
using System.Collections.Generic;
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
                        
                        var filledQtyString = orderNode.GetProperty("filled_qty").GetString();
                        decimal.TryParse(filledQtyString, out var filledQty);
                        
                        var filledPriceString = orderNode.TryGetProperty("filled_avg_price", out var priceProp) ? priceProp.GetString() : "0";
                        decimal.TryParse(filledPriceString, out var filledPrice);

                        var timestampString = orderNode.GetProperty("updated_at").GetString();
                        DateTimeOffset.TryParse(timestampString, out var timestamp);

                        var status = MapStatus(eventType);
                        yield return new OrderUpdate(orderId, clientOrderId, ticker, status, filledQty, filledPrice, timestamp);
                    }
                }
            }
        }
    }

    private static OrderStatus MapStatus(string? eventType) => eventType switch
    {
        "new" => OrderStatus.New,
        "fill" => OrderStatus.Filled,
        "partial_fill" => OrderStatus.PartiallyFilled,
        "canceled" => OrderStatus.Canceled,
        "expired" => OrderStatus.Expired,
        "done_for_day" => OrderStatus.DoneForDay,
        "replaced" => OrderStatus.Replaced,
        "rejected" => OrderStatus.Rejected,
        "pending_new" => OrderStatus.PendingNew,
        "accepted" => OrderStatus.Accepted,
        "pending_cancel" => OrderStatus.PendingCancel,
        "pending_replace" => OrderStatus.PendingReplace,
        "suspended" => OrderStatus.Suspended,
        "calculated" => OrderStatus.Calculated,
        "accepted_for_bidding" => OrderStatus.AcceptedForBidding,
        "stopped" => OrderStatus.Stopped,
        _ => OrderStatus.New // Fallback
    };

    public void Dispose()
    {
        _client.Dispose();
    }
}
