namespace TradingFlow.Mobile.Controls;

public partial class EnvironmentBadgeView : ContentView
{
    private readonly IDispatcherTimer clockTimer;
    private readonly TimeZoneInfo marketTimeZone;

    // Update a compact environment chip locally; tapping it exposes the complete clocks.
    public EnvironmentBadgeView()
    {
        InitializeComponent();
        marketTimeZone = ResolveMarketTimeZone();
        clockTimer = Dispatcher.CreateTimer();
        clockTimer.Interval = TimeSpan.FromSeconds(30);
        clockTimer.Tick += (_, _) => UpdateClocks();
        Loaded += (_, _) =>
        {
            UpdateClocks();
            clockTimer.Start();
        };
        Unloaded += (_, _) => clockTimer.Stop();
    }

    // Keep market time visible and expose full market, local and UTC clocks to assistive technology.
    private void UpdateClocks()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var localTimeZone = TimeZoneInfo.Local;
        var marketTime = TimeZoneInfo.ConvertTime(nowUtc, marketTimeZone);
        var localTime = TimeZoneInfo.ConvertTime(nowUtc, localTimeZone);
        MarketClockLabel.Text = $"NY {marketTime:HH:mm}";
        LocalClockLabel.Text = $"{GetCity(localTimeZone)} {localTime:HH:mm}";
        SemanticProperties.SetDescription(this, $"Paper trading. New York {marketTime:HH:mm}. {LocalClockLabel.Text}. UTC {nowUtc:HH:mm}. Tap for clocks.");
    }

    // Show precise clocks on demand without expanding every screen's header.
    private async void OnClocksTapped(object? sender, TappedEventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        await Shell.Current.DisplayAlertAsync("Market clocks",
            $"Paper trading\nUTC {now:yyyy-MM-dd HH:mm:ss}\nNew York {TimeZoneInfo.ConvertTime(now, marketTimeZone):yyyy-MM-dd HH:mm:ss}\n{GetCity(TimeZoneInfo.Local)} {now.ToLocalTime():yyyy-MM-dd HH:mm:ss}", "Close");
    }

    private static TimeZoneInfo ResolveMarketTimeZone()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        throw new InvalidOperationException("A New York market timezone is required.");
    }

    private static string GetCity(TimeZoneInfo timeZone)
    {
        var id = timeZone.Id;
        if (!id.Contains('/') && TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId))
        {
            id = ianaId;
        }

        var segment = id.Split('/').LastOrDefault();
        return String.IsNullOrWhiteSpace(segment)
            ? "Local"
            : segment.Replace('_', ' ');
    }
}
