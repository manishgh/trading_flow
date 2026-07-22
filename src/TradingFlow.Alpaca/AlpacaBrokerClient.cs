using System;
using System.Collections.Concurrent;
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

public sealed class AlpacaBrokerClient :
    IBrokerClient,
    IBrokerAccountProvider,
    IBrokerMarketObservationProvider,
    IDisposable
{
    private readonly AlpacaTradingRestClient _tradingClient;
    private readonly AlpacaMarketDataRestClient _marketDataClient;
    private readonly AlpacaOptions _options;
    private readonly ConcurrentDictionary<string, CachedAssetEligibility> assetEligibilityCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<DateOnly, CachedCalendarDay> tradingCalendarCache = new();

    public AlpacaBrokerClient(
        HttpClient tradingHttpClient,
        HttpClient marketDataHttpClient,
        AlpacaOptions options,
        IRawArchiveWriter rawArchiveWriter)
    {
        _options = options;
        _tradingClient = new AlpacaTradingRestClient(tradingHttpClient, options, rawArchiveWriter);
        _marketDataClient = new AlpacaMarketDataRestClient(marketDataHttpClient, options, rawArchiveWriter);
    }

    public async Task<BrokerAccountSnapshot> GetAccountSnapshotAsync(
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v2/account");
        var response = await _tradingClient.SendAsync(
            request,
            "broker-account-snapshot",
            cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                AlpacaTradingRestClient.DescribeFailure("account snapshot", response));
        }

        using var document = JsonDocument.Parse(response.Payload);
        var root = document.RootElement;
        return new BrokerAccountSnapshot(
            RequireString(root, "id", "account snapshot"),
            RequireString(root, "status", "account snapshot"),
            ReadBoolean(root, "account_blocked"),
            ReadBoolean(root, "trading_blocked"),
            ReadBoolean(root, "trade_suspended_by_user"),
            ReadBoolean(root, "shorting_enabled"),
            ParseRequiredDecimal(root, "buying_power", "account snapshot"),
            ParseRequiredDecimal(root, "equity", "account snapshot"),
            ParseRequiredDecimal(root, "long_market_value", "account snapshot"),
            ParseRequiredDecimal(root, "short_market_value", "account snapshot"),
            DateTimeOffset.UtcNow);
    }

    public async Task<BrokerMarketObservation> GetMarketObservationAsync(
        string symbol,
        EquityTradingSession session,
        CancellationToken cancellationToken)
    {
        var normalized = symbol.Trim().ToUpperInvariant();
        if (String.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Asset symbol is required.", nameof(symbol));
        }

        var feed = session == EquityTradingSession.Overnight
            ? "overnight"
            : _options.ResolveMarketDataFeed();
        var escaped = Uri.EscapeDataString(normalized);
        using var quoteRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v2/stocks/{escaped}/quotes/latest?feed={feed}");
        using var tradeRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v2/stocks/{escaped}/trades/latest?feed={feed}");
        var quoteTask = _marketDataClient.SendAsync(
            quoteRequest,
            "market-latest-quote",
            normalized,
            cancellationToken);
        var tradeTask = _marketDataClient.SendAsync(
            tradeRequest,
            "market-latest-trade",
            normalized,
            cancellationToken);
        await Task.WhenAll(quoteTask, tradeTask);
        var quoteResponse = await quoteTask;
        var tradeResponse = await tradeTask;
        if (!quoteResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                AlpacaTradingRestClient.DescribeFailure("latest quote", quoteResponse));
        }

        if (!tradeResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                AlpacaTradingRestClient.DescribeFailure("latest trade", tradeResponse));
        }

        using var quoteDocument = JsonDocument.Parse(quoteResponse.Payload);
        using var tradeDocument = JsonDocument.Parse(tradeResponse.Payload);
        var quote = quoteDocument.RootElement.GetProperty("quote");
        var trade = tradeDocument.RootElement.GetProperty("trade");
        return new BrokerMarketObservation(
            normalized,
            feed,
            ParseOptionalDecimal(quote, "bp"),
            ParseOptionalDecimal(quote, "ap"),
            ParseOptionalTimestamp(quote, "t"),
            ParseOptionalDecimal(trade, "p"),
            ParseOptionalTimestamp(trade, "t"),
            DateTimeOffset.UtcNow);
    }

    public async Task<BrokerOrderReceipt> SubmitOrderAsync(BrokerEntryOrder entryOrder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entryOrder);
        ArgumentNullException.ThrowIfNull(entryOrder.Order);
        ValidateEntryOrder(entryOrder);

        return await ApiProfiler.ProfileAsync("Alpaca", "/v2/orders", "POST", async () =>
        {
            var order = entryOrder.Order;
            var entryType = entryOrder.OrderType.Trim().ToLowerInvariant();
            object requestBody;

            if (entryOrder.SubmitOutsideRegularHours)
            {
                requestBody = new
                {
                    symbol = order.Ticker.ToUpperInvariant(),
                    qty = order.ShareQuantity.ToString(CultureInfo.InvariantCulture),
                    side = entryOrder.Side,
                    type = entryType,
                    time_in_force = entryOrder.TimeInForce,
                    limit_price = FormatOrderPrice(order.LimitPrice),
                    extended_hours = true,
                    client_order_id = order.ClientOrderId
                };
            }
            else if (entryType == "market")
            {
                requestBody = new
                {
                    symbol = order.Ticker.ToUpperInvariant(),
                    qty = order.ShareQuantity.ToString(CultureInfo.InvariantCulture),
                    side = entryOrder.Side,
                    type = "market",
                    time_in_force = entryOrder.TimeInForce,
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
                    qty = order.ShareQuantity.ToString(CultureInfo.InvariantCulture),
                    side = entryOrder.Side,
                    type = "limit",
                    time_in_force = entryOrder.TimeInForce,
                    limit_price = FormatOrderPrice(order.LimitPrice),
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
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v2/orders") { Content = content };
            var response = await _tradingClient.SendAsync(
                httpRequest,
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

    public async Task<AssetTradingEligibility?> GetEligibilityAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        var normalized = symbol.Trim().ToUpperInvariant();
        if (String.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Asset symbol is required.", nameof(symbol));
        }

        var now = DateTimeOffset.UtcNow;
        if (assetEligibilityCache.TryGetValue(normalized, out var cached) &&
            now - cached.CachedAtUtc < TimeSpan.FromSeconds(_options.AssetEligibilityCacheSeconds))
        {
            return cached.Eligibility;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v2/assets/{Uri.EscapeDataString(normalized)}");
        var response = await _tradingClient.SendAsync(
            request,
            "broker-get-asset-eligibility",
            providerRecordId: normalized,
            cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                AlpacaTradingRestClient.DescribeFailure("asset eligibility", response));
        }

        using var document = JsonDocument.Parse(response.Payload);
        var root = document.RootElement;
        var eligibility = new AssetTradingEligibility(
            root.GetProperty("symbol").GetString() ?? normalized,
            String.Equals(root.GetProperty("status").GetString(), "active", StringComparison.OrdinalIgnoreCase),
            root.GetProperty("tradable").GetBoolean(),
            root.TryGetProperty("overnight_tradable", out var overnight) && overnight.GetBoolean(),
            now,
            root.TryGetProperty("shortable", out var shortable) && shortable.GetBoolean(),
            root.TryGetProperty("borrow_status", out var borrowStatus) && borrowStatus.ValueKind == JsonValueKind.String
                ? borrowStatus.GetString()
                : null);
        assetEligibilityCache[normalized] = new CachedAssetEligibility(eligibility, now);
        return eligibility;
    }

    public async Task<TradingSessionSnapshot> GetSessionAsync(
        DateTimeOffset timestampUtc,
        CancellationToken cancellationToken)
    {
        var easternZone = ResolveEasternTimeZone();
        var eastern = TimeZoneInfo.ConvertTime(timestampUtc, easternZone);
        var localDate = DateOnly.FromDateTime(eastern.DateTime);
        var localTime = TimeOnly.FromDateTime(eastern.DateTime);
        var tradeDate = localTime >= new TimeOnly(20, 0) ? localDate.AddDays(1) : localDate;
        var calendarDay = await GetCalendarDayAsync(tradeDate, cancellationToken);
        if (calendarDay is null)
        {
            return new TradingSessionSnapshot(
                tradeDate,
                EquityTradingSession.Closed,
                DateTimeOffset.UtcNow,
                null,
                null);
        }

        var regularOpen = ToUtc(tradeDate, calendarDay.Open, easternZone);
        var regularClose = ToUtc(tradeDate, calendarDay.Close, easternZone);
        var session = localTime switch
        {
            _ when localTime < new TimeOnly(4, 0) || localTime >= new TimeOnly(20, 0) =>
                EquityTradingSession.Overnight,
            _ when timestampUtc < regularOpen => EquityTradingSession.Premarket,
            _ when timestampUtc < regularClose => EquityTradingSession.Regular,
            _ => EquityTradingSession.AfterHours
        };
        return new TradingSessionSnapshot(
            tradeDate,
            session,
            DateTimeOffset.UtcNow,
            regularOpen,
            regularClose);
    }

    private async Task<AlpacaCalendarDay?> GetCalendarDayAsync(
        DateOnly tradeDate,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (tradingCalendarCache.TryGetValue(tradeDate, out var cached) &&
            now - cached.CachedAtUtc < TimeSpan.FromSeconds(_options.TradingCalendarCacheSeconds))
        {
            return cached.Day;
        }

        var date = tradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v2/calendar?start={date}&end={date}");
        var response = await _tradingClient.SendAsync(
            request,
            "broker-get-trading-calendar",
            providerRecordId: date,
            cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                AlpacaTradingRestClient.DescribeFailure("trading-calendar lookup", response));
        }

        using var document = JsonDocument.Parse(response.Payload);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Alpaca trading-calendar response must be an array.");
        }

        AlpacaCalendarDay? day = null;
        if (document.RootElement.GetArrayLength() > 0)
        {
            var item = document.RootElement[0];
            var responseDate = DateOnly.ParseExact(
                item.GetProperty("date").GetString() ?? String.Empty,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture);
            if (responseDate != tradeDate)
            {
                throw new InvalidOperationException(
                    $"Alpaca trading-calendar returned {responseDate:yyyy-MM-dd} for requested trade date {tradeDate:yyyy-MM-dd}.");
            }

            day = new AlpacaCalendarDay(
                TimeOnly.Parse(item.GetProperty("open").GetString() ?? String.Empty, CultureInfo.InvariantCulture),
                TimeOnly.Parse(item.GetProperty("close").GetString() ?? String.Empty, CultureInfo.InvariantCulture));
        }

        tradingCalendarCache[tradeDate] = new CachedCalendarDay(day, now);
        return day;
    }

    private static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static TimeZoneInfo ResolveEasternTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }

    private sealed record AlpacaCalendarDay(TimeOnly Open, TimeOnly Close);

    private sealed record CachedCalendarDay(AlpacaCalendarDay? Day, DateTimeOffset CachedAtUtc);

    private sealed record CachedAssetEligibility(AssetTradingEligibility Eligibility, DateTimeOffset CachedAtUtc);

    private static void ValidateEntryOrder(BrokerEntryOrder request)
    {
        var side = request.Side.Trim().ToLowerInvariant();
        var type = request.OrderType.Trim().ToLowerInvariant();
        var timeInForce = request.TimeInForce.Trim().ToLowerInvariant();
        if (side != "buy" || type is not ("market" or "limit") || timeInForce is not ("day" or "gtc"))
        {
            throw new InvalidOperationException("Unsupported Alpaca entry contract.");
        }

        if (request.SubmitOutsideRegularHours && (type != "limit" || timeInForce != "day"))
        {
            throw new InvalidOperationException(
                "Alpaca extended-hours entries require an explicit DAY limit order.");
        }
    }

    public async Task<BrokerOrderReceipt> SubmitProtectiveStopAsync(
        ProtectiveStopOrder order,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.Quantity <= 0m || order.StopPrice <= 0m ||
            order.Side.Trim().ToLowerInvariant() is not ("buy" or "sell") ||
            !order.TimeInForce.Equals("gtc", StringComparison.OrdinalIgnoreCase) ||
            String.IsNullOrWhiteSpace(order.ClientOrderId))
        {
            throw new InvalidOperationException("A protective stop requires side, positive quantity/price, GTC, and client order ID.");
        }

        return await ApiProfiler.ProfileAsync("Alpaca", "/v2/orders", "POST (Protective Stop)", async () =>
        {
            var requestBody = new
            {
                symbol = order.Ticker.Trim().ToUpperInvariant(),
                qty = order.Quantity.ToString("0.#########", CultureInfo.InvariantCulture),
                side = order.Side.Trim().ToLowerInvariant(),
                type = "stop",
                time_in_force = "gtc",
                stop_price = FormatOrderPrice(order.StopPrice),
                client_order_id = order.ClientOrderId
            };
            using var content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v2/orders") { Content = content };
            var response = await _tradingClient.SendAsync(
                request,
                "broker-submit-protective-stop",
                correlationId: order.ClientOrderId,
                cancellationToken: cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    AlpacaTradingRestClient.DescribeFailure("protective-stop submission", response));
            }

            using var document = JsonDocument.Parse(response.Payload);
            return RequireOrderReceipt(document.RootElement, response, "protective-stop submission");
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
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v2/orders?status=open&nested=true&limit=500");
            var response = await _tradingClient.SendAsync(
                request,
                "broker-open-orders",
                cancellationToken: cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(AlpacaTradingRestClient.DescribeFailure("open-order query", response));
            }

            using var doc = JsonDocument.Parse(response.Payload);

            var orders = new System.Collections.Generic.List<ActiveBrokerOrder>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var parent = ParseOrder(element, "open-order query");
                if (IsOpenStatus(parent.Status))
                {
                    orders.Add(parent);
                }

                if (element.TryGetProperty("legs", out var legs) && legs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var leg in legs.EnumerateArray())
                    {
                        var child = ParseOrder(leg, "open-order leg query", parent.ClientOrderId);
                        if (IsOpenStatus(child.Status))
                        {
                            orders.Add(child);
                        }
                    }
                }
            }

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

    private static ActiveBrokerOrder ParseOrder(
        JsonElement element,
        string operation,
        string? parentClientOrderId = null)
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
            RequireTimestamp(element, "updated_at", operation),
            parentClientOrderId);
    }

    private static bool IsOpenStatus(string status) =>
        status.Trim().ToLowerInvariant() is
            "accepted" or "pending_new" or "accepted_for_bidding" or "new" or
            "partially_filled" or "pending_cancel" or "pending_replace";

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

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var numericValue))
        {
            return numericValue;
        }

        if (property.ValueKind == JsonValueKind.String && decimal.TryParse(
                property.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var stringValue))
        {
            return stringValue;
        }

        throw new InvalidOperationException($"Alpaca response has invalid {propertyName}.");
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
        _marketDataClient.Dispose();
        _tradingClient.Dispose();
    }

    private static bool ReadBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? ParseOptionalTimestamp(
        JsonElement element,
        string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            property.TryGetDateTimeOffset(out var timestamp)
                ? timestamp.ToUniversalTime()
                : null;
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

    private static string FormatOrderPrice(decimal price)
    {
        var decimals = price >= 1m ? 2 : 4;
        return Decimal.Round(price, decimals, MidpointRounding.ToZero)
            .ToString(decimals == 2 ? "0.00" : "0.0000", CultureInfo.InvariantCulture);
    }
}
