using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TradingFlow.Contracts.Evidence;

namespace TradingFlow.Data.Evidence.Collection.SharedNews;

internal static class CollectionWire
{
    internal const int MaximumPlanBytes = 4_194_304;
    internal const int MaximumRecordBytes = 16_384;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
    internal static bool Matches(string value, string pattern) => Regex.IsMatch(value, "\\A(?:" + pattern + ")\\z", RegexOptions.CultureInvariant);
    internal static bool Digest(string value) => Matches(value, "[0-9a-f]{64}");

    internal static JsonElement Parse(byte[] bytes)
    {
        _ = Utf8.GetCharCount(bytes);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        CheckKeys(root);
        Require(root.ValueKind == JsonValueKind.Object, "Collection record must be an object.");
        Require(bytes.AsSpan().SequenceEqual(Encode(root)), "Noncanonical collection record.");
        return root.Clone();
    }

    private static void CheckKeys(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                Require(names.Add(property.Name), "Duplicate collection JSON field.");
                CheckKeys(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) CheckKeys(item);
    }

    // Python collection metadata uses ensure_ascii=True, sorted keys and compact separators.
    // Its numbers are integers; original receipt bytes are never re-encoded.
    internal static byte[] Encode(JsonElement root)
    {
        var builder = new StringBuilder();
        Write(root, builder);
        return Utf8.GetBytes(builder.ToString());
    }
    internal static byte[] Encode(object value) => Encode(JsonSerializer.SerializeToElement(value));
    private static void Write(JsonElement node, StringBuilder builder)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in node.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) builder.Append(',');
                    first = false;
                    String(property.Name, builder);
                    builder.Append(':');
                    Write(property.Value, builder);
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var separator = "";
                foreach (var item in node.EnumerateArray())
                {
                    builder.Append(separator);
                    separator = ",";
                    Write(item, builder);
                }
                builder.Append(']');
                break;
            case JsonValueKind.String: String(node.GetString()!, builder); break;
            case JsonValueKind.Number: builder.Append(node.GetInt64().ToString(CultureInfo.InvariantCulture)); break;
            case JsonValueKind.True: builder.Append("true"); break;
            case JsonValueKind.False: builder.Append("false"); break;
            case JsonValueKind.Null: builder.Append("null"); break;
            default: throw new InvalidDataException("Unsupported collection JSON value.");
        }
    }

    private static void String(string value, StringBuilder builder)
    {
        _ = Utf8.GetByteCount(value);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (character < 32 || character >= 127)
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(character);
                    break;
            }
        }
        builder.Append('"');
    }

    internal static void Fields(JsonElement value, params string[] names) => Require(
        value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(names),
        "Missing or unexpected collection fields.");
    internal static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()
        ?? throw new InvalidDataException($"Missing collection text: {name}.");
    internal static void Equal(JsonElement value, string name, string expected) => Require(Text(value, name) == expected, $"Collection {name} mismatch.");
    internal static int Number(JsonElement value, string name, int minimum, int maximum)
    {
        var number = value.GetProperty(name).GetInt32();
        Require(number >= minimum && number <= maximum, $"Collection {name} exceeds bounds.");
        return number;
    }
    internal static NewsPageRequest Request(JsonElement request)
    {
        Fields(request, "symbol", "start_utc", "end_utc", "page_token", "include_content", "limit");
        var symbol = Text(request, "symbol");
        Require(Matches(symbol, "[A-Z][A-Z0-9.-]{0,14}"), "Invalid collection symbol.");
        var start = Clock(Text(request, "start_utc"));
        var end = Clock(Text(request, "end_utc"));
        Require(start < end, "Invalid collection time range.");
        var token = request.GetProperty("page_token").GetString();
        Require(token is null || Matches(token, "[!-~]{1,2048}"), "Invalid collection page token.");
        return new NewsPageRequest(symbol, start, end, token, request.GetProperty("include_content").GetBoolean(), Number(request, "limit", 1, 50));
    }
    internal static DateTimeOffset Clock(string text)
    {
        Require(Matches(text, "[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{6}Z"), "Collection clock must use UTC microseconds.");
        return DateTimeOffset.ParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }
}
