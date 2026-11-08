using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingFlow.Contracts.Preparation;
using TradingFlow.Domain.Preparation;
namespace TradingFlow.Application.Preparation;

public static class PreparationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidOperationException("Missing preparation data.");
    public static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Write(value))));
    public static PreparationDefinition View(PreparationDefinitionRecord row) => new(row.WorkflowId, row.Revision, row.ContentSha256,
        new(row.EffectiveTicks, TimeSpan.Zero), Read<PreparationSettings>(row.SettingsJson));
}
public interface IPreparationCalendar { Task<PreparationCalendar> LoadAsync(DateOnly start, DateOnly end, CancellationToken token); }
public interface IPreparationAuthorization { Task<string?> StampAsync(string ownerId, CancellationToken token); }
public interface IPreparationEvidence
{
    Task<PreparationSymbol> CollectAsync(string symbol, PreparationSlot slot, PreparationSettings settings, DateTimeOffset cutoff, CancellationToken token);
}
public static class PreparationPlanning
{
    public static void Validate(PreparationSettings s)
    {
        if (s is null || String.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 120 || s.Calendar != "us-equities" || s.TimeZone != "America/New_York")
            throw new InvalidOperationException("Choose a name and the supported US equity exchange calendar.");
        if (s.Sources is null || s.Sources.Length is < 1 or > 20 || s.Sources.Any(x => x is null || x.Kind is not ("wishlist" or "finviz") || String.IsNullOrWhiteSpace(x.Key) || x.Key.Length > 500 || x.Query?.Length > 2000 || x.Kind == "wishlist" && !Guid.TryParse((x.WishlistId?.ToString() ?? x.Key), out _)) || s.Sources.Select(x => (x.Kind,x.Key)).Distinct().Count() != s.Sources.Length)
            throw new InvalidOperationException("Select 1 to 20 distinct wishlist or Finviz sources.");
        if (s.Merge is not ("union" or "intersection") || s.NightlyAfterCloseMinutes is < 1 or > 240 ||
            s.MorningStartBeforeOpenMinutes is < 30 or > 240 || s.PublicationBeforeOpenMinutes < 1 ||
            s.PublicationBeforeOpenMinutes >= s.MorningStartBeforeOpenMinutes || s.HandoffBeforeOpenMinutes < 0 ||
            s.HandoffBeforeOpenMinutes >= s.PublicationBeforeOpenMinutes || s.RefreshMinutes is < 1 or > 120 ||
            s.CatchUpMinutes is < 0 or > 120 || s.MaximumSourceAgeMinutes is < 1 or > 60 || s.SnapshotLifetimeMinutes is < 1 or > 60 ||
            s.MaximumSymbols is < 1 or > 500 || s.ParallelSymbols is < 1 or > 4 || s.HistorySessions is < 20 or > 252 ||
            s.MaximumAttempts is < 1 or > 5 || s.RetrySeconds is < 1 or > 300 || s.CollectionTimeoutSeconds is < 10 or > 600)
            throw new InvalidOperationException("Schedule, freshness or resource settings exceed the supported bounds.");
        if (s.Filters is null || s.Filters.MinimumPrice is <= 0 or > 1000000 || s.Filters.MinimumAverageShares is <= 0 or > 1000000000000 ||
            s.Filters.MinimumAverageDollarVolume is <= 0 or > 1000000000000000 || s.Filters.TrendAverageDays is < 2 || s.Filters.TrendAverageDays > s.HistorySessions ||
            s.Filters.MaximumRangePercent is <= 0 or > 1000 || s.GapAnnotationPercent is <= 0 or > 1000 || s.RelativeVolumeAnnotation is <= 0 or > 1000 || s.EarningsWithinDays is < 0 or > 30)
            throw new InvalidOperationException("Discovery filter values or context thresholds are invalid.");
    }
    public static PreparationSlot[] Plan(PreparationSettings s, PreparationCalendar calendar, DateTimeOffset now, DateTimeOffset effective)
    {
        Validate(s);
        if (calendar.RetrievedAtUtc > now || now - calendar.RetrievedAtUtc > TimeSpan.FromHours(6) || String.IsNullOrWhiteSpace(calendar.Provider))
            throw new InvalidOperationException("Exchange calendar is unavailable or stale.");
        var all = calendar.Sessions.OrderBy(x => x.Date).ToArray();
        if (all.Length < 2 || all.Select(x => x.Date).Distinct().Count() != all.Length || all.Zip(all.Skip(1)).Any(x => x.Second.Date != x.First.Date.AddDays(1)) ||
            all.Any(x => x.TradingDay && (x.OpenUtc.Offset != TimeSpan.Zero || x.CloseUtc <= x.OpenUtc || x.CloseUtc.Offset != TimeSpan.Zero)))
            throw new InvalidOperationException("Exchange calendar coverage is incomplete.");
        var sessions = all.Where(x => x.TradingDay).ToArray();
        var result = new List<PreparationSlot>();
        for (var i = 1; i < sessions.Length; i++)
        {
            var target = sessions[i]; var previous = sessions[i-1];
            var start = target.OpenUtc.AddMinutes(-s.MorningStartBeforeOpenMinutes);
            var cutoff = target.OpenUtc.AddMinutes(-s.PublicationBeforeOpenMinutes);
            var use = target.OpenUtc.AddMinutes(-s.HandoffBeforeOpenMinutes);
            var night = previous.CloseUtc.AddMinutes(s.NightlyAfterCloseMinutes);
            if (night >= start) throw new InvalidOperationException("Nightly preparation overlaps morning review.");
            if (night >= effective) result.Add(new(target.Date, "nightly", night, start, use, previous, calendar));
            // The cutoff is exclusive; the final slot must have time to publish before it.
            for (var at = start; at < cutoff; at = at.AddMinutes(s.RefreshMinutes))
                if (at >= effective) result.Add(new(target.Date,"morning",at,cutoff,use,previous,calendar));
        }
        return result.OrderBy(x => x.ScheduledAtUtc).ToArray();
    }
}
