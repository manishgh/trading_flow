using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;

namespace TradingFlow.Web.Services.Charts;

/// <summary>Uses the same provider schedule and bucket boundaries as completed chart candles.</summary>
internal sealed class SipFormingCalendar(IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules)
{
    private readonly IReadOnlyDictionary<DateOnly, MarketSessionSchedule> sessions = schedules;
    private readonly BarResampler resampler = new(MarketEvidenceProfile.ProductionDefault.WithSessionSchedules(schedules));

    // A verified schedule can be closed; a missing or inconsistent record cannot prove closure.
    public bool TryReadWindow(DateTimeOffset at, string interval, out BarResampler.BucketWindow? window)
    {
        window = null;
        var date = ChartBarProjector.NewYorkDate(at);
        if (!sessions.TryGetValue(date, out var schedule) || schedule.TradeDate != date ||
            (schedule.IsTradingDay && schedule.RegularOpen >= schedule.RegularClose)) return false;

        if (schedule.IsTradingDay && at >= ChartBarProjector.At(date, new TimeOnly(4, 0)) &&
            at < ChartBarProjector.At(date, new TimeOnly(20, 0)))
            window = resampler.GetBucketWindow(at, interval);
        return true;
    }

    // A refresh for the same effective boundaries leaves an already complete minute eligible.
    public bool SameSession(SipFormingCalendar other, DateTimeOffset now)
    {
        var date = ChartBarProjector.NewYorkDate(now);
        return sessions.TryGetValue(date, out var one) && other.sessions.TryGetValue(date, out var two) && one == two;
    }
}
