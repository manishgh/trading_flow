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

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _client.ConnectAsync(cancellationToken);
        await _client.SubscribeTradeUpdatesAsync(cancellationToken);
    }

    public async IAsyncEnumerable<OrderUpdate> ReadUpdatesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var element in _client.ReadMessagesAsync(cancellationToken))
        {
            var update = AlpacaTradeUpdateParser.Parse(element);
            if (update is not null)
            {
                yield return update;
            }
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}

public static class AlpacaTradeUpdateParser
{
    public static OrderUpdate? Parse(System.Text.Json.JsonElement element)
    {
        if (!element.TryGetProperty("stream", out var streamProperty) ||
            !String.Equals(streamProperty.GetString(), "trade_updates", StringComparison.Ordinal))
        {
            return null;
        }

        if (!element.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("event", out var eventProperty) ||
            !data.TryGetProperty("order", out var order))
        {
            throw new InvalidOperationException("Alpaca trade update is missing data.event or data.order.");
        }

        var status = OrderStatusCodec.ParseBrokerValue(RequireString(data, "event"));
        var filledQuantity = ParseDecimal(order, "filled_qty", required: true);
        var filledPrice = ParseDecimal(order, "filled_avg_price", required: false);
        if (status is (OrderStatus.PartiallyFilled or OrderStatus.Filled) && filledPrice <= 0m)
        {
            throw new InvalidOperationException("Alpaca fill update is missing a positive filled_avg_price.");
        }

        var timestampText = RequireString(order, "updated_at");
        if (!DateTimeOffset.TryParse(
                timestampText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            throw new InvalidOperationException("Alpaca trade update has invalid updated_at.");
        }

        return new OrderUpdate(
            RequireString(order, "id"),
            RequireString(order, "client_order_id"),
            RequireString(order, "symbol").Trim().ToUpperInvariant(),
            status,
            filledQuantity,
            filledPrice,
            timestamp.ToUniversalTime(),
            BrokerUpdateSource.TradeStream);
    }

    private static string RequireString(System.Text.Json.JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == System.Text.Json.JsonValueKind.String &&
            !String.IsNullOrWhiteSpace(property.GetString()))
        {
            return property.GetString()!;
        }

        throw new InvalidOperationException($"Alpaca trade update is missing {propertyName}.");
    }

    private static decimal ParseDecimal(
        System.Text.Json.JsonElement element,
        string propertyName,
        bool required)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            return required
                ? throw new InvalidOperationException($"Alpaca trade update is missing {propertyName}.")
                : 0m;
        }

        if (property.ValueKind == System.Text.Json.JsonValueKind.String &&
            decimal.TryParse(
                property.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"Alpaca trade update has invalid {propertyName}.");
    }
}
