using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Web.Services.Charts;

internal static class ChartInputRevision
{
    private static readonly ConditionalWeakTable<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>, Lazy<string>> Calendars = new();

    // Hash the effective New York exchange windows once per immutable source calendar.
    public static string Calendar(IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules) =>
        Calendars.GetValue(schedules, static calendar => new Lazy<string>(() => Digest(writer =>
        {
            writer.Write(calendar.Count);
            foreach (var (day, schedule) in calendar.OrderBy(pair => pair.Key))
            {
                writer.Write(day.DayNumber);
                writer.Write(schedule.IsTradingDay);
                if (!schedule.IsTradingDay) continue;
                writer.Write(ChartBarProjector.At(day, schedule.RegularOpen).UtcTicks);
                writer.Write(ChartBarProjector.At(day, schedule.RegularClose).UtcTicks);
            }
        }))).Value;

    // Price, volume, availability, quiet proof and basis seams all participate in derived invalidation.
    public static string History(IReadOnlyList<DateOnly> sessions,
        IReadOnlyDictionary<DateTimeOffset, ChartSourceBar> bars, IReadOnlyDictionary<DateTimeOffset, DateTimeOffset> quiet,
        IReadOnlySet<DateTimeOffset> breaks, IReadOnlySet<DateOnly>? dailyConflicts = null) => Digest(writer =>
    {
        writer.Write(sessions.Count);
        foreach (var day in sessions) writer.Write(day.DayNumber);
        writer.Write(bars.Count);
        foreach (var entry in bars.OrderBy(item => item.Key))
        {
            WriteBar(writer, entry.Value.Bar);
            writer.Write(entry.Value.AvailableAtUtc.UtcTicks);
            writer.Write(entry.Value.RetrievedAtUtc?.UtcTicks ?? 0);
            writer.Write(entry.Value.SourceKind);
        }
        writer.Write(quiet.Count);
        foreach (var entry in quiet.OrderBy(item => item.Key))
        {
            writer.Write(entry.Key.UtcTicks);
            writer.Write(entry.Value.UtcTicks);
        }
        writer.Write(breaks.Count);
        foreach (var stamp in breaks.Order()) writer.Write(stamp.UtcTicks);
        writer.Write(dailyConflicts?.Count ?? 0);
        if (dailyConflicts is not null)
            foreach (var date in dailyConflicts.Order()) writer.Write(date.DayNumber);
    });

    // An accepted canonical correction invalidates chart output even if an archive remains unchanged.
    public static string Canonical(ChartCanonicalSession? canonical) => canonical is null ? "-" : Digest(writer =>
    {
        writer.Write(canonical.OwnershipFence);
        writer.Write(canonical.SubscriptionGeneration);
        writer.Write(canonical.NoTradeProofAtUtc?.UtcTicks ?? 0);
        writer.Write(canonical.Bars.Count);
        foreach (var bar in canonical.Bars) WriteBar(writer, bar);
        writer.Write(canonical.NoTradeMinutes.Count);
        foreach (var minute in canonical.NoTradeMinutes.Order()) writer.Write(minute.UtcTicks);
    });

    // Compress the joined canonical source identities to one bounded wire revision.
    public static string Combine(IEnumerable<string> parts) => Digest(writer =>
    {
        foreach (var part in parts) writer.Write(part);
    });

    // Hash only deterministic binary source fields; display rounding never participates in identity.
    private static void WriteBar(BinaryWriter writer, OhlcvBar bar)
    {
        writer.Write(bar.Timestamp.UtcTicks);
        writer.Write(bar.Open);
        writer.Write(bar.High);
        writer.Write(bar.Low);
        writer.Write(bar.Close);
        writer.Write(bar.Volume);
        writer.Write(bar.KnownAtUtc?.UtcTicks ?? 0);
    }

    // Stream the content into SHA-256 without retaining a second serialized history window.
    private static string Digest(Action<BinaryWriter> write)
    {
        using var sha = SHA256.Create();
        using (var target = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
        using (var writer = new BinaryWriter(target))
            write(writer);
        return Convert.ToHexString(sha.Hash!);
    }
}
