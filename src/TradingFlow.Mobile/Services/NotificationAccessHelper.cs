namespace TradingFlow.Mobile.Services;

public interface INotificationAccessHelper
{
    bool IsNotificationAccessEnabled();
    Task OpenNotificationAccessSettingsAsync();
    Task<IReadOnlyList<InstalledNotificationApp>> FindInstalledAppsAsync(string appNameQuery);
}

public sealed record InstalledNotificationApp(
    string AppName,
    string PackageName,
    bool NotificationAccessEnabled)
{
    public string DisplayText => $"{AppName} ({PackageName})";
    public override string ToString() => DisplayText;
}
