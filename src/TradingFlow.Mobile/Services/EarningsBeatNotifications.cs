using System.Text.Json;

namespace TradingFlow.Mobile.Services;

/// <summary>
/// Raises an Android notification the first time an earnings event shows an EPS beat. It reads the same
/// today-and-next-business-day calendar as the Earnings page every 30 seconds while the app is open, using the
/// page's unfiltered view. Each event alerts once per backend. When the app has no alert history yet, the first
/// read records the beats already listed without alerting, so installing or signing in does not raise a burst;
/// after that, a beat whose result was first seen in the last two hours alerts even if it arrived while the
/// app was closed. The server stays the source of truth; this only surfaces what it already reports.
/// </summary>
public static class EarningsBeatNotifications
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private const int MaximumRemembered = 500;
    private static CancellationTokenSource? lifetime;

    // Event ids already alerted or recorded, per backend so two servers do not share one history.
    private static string HistoryKey(string backend) => "EarningsBeatAlerts:" + backend;

    /// <summary>Starts the polling loop once; later calls while it runs do nothing.</summary>
    public static void Start()
    {
        if (lifetime is not null)
        {
            return;
        }

        lifetime = new CancellationTokenSource();
        _ = PollAsync(lifetime.Token);
    }

    /// <summary>Stops the polling loop, for example when the app goes to the background.</summary>
    public static void Stop()
    {
        lifetime?.Cancel();
        lifetime?.Dispose();
        lifetime = null;
    }

    /// <summary>Reads the calendar, alerts new beats, then waits; a failed read is retried on the next turn.</summary>
    private static async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // The backend is read before the request: a switch while it runs must not file one server's beats
                // under the other's history.
                var backend = AppServices.Api.BaseUrl;
                var calendar = await AppServices.Api.GetTodayAndNextBusinessDayEarningsAsync(cancellationToken: cancellationToken);
                if (calendar is not null && backend == AppServices.Api.BaseUrl)
                {
                    AlertNewBeats(calendar.Items, backend, DateTimeOffset.UtcNow);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // The backend may be unreachable; the history is unchanged, so nothing is lost before the retry.
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Records every EPS beat in the stored history and notifies the ones <see cref="EarningsBeatAlertRule"/> selects.</summary>
    private static void AlertNewBeats(IReadOnlyList<MobileEarningsCalendarItem> items, string backend, DateTimeOffset nowUtc)
    {
        var stored = Preferences.Get(HistoryKey(backend), null);
        var history = ReadHistory(stored);
        var alerts = EarningsBeatAlertRule.SelectAlerts(
            items.Select(item => new EarningsBeatCandidate(
                item.Id,
                item.EpsOutcome.Equals("Beat", StringComparison.OrdinalIgnoreCase),
                item.FirstResultSignUtc)),
            history,
            historyExisted: stored is not null,
            nowUtc);
        foreach (var item in items.Where(item => alerts.Contains(item.Id)))
        {
            LocalSignalNotificationService.ShowSignal(item.Ticker, "EPS beat", Describe(item));
        }

        Preferences.Set(HistoryKey(backend), JsonSerializer.Serialize(history.TakeLast(MaximumRemembered)));
    }

    /// <summary>Notification text such as "0.48 vs 0.40 estimate (+20.62%), +6.31% since the close before release."</summary>
    private static string Describe(MobileEarningsCalendarItem item)
    {
        var move = item.LatestTradePrice.HasValue ? item.LatestTradeMovePercent : item.EventReturnPercent;
        var text = $"{item.EpsActual:0.00} vs {item.EpsEstimate:0.00} estimate ({item.EpsSurprisePercent:+0.00;-0.00;0.00}%)";
        return move.HasValue ? $"{text}, {move.Value:+0.00;-0.00;0.00}% since the close before release." : text + ".";
    }

    /// <summary>The stored ids in order, or an empty list when nothing or something unreadable is stored.</summary>
    private static List<string> ReadHistory(string? stored)
    {
        if (stored is null)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(stored) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
