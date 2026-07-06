namespace TradingFlow.Mobile.Services;

public static class NewsNavigation
{
    public static async Task<bool> OpenAsync(string? url)
    {
        if (String.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        await Browser.Default.OpenAsync(url.Trim(), BrowserLaunchMode.SystemPreferred);
        return true;
    }

    public static Task<bool> OpenAsync(MobileNewsItem? item)
    {
        return OpenAsync(item?.Url);
    }
}
