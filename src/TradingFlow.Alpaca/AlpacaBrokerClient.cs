using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Logging;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Alpaca;

public sealed class AlpacaBrokerClient : IBrokerClient, IDisposable
{
    private readonly AlpacaTradingRestClient _tradingClient;
    private readonly AlpacaOptions _options;

    public AlpacaBrokerClient(
        HttpClient httpClient,
        AlpacaOptions options,
        IRawArchiveWriter rawArchiveWriter)
    {
        _options = options;
        _tradingClient = new AlpacaTradingRestClient(httpClient, options, rawArchiveWriter);
    }

    public async Task<BrokerOrderReceipt> SubmitOrderAsync(FinalizedOrder order, CancellationToken cancellationToken)
    {
        return await ApiProfiler.ProfileAsync("Alpaca", "/v2/orders", "POST", async () =>
        {
            var entryType = _options.EntryOrderType.ToLowerInvariant();
            object requestBody;

            if (_options.ExtendedHours)
            {
                // Industry Standard: Use the exact LimitPrice calculated by the Risk Engine
                // which adheres strictly to the strategy's configured SlippageBps.
                requestBody = new
                {
                    symbol = order.Ticker.ToUpperInvariant(),
                    qty = order.ShareQuantity.ToString(),
                    side = "buy",
                    type = "limit",
                    time_in_force = "day",
                    limit_price = order.LimitPrice.ToString("0.00"),
                    extended_hours = true,
                    client_order_id = order.ClientOrderId
                };
            }
            else if (entryType == "market")
            {
                requestBody = new
                {
                    symbol = order.Ticker.ToUpperInvariant(),
                    qty = order.ShareQuantity.ToString(),
                    side = "buy",
                    type = "market",
                    time_in_force = _options.TimeInForce,
                    client_order_id = order.ClientOrderId,
                    order_class = "bracket",
                    take_profit = new
                    {
                        limit_price = order.TakeProfitPrice.ToString("0.00")
                    },
                    stop_loss = new
                    {
                        stop_price = order.StopLossPrice.ToString("0.00")
                    }
                };
            }
            else
            {
                requestBody = new
                {
                    symbol = order.Ticker.ToUpperInvariant(),
                    qty = order.ShareQuantity.ToString(),
                    side = "buy",
                    type = "limit",
                    time_in_force = _options.TimeInForce,
                    limit_price = order.LimitPrice.ToString("0.00"),
                    client_order_id = order.ClientOrderId,
                    order_class = "bracket",
                    take_profit = new
                    {
                        limit_price = order.TakeProfitPrice.ToString("0.00")
                    },
                    stop_loss = new
                    {
                        stop_price = order.StopLossPrice.ToString("0.00")
                    }
                };
            }

            var json = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v2/orders") { Content = content };
            var response = await _tradingClient.SendAsync(
                request,
                "broker-submit-order",
                correlationId: order.ClientOrderId,
                cancellationToken: cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(AlpacaTradingRestClient.DescribeFailure("order submission", response));
            }

            using var doc = JsonDocument.Parse(response.Payload);
            return RequireOrderReceipt(doc.RootElement, response, "order submission");
        });
    }

