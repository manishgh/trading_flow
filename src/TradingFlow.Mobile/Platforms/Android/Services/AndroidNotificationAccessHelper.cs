using Android.Content;
using Android.Content.PM;
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

    public Task<IReadOnlyList<InstalledNotificationApp>> FindInstalledAppsAsync(string appNameQuery)
    {
        var context = global::Android.App.Application.Context;
        if (context is null || string.IsNullOrWhiteSpace(appNameQuery))
        {
            return Task.FromResult((IReadOnlyList<InstalledNotificationApp>)Array.Empty<InstalledNotificationApp>());
        }

        var query = appNameQuery.Trim();
        var normalizedQuery = NormalizeForMatch(query);
        var notificationAccessEnabled = IsNotificationAccessEnabled();
        var packageManager = context.PackageManager;
        if (packageManager is null)
        {
            return Task.FromResult((IReadOnlyList<InstalledNotificationApp>)Array.Empty<InstalledNotificationApp>());
        }

        var candidates = GetLaunchableApps(packageManager)
            .Select(app => app.ActivityInfo?.PackageName ?? string.Empty)
            .Concat(GetVisibleInstalledApplications(packageManager))
            .Where(packageName => !string.IsNullOrWhiteSpace(packageName))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var matches = new List<InstalledNotificationApp>();
        foreach (var packageName in candidates)
        {
            var appName = ResolveAppName(packageManager, packageName);
            var normalizedAppName = NormalizeForMatch(appName);
            var normalizedPackageName = NormalizeForMatch(packageName);
            if (!appName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !packageName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !normalizedAppName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) &&
                !normalizedPackageName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matches.Add(new InstalledNotificationApp(appName, packageName, notificationAccessEnabled));
        }

        return Task.FromResult((IReadOnlyList<InstalledNotificationApp>)matches
            .DistinctBy(x => x.PackageName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.AppName.Equals(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.AppName)
            .Take(20)
            .ToArray());
    }

    private static IEnumerable<ResolveInfo> GetLaunchableApps(PackageManager packageManager)
    {
        var intent = new Intent(Intent.ActionMain);
        intent.AddCategory(Intent.CategoryLauncher);
        return packageManager.QueryIntentActivities(intent, (PackageInfoFlags)0) ?? Array.Empty<ResolveInfo>();
    }

    private static IEnumerable<string> GetVisibleInstalledApplications(PackageManager packageManager)
    {
        try
        {
            return (packageManager.GetInstalledApplications((PackageInfoFlags)0) ?? Array.Empty<ApplicationInfo>())
                .Select(app => app.PackageName ?? string.Empty);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string ResolveAppName(PackageManager packageManager, string packageName)
    {
        try
        {
            var info = packageManager.GetApplicationInfo(packageName, 0);
            var label = info is null ? null : packageManager.GetApplicationLabel(info)?.ToString();
            return string.IsNullOrWhiteSpace(label) ? packageName : label;
        }
        catch
        {
            return packageName;
        }
    }

    private static string NormalizeForMatch(string value)
    {
        return new string(value
            .Where(Char.IsLetterOrDigit)
            .Select(Char.ToLowerInvariant)
            .ToArray());
    }
}
