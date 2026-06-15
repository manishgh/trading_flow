using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile;

public static class AppServices
{
    public static TradingFlowApiClient Api { get; } = new();
    public static NotificationAutomationHub AutomationHub { get; } = new(Api);
    public static Services.INotificationAccessHelper NotificationAccess { get; } = new Platforms.Android.Services.AndroidNotificationAccessHelper();
}
