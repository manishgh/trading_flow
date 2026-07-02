using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Pages;

public partial class WishlistsPage : ContentPage
{
    private readonly TradingFlowApiClient api = AppServices.Api;
    private readonly ObservableCollection<MobileWishlistItemResponse> items = new();
    private readonly ObservableCollection<WishlistSignalCard> signals = new();
    private readonly ObservableCollection<string> suggestions = new();
    private readonly IDispatcherTimer refreshTimer;
    private MobileCatalogResponse? catalog;
    private IReadOnlyList<MobileWishlistResponse> wishlists = Array.Empty<MobileWishlistResponse>();
    private MobileWishlistResponse? selectedWishlist;
    private bool isLoading;

    public WishlistsPage()
    {
        InitializeComponent();
        ItemsView.ItemsSource = items;
        SignalsView.ItemsSource = signals;
        SuggestionsView.ItemsSource = suggestions;
        refreshTimer = Dispatcher.CreateTimer();
        refreshTimer.Interval = TimeSpan.FromSeconds(12);
        refreshTimer.Tick += async (_, _) => await LoadAsync(showBusy: false);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        refreshTimer.Start();
        await LoadAsync();
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
        BusyIndicator.IsVisible = showBusy;
        BusyIndicator.IsRunning = showBusy;
        try
        {
            var healthy = await api.CheckHealthAsync();
            StatusLabel.Text = healthy ? $"Connected: {api.BaseUrl}" : $"Backend unreachable: {api.BaseUrl}";
            StatusLabel.TextColor = healthy ? Color.FromArgb("#067647") : Color.FromArgb("#B42318");

            catalog ??= await api.GetCatalogAsync();
            ConfigPicker.ItemsSource = catalog?.PaperConfigs.ToList();
            StrategyPicker.ItemsSource = catalog?.Strategies.ToList();
            ConfigPicker.SelectedIndex = ConfigPicker.SelectedIndex < 0 && ConfigPicker.Items.Count > 0 ? 0 : ConfigPicker.SelectedIndex;
            StrategyPicker.SelectedIndex = StrategyPicker.SelectedIndex < 0 && StrategyPicker.Items.Count > 0 ? 0 : StrategyPicker.SelectedIndex;

            var previousWishlistId = selectedWishlist?.Id;
            wishlists = await api.GetWishlistsAsync() ?? Array.Empty<MobileWishlistResponse>();
            WishlistPicker.ItemsSource = wishlists.ToList();
            selectedWishlist = wishlists.FirstOrDefault(wishlist => wishlist.Id == previousWishlistId)
                ?? wishlists.FirstOrDefault(wishlist => wishlist.IsDefault)
                ?? wishlists.FirstOrDefault();
            WishlistPicker.SelectedItem = selectedWishlist;
            RenderWishlist();
            await LoadSignalsAsync();
            RefreshSuggestions();
        }
        catch (Exception exception)
        {
            StatusLabel.Text = $"Wishlist error: {exception.Message}";
            StatusLabel.TextColor = Color.FromArgb("#B42318");
            await DisplayAlertAsync("Wishlists", exception.Message, "OK");
        }
        finally
        {
            isLoading = false;
            BusyIndicator.IsRunning = false;
            BusyIndicator.IsVisible = false;
            RefreshRoot.IsRefreshing = false;
        }
    }

    private async Task LoadSignalsAsync()
    {
        var latestSignals = await api.GetWishlistSignalsAsync(selectedWishlist?.Id, 1) ?? Array.Empty<MobileWishlistSignalResponse>();
        var since = DateTimeOffset.Now.AddMinutes(-20);
        signals.Clear();
        foreach (var signal in latestSignals.Where(signal => signal.DetectedAtUtc.ToLocalTime() >= since).Take(20))
        {
            signals.Add(new WishlistSignalCard(signal));
        }
    }

    private void RenderWishlist()
    {
        items.Clear();
        if (selectedWishlist is null)
        {
            WishlistDetailLabel.Text = "No group";
            StockCountLabel.Text = String.Empty;
            WishlistNameEntry.Text = string.Empty;
            return;
        }

        WishlistNameEntry.Text = selectedWishlist.Name;
        WishlistDetailLabel.Text = selectedWishlist.DetailText;
        StockCountLabel.Text = $"{selectedWishlist.ActiveItemCount} active";
        ObserveButton.Text = selectedWishlist.IsObserved ? "Pause" : "Observe";
        ObserveButton.BackgroundColor = selectedWishlist.IsObserved ? Color.FromArgb("#B42318") : Color.FromArgb("#067647");
        foreach (var item in selectedWishlist.Items.Where(item => item.Active).OrderBy(item => item.Ticker))
        {
            items.Add(item);
        }
    }

    private async void OnRefresh(object? sender, EventArgs e) => await LoadAsync();

