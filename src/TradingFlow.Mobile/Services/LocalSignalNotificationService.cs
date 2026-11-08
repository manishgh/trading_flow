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

    public static void ShowSignal(string ticker, string signalType, string reason, Guid? runId = null, Guid? candidateId = null, string? articleId = null, PreparationLink? preparation = null)
    {
        if (runId is { } alertRun && candidateId is { } alertCandidate)
            CandidateAlertHistory.Add(ticker, reason ?? signalType, new(alertRun, alertCandidate, articleId, AppServices.Api.BaseUrl));
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
        if (launchIntent is null) return;
        if (runId is { } run && candidateId is { } candidate)
        {
            launchIntent.PutExtra("candidateBackend", AppServices.Api.BaseUrl);
            launchIntent.PutExtra("candidateRunId", run.ToString());
            launchIntent.PutExtra("candidateId", candidate.ToString());
            if (articleId is not null) launchIntent.PutExtra("articleId", articleId);
            launchIntent.SetData(Android.Net.Uri.Parse("tradingflow://candidate/" + candidate));
        }
        if (preparation is not null)
        {
            launchIntent.PutExtra("preparationBackend", preparation.Backend);
            launchIntent.PutExtra("preparationWorkflow", preparation.WorkflowId.ToString());
            launchIntent.PutExtra("preparationOccurrence", preparation.OccurrenceId?.ToString());
            launchIntent.PutExtra("preparationRevision", preparation.RevisionId?.ToString());
            launchIntent.SetData(Android.Net.Uri.Parse("tradingflow://preparation/"+preparation.RevisionId));
        }
        var notificationId = HashCode.Combine(candidateId, ticker, signalType, preparation?.RevisionId) & Int32.MaxValue;
        var pendingIntent = PendingIntent.GetActivity(
            context,
            notificationId,
            launchIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var title = $"{ticker.Trim().ToUpperInvariant()} · {signalType}";
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
        manager?.Notify(notificationId, notification);
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
