using Android.Content;
using AndroidX.Core.App;
using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Platforms.Android.Services;

public sealed class AndroidNotificationAccessHelper : INotificationAccessHelper
{
    private const string NotificationListenerSettingsAction = "android.settings.ACTION_NOTIFICATION_LISTENER_SETTINGS";

    public bool IsNotificationAccessEnabled()
    {
        var context = global::Android.App.Application.Context;
        if (context is null)
        {
            return false;
        }

        var packageName = context?.PackageName ?? string.Empty;
        return (NotificationManagerCompat.GetEnabledListenerPackages(context) ?? Array.Empty<string>())
            .Contains(packageName);
    }

    public Task OpenNotificationAccessSettingsAsync()
    {
        var context = global::Android.App.Application.Context;
        if (context is null)
        {
            return Task.CompletedTask;
        }

        var intent = new Intent(NotificationListenerSettingsAction);
        intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
        return Task.CompletedTask;
    }
}