    private async void OnRefreshClicked(object? sender, EventArgs e) => await LoadAsync();

    private async void OnRefreshSignals(object? sender, EventArgs e)
    {
        try
        {
            await LoadSignalsAsync();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Alerts", exception.Message, "OK");
        }
    }

    private void OnWishlistChanged(object? sender, EventArgs e)
    {
        if (WishlistPicker.SelectedItem is not MobileWishlistResponse wishlist || selectedWishlist?.Id == wishlist.Id)
        {
            return;
        }

        selectedWishlist = wishlist;
        RenderWishlist();
        _ = LoadSignalsAsync();
        RefreshSuggestions();
    }

    private async void OnSaveWishlist(object? sender, EventArgs e)
    {
        var name = WishlistNameEntry.Text?.Trim();
        if (String.IsNullOrWhiteSpace(name))
        {
            await DisplayAlertAsync("Wishlists", "Enter a group name.", "OK");
            return;
        }

        try
        {
            var saved = await api.SaveWishlistAsync(new MobileWishlistSaveRequest(
                selectedWishlist?.Id,
                name,
                selectedWishlist?.Description,
                selectedWishlist?.IsDefault,
                true,
                selectedWishlist?.IsObserved));
            selectedWishlist = saved;
            await LoadAsync();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Wishlists", exception.Message, "OK");
        }
    }

    private async void OnToggleObserve(object? sender, EventArgs e)
    {
        if (selectedWishlist is null)
        {
            await DisplayAlertAsync("Wishlists", "Create or select a wishlist first.", "OK");
            return;
        }

        try
        {
            selectedWishlist = await api.SetWishlistObservedAsync(selectedWishlist.Id, !selectedWishlist.IsObserved);
            await LoadAsync();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Observe", exception.Message, "OK");
        }
    }

    private async void OnAddTicker(object? sender, EventArgs e)
    {
        await AddTickerAsync(TickerEntry.Text);
    }

    private async Task AddTickerAsync(string? ticker)
    {
        if (selectedWishlist is null)
        {
            await DisplayAlertAsync("Wishlists", "Create or select a wishlist first.", "OK");
            return;
        }

        var normalizedTicker = NormalizeTicker(ticker);
        if (String.IsNullOrWhiteSpace(normalizedTicker))
        {
            await DisplayAlertAsync("Wishlists", "Enter a ticker.", "OK");
            return;
        }

        try
        {
            await api.AddWishlistTickerAsync(selectedWishlist.Id, new MobileWishlistItemRequest(normalizedTicker, null, null));
            TickerEntry.Text = string.Empty;
            await LoadAsync();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Wishlists", exception.Message, "OK");
        }
    }

    private async void OnRemoveTicker(object? sender, EventArgs e)
    {
        if (selectedWishlist is null || sender is not Button { CommandParameter: MobileWishlistItemResponse item })
        {
            return;
        }

        try
        {
            await api.DeleteWishlistTickerAsync(selectedWishlist.Id, item.Ticker);
            await LoadAsync();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Wishlists", exception.Message, "OK");
        }
    }

    private async void OnTradeWishlist(object? sender, EventArgs e)
    {
        if (selectedWishlist is null)
        {
            await DisplayAlertAsync("Paper", "Select a wishlist first.", "OK");
            return;
        }

        await StartPaperRunAsync(selectedWishlist.Items.Where(item => item.Active).Select(item => item.Ticker).ToArray(), selectedWishlist.Id);
    }

