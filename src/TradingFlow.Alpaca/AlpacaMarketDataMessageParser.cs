using System.Text.Json;
using TradingFlow.Domain.Market;

namespace TradingFlow.Alpaca;

public static class AlpacaMarketDataMessageParser
{
    public static bool TryParseBar(
        JsonElement message,
        DateTimeOffset observedAtUtc,
        long fencingToken,
        out MarketBarEvent? marketEvent,
        out AlpacaBarParseFailure? failure) =>
        TryParseBar(message, observedAtUtc, fencingToken, "sip", out marketEvent, out failure);

    public static bool TryParseBar(
        JsonElement message,
        DateTimeOffset observedAtUtc,
        long fencingToken,
        string dataFeed,
        out MarketBarEvent? marketEvent,
        out AlpacaBarParseFailure? failure)
    {
        marketEvent = null;
        failure = null;
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("T", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var type = typeElement.GetString();
        if (type is not ("b" or "u"))
        {
            return false;
        }

        _ = TryReadString(message, "S", out var symbol);
        if (String.IsNullOrWhiteSpace(symbol) ||
            !message.TryGetProperty("t", out var timestampElement) ||
            !timestampElement.TryGetDateTimeOffset(out var timestamp) ||
            !TryReadDecimal(message, "o", out var open) ||
            !TryReadDecimal(message, "h", out var high) ||
            !TryReadDecimal(message, "l", out var low) ||
            !TryReadDecimal(message, "c", out var close) ||
            !TryReadDecimal(message, "v", out var volume))
        {
            failure = new AlpacaBarParseFailure(
                type,
                symbol?.Trim().ToUpperInvariant(),
                "Recognized Alpaca bar frame is missing a valid symbol, timestamp, or OHLCV value.");
            return false;
        }

        var bar = new OhlcvBar(
            symbol!.Trim().ToUpperInvariant(),
            timestamp.ToUniversalTime(),
            "1m",
            open,
            high,
            low,
            close,
            volume,
            NormalizeFeed(dataFeed),
            "all",
            observedAtUtc.ToUniversalTime(),
            timestamp.ToUniversalTime().AddMinutes(1));
        marketEvent = new MarketBarEvent(
            "alpaca",
            NormalizeFeed(dataFeed),
            bar,
            type == "u" ? MarketBarEventKind.ProviderRevision : MarketBarEventKind.CompletedBar,
            observedAtUtc.ToUniversalTime(),
            fencingToken);
        return true;
    }

    private static string NormalizeFeed(string dataFeed)
    {
        if (String.IsNullOrWhiteSpace(dataFeed))
        {
            throw new ArgumentException("Alpaca data feed is required.", nameof(dataFeed));
        }

        return dataFeed.Trim().ToLowerInvariant();
    }

    private static bool TryReadString(JsonElement element, string name, out string? value)
    {
        value = element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
        return !String.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadDecimal(JsonElement element, string name, out decimal value)
    {
        value = 0m;
        return element.TryGetProperty(name, out var property) && property.TryGetDecimal(out value);
    }
}

public sealed record AlpacaBarParseFailure(
    string MessageType,
    string? Symbol,
    string Detail);
