using Microsoft.Maui.ApplicationModel;

#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

// This file is compiled only for Android. Each version-specific API call is
// protected by a runtime SDK check, but the cross-target MAUI analyzer cannot
// prove that from the surrounding #if ANDROID block.
#pragma warning disable CA1416
#pragma warning disable CA1422
#endif

namespace TradingFlow.Mobile.Services;

/// <summary>
/// Shows local device notifications for newly persisted wishlist signals.
/// The backend remains the source of truth; this service only surfaces signal
/// events while the mobile app is connected to the desk feed.
/// </summary>
public static class LocalSignalNotificationService
{
#if ANDROID
    private const string ChannelId = "wishlist_signals";
    private const int PermissionRequestCode = 5107;
    private static bool channelCreated;
#endif

    public static void ShowSignal(string ticker, string signalType, string reason)
    {
#if ANDROID
        var context = Platform.AppContext;
        if (context is null)
        {
            return;
        }

        EnsureNotificationChannel(context);
        if (!HasNotificationPermission())
        {
            RequestNotificationPermission();
            return;
        }

        var launchIntent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName ?? String.Empty);
        var pendingIntent = PendingIntent.GetActivity(
            context,
            0,
            launchIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var title = $"{ticker.Trim().ToUpperInvariant()} eligible";
        var body = String.IsNullOrWhiteSpace(reason) ? signalType : reason;
        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(context, ChannelId)
            : new Notification.Builder(context);
        var notification = builder
            .SetSmallIcon(context.ApplicationInfo?.Icon ?? Android.Resource.Drawable.StatNotifyMore)
            .SetContentTitle(title)
            .SetContentText(body)
            .SetStyle(new Notification.BigTextStyle().BigText(body))
            .SetContentIntent(pendingIntent)
            .SetAutoCancel(true)
            .Build();

        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        manager?.Notify(Math.Abs(HashCode.Combine(ticker, signalType, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60)), notification);
#endif
    }

#if ANDROID
    private static void EnsureNotificationChannel(Context context)
    {
        if (channelCreated || Build.VERSION.SdkInt < BuildVersionCodes.O)
        {
            return;
        }

        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        var channel = new NotificationChannel(
            ChannelId,
            "Wishlist Signals",
            NotificationImportance.High)
        {
            Description = "TradingFlow wishlist signal alerts"
        };
        manager?.CreateNotificationChannel(channel);
        channelCreated = true;
    }

    private static bool HasNotificationPermission()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu)
        {
            return true;
        }

        var activity = Platform.CurrentActivity;
        return activity?.CheckSelfPermission(Android.Manifest.Permission.PostNotifications) == Permission.Granted;
    }

    private static void RequestNotificationPermission()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu)
        {
            return;
        }

        Platform.CurrentActivity?.RequestPermissions(
            [Android.Manifest.Permission.PostNotifications],
            PermissionRequestCode);
    }
#endif
}

#if ANDROID
#pragma warning restore CA1422
#pragma warning restore CA1416
#endif