    private async void OnTradeTicker(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: MobileWishlistItemResponse item })
        {
            await StartPaperRunAsync(new[] { item.Ticker }, selectedWishlist?.Id);
        }
    }

    private async void OnSellTicker(object? sender, EventArgs e)
    {
        await DisplayAlertAsync(
            "Sell",
            "Sell/short from wishlist needs a broker-side manual exit or short-entry endpoint. Buy/paper-run is wired now; sell will be enabled only after that endpoint exists.",
            "OK");
    }

    private async void OnTradeSignal(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: WishlistSignalCard signal })
        {
            await StartPaperRunAsync(new[] { signal.Ticker }, signal.WishlistId);
        }
    }

    private async Task StartPaperRunAsync(IReadOnlyList<string> tickers, Guid? wishlistId)
    {
        if (ConfigPicker.SelectedItem is not MobileRunConfigOption config ||
            StrategyPicker.SelectedItem is not MobileStrategyOption strategy)
        {
            await DisplayAlertAsync("Paper", "Select a paper profile and strategy first.", "OK");
            return;
        }

        var normalizedTickers = tickers
            .Select(NormalizeTicker)
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedTickers.Length == 0 && wishlistId is null)
        {
            await DisplayAlertAsync("Paper", "Add at least one ticker to trade.", "OK");
            return;
        }

        try
        {
            var job = await api.StartPaperRunAsync(new MobilePaperRunRequest(
                config.Path,
                strategy.Path,
                $"wishlist_{DateTimeOffset.Now:yyyyMMdd_HHmmss}",
                normalizedTickers,
                null,
                true,
                strategy.UsesNews,
                "day",
                "market",
                wishlistId));

            await DisplayAlertAsync("Paper", job is null ? "Paper run submitted." : $"Started {job.RunName}.", "OK");
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Paper", exception.Message, "OK");
        }
    }

    private async void OnOpenSignalNews(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: WishlistSignalCard signal } || String.IsNullOrWhiteSpace(signal.NewsUrl))
        {
            await DisplayAlertAsync("News", "No matched news link is available for this alert.", "OK");
            return;
        }

        await Browser.Default.OpenAsync(signal.NewsUrl, BrowserLaunchMode.SystemPreferred);
    }

    private async void OnAcknowledgeSignal(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: WishlistSignalCard signal })
        {
            return;
        }

        try
        {
            await api.AcknowledgeWishlistSignalAsync(signal.Id);
            await LoadSignalsAsync();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Alerts", exception.Message, "OK");
        }
    }

    private void OnToggleSignalDetails(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: WishlistSignalCard signal })
        {
            signal.IsExpanded = !signal.IsExpanded;
        }
    }

    private void OnTickerSearchChanged(object? sender, TextChangedEventArgs e)
    {
        RefreshSuggestions();
    }

    private async void OnSuggestionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is string ticker)
        {
            SuggestionsView.SelectedItem = null;
            await AddTickerAsync(ticker);
        }
    }

    private void RefreshSuggestions()
    {
        var query = NormalizeTicker(TickerEntry.Text);
        suggestions.Clear();
        if (query.Length == 0)
        {
            SuggestionsView.HeightRequest = 0;
            return;
        }

        var currentTickers = selectedWishlist?.Items
            .Where(item => item.Active)
            .Select(item => item.Ticker)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ticker in BuildKnownTickerUniverse()
                     .Where(ticker => ticker.StartsWith(query, StringComparison.OrdinalIgnoreCase) && !currentTickers.Contains(ticker))
                     .Take(8))
        {
            suggestions.Add(ticker);
        }

        SuggestionsView.HeightRequest = suggestions.Count == 0 ? 0 : Math.Min(220, suggestions.Count * 48);
    }

    private IEnumerable<string> BuildKnownTickerUniverse()
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var wishlist in wishlists)
        {
            foreach (var item in wishlist.Items)
            {
                values.Add(item.Ticker);
            }
        }

        foreach (var config in catalog?.PaperConfigs ?? Array.Empty<MobileRunConfigOption>())
        {
            foreach (var ticker in config.Tickers)
            {
                values.Add(ticker);
            }
        }

        foreach (var signal in signals)
        {
            values.Add(signal.Ticker);
        }

        return values.OrderBy(ticker => ticker);
    }

    private static string NormalizeTicker(string? value)
    {
        return new String((value ?? String.Empty)
            .Trim()
            .ToUpperInvariant()
            .Where(character => Char.IsLetterOrDigit(character) || character is '.' or '-')
            .ToArray());
    }
}

internal sealed class WishlistSignalCard : INotifyPropertyChanged
{
    private bool isExpanded;

    public WishlistSignalCard(MobileWishlistSignalResponse signal)
    {
        Signal = signal;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MobileWishlistSignalResponse Signal { get; }

    public Guid Id => Signal.Id;

    public Guid WishlistId => Signal.WishlistId;

    public string Ticker => Signal.Ticker;

    public string StatusText => Signal.StatusText;

    public string HeaderText => String.IsNullOrWhiteSpace(Signal.PriceText)
        ? Signal.DisplayTime
        : $"{Signal.DisplayTime} | {Signal.PriceText}";

    public string CompactReason => FirstUsefulReason(Signal.Reason);

    public string Reason => Signal.Reason;

    public string SnapshotSummary => CollapseTechnicalJson(Signal.SnapshotJson);

    public string NewsText => Signal.NewsText;

    public string? NewsUrl => Signal.NewsUrl;

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

    private static string FirstUsefulReason(string value)
    {
        var reason = (value ?? String.Empty).Trim();
        if (reason.Length == 0)
        {
            return "Signal matched.";
        }

        var line = reason.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? reason;
        return line.Length <= 110 ? line : line[..107] + "...";
    }

    private static string CollapseTechnicalJson(string value)
    {
        var snapshot = (value ?? String.Empty).Trim();
        if (snapshot.Length == 0)
        {
            return "No technical snapshot attached.";
        }

        snapshot = snapshot.Replace("\r", " ").Replace("\n", " ");
        return snapshot.Length <= 260 ? snapshot : snapshot[..257] + "...";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

