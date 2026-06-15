namespace TradingFlow.Mobile.Services;

public interface INotificationAccessHelper
{
    bool IsNotificationAccessEnabled();
    Task OpenNotificationAccessSettingsAsync();
}
