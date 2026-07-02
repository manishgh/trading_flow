using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Pages;

public partial class WishlistsPage : ContentPage
{
    private readonly TradingFlowApiClient api = AppServices.Api;
    private readonly ObservableCollection<WishlistStockCard> items = new();
    private readonly ObservableCollection<WishlistSignalCard> signals = new();
    private readonly ObservableCollection<WishlistActivityNews> newsItems = new();
    private readonly ObservableCollection<string> suggestions = new();
    private readonly IDispatcherTimer refreshTimer;
    private MobileCatalogResponse? catalog;
    private IReadOnlyList<MobileWishlistResponse> wishlists = Array.Empty<MobileWishlistResponse>();
    private MobileWishlistResponse? selectedWishlist;
    private CancellationTokenSource? streamCts;
    private Guid? streamedWishlistId;
    private bool isLoading;

    public WishlistsPage()
    {
        InitializeComponent();
        ItemsView.ItemsSource = items;
        SignalsView.ItemsSource = signals;
        NewsView.ItemsSource = newsItems;
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
        StopStreams();
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
            EnsureStreams();
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
        var recent = latestSignals
            .Where(signal => signal.DetectedAtUtc.ToLocalTime() >= since)
            .Take(20)
            .ToArray();
        MergeSignals(recent);
    }

    // Reconcile the signal cards in place so expand/collapse state survives refreshes.
    private void MergeSignals(IReadOnlyList<MobileWishlistSignalResponse> latest)
    {
        var desiredIds = latest.Select(signal => signal.Id).ToHashSet();
        for (var index = signals.Count - 1; index >= 0; index--)
        {
            if (!desiredIds.Contains(signals[index].Id))
            {
                signals.RemoveAt(index);
            }
        }

        for (var index = 0; index < latest.Count; index++)
        {
            var signal = latest[index];
            var existing = signals.FirstOrDefault(card => card.Id == signal.Id);
            if (existing is null)
            {
                signals.Insert(Math.Min(index, signals.Count), new WishlistSignalCard(signal));
            }
        }
    }

