using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Pages;

public partial class EarningsPage : ContentPage
{
    private readonly ObservableCollection<EarningsDayGroup> groups = new();
    private readonly IDispatcherTimer refreshTimer;
    private bool isLoading;

    public EarningsPage()
    {
        InitializeComponent();
        SessionPicker.ItemsSource = new[]
        {
            new EarningsFilterChoice("all", "All sessions"),
            new EarningsFilterChoice("beforeMarketOpen", "Pre-market"),
            new EarningsFilterChoice("duringMarket", "Market hours"),
            new EarningsFilterChoice("afterMarketClose", "Post-market")
        };
        MarketCapPicker.ItemsSource = new[]
        {
            new EarningsFilterChoice("all", "All caps"),
            new EarningsFilterChoice("under10b", "Under $10B"),
            new EarningsFilterChoice("10bTo50b", "$10B-$50B"),
            new EarningsFilterChoice("50bTo100b", "$50B-$100B"),
            new EarningsFilterChoice("100bPlus", "$100B+"),
            new EarningsFilterChoice("unknown", "Cap unknown")
        };
        SessionPicker.SelectedIndex = 0;
        MarketCapPicker.SelectedIndex = 0;
        EarningsView.ItemsSource = groups;
        refreshTimer = Dispatcher.CreateTimer();
        refreshTimer.Interval = TimeSpan.FromMinutes(1);
        refreshTimer.Tick += async (_, _) => await LoadAsync(showBusy: false);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
        refreshTimer.Start();
    }

    protected override void OnDisappearing()
    {
        refreshTimer.Stop();
        base.OnDisappearing();
    }

    private async Task LoadAsync(bool showBusy = true)
    {
        if (isLoading)
        {
            return;
        }

        isLoading = true;
        BusyIndicator.IsRunning = showBusy;
        BusyIndicator.IsVisible = showBusy;
        try
        {
            var session = (SessionPicker.SelectedItem as EarningsFilterChoice)?.Value ?? "all";
            var marketCap = (MarketCapPicker.SelectedItem as EarningsFilterChoice)?.Value ?? "all";
            var response = await AppServices.Api.GetTodayAndNextBusinessDayEarningsAsync(session, marketCap) ??
                new MobileEarningsCalendarResponse(
                    DateTimeOffset.UtcNow,
                    TimeZoneInfo.Local.Id,
                    "America/New_York",
                    DateOnly.FromDateTime(DateTime.Today.AddDays(1)),
                    "Next business day",
                    false,
                    60,
                    null,
                    new MobileEarningsFilterStateResponse(
                        session,
                        marketCap,
                        Array.Empty<MobileEarningsFilterOptionResponse>(),
                        Array.Empty<MobileEarningsFilterOptionResponse>()),
                    Array.Empty<MobileEarningsCalendarItem>());
            groups.Clear();
            AddGroup("Today", response.Items.Where(item => item.DayGroup == "today"));
            AddGroup($"Next | {response.NextBusinessDateLabel}", response.Items.Where(item => item.DayGroup == "nextBusinessDay"));
            var cadence = response.MonitoringIntervalSeconds >= 60 && response.MonitoringIntervalSeconds % 60 == 0
                ? $"{response.MonitoringIntervalSeconds / 60}m"
                : $"{response.MonitoringIntervalSeconds}s";
            StatusLabel.Text = response.MonitoringActive
                ? $"{response.Items.Count} event(s) | monitoring every {cadence} | updated {response.GeneratedAtUtc.LocalDateTime:HH:mm:ss}"
                : $"{response.Items.Count} event(s) | monitor offline | updated {response.GeneratedAtUtc.LocalDateTime:HH:mm:ss}";
            StatusLabel.TextColor = Color.FromArgb("#067647");
        }
        catch (Exception exception)
        {
            StatusLabel.Text = $"Earnings error: {exception.Message}";
            StatusLabel.TextColor = Color.FromArgb("#B42318");
        }
        finally
        {
            isLoading = false;
            BusyIndicator.IsRunning = false;
            BusyIndicator.IsVisible = false;
            RefreshRoot.IsRefreshing = false;
        }
    }

    private void AddGroup(string name, IEnumerable<MobileEarningsCalendarItem> items)
    {
        var group = new EarningsDayGroup(name);
        foreach (var item in items)
        {
            group.Add(new EarningsItemViewModel(item));
        }

        groups.Add(group);
    }

    private async void OnRefresh(object? sender, EventArgs e) => await LoadAsync();

    private async void OnFilterChanged(object? sender, EventArgs e)
    {
        if (SessionPicker.SelectedIndex >= 0 && MarketCapPicker.SelectedIndex >= 0)
        {
            await LoadAsync(showBusy: false);
        }
    }

    private async void OnRefreshProviders(object? sender, EventArgs e)
    {
        if (isLoading)
        {
            return;
        }

        try
        {
            StatusLabel.Text = "Refreshing Finviz, Alpaca bars, and earnings analysis...";
            await AppServices.Api.RefreshEarningsAsync();
            await LoadAsync(showBusy: false);
        }
        catch (Exception exception)
        {
            StatusLabel.Text = $"Refresh error: {exception.Message}";
            StatusLabel.TextColor = Color.FromArgb("#B42318");
        }
    }

    private void OnToggleDetails(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: EarningsItemViewModel item })
        {
            item.IsExpanded = !item.IsExpanded;
        }
    }

    private async void OnOpenNews(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: EarningsItemViewModel item } ||
            !Uri.TryCreate(item.Item.NewsUrl, UriKind.Absolute, out var uri))
        {
            return;
        }

        await Launcher.Default.OpenAsync(uri);
    }

    public sealed class EarningsDayGroup : ObservableCollection<EarningsItemViewModel>
    {
        public EarningsDayGroup(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public string CountText => $"{Count} event{(Count == 1 ? String.Empty : "s")}";
    }

    public sealed record EarningsFilterChoice(string Value, string Label);

    public sealed class EarningsItemViewModel : INotifyPropertyChanged
    {
        private bool isExpanded;

        public EarningsItemViewModel(MobileEarningsCalendarItem item)
        {
            Item = item;
        }

        public MobileEarningsCalendarItem Item { get; }

        public bool IsExpanded
        {
            get => isExpanded;
            set
            {
                if (isExpanded == value)
                {
                    return;
                }

                isExpanded = value;
                OnPropertyChanged();
            }
        }

        public string TechnicalText =>
            $"EMA10/20 {Format(Item.Ema10)}/{Format(Item.Ema20)} · MACD {Format(Item.MacdHistogram, "0.0000")} · slot RVOL {Format(Item.SlotRelativeVolume)}x";

        public bool HasResultObservedTime => Item.ResultFirstSeenAtUtc.HasValue;

        public string ResultObservedText => Item.ResultFirstSeenAtUtc is { } observedAt
            ? $"Finviz result first observed: {observedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}"
            : String.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private static string Format(decimal? value, string pattern = "0.00") =>
            value.HasValue ? value.Value.ToString(pattern) : "-";
    }
}
