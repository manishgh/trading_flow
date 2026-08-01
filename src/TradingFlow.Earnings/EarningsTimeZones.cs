namespace TradingFlow.Earnings;

public static class EarningsTimeZones
{
    public static TimeZoneInfo NewYork { get; } = Resolve("America/New_York", "Eastern Standard Time");

    public static string NewYorkClientId { get; } = ResolveClientId(NewYork);

    /// <summary>
    /// Resolves the timezone used for operator-facing calendar boundaries. A deployment can
    /// set TRADINGFLOW_OPERATOR_TIME_ZONE; local development follows the host timezone.
    /// Persisted and transported timestamps remain UTC regardless of this setting.
    /// </summary>
    public static TimeZoneInfo OperatorLocal { get; } = ResolveOperatorLocal();

    /// <summary>
    /// Classifies a US-equity bar by its New York session. The timestamp is the
    /// start of the bar and all boundaries are evaluated with date-specific DST.
    /// </summary>
    public static string ClassifyUsEquitySession(DateTimeOffset timestampUtc)
    {
        var newYork = TimeZoneInfo.ConvertTime(timestampUtc, NewYork);
        if (newYork.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return "Market closed";
        }

        var time = TimeOnly.FromDateTime(newYork.DateTime);
        if (time >= new TimeOnly(4, 0) && time < new TimeOnly(9, 30))
        {
            return "Premarket";
        }

        if (time >= new TimeOnly(9, 30) && time < new TimeOnly(16, 0))
        {
            return "Regular market";
        }

        if (time >= new TimeOnly(16, 0) && time < new TimeOnly(20, 0))
        {
            return "Post-market";
        }

        return "Overnight";
    }

    private static TimeZoneInfo Resolve(params string[] ids)
    {
        foreach (var id in ids)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        throw new InvalidOperationException($"None of the required time zones are available: {String.Join(", ", ids)}.");
    }

    private static TimeZoneInfo ResolveOperatorLocal()
    {
        var configuredId = Environment.GetEnvironmentVariable("TRADINGFLOW_OPERATOR_TIME_ZONE");
        if (String.IsNullOrWhiteSpace(configuredId))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(configuredId.Trim());
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException(
                $"TRADINGFLOW_OPERATOR_TIME_ZONE '{configuredId}' is not a valid system timezone.",
                exception);
        }
    }

    private static string ResolveClientId(TimeZoneInfo timeZone)
    {
        if (timeZone.Id.Contains('/'))
        {
            return timeZone.Id;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZone.Id, out var ianaId)
            ? ianaId
            : timeZone.Id;
    }
}
