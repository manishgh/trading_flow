namespace TradingFlow.Mobile.Services;

/// <summary>One calendar row as the EPS-beat alert rule sees it.</summary>
public sealed record EarningsBeatCandidate(string EventId, bool IsEpsBeat, DateTimeOffset? FirstResultSignUtc);

/// <summary>
/// Decides which EPS beats raise a notification; kept free of device APIs so it can be tested. Each event
/// alerts once. With no stored history (a new install or backend), the first read only records the beats
/// already listed. After that, a beat whose result was first seen within <see cref="AlertWindow"/> alerts,
/// even if it arrived while the app was closed; older ones are recorded without alerting.
/// </summary>
public static class EarningsBeatAlertRule
{
    public static readonly TimeSpan AlertWindow = TimeSpan.FromHours(2);

    /// <summary>
    /// Adds every new beat to <paramref name="history"/> (oldest first) and returns the event ids to notify.
    /// </summary>
    public static IReadOnlyList<string> SelectAlerts(
        IEnumerable<EarningsBeatCandidate> candidates,
        List<string> history,
        bool historyExisted,
        DateTimeOffset nowUtc)
    {
        var alerts = new List<string>();
        foreach (var candidate in candidates.Where(item => item.IsEpsBeat && !history.Contains(item.EventId)))
        {
            history.Add(candidate.EventId);
            if (historyExisted && candidate.FirstResultSignUtc is { } seen && nowUtc - seen <= AlertWindow)
            {
                alerts.Add(candidate.EventId);
            }
        }

        return alerts;
    }
}
