using Android.App;
using Android.Content;
using Android.OS;
using Android.Service.Notification;

namespace TradingFlow.Mobile.Platforms.Android.Services;

[Service(
    Label = "TradingFlow Notification Listener",
    Permission = "android.permission.BIND_NOTIFICATION_LISTENER_SERVICE",
    Exported = true)]
[IntentFilter(new[] { "android.service.notification.NotificationListenerService" })]
[MetaData("android.service.notification", Resource = "@xml/tradingflow_notification_listener")]
public sealed class TradingFlowNotificationListenerService : NotificationListenerService
{
    public override void OnNotificationPosted(StatusBarNotification? sbn)
    {
        if (sbn?.Notification?.Extras is not Bundle extras)
        {
            return;
        }

        var title = extras.GetString(Notification.ExtraTitle) ?? string.Empty;
        var text = extras.GetCharSequence(Notification.ExtraText)?.ToString() ?? string.Empty;
        var packageName = sbn.PackageName ?? string.Empty;
        var appName = ResolveAppName(packageName);
        _ = AppServices.AutomationHub.PublishAsync(packageName, appName, title, text);
    }

    private string ResolveAppName(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return string.Empty;
        }

        try
        {
            var info = PackageManager?.GetApplicationInfo(packageName, 0);
            var label = info is null ? null : PackageManager?.GetApplicationLabel(info)?.ToString();
            return string.IsNullOrWhiteSpace(label) ? packageName : label;
        }
        catch
        {
            return packageName;
        }
    }
}
