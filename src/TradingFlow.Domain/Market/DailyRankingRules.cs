namespace TradingFlow.Domain.Market;

/// <summary>How a volume baseline averages its earlier sessions.</summary>
public enum VolumeBaselineAverage
{
    /// <summary>Arithmetic mean, the rule of Zarattini, Barbon and Aziz (SSRN 4729284).</summary>
    Mean,

    /// <summary>Middle value (mean of the two middle values for an even count), the production relative-volume rule.</summary>
    Median
}

/// <summary>
/// Frozen per-session ranking of the stocks that passed the daily eligibility rules. The only measure today is the
/// first five minutes' share volume (09:30-09:35 New York) divided by the average of the same interval over the
/// previous <see cref="BaselineSessions"/> sessions; it is known at 09:35. Stocks below the minimum are dropped and the
/// highest <see cref="MaxMembersPerSession"/> become that session's members; ties go to the lower issuer identifier.
/// </summary>
public sealed record DailyRankingRules(
    string Measure = DailyRankingRules.FirstFiveMinuteRelativeVolume,
    int BaselineSessions = 14,
    VolumeBaselineAverage BaselineAverage = VolumeBaselineAverage.Mean,
    decimal MinRelativeVolume = 1m,
    int MaxMembersPerSession = 20)
{
    /// <summary>First five-minute volume over the average of the same interval in earlier sessions.</summary>
    public const string FirstFiveMinuteRelativeVolume = "first_five_minute_relative_volume";

    /// <summary>Length of the ranked opening interval in minutes; one five-minute bar.</summary>
    public const int OpeningMinutes = 5;

    /// <summary>Calendar days of intraday bars needed before the first decision day so <see cref="BaselineSessions"/> sessions exist.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int BaselineFetchCalendarDays => checked(BaselineSessions * 3 + 15);

    /// <summary>
    /// Fails on a measure the engine cannot compute or on limits that could select nothing meaningful. The minimum relative
    /// volume must be above zero: a session whose first five minutes had no trade (proven zero, which a backtest proves from
    /// bars after 09:35) has relative volume zero and must never qualify.
    /// </summary>
    public void Validate()
    {
        if (Measure != FirstFiveMinuteRelativeVolume)
            throw new ArgumentException($"Unknown daily ranking measure '{Measure}'. Use {FirstFiveMinuteRelativeVolume}.");
        if (BaselineSessions < 1 || MinRelativeVolume <= 0m || MaxMembersPerSession < 1)
            throw new ArgumentException("Daily ranking needs at least one baseline session, a minimum relative volume above zero and at least one member per session.");
    }

    /// <summary>Reads the settings-file spelling of a baseline average; unknown spellings fail instead of defaulting.</summary>
    public static VolumeBaselineAverage ParseAverage(string value) => value.Trim().ToLowerInvariant() switch
    {
        "mean" => VolumeBaselineAverage.Mean,
        "median" => VolumeBaselineAverage.Median,
        var other => throw new ArgumentException($"Unknown baseline average '{other}'. Use mean or median.")
    };

    /// <summary>The settings-file spelling of a baseline average, the inverse of <see cref="ParseAverage"/>.</summary>
    public static string AverageName(VolumeBaselineAverage average) =>
        average == VolumeBaselineAverage.Median ? "median" : "mean";
}