    public async Task<bool> CancelOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        return await ApiProfiler.ProfileAsync("Alpaca", $"/v2/orders/{orderId}", "DELETE", async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"/v2/orders/{Uri.EscapeDataString(orderId)}");
            var response = await _tradingClient.SendAsync(
                request,
                "broker-cancel-order",
                providerRecordId: orderId,
                cancellationToken: cancellationToken);
            return response.IsSuccessStatusCode;
        });
    }

    public async Task<bool> CancelAllOrdersAsync(CancellationToken cancellationToken)
    {
        return await ApiProfiler.ProfileAsync("Alpaca", "/v2/orders", "DELETE", async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, "/v2/orders");
            var response = await _tradingClient.SendAsync(
                request,
                "broker-cancel-all-orders",
                cancellationToken: cancellationToken);
            return response.IsSuccessStatusCode;
        });
    }

    public async Task<bool> ModifyOrderAsync(string orderId, decimal newStopLoss, decimal newTakeProfit, CancellationToken cancellationToken)
    {
        return await ApiProfiler.ProfileAsync("Alpaca", $"/v2/orders/{orderId}", "PATCH", async () =>
        {
            if (String.IsNullOrWhiteSpace(orderId))
            {
                throw new ArgumentException("Order id is required when modifying an Alpaca order.", nameof(orderId));
            }

            var request = new System.Collections.Generic.Dictionary<string, string>();
            if (newStopLoss > 0m)
            {
                request["stop_price"] = newStopLoss.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            }

            if (newTakeProfit > 0m)
            {
                request["limit_price"] = newTakeProfit.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            }

            if (request.Count == 0)
            {
                return false;
            }

            var json = JsonSerializer.Serialize(request);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Patch, $"/v2/orders/{orderId}")
            {
                Content = content
            };

            var response = await _tradingClient.SendAsync(
                httpRequest,
                "broker-modify-order",
                providerRecordId: orderId,
                cancellationToken: cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            throw new InvalidOperationException(AlpacaTradingRestClient.DescribeFailure("order modification", response));
        });
    }

    public async Task<string[]> SubmitExitOrdersAsync(string ticker, int quantity, decimal stopLossPrice, decimal takeProfitPrice, CancellationToken cancellationToken)
    {
        return await ApiProfiler.ProfileAsync("Alpaca", "/v2/orders", "POST (Exit OCO)", async () =>
        {
            var requestBody = new
            {
                symbol = ticker.ToUpperInvariant(),
                qty = quantity.ToString(),
                side = "sell",
                type = "limit",
                time_in_force = "gtc",
                order_class = "oco",
                take_profit = new
                {
                    limit_price = takeProfitPrice.ToString("0.00")
                },
                stop_loss = new
                {
                    stop_price = stopLossPrice.ToString("0.00")
                }
            };

            var json = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v2/orders") { Content = content };
            var response = await _tradingClient.SendAsync(
                request,
                "broker-submit-exit-order",
                correlationId: ticker.ToUpperInvariant(),
                cancellationToken: cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(AlpacaTradingRestClient.DescribeFailure("exit-order submission", response));
            }

            using var doc = JsonDocument.Parse(response.Payload);
            var id = RequireOrderId(doc.RootElement, response, "exit-order submission");

            // OCO is submitted as one parent order which creates two legs. We just return the parent ID.
            return new[] { id };
        });
    }

    public async Task<System.Collections.Generic.IReadOnlyList<ActiveBrokerOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken)
    {
        return await ApiProfiler.ProfileAsync("Alpaca", "/v2/orders", "GET", async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v2/orders?status=open");
            var response = await _tradingClient.SendAsync(
                request,
                "broker-open-orders",
                cancellationToken: cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(AlpacaTradingRestClient.DescribeFailure("open-order query", response));
            }

            using var doc = JsonDocument.Parse(response.Payload);

            var orders = doc.RootElement
                .EnumerateArray()
                .Select(element => ParseOrder(element, "open-order query"))
                .ToArray();

            return (System.Collections.Generic.IReadOnlyList<ActiveBrokerOrder>)orders;
        });
    }

    public async Task<ActiveBrokerOrder?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(clientOrderId))
        {
            throw new ArgumentException("Client order ID is required.", nameof(clientOrderId));
        }

        var normalized = clientOrderId.Trim();
        return await ApiProfiler.ProfileAsync("Alpaca", "/v2/orders:by_client_order_id", "GET", async () =>
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"/v2/orders:by_client_order_id?client_order_id={Uri.EscapeDataString(normalized)}");
            var response = await _tradingClient.SendAsync(
                request,
                "broker-order-by-client-id",
                correlationId: normalized,
                cancellationToken: cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    AlpacaTradingRestClient.DescribeFailure("order-by-client-ID query", response));
            }

            using var document = JsonDocument.Parse(response.Payload);
            return ParseOrder(document.RootElement, "order-by-client-ID query");
        });
    }

    private static ActiveBrokerOrder ParseOrder(JsonElement element, string operation)
    {
        var id = RequireString(element, "id", operation);
        var symbol = RequireString(element, "symbol", operation).Trim().ToUpperInvariant();
        var side = RequireString(element, "side", operation);
        var status = RequireString(element, "status", operation);
        var orderType = RequireString(element, "type", operation);
        var clientOrderId = RequireString(element, "client_order_id", operation);
        return new ActiveBrokerOrder(
            id,
            symbol,
            side,
            status,
            orderType,
            ParseOptionalDecimal(element, "limit_price"),
            ParseOptionalDecimal(element, "stop_price"),
            ParseOptionalDecimal(element, "qty"),
            RequireTimestamp(element, "created_at", operation),
            clientOrderId,
            ParseRequiredDecimal(element, "filled_qty", operation),
            ParseOptionalDecimal(element, "filled_avg_price"),
            RequireTimestamp(element, "updated_at", operation));
    }

    private static string RequireString(JsonElement element, string propertyName, string operation)
    {
        if (element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !String.IsNullOrWhiteSpace(property.GetString()))
        {
            return property.GetString()!;
        }

        throw new InvalidOperationException(
            $"Alpaca {operation} response is missing {propertyName}.");
    }

    private static decimal? ParseOptionalDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return decimal.TryParse(
            property.GetString(),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var value)
                ? value
                : throw new InvalidOperationException(
                    $"Alpaca open-order response has invalid {propertyName}.");
    }

    private static decimal ParseRequiredDecimal(
        JsonElement element,
        string propertyName,
        string operation) =>
        ParseOptionalDecimal(element, propertyName)
        ?? throw new InvalidOperationException(
            $"Alpaca {operation} response is missing {propertyName}.");

    private static DateTimeOffset RequireTimestamp(
        JsonElement element,
        string propertyName,
        string operation)
    {
        if (element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            property.TryGetDateTimeOffset(out var timestamp))
        {
            return timestamp.ToUniversalTime();
        }

        throw new InvalidOperationException(
            $"Alpaca {operation} response is missing a valid {propertyName} timestamp.");
    }

    public async Task<System.Collections.Generic.IReadOnlyList<BrokerPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken)
    {
        return await ApiProfiler.ProfileAsync("Alpaca", "/v2/positions", "GET", async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v2/positions");
            var response = await _tradingClient.SendAsync(
                request,
                "broker-open-positions",
                cancellationToken: cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(AlpacaTradingRestClient.DescribeFailure("open-position query", response));
            }

            using var doc = JsonDocument.Parse(response.Payload);

            var positions = new System.Collections.Generic.List<BrokerPosition>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var symbol = RequireString(element, "symbol", "open-position query").Trim().ToUpperInvariant();
                var side = RequireString(element, "side", "open-position query").Trim().ToLowerInvariant();
                if (side is not ("long" or "short"))
                {
                    throw new InvalidOperationException(
                        $"Alpaca open-position query returned unsupported side '{side}' for {symbol}.");
                }

                positions.Add(new BrokerPosition(
                    symbol,
                    side,
                    ParseRequiredDecimal(element, "qty", "open-position query"),
                    ParseRequiredDecimal(element, "avg_entry_price", "open-position query"),
                    ParseRequiredDecimal(element, "current_price", "open-position query"),
                    ParseRequiredDecimal(element, "unrealized_pl", "open-position query")));
            }

            return (System.Collections.Generic.IReadOnlyList<BrokerPosition>)positions;
        });
    }

    public async Task<bool> ClosePositionAsync(string ticker, CancellationToken cancellationToken)
    {
        return await ApiProfiler.ProfileAsync("Alpaca", $"/v2/positions/{ticker}", "DELETE", async () =>
        {
            var symbol = Uri.EscapeDataString(ticker.ToUpperInvariant());
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"/v2/positions/{symbol}");
            var response = await _tradingClient.SendAsync(
                request,
                "broker-close-position",
                correlationId: ticker.ToUpperInvariant(),
                cancellationToken: cancellationToken);
            return response.IsSuccessStatusCode;
        });
    }

    public async Task<bool> ClosePositionAsync(string ticker, int quantity, CancellationToken cancellationToken)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Close quantity must be greater than zero.");
        }

        return await ApiProfiler.ProfileAsync("Alpaca", $"/v2/positions/{ticker}?qty={quantity}", "DELETE", async () =>
        {
            var symbol = Uri.EscapeDataString(ticker.ToUpperInvariant());
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"/v2/positions/{symbol}?qty={quantity}");
            var response = await _tradingClient.SendAsync(
                request,
                "broker-close-position-partial",
                correlationId: ticker.ToUpperInvariant(),
                cancellationToken: cancellationToken);
            return response.IsSuccessStatusCode;
        });
    }

    public void Dispose()
    {
        _tradingClient.Dispose();
    }

    private static string RequireOrderId(
        JsonElement root,
        ArchivedAlpacaResponse response,
        string operation)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("id", out var id) &&
            id.ValueKind == JsonValueKind.String &&
            !String.IsNullOrWhiteSpace(id.GetString()))
        {
            return id.GetString()!;
        }

        throw new InvalidOperationException(
            $"Alpaca {operation} returned no order id. RawArchiveId={response.Archive.Manifest.ArchiveId}.");
    }

    private static BrokerOrderReceipt RequireOrderReceipt(
        JsonElement root,
        ArchivedAlpacaResponse response,
        string operation)
    {
        var orderId = RequireOrderId(root, response, operation);
        if (root.TryGetProperty("created_at", out var createdAt) &&
            createdAt.ValueKind == JsonValueKind.String &&
            createdAt.TryGetDateTimeOffset(out var brokerAcceptedAt))
        {
            return new BrokerOrderReceipt(orderId, brokerAcceptedAt.ToUniversalTime());
        }

        throw new InvalidOperationException(
            $"Alpaca {operation} returned no valid created_at timestamp. RawArchiveId={response.Archive.Manifest.ArchiveId}.");
    }
}
