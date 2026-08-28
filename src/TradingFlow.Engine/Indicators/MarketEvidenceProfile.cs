using System.Collections.ObjectModel;

namespace TradingFlow.Engine.Indicators;

/// <summary>
/// Versioned rules for market-participation evidence. The profile is immutable so
/// a backtest, paper run, and replay can record the exact RVOL contract they used.
/// </summary>
public sealed record MarketEvidenceProfile
{
    public const string ProductionVersion = "us_equities_same_time_rvol_v1";
    public const int DefaultLookbackSessions = 20;
    public const int DefaultMinimumValidSamples = 20;

    public static MarketEvidenceProfile ProductionDefault { get; } = new(
        ProductionVersion,
        DefaultLookbackSessions,
        DefaultMinimumValidSamples,
        "America/New_York",
        allowStandardWeekdayFallback: false);

    public MarketEvidenceProfile(
        string version,
        int lookbackSessions,
        int minimumValidSamples,
        string exchangeTimeZone,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? sessionSchedules = null,
        MarketEvidenceOperationalRules? operationalRules = null,
        bool allowStandardWeekdayFallback = true)
    {
        if (String.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("Market-evidence profile version is required.", nameof(version));
        }

        if (lookbackSessions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lookbackSessions));
        }

        if (minimumValidSamples <= 0 || minimumValidSamples > lookbackSessions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumValidSamples),
                "Minimum valid samples must be positive and no greater than the lookback.");
        }

        if (String.IsNullOrWhiteSpace(exchangeTimeZone))
        {
            throw new ArgumentException("Exchange time zone is required.", nameof(exchangeTimeZone));
        }

        Version = version.Trim();
        LookbackSessions = lookbackSessions;
        MinimumValidSamples = minimumValidSamples;
        ExchangeTimeZone = exchangeTimeZone.Trim();
        OperationalRules = operationalRules ?? MarketEvidenceOperationalRules.Production;
        AllowStandardWeekdayFallback = allowStandardWeekdayFallback;
        OperationalRules.Validate();
        SessionSchedules = new ReadOnlyDictionary<DateOnly, MarketSessionSchedule>(
            new Dictionary<DateOnly, MarketSessionSchedule>(sessionSchedules ??
                new Dictionary<DateOnly, MarketSessionSchedule>()));
    }

    public string Version { get; }

    public int LookbackSessions { get; }

    public int MinimumValidSamples { get; }

    public string ExchangeTimeZone { get; }

    public MarketEvidenceOperationalRules OperationalRules { get; }

    public bool AllowStandardWeekdayFallback { get; }

    /// <summary>
    /// Complete provider calendar used by production evidence. A standard weekday
    /// fallback is available only when a controlled test/research profile opts in.
    /// </summary>
    public IReadOnlyDictionary<DateOnly, MarketSessionSchedule> SessionSchedules { get; }

    public bool TryResolveSchedule(DateOnly tradeDate, out MarketSessionSchedule schedule)
    {
        if (SessionSchedules.TryGetValue(tradeDate, out var configured))
        {
            schedule = configured;
            return true;
        }

        if (!AllowStandardWeekdayFallback)
        {
            schedule = MarketSessionSchedule.Closed(tradeDate);
            return false;
        }

        var dayOfWeek = tradeDate.DayOfWeek;
        schedule = dayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
            ? MarketSessionSchedule.Closed(tradeDate)
            : MarketSessionSchedule.RegularDay(tradeDate);
        return true;
    }

    public MarketSessionSchedule ResolveSchedule(DateOnly tradeDate) =>
        TryResolveSchedule(tradeDate, out var schedule)
            ? schedule
            : throw new InvalidOperationException(
                $"Authoritative exchange calendar is unavailable for {tradeDate:yyyy-MM-dd}.");

    public MarketEvidenceProfile WithSessionSchedules(
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> sessionSchedules) =>
        new(
            Version,
            LookbackSessions,
            MinimumValidSamples,
            ExchangeTimeZone,
            sessionSchedules,
            OperationalRules,
            allowStandardWeekdayFallback: false);
}

/// <summary>
/// Frozen non-mathematical evidence rules that must match between REST warm-up,
/// stream maintenance, replay, paper trading, and backtests.
/// </summary>
public sealed record MarketEvidenceOperationalRules(
    string RequiredDataFeed,
    string RequiredAdjustmentPolicy,
    string BaselineStatistic,
    string PartialBarPolicy,
    string ConfirmedNoTradePolicy,
    string ExtendedHoursCohortPolicy,
    int RevisionAcceptanceMinutes,
    int RecoveryLookbackDays)
{
    public static MarketEvidenceOperationalRules Production { get; } = new(
        "sip",
        "all",
        "median",
        "completed_bars_only",
        "confirmed_no_trade_only",
        "isolated_by_market_session",
        2,
        45);

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RequiredDataFeed);
        ArgumentException.ThrowIfNullOrWhiteSpace(RequiredAdjustmentPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(BaselineStatistic);
        ArgumentException.ThrowIfNullOrWhiteSpace(PartialBarPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(ConfirmedNoTradePolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(ExtendedHoursCohortPolicy);
        if (RevisionAcceptanceMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RevisionAcceptanceMinutes));
        }

        if (RecoveryLookbackDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RecoveryLookbackDays));
        }
    }
}

public sealed record MarketSessionSchedule(
    DateOnly TradeDate,
    bool IsTradingDay,
    TimeOnly RegularOpen,
    TimeOnly RegularClose)
{
    public static MarketSessionSchedule RegularDay(DateOnly tradeDate) =>
        new(tradeDate, true, new TimeOnly(9, 30), new TimeOnly(16, 0));

    public static MarketSessionSchedule Closed(DateOnly tradeDate) =>
        new(tradeDate, false, default, default);
}

public enum MarketVolumeCohort
{
    Overnight,
    Premarket,
    Regular,
    Postmarket,
    Daily
}
