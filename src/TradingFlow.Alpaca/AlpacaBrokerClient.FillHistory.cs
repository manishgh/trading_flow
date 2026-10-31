using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using TradingFlow.Domain.Execution;

namespace TradingFlow.Alpaca;

public sealed partial class AlpacaBrokerClient
{
    private const int AccountActivityPageSize = 100; // Alpaca's documented maximum page size.
    private readonly ConcurrentDictionary<string, string> activityOrderIntents = new(StringComparer.Ordinal);

    /// <summary>Collect all account fills through a frozen cutoff; incomplete pages or unsupported activities never become an empty count.</summary>
    public async Task<BrokerFillHistorySnapshot> GetFillHistoryAsync(string accountId, DateOnly newYorkDate,
        CancellationToken cancellationToken)
    {
        var requestedAt = DateTimeOffset.UtcNow;
        if (String.IsNullOrWhiteSpace(accountId) || newYorkDate != TradingLimitDay.At(requestedAt))
            throw new InvalidOperationException("Operational fill history requires the current New York day and a bound broker account.");
        if (_options.AccountFillHistoryMaxPages <= 0)
            throw new InvalidOperationException("The account fill-history page budget must be positive.");
        var start = TradingLimitDay.StartUtc(newYorkDate);
        var values = new List<BrokerFillActivity>();
        var archiveIds = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? pageToken = null;
        for (var page = 1; page <= _options.AccountFillHistoryMaxPages; page++)
        {
            var url = "/v2/account/activities?activity_types=FILL&direction=asc&page_size=" + AccountActivityPageSize +
                "&after=" + Uri.EscapeDataString(start.AddTicks(-1).ToString("O", CultureInfo.InvariantCulture)) +
                "&until=" + Uri.EscapeDataString(requestedAt.ToString("O", CultureInfo.InvariantCulture)) +
                (pageToken is null ? "" : "&page_token=" + Uri.EscapeDataString(pageToken));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await _tradingClient.SendAsync(request, "broker-account-fill-history", cancellationToken: cancellationToken);
            archiveIds.Add(response.Archive.Manifest.ArchiveId);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(AlpacaTradingRestClient.DescribeFailure("account fill history", response));
            using var document = JsonDocument.Parse(response.Payload);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Alpaca account fill history did not return an activity array.");
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var fill = ParseFillActivity(item, start, requestedAt);
                if (!ids.Add(fill.ActivityId))
                    throw new InvalidDataException("Alpaca fill pagination repeated an activity; complete daily history was not established.");
                var positionIntent = await ReadActivityOrderIntentAsync(fill.BrokerOrderId, archiveIds, cancellationToken);
                values.Add(fill with { PositionIntent = positionIntent });
                pageToken = fill.ActivityId;
            }
            if (document.RootElement.GetArrayLength() < AccountActivityPageSize)
            {
                var pending = await ReadOpenOrdersWithArchiveAsync(cancellationToken);
                archiveIds.Add(pending.ArchiveId);
                return new(accountId.Trim(), newYorkDate, requestedAt, DateTimeOffset.UtcNow, start,
                    requestedAt, true, values, page, archiveIds, pending.Orders);
            }
        }
        throw new InvalidOperationException("Account fill history exceeded its configured page budget; new entries cannot assume the missing fills are zero.");
    }

    /// <summary>Read and cache immutable opening/closing intent; unknown intent stays unknown for local-role resolution or explicit rejection.</summary>
    private async Task<string?> ReadActivityOrderIntentAsync(string orderId, ICollection<string> archiveIds, CancellationToken token)
    {
        if (activityOrderIntents.TryGetValue(orderId, out var existing)) return existing.Length == 0 ? null : existing;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v2/orders/" + Uri.EscapeDataString(orderId));
        var response = await _tradingClient.SendAsync(request, "broker-fill-order-intent", providerRecordId: orderId, cancellationToken: token);
        archiveIds.Add(response.Archive.Manifest.ArchiveId);
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(response.Payload);
        if (RequireString(document.RootElement, "id", "fill order intent") != orderId)
            throw new InvalidDataException("Alpaca order metadata identifies a different fill order.");
        var intent = ParseOptionalString(document.RootElement, "position_intent");
        if (intent is not ("buy_to_open" or "sell_to_open" or "buy_to_close" or "sell_to_close")) intent = null;
        if (activityOrderIntents.Count < (long)_options.AccountFillHistoryMaxPages * AccountActivityPageSize)
            activityOrderIntents.TryAdd(orderId, intent ?? "");
        return intent;
    }

    /// <summary>Require genuine positive execution quantities, supported sides/types and timestamps inside the declared day/cutoff.</summary>
    private static BrokerFillActivity ParseFillActivity(JsonElement item, DateTimeOffset start, DateTimeOffset end)
    {
        const string operation = "account fill history";
        var type = RequireString(item, "type", operation);
        var side = RequireString(item, "side", operation);
        var time = RequireTimestamp(item, "transaction_time", operation).ToUniversalTime();
        var quantity = ParseRequiredDecimal(item, "qty", operation);
        var cumulative = ParseRequiredDecimal(item, "cum_qty", operation);
        if (RequireString(item, "activity_type", operation) != "FILL" || type is not ("fill" or "partial_fill") ||
            side is not ("buy" or "sell") || quantity <= 0 || cumulative < quantity || time < start || time > end)
            throw new InvalidDataException("Alpaca returned an unsupported, out-of-window or invalid fill activity; daily entry count is unavailable.");
        return new(RequireString(item, "id", operation), RequireString(item, "order_id", operation),
            RequireString(item, "symbol", operation).Trim().ToUpperInvariant(), side, quantity, cumulative, time, null);
    }
}
