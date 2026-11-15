using System.Globalization;
using System.Text.Json;

namespace TradingFlow.Alpaca;

public enum AlpacaSipTradeKind { Print, Correction, Cancellation, Error }

[Flags]
public enum AlpacaSipMinuteFields { None = 0, OpenClose = 1, HighLow = 2, Volume = 4 }

public readonly record struct AlpacaSipEventClock(DateTimeOffset Utc, int LastTwoNanoseconds) : IComparable<AlpacaSipEventClock>
{
    // Keep the provider's last two digits when two prints occupy the same .NET clock tick.
    public int CompareTo(AlpacaSipEventClock other) => Utc == other.Utc
        ? LastTwoNanoseconds.CompareTo(other.LastTwoNanoseconds) : Utc.CompareTo(other.Utc);

    // Serialize the actual event clock at its full precision for chart evidence.
    public string ToUtcText() => Utc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture) +
        LastTwoNanoseconds.ToString("D2", CultureInfo.InvariantCulture) + "Z";
}

public readonly record struct AlpacaSipTradePrint(long Id, decimal Price, long Size, string Conditions);

public sealed record AlpacaSipTradeFrame(
    AlpacaSipTradeKind Kind, string Symbol, char Tape, char Exchange, AlpacaSipEventClock Clock,
    AlpacaSipTradePrint Original, AlpacaSipTradePrint? Corrected);

public sealed record AlpacaSipTradeFailure(string? Symbol, AlpacaSipEventClock? Clock, bool CouldBeMutation);

public static partial class AlpacaMarketDataMessageParser
{
    private const AlpacaSipMinuteFields AllMinuteFields = AlpacaSipMinuteFields.OpenClose |
        AlpacaSipMinuteFields.HighLow | AlpacaSipMinuteFields.Volume;

    // Recognize Alpaca's trade channels while leaving unrelated market-state frames on their existing path.
    public static bool TryParseSipTrade(JsonElement message, out AlpacaSipTradeFrame? frame, out AlpacaSipTradeFailure? failure)
    {
        frame = null;
        failure = null;
        if (message.ValueKind != JsonValueKind.Object || !TryReadString(message, "T", out var type) ||
            type is not ("t" or "c" or "x")) return false;

        var symbol = TryReadString(message, "S", out var supplied) && supplied!.Length <= 16
            ? AlpacaSymbolMapper.ToCanonicalSymbol(supplied!) : null;
        var hasClock = TryReadSipClock(message, out var clock);
        if (symbol is not null && hasClock && TryReadSipCode(message, "z", out var tape) &&
            TryReadSipCode(message, "x", out var exchange) &&
            TryReadSipTradePayload(message, type!, symbol, tape, exchange, clock, out frame)) return true;

        failure = new AlpacaSipTradeFailure(symbol, hasClock ? clock : null, type != "t");
        return false;
    }

    // Only recognized, structurally valid payloads can reach the minute ledger.
    private static bool TryReadSipTradePayload(JsonElement message, string type, string symbol, char tape,
        char exchange, AlpacaSipEventClock clock, out AlpacaSipTradeFrame? frame)
    {
        frame = null;
        if (type == "c")
        {
            if (!TryReadPrint(message, "oi", "op", "os", "oc", out var original) ||
                !TryReadPrint(message, "ci", "cp", "cs", "cc", out var corrected)) return false;
            frame = new(AlpacaSipTradeKind.Correction, symbol, tape, exchange, clock, original, corrected);
        }
        else
        {
            if (!TryReadPrint(message, "i", "p", "s", type == "t" ? "c" : null, out var original)) return false;
            var kind = AlpacaSipTradeKind.Print;
            if (type == "x")
            {
                if (!TryReadSipCode(message, "a", out var action) || action is not ('C' or 'E')) return false;
                kind = action == 'C' ? AlpacaSipTradeKind.Cancellation : AlpacaSipTradeKind.Error;
            }
            frame = new(kind, symbol, tape, exchange, clock, original, null);
        }

        return true;
    }

    // Apply the strictest rule independently to each field from the provider's SIP minute table.
    public static bool TryResolveSipMinuteFields(char tape, string conditions, out AlpacaSipMinuteFields fields)
    {
        fields = AllMinuteFields;
        if (tape is not ('A' or 'B' or 'C') || conditions.Length == 0) return false;
        var full = tape == 'C' ? "@ABDFKLOTXY56" : " EFKLOTX56";
        var volume = tape == 'C' ? "CGHINPRUVWZ47" : "BCHINPRUVZ47";
        foreach (var code in conditions)
        {
            if (full.Contains(code)) continue;
            if (volume.Contains(code)) fields &= AlpacaSipMinuteFields.Volume;
            else if (code is 'M' or 'Q' or '9') fields = AlpacaSipMinuteFields.None;
            else return false;
        }
        return true;
    }

    // Parse the exact channel fields. A cancellation carries price and size but no sale conditions.
    private static bool TryReadPrint(JsonElement message, string idField, string priceField, string sizeField,
        string? conditionField, out AlpacaSipTradePrint print)
    {
        print = default;
        if (!message.TryGetProperty(idField, out var idValue) || idValue.ValueKind != JsonValueKind.Number ||
            !idValue.TryGetInt64(out var id) || id < 0 ||
            !message.TryGetProperty(priceField, out var priceValue) || priceValue.ValueKind != JsonValueKind.Number ||
            !priceValue.TryGetDecimal(out var price) || price <= 0m ||
            !message.TryGetProperty(sizeField, out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number ||
            !sizeValue.TryGetInt64(out var size) || size <= 0) return false;

        var conditions = "";
        if (conditionField is not null && !TryReadSipConditions(message, conditionField, out conditions)) return false;
        print = new(id, price, size, conditions);
        return true;
    }

    // Preserve literal regular-sale space and reject missing or multi-character condition entries.
    private static bool TryReadSipConditions(JsonElement message, string field, out string conditions)
    {
        conditions = "";
        if (!message.TryGetProperty(field, out var array) || array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() is < 1 or > 16) return false;
        Span<char> codes = stackalloc char[array.GetArrayLength()];
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: 1 } code) return false;
            codes[index++] = code[0];
        }
        conditions = new string(codes);
        return true;
    }

    // Decode only the documented single-letter tape, exchange and cancellation action fields.
    private static bool TryReadSipCode(JsonElement message, string field, out char code)
    {
        code = default;
        if (!TryReadString(message, field, out var value) || value is not { Length: 1 } ||
            value[0] is < 'A' or > 'Z') return false;
        code = value[0];
        return true;
    }

    // Require an explicit RFC 3339 offset and capture precision below a .NET tick.
    private static bool TryReadSipClock(JsonElement message, out AlpacaSipEventClock clock)
    {
        clock = default;
        if (!message.TryGetProperty("t", out var element) || element.ValueKind != JsonValueKind.String ||
            element.GetString() is not { Length: >= 20 and <= 35 } text ||
            !(text[^1] == 'Z' || text[^6] is '+' or '-') || !element.TryGetDateTimeOffset(out var time)) return false;

        var end = text[^1] == 'Z' ? text.Length - 1 : text.Length - 6;
        var length = end - 20;
        if (length > 9) return false;
        var remainder = length > 7 ? (text[27] - '0') * 10 : 0;
        if (length == 9) remainder += text[28] - '0';
        clock = new(time.ToUniversalTime(), remainder);
        return true;
    }
}
