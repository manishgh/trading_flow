using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TradingFlow.Mobile.Controls;
using TradingFlow.Mobile.Services;
using TradingFlow.Mobile.Resources.Styles;

namespace TradingFlow.Mobile.Pages;

public partial class EarningsPage : ContentPage
{
    private readonly ObservableCollection<EarningsDayGroup> groups = new();
    private readonly IDispatcherTimer refreshTimer;
    private readonly MarketFilterForm filters = new();
    private readonly LatestMarketRead reads = new(() => AppServices.Api.SessionVersion);
    private bool providerBusy, settingDates;
    private long groupsSession = -1;

    public EarningsPage()
    {
        InitializeComponent();
        SessionPicker.ItemsSource = new[] { new EarningsFilterChoice("all", "All sessions"), new("beforeMarketOpen", "Pre-market"), new("duringMarket", "Market hours"), new("afterMarketClose", "Post-market") };
        MarketCapPicker.ItemsSource = new[] { new EarningsFilterChoice("all", "All caps"), new("under10b", "Under $10B"), new("10bTo50b", "$10B-$50B"), new("50bTo100b", "$50B-$100B"), new("100bPlus", "$100B+"), new("unknown", "Cap unknown") };
        SessionPicker.SelectedIndex = MarketCapPicker.SelectedIndex = 0;
        EarningsView.ItemsSource = groups;
        FilterHost.Children.Add(filters);
        filters.EntryField("provider", "Earnings provider");
        filters.EntryField("newsProvider", "News provider");
        filters.Choice("day", "Reporting day", [new("all", "All days"), new("today", "Today"), new("nextbusinessday", "Next business day"), new("other", "Other dates")]);
        filters.Choice("assessment", "EPS outcome", [new("all", "Any"), new("beat", "Beat"), new("miss", "Miss"), new("inline", "Inline"), new("pending", "Pending")]);
        filters.Choice("breakout", "Analysis state", [new("all", "Any"), new("awaitingearningsrelease", "Awaiting release"), new("breakoutdatainsufficient", "Insufficient bars"), new("possiblebreakout", "Possible breakout"), new("breakoutnotconfirmed", "Breakout not confirmed")]);
        foreach (var (key, label) in new[] { ("minEpsSurprise", "Minimum EPS surprise %"), ("maxEpsSurprise", "Maximum EPS surprise %"), ("minSurprise", "Minimum absolute EPS surprise %"), ("minRevenueSurprise", "Minimum revenue surprise %"), ("maxRevenueSurprise", "Maximum revenue surprise %"), ("minRvol", "Minimum slot RVOL"), ("maxRvol", "Maximum slot RVOL"), ("minSentiment", "Minimum news sentiment (−1 to 1)"), ("maxSentiment", "Maximum news sentiment (−1 to 1)") }) filters.EntryField(key, label, true);
        filters.Toggle("hasNews", "Has news");
        filters.GroupField();
        filters.Toggle("onlyGroups", "My groups only", true);
        filters.Toggle("onlyHeld", "Held positions only", true);
        filters.Choice("sort", "Order", [new("", "Server default"), new("time", "Event time"), new("cap", "Market cap"), new("surprise", "Absolute EPS surprise"), new("revenue", "Revenue surprise"), new("move", "Event return"), new("rvol", "Slot RVOL")]);
        filters.Changed += async (_, _) => await LoadAsync(false);
        settingDates = true; FromDate.Date = ToDate.Date = MarketToday().ToDateTime(TimeOnly.MinValue); settingDates = false;
        refreshTimer = Dispatcher.CreateTimer(); refreshTimer.Interval = TimeSpan.FromMinutes(1);
        refreshTimer.Tick += async (_, _) => await LoadAsync(false);
    }
    private static readonly TimeZoneInfo MarketZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static DateOnly MarketToday() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, MarketZone).DateTime);
    protected override async void OnAppearing()
    {
        base.OnAppearing(); reads.Activate(); AppServices.Api.SessionChanged += OnSessionChanged;
        SetSession(); refreshTimer.Start(); await LoadAsync();
    }
    protected override void OnDisappearing()
    {
        refreshTimer.Stop(); AppServices.Api.SessionChanged -= OnSessionChanged; reads.Deactivate(); SetBusy(false); base.OnDisappearing();
    }
    private void SetSession()
    {
        groupsSession = -1; groups.Clear(); filters.SetAuthenticated(false);
        ProviderStatusLabel.IsVisible = false;
        filters.SetAuthenticated(AppServices.Api.CurrentUserName is not null);
        RefreshProvidersButton.IsVisible = AppServices.Api.CurrentUserName is not null;
    }
    private void OnSessionChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(async () => {
        if (!reads.Active) return; reads.Cancel(); SetSession(); await LoadAsync();
    });
    private void SetBusy(bool busy) { BusyIndicator.IsRunning = BusyIndicator.IsVisible = busy; if (!busy) RefreshRoot.IsRefreshing = false; }
    private async Task LoadAsync(bool showBusy = true)
    {
        if (!reads.Active || providerBusy || settingDates) return;
        var read = reads.Begin(); SetBusy(showBusy);
        var query = filters.Snapshot();
        query["session"] = (SessionPicker.SelectedItem as EarningsFilterChoice)?.Value ?? "all";
        query["marketCap"] = (MarketCapPicker.SelectedItem as EarningsFilterChoice)?.Value ?? "all";
        var calendar = CalendarSwitch.IsToggled;
        DateOnly? from = calendar && FromDate.Date is { } start ? DateOnly.FromDateTime(start) : null;
        DateOnly? to = calendar && ToDate.Date is { } end ? DateOnly.FromDateTime(end) : null;
        try {
            if (calendar && (from is null || to is null)) throw new InvalidOperationException("Choose both calendar dates.");
            if (AppServices.Api.CurrentUserName is not null && groupsSession != read.Session) {
                var available = await AppServices.Api.GetWishlistsAsync(read.Token) ?? throw new InvalidOperationException("No group response returned.");
                if (!reads.IsCurrent(read)) return;
                filters.SetGroups(available.Select(x => (x.Id, x.Name))); groupsSession = read.Session;
            }
            var response = await AppServices.Api.GetFilteredEarningsAsync(query, from, to, read.Token) ?? throw new InvalidOperationException("No earnings response returned.");
            if (!reads.IsCurrent(read)) return;
            groups.Clear();
            if (calendar) {
                var dated = response.Items.GroupBy(x => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.ScheduledAtUtc, MarketZone).DateTime)).ToDictionary(x => x.Key, x => x.AsEnumerable());
                var requested = Enumerable.Range(0, to!.Value.DayNumber - from!.Value.DayNumber + 1).Select(offset => from.Value.AddDays(offset));
                foreach (var date in requested.Concat(dated.Keys).Distinct().Order()) AddGroup($"{date:ddd yyyy-MM-dd} | New York", dated.GetValueOrDefault(date) ?? []);
            } else {
                AddGroup("Today", response.Items.Where(x => x.DayGroup == "today"));
                AddGroup($"Next | {response.NextBusinessDateLabel}", response.Items.Where(x => x.DayGroup == "nextBusinessDay"));
                foreach (var other in response.Items.Where(x => x.DayGroup is not ("today" or "nextBusinessDay")).GroupBy(x => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.ScheduledAtUtc, MarketZone).DateTime))) AddGroup($"{other.Key:yyyy-MM-dd} | New York", other);
            }
            StatusLabel.Text = $"{response.Items.Count} displayed / {response.TotalMatching?.ToString() ?? "unknown"} matching{(response.Truncated ? " | capped at 2,000" : "")} | {(response.MonitoringActive ? $"monitoring every {response.MonitoringIntervalSeconds}s" : "monitor offline")} | updated {response.GeneratedAtUtc.LocalDateTime:HH:mm:ss}";
            StatusLabel.TextColor = ThemePalette.Positive;
        } catch (OperationCanceledException) when (read.Token.IsCancellationRequested || !reads.IsCurrent(read)) { }
        catch (Exception exception) { if (reads.IsCurrent(read)) { groups.Clear(); StatusLabel.Text = $"Earnings unavailable: {exception.Message}"; StatusLabel.TextColor = ThemePalette.Negative; } }
        finally { if (reads.IsCurrent(read)) SetBusy(false); }
    }
    private void AddGroup(string name, IEnumerable<MobileEarningsCalendarItem> items)
    {
        var group = new EarningsDayGroup(name); foreach (var item in items) group.Add(new EarningsItemViewModel(item)); groups.Add(group);
    }
    private async void OnRefresh(object? sender, EventArgs e) => await LoadAsync();
    private async void OnFilterChanged(object? sender, EventArgs e) => await LoadAsync(false);
    private void OnToggleFilters(object? sender, EventArgs e) => FilterHost.IsVisible = !FilterHost.IsVisible;
    private async void OnCalendarChanged(object? sender, ToggledEventArgs e) => await LoadAsync(false);
    private async void OnDateChanged(object? sender, DateChangedEventArgs e) => await LoadAsync(false);
    private async void OnThisWeek(object? sender, EventArgs e)
    {
        settingDates = true;
        try { var today = MarketToday(); var monday = today.AddDays(-((int)today.DayOfWeek + 6) % 7); FromDate.Date = monday.ToDateTime(TimeOnly.MinValue); ToDate.Date = monday.AddDays(6).ToDateTime(TimeOnly.MinValue); CalendarSwitch.IsToggled = true; }
        finally { settingDates = false; }
        await LoadAsync();
    }
    private async void OnRefreshProviders(object? sender, EventArgs e)
    {
        if (!reads.Active || providerBusy || AppServices.Api.CurrentUserName is null) return;
        var read = reads.Begin(); providerBusy = true; RefreshProvidersButton.IsEnabled = false; SetBusy(true);
        ProviderStatusLabel.IsVisible = false;
        try { await AppServices.Api.RefreshEarningsAsync(read.Token); }
        catch (OperationCanceledException) when (read.Token.IsCancellationRequested || !reads.IsCurrent(read)) { }
        catch (Exception exception) { if (reads.IsCurrent(read)) { ProviderStatusLabel.Text = $"Provider refresh failed: {exception.Message}"; ProviderStatusLabel.TextColor = ThemePalette.Negative; ProviderStatusLabel.IsVisible = true; } }
        finally { providerBusy = false; RefreshProvidersButton.IsEnabled = true; if (reads.Active) await LoadAsync(false); }
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
            $"EMA10/20 {Format(Item.Ema10)}/{Format(Item.Ema20)} Â· MACD {Format(Item.MacdHistogram, "0.0000")} Â· slot RVOL {Format(Item.SlotRelativeVolume)}x";

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
