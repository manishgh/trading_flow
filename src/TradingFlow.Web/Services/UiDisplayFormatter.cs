using System.Globalization;

namespace TradingFlow.Web.Services;

public static class UiDisplayFormatter
{
    public static TimeZoneInfo LocalTradingTimeZone { get; } = ResolveLocalTradingTimeZone();

    public static string LocalTradingTimeZoneLabel => LocalTradingTimeZone.Id;

    public static string FormatLocal(DateTimeOffset timestamp, bool includeMilliseconds = false)
    {
        var local = TimeZoneInfo.ConvertTime(timestamp, LocalTradingTimeZone);
        var format = includeMilliseconds
            ? "yyyy-MM-dd HH:mm:ss.fff"
            : "yyyy-MM-dd HH:mm:ss";
        return local.ToString(format, CultureInfo.InvariantCulture);
    }

    public static string FormatLocalTime(DateTimeOffset timestamp)
    {
        return TimeZoneInfo
            .ConvertTime(timestamp, LocalTradingTimeZone)
            .ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static string FormatLocalDate(DateTimeOffset timestamp)
    {
        return TimeZoneInfo
            .ConvertTime(timestamp, LocalTradingTimeZone)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public static string FormatRejectionReason(string? reason)
    {
        if (String.IsNullOrWhiteSpace(reason))
        {
            return "-";
        }

        var trimmed = reason.Trim();
        var normalized = NormalizeRejectionReasonKey(trimmed);
        var display = normalized
            .Trim()
            .Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Capitalize)
            .Aggregate((left, right) => $"{left} {right}");

        var detailsStart = trimmed.IndexOf(" (", StringComparison.Ordinal);
        return detailsStart > 0
            ? $"{display}{trimmed[detailsStart..]}"
            : display;
    }

    public static string NormalizeRejectionReasonKey(string? reason)
    {
        if (String.IsNullOrWhiteSpace(reason))
        {
            return String.Empty;
        }

        var trimmed = reason.Trim();
        var detailsStart = trimmed.IndexOf(" (", StringComparison.Ordinal);
        return detailsStart > 0 ? trimmed[..detailsStart] : trimmed;
    }

    private static string Capitalize(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        return value.Length == 1
            ? value.ToUpperInvariant()
            : Char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
    }

    private static TimeZoneInfo ResolveLocalTradingTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }
}
