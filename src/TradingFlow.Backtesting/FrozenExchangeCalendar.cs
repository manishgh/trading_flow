using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace TradingFlow.Backtesting;

public sealed record FrozenExchangeSession(DateOnly Date, DateTimeOffset OpenUtc, DateTimeOffset CloseUtc);

/// <summary>Reads the existing Alpaca calendar capture/receipt format; weekdays are never used to invent sessions.</summary>
public sealed record FrozenExchangeCalendar(DateOnly From, DateOnly Through, string PayloadPath,
    string PayloadSha256, IReadOnlyList<FrozenExchangeSession> Sessions)
{
    /// <summary>Verifies receipt and payload bytes, declared capture range and actual exchange-session boundaries.</summary>
    public static FrozenExchangeCalendar Read(string receiptPath, string receiptSha256)
    {
        if (!Path.IsPathFullyQualified(receiptPath) || !receiptPath.EndsWith(".receipt.json", StringComparison.Ordinal))
            throw new InvalidDataException("A frozen calendar requires an absolute captured .receipt.json path.");
        try
        {
            using var receipt = ReadVerified(receiptPath, receiptSha256);
            var metadata = receipt.RootElement;
            if (metadata.GetProperty("HttpStatus").GetInt32() != 200 || metadata.GetProperty("OriginalHistoricalReceiptClaim").GetBoolean() ||
                metadata.GetProperty("CapturedUtc").GetDateTimeOffset().Offset != TimeSpan.Zero)
                throw new InvalidDataException("Calendar capture must be successful, UTC and explicit about missing original historical receipts.");
            var from = DateOnly.ParseExact(metadata.GetProperty("Start").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var through = DateOnly.ParseExact(metadata.GetProperty("End").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var payloadPath = receiptPath[..^".receipt.json".Length] + ".json";
            var payloadHash = metadata.GetProperty("Sha256").GetString()!;
            using var payload = ReadVerified(payloadPath, payloadHash);
            if (through < from || payload.RootElement.ValueKind != JsonValueKind.Array ||
                payload.RootElement.GetArrayLength() != metadata.GetProperty("RowCount").GetInt32())
                throw new InvalidDataException("Calendar range or captured row count is invalid.");
            var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var sessions = payload.RootElement.EnumerateArray().Select(row =>
            {
                var date = DateOnly.ParseExact(row.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var open = SessionTime(date, row.GetProperty("open").GetString()!, zone);
                var close = SessionTime(date, row.GetProperty("close").GetString()!, zone);
                if (date < from || date > through || close <= open)
                    throw new InvalidDataException("A captured exchange session exceeds its range or has invalid boundaries.");
                return new FrozenExchangeSession(date, open, close);
            }).OrderBy(session => session.Date).ToArray();
            if (sessions.Select(session => session.Date).Distinct().Count() != sessions.Length)
                throw new InvalidDataException("Captured exchange calendar contains duplicate sessions.");
            return new(from, through, payloadPath, payloadHash, sessions);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or FormatException or ArgumentException)
        { throw new InvalidDataException("Captured calendar metadata or session rows are malformed.", error); }
    }

    /// <summary>Requires every declared exchange session intersecting a holding interval, including intermediate days.</summary>
    public IReadOnlyList<FrozenExchangeSession> RequiredSessions(DateTimeOffset entry, DateTimeOffset end)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(entry, zone).DateTime);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(end.AddTicks(-1), zone).DateTime);
        if (end <= entry || first < From || last > Through)
            throw new InvalidDataException("Frozen exchange calendar does not cover the complete candidate execution interval.");
        return Sessions.Where(session => session.CloseUtc > entry && session.OpenUtc < end).ToArray();
    }

    /// <summary>Converts each actual session boundary using the exchange date's applicable UTC offset.</summary>
    private static DateTimeOffset SessionTime(DateOnly date, string time, TimeZoneInfo zone)
    {
        var local = DateTime.ParseExact(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + time,
            "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone));
    }

    /// <summary>Verifies exact immutable input bytes while preventing ordinary replacement during parsing.</summary>
    private static JsonDocument ReadVerified(string path, string expected)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (expected is not { Length: 64 } || !Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Frozen exchange calendar content differs from its recorded SHA-256.");
        stream.Position = 0;
        return JsonDocument.Parse(stream);
    }
}
