using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Logging;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Alpaca;

public sealed class AlpacaBrokerClient : IBrokerClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly AlpacaOptions _options;

    public AlpacaBrokerClient(HttpClient httpClient, AlpacaOptions options)
    {
        _httpClient = httpClient;
        _options = options;

        _httpClient.BaseAddress = _options.BaseUrl;
        _httpClient.DefaultRequestHeaders.Add("APCA-API-KEY-ID", _options.KeyId);
        _httpClient.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", _options.SecretKey);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<string> SubmitOrderAsync(FinalizedOrder order, CancellationToken cancellationToken)
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
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/v2/orders", content, cancellationToken);
            var responseString = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Alpaca API Error: {response.StatusCode} - {responseString}");
            }

            using var doc = JsonDocument.Parse(responseString);
            return doc.RootElement.GetProperty("id").GetString() ?? Guid.NewGuid().ToString();
        });
    }

    public async Task<bool> CancelOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        return await ApiProfiler.ProfileAsync("Alpaca", $"/v2/orders/{orderId}", "DELETE", async () =>
        {
            var response = await _httpClient.DeleteAsync($"/v2/orders/{orderId}", cancellationToken);
            return response.IsSuccessStatusCode;
        });
    }

    public async Task<bool> CancelAllOrdersAsync(CancellationToken cancellationToken)
    {
        return await ApiProfiler.ProfileAsync("Alpaca", "/v2/orders", "DELETE", async () =>
        {
            var response = await _httpClient.DeleteAsync("/v2/orders", cancellationToken);
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

            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            var responseString = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Alpaca API Error (ModifyOrderAsync): {response.StatusCode} - {responseString}");
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
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/v2/orders", content, cancellationToken);
            var responseString = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Alpaca API Error (SubmitExitOrdersAsync): {response.StatusCode} - {responseString}");
            }

            using var doc = JsonDocument.Parse(responseString);
            var id = doc.RootElement.GetProperty("id").GetString() ?? Guid.NewGuid().ToString();

            // OCO is submitted as one parent order which creates two legs. We just return the parent ID.
            return new[] { id };
        });
    }

    public async Task<System.Collections.Generic.IReadOnlyList<ActiveBrokerOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken)
    {
        return await ApiProfiler.ProfileAsync("Alpaca", "/v2/orders", "GET", async () =>
        {
            var response = await _httpClient.GetAsync("/v2/orders?status=open", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (System.Collections.Generic.IReadOnlyList<ActiveBrokerOrder>)Array.Empty<ActiveBrokerOrder>();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            var orders = new System.Collections.Generic.List<ActiveBrokerOrder>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var id = element.GetProperty("id").GetString() ?? "";
                var symbol = element.GetProperty("symbol").GetString() ?? "";
                var side = element.GetProperty("side").GetString() ?? "";
                var status = element.GetProperty("status").GetString() ?? "";
                var type = element.TryGetProperty("type", out var tp) && tp.ValueKind != JsonValueKind.Null ? tp.GetString() ?? "" : "";

                decimal? limit = null;
                if (element.TryGetProperty("limit_price", out var lp) && lp.ValueKind != JsonValueKind.Null && decimal.TryParse(lp.GetString(), out var lVal))
                    limit = lVal;

                decimal? stop = null;
                if (element.TryGetProperty("stop_price", out var sp) && sp.ValueKind != JsonValueKind.Null && decimal.TryParse(sp.GetString(), out var sVal))
                    stop = sVal;

                decimal? qty = null;
                if (element.TryGetProperty("qty", out var qp) && qp.ValueKind != JsonValueKind.Null && decimal.TryParse(qp.GetString(), out var qVal))
                    qty = qVal;

                DateTimeOffset createdAt = DateTimeOffset.UtcNow;
                if (element.TryGetProperty("created_at", out var cp) && cp.ValueKind != JsonValueKind.Null)
                    createdAt = cp.GetDateTimeOffset();

                var clientOrderId = element.TryGetProperty("client_order_id", out var clientOrderIdProperty) &&
                    clientOrderIdProperty.ValueKind != JsonValueKind.Null
                        ? clientOrderIdProperty.GetString() ?? ""
                        : "";

                orders.Add(new ActiveBrokerOrder(id, symbol, side, status, type, limit, stop, qty, createdAt, clientOrderId));
            }

            return (System.Collections.Generic.IReadOnlyList<ActiveBrokerOrder>)orders;
        });
    }

    public async Task<System.Collections.Generic.IReadOnlyList<BrokerPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken)
    {
        return await ApiProfiler.ProfileAsync("Alpaca", "/v2/positions", "GET", async () =>
        {
            var response = await _httpClient.GetAsync("/v2/positions", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (System.Collections.Generic.IReadOnlyList<BrokerPosition>)Array.Empty<BrokerPosition>();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            var positions = new System.Collections.Generic.List<BrokerPosition>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var symbol = element.GetProperty("symbol").GetString() ?? "";
                var side = element.GetProperty("side").GetString() ?? "";

                decimal qty = 0;
                if (element.TryGetProperty("qty", out var qp) && decimal.TryParse(qp.GetString(), out var qVal))
                    qty = qVal;

                decimal avgEntry = 0;
                if (element.TryGetProperty("avg_entry_price", out var ap) && decimal.TryParse(ap.GetString(), out var aVal))
                    avgEntry = aVal;

                decimal currentPrice = 0;
                if (element.TryGetProperty("current_price", out var cp) && decimal.TryParse(cp.GetString(), out var cVal))
                    currentPrice = cVal;

                decimal unPl = 0;
                if (element.TryGetProperty("unrealized_pl", out var up) && decimal.TryParse(up.GetString(), out var uVal))
                    unPl = uVal;

                positions.Add(new BrokerPosition(symbol, side, qty, avgEntry, currentPrice, unPl));
            }

            return (System.Collections.Generic.IReadOnlyList<BrokerPosition>)positions;
        });
    }

    public async Task<bool> ClosePositionAsync(string ticker, CancellationToken cancellationToken)
    {
        return await ApiProfiler.ProfileAsync("Alpaca", $"/v2/positions/{ticker}", "DELETE", async () =>
        {
            var response = await _httpClient.DeleteAsync($"/v2/positions/{ticker.ToUpperInvariant()}", cancellationToken);
            return response.IsSuccessStatusCode;
        });
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
