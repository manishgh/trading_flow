using TradingFlow.Engine.Regime;

namespace TradingFlow.Engine.Risk;

/// <summary>
/// Calculates accrued annualized short-borrow fees for closed short positions in backtest portfolio accounting:
/// <c>entryNotional * (shortBorrowAprPct / 100) * max(1, heldCalendarDays) / 365</c>. Calendar days are measured
/// on New York exchange dates so same-day and overnight shorts always accrue at least one calendar day.
/// </summary>
public static class ShortBorrowCostCalculator
{
    /// <summary>
    /// Returns the short-borrow fee rounded to 4 decimal places for a closed short held from
    /// <paramref name="entrySessionDate"/> to <paramref name="exitSessionDate"/>, or 0 when disabled or non-positive.
    /// </summary>
    public static decimal Calculate(
        decimal entryNotional,
        decimal? shortBorrowAprPct,
        DateOnly entrySessionDate,
        DateOnly exitSessionDate)
    {
        if (entryNotional <= 0m || shortBorrowAprPct is not > 0m)
        {
            return 0m;
        }

        var heldCalendarDays = Math.Max(1, exitSessionDate.DayNumber - entrySessionDate.DayNumber);
        return Decimal.Round(
            entryNotional * (shortBorrowAprPct.Value / 100m) * heldCalendarDays / 365m,
            4,
            MidpointRounding.ToEven);
    }

    /// <summary>
    /// Returns the short-borrow fee rounded to 4 decimal places by projecting UTC entry/exit timestamps onto
    /// New York calendar dates, or 0 when disabled or non-positive.
    /// </summary>
    public static decimal Calculate(
        decimal entryNotional,
        decimal? shortBorrowAprPct,
        DateTimeOffset entryTimestampUtc,
        DateTimeOffset exitTimestampUtc)
    {
        if (entryNotional <= 0m || shortBorrowAprPct is not > 0m)
        {
            return 0m;
        }

        var zone = VixRegime.NewYorkTimeZone;
        var entryDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(entryTimestampUtc, zone).DateTime);
        var exitDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(exitTimestampUtc, zone).DateTime);
        return Calculate(entryNotional, shortBorrowAprPct, entryDate, exitDate);
    }
}
