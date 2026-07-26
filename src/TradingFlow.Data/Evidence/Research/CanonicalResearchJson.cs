using System.Globalization;
using System.Text.Json;

namespace TradingFlow.Data.Evidence.Research;

internal static class CanonicalResearchJson
{
    public static byte[] Canonicalize(ReadOnlyMemory<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(
            utf8Json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128
            });
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Indented = false,
                       SkipValidation = false
                   }))
        {
            WriteElement(writer, document.RootElement);
        }

        return stream.ToArray();
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(writer, element);
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item);
                }

                writer.WriteEndArray();
                return;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                return;
            case JsonValueKind.Number:
                WriteNumber(writer, element);
                return;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                return;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                return;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                return;
            default:
                throw new JsonException(
                    $"Unsupported JSON value kind '{element.ValueKind}'.");
        }
    }

    private static void WriteObject(Utf8JsonWriter writer, JsonElement element)
    {
        var properties = element.EnumerateObject().ToArray();
        var duplicate = properties
            .GroupBy(property => property.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new JsonException(
                $"Canonical research JSON cannot contain duplicate property '{duplicate.Key}'.");
        }

        writer.WriteStartObject();
        foreach (var property in properties.OrderBy(
                     property => property.Name,
                     StringComparer.Ordinal))
        {
            writer.WritePropertyName(property.Name);
            WriteElement(writer, property.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteNumber(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.TryGetInt64(out var signed))
        {
            writer.WriteNumberValue(signed);
            return;
        }

        if (element.TryGetUInt64(out var unsigned))
        {
            writer.WriteNumberValue(unsigned);
            return;
        }

        if (element.TryGetDecimal(out var decimalValue))
        {
            var canonicalDecimal = decimalValue == 0m
                ? "0"
                : NormalizeExponent(decimalValue.ToString("G29", CultureInfo.InvariantCulture));
            writer.WriteRawValue(canonicalDecimal, skipInputValidation: false);
            return;
        }

        var doubleValue = element.GetDouble();
        if (!Double.IsFinite(doubleValue))
        {
            throw new JsonException("Canonical research JSON requires finite numeric values.");
        }

        var canonicalDouble = doubleValue == 0d
            ? "0"
            : NormalizeExponent(doubleValue.ToString("R", CultureInfo.InvariantCulture));
        writer.WriteRawValue(canonicalDouble, skipInputValidation: false);
    }

    private static string NormalizeExponent(string value)
    {
        var exponentIndex = value.IndexOfAny(['E', 'e']);
        if (exponentIndex < 0)
        {
            return value;
        }

        var mantissa = value[..exponentIndex];
        var exponent = value[(exponentIndex + 1)..];
        var negative = exponent.StartsWith("-", StringComparison.Ordinal);
        exponent = exponent.TrimStart('+', '-').TrimStart('0');
        if (exponent.Length == 0)
        {
            exponent = "0";
        }

        return $"{mantissa}e{(negative ? "-" : String.Empty)}{exponent}";
    }
}
