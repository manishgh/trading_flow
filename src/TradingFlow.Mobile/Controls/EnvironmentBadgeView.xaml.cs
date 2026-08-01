namespace TradingFlow.Mobile.Controls;

public partial class EnvironmentBadgeView : ContentView
{
    private readonly IDispatcherTimer clockTimer;
    private readonly TimeZoneInfo marketTimeZone;

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

    private void UpdateClocks()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var localTimeZone = TimeZoneInfo.Local;
        var marketTime = TimeZoneInfo.ConvertTime(nowUtc, marketTimeZone);
        var localTime = TimeZoneInfo.ConvertTime(nowUtc, localTimeZone);
        MarketClockLabel.Text = $"{GetCity(marketTimeZone)} {marketTime:HH:mm}";
        LocalClockLabel.Text = $"{GetCity(localTimeZone)} {localTime:HH:mm}";
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