    private void RenderWishlist()
    {
        if (selectedWishlist is null)
        {
            items.Clear();
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

        var desired = selectedWishlist.Items
            .Where(item => item.Active)
            .OrderBy(item => item.Ticker)
            .ToArray();

        // Reconcile in place, reusing existing card instances so live quote state is preserved.
        var desiredTickers = desired.Select(item => item.Ticker).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = items.Count - 1; index >= 0; index--)
        {
            if (!desiredTickers.Contains(items[index].Ticker))
            {
                items.RemoveAt(index);
            }
        }

        for (var index = 0; index < desired.Length; index++)
        {
            var item = desired[index];
            var existing = items.FirstOrDefault(card => card.Ticker.Equals(item.Ticker, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                items.Insert(Math.Min(index, items.Count), new WishlistStockCard(item));
            }
            else
            {
                existing.UpdateStatic(item);
            }
        }
    }

    private void EnsureStreams()
    {
        if (selectedWishlist is null)
        {
            StopStreams();
            return;
        }

        if (streamedWishlistId == selectedWishlist.Id && streamCts is { IsCancellationRequested: false })
        {
            return;
        }

        StartStreams(selectedWishlist.Id);
    }

    private void StartStreams(Guid wishlistId)
    {
        StopStreams();
        var cts = new CancellationTokenSource();
        streamCts = cts;
        streamedWishlistId = wishlistId;
        _ = RunQuoteStreamAsync(wishlistId, cts.Token);
        _ = RunActivityStreamAsync(wishlistId, cts.Token);
    }

    private void StopStreams()
    {
        if (streamCts is null)
        {
            return;
        }

        try
        {
            streamCts.Cancel();
            streamCts.Dispose();
        }
        catch
        {
            // Ignore cancellation races on teardown.
        }
        finally
        {
            streamCts = null;
            streamedWishlistId = null;
        }
    }

    private async Task RunQuoteStreamAsync(Guid wishlistId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await api.StreamWishlistQuotesAsync(wishlistId, updates =>
                {
                    MainThread.BeginInvokeOnMainThread(() => ApplyQuoteUpdates(updates));
                    return Task.CompletedTask;
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Stream faulted; fall through to reconnect after a short delay.
            }

            if (!await DelayForReconnectAsync(cancellationToken))
            {
                break;
            }
        }
    }

    private async Task RunActivityStreamAsync(Guid wishlistId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await api.StreamWishlistActivityAsync(wishlistId, update =>
                {
                    MainThread.BeginInvokeOnMainThread(() => ApplyActivityUpdate(update));
                    return Task.CompletedTask;
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Stream faulted; fall through to reconnect after a short delay.
            }

            if (!await DelayForReconnectAsync(cancellationToken))
            {
                break;
            }
        }
    }

    private static async Task<bool> DelayForReconnectAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void ApplyQuoteUpdates(IReadOnlyList<WishlistQuoteUpdate> updates)
    {
        foreach (var update in updates)
        {
            var card = items.FirstOrDefault(item => item.Ticker.Equals(update.Ticker, StringComparison.OrdinalIgnoreCase));
            card?.ApplyQuote(update);
        }
    }

    private void ApplyActivityUpdate(WishlistActivityUpdate update)
    {
        newsItems.Clear();
        foreach (var item in update.News ?? Array.Empty<WishlistActivityNews>())
        {
            newsItems.Add(item);
        }

        NewsStatusLabel.Text = newsItems.Count == 0
            ? "Rolling 4-hour window for this group's tickers. Live."
            : $"{newsItems.Count} live item(s) in the rolling 4-hour window.";

        // The activity feed signals its own updates; refresh the rich signal cards so
        // Trade/News/Details/Done stay actionable while keeping expand state.
        _ = LoadSignalsAsync();
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
        newsItems.Clear();
        RenderWishlist();
        _ = LoadSignalsAsync();
        RefreshSuggestions();
        EnsureStreams();
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
        if (selectedWishlist is null || sender is not Button { CommandParameter: WishlistStockCard card })
        {
            return;
        }

        try
        {
            await api.DeleteWishlistTickerAsync(selectedWishlist.Id, card.Ticker);
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
        if (sender is Button { CommandParameter: WishlistStockCard card })
        {
            await StartPaperRunAsync(new[] { card.Ticker }, selectedWishlist?.Id);
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

    private async void OnNewsSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is WishlistActivityNews news)
        {
            NewsView.SelectedItem = null;
            if (news.HasLink)
            {
                await Browser.Default.OpenAsync(news.Url!, BrowserLaunchMode.SystemPreferred);
            }
        }
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

// Mutable per-ticker card so live SSE quote updates can refresh price/bid-ask in place
// without rebuilding the list (which would drop scroll position and card state).
internal sealed class WishlistStockCard : INotifyPropertyChanged
{
    private string displayTitle;
    private string detailText;
    private string priceText = "Last --";
    private string bidAskText = "Streaming quotes...";
    private string movementText = String.Empty;
    private bool hasMovement;

    public WishlistStockCard(MobileWishlistItemResponse item)
    {
        Ticker = item.Ticker;
        displayTitle = item.DisplayTitle;
        detailText = item.DetailText;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Ticker { get; }

    public string DisplayTitle
    {
        get => displayTitle;
        private set => SetField(ref displayTitle, value);
    }

    public string DetailText
    {
        get => detailText;
        private set => SetField(ref detailText, value);
    }

    public string PriceText
    {
        get => priceText;
        private set => SetField(ref priceText, value);
    }

    public string BidAskText
    {
        get => bidAskText;
        private set => SetField(ref bidAskText, value);
    }

    public string MovementText
    {
        get => movementText;
        private set => SetField(ref movementText, value);
    }

    public bool HasMovement
    {
        get => hasMovement;
        private set => SetField(ref hasMovement, value);
    }

    public Color MovementColor => Color.FromArgb("#667085");

    public void UpdateStatic(MobileWishlistItemResponse item)
    {
        DisplayTitle = item.DisplayTitle;
        DetailText = item.DetailText;
    }

    public void ApplyQuote(WishlistQuoteUpdate update)
    {
        PriceText = FirstNonEmpty(update.MidText, Format(update.MidPrice), "Last --");

        var bid = FirstNonEmpty(update.BidText, Format(update.BidPrice), "--");
        var ask = FirstNonEmpty(update.AskText, Format(update.AskPrice), "--");
        BidAskText = $"Bid {bid} / Ask {ask}";

        if (update.Timestamp is { } stamp)
        {
            MovementText = $"as of {stamp.LocalDateTime:HH:mm}";
            HasMovement = true;
        }
        else
        {
            MovementText = String.Empty;
            HasMovement = false;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!String.IsNullOrWhiteSpace(value))
            {
                return value!.Trim();
            }
        }

        return String.Empty;
    }

    private static string? Format(decimal? value)
    {
        return value is null or <= 0 ? null : value.Value.ToString("C2");
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
