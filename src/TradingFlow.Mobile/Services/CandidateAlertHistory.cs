using System.Text.Json;

namespace TradingFlow.Mobile.Services;

public sealed record CandidateAlert(DateTimeOffset Timestamp, string Backend, string Symbol, string Message, CandidateLink Link);
public static class CandidateAlertHistory
{
    private static string StorageKey => "CandidateAlertHistory:"+AppServices.Api.BaseUrl+":"+AppServices.Api.CurrentUserName;
    public static IReadOnlyList<CandidateAlert> Read()
    {
        try { return JsonSerializer.Deserialize<CandidateAlert[]>(Preferences.Get(StorageKey, "[]")) ?? []; }
        catch (JsonException) { return []; }
    }
    public static void Add(string symbol, string message, CandidateLink link)
    {
        var item = new CandidateAlert(DateTimeOffset.UtcNow, AppServices.Api.BaseUrl, symbol, message, link);
        Preferences.Set(StorageKey, JsonSerializer.Serialize(new[] { item }.Concat(Read())
            .DistinctBy(alert => (alert.Backend, alert.Link.CandidateId)).Take(50)));
    }
}
