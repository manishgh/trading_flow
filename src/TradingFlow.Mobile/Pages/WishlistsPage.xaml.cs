using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Pages;

public partial class WishlistsPage : ContentPage
{
    private readonly TradingFlowApiClient api = AppServices.Api;
    private readonly ObservableCollection<WishlistStockCard> items = new();
    private readonly ObservableCollection<string> suggestions = new();
    private readonly HashSet<Guid> seenSignalIds = new();
    private readonly IDispatcherTimer refreshTimer;
    private IReadOnlyList<MobileWishlistResponse> wishlists = Array.Empty<MobileWishlistResponse>();
    private MobileWishlistResponse? selectedWishlist;
    private CancellationTokenSource? streamCts;
    private Guid? streamedWishlistId;
    private bool isLoading;

    public WishlistsPage()
    {
        InitializeComponent();
        ItemsView.ItemsSource = items;
        SuggestionsPicker.ItemsSource = suggestions;
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
            StatusLabel.Text = healthy ? "Connected · live watch data" : "Disconnected · watch data may be stale";
            StatusLabel.TextColor = healthy ? Color.FromArgb("#067647") : Color.FromArgb("#B42318");

            var previousWishlistId = selectedWishlist?.Id;
            wishlists = await api.GetWishlistsAsync() ?? Array.Empty<MobileWishlistResponse>();
            WishlistPicker.ItemsSource = wishlists.ToList();
            selectedWishlist = wishlists.FirstOrDefault(wishlist => wishlist.Id == previousWishlistId)
                ?? wishlists.FirstOrDefault(wishlist => wishlist.IsDefault)
                ?? wishlists.FirstOrDefault();
            WishlistPicker.SelectedItem = selectedWishlist;
            await LoadDeskAsync(notifyNewSignals: false);
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

    private async Task LoadDeskAsync(bool notifyNewSignals)
    {
        if (selectedWishlist is null)
        {
            RenderWishlist();
            ProcessSignals([], notifyNew: false);
            return;
        }

        var desk = await api.GetWishlistDeskAsync(selectedWishlist.Id, signalMinutes: 20, newsHours: 4);
        if (desk is null)
        {
            RenderWishlist();
            return;
        }

        selectedWishlist = desk.Wishlist;
        RenderDesk(desk);
        ProcessSignals(desk.RecentSignals, notifyNewSignals);
    }

    private void ProcessSignals(IReadOnlyList<MobileWishlistSignalResponse> latest, bool notifyNew)
    {
        foreach (var signal in latest)
        {
            var isNewSignal = seenSignalIds.Add(signal.Id);
            if (notifyNew && isNewSignal)
            {
                LocalSignalNotificationService.ShowSignal(signal.Ticker, signal.SignalType, signal.Reason);
            }
        }

        ApplySignalsToTickerCards(latest);
    }

    private void ApplySignalsToTickerCards(IReadOnlyList<MobileWishlistSignalResponse> latest)
    {
        var latestByTicker = latest
            .GroupBy(signal => signal.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(signal => signal.DetectedAtUtc).First(), StringComparer.OrdinalIgnoreCase);
        foreach (var card in items)
        {
            if (latestByTicker.TryGetValue(card.Ticker, out var signal))
            {
                card.ApplySignal(signal);
            }
            else
            {
                card.ClearSignal();
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
            return;
        }

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

    private void RenderDesk(MobileWishlistDeskResponse desk)
    {
        selectedWishlist = desk.Wishlist;
        WishlistDetailLabel.Text = selectedWishlist.DetailText;
        StockCountLabel.Text = $"{desk.Rows.Count} active | {desk.TotalText}";
        ObserveButton.Text = selectedWishlist.IsObserved ? "Pause" : "Observe";
        ObserveButton.BackgroundColor = selectedWishlist.IsObserved ? Color.FromArgb("#B42318") : Color.FromArgb("#067647");

        var desiredTickers = desk.Rows.Select(row => row.Ticker).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = items.Count - 1; index >= 0; index--)
        {
            if (!desiredTickers.Contains(items[index].Ticker))
            {
                items.RemoveAt(index);
            }
        }

        for (var index = 0; index < desk.Rows.Count; index++)
        {
            var row = desk.Rows[index];
            var existing = items.FirstOrDefault(card => card.Ticker.Equals(row.Ticker, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                items.Insert(Math.Min(index, items.Count), new WishlistStockCard(row));
            }
            else
            {
                existing.UpdateFromDesk(row);
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
        var signalResponses = (update.Signals ?? Array.Empty<WishlistActivitySignal>())
            .Select(ToMobileWishlistSignal)
            .ToArray();
        ProcessSignals(signalResponses, notifyNew: true);
    }

    private MobileWishlistSignalResponse ToMobileWishlistSignal(WishlistActivitySignal signal)
    {
        return new MobileWishlistSignalResponse(
            signal.Id,
            selectedWishlist?.Id ?? Guid.Empty,
            signal.Ticker,
            signal.SignalType,
            "high",
            signal.DetectedAt,
            0m,
            signal.Reason,
            "{}",
            null,
            null,
            null,
            false);
    }

    private async void OnRefresh(object? sender, EventArgs e) => await LoadAsync();

    private async void OnRefreshClicked(object? sender, EventArgs e) => await LoadAsync();

    private void OnWishlistChanged(object? sender, EventArgs e)
    {
        if (WishlistPicker.SelectedItem is not MobileWishlistResponse wishlist || selectedWishlist?.Id == wishlist.Id)
        {
            return;
        }

        selectedWishlist = wishlist;
        RenderWishlist();
        _ = LoadDeskAsync(notifyNewSignals: false);
        RefreshSuggestions();
        EnsureStreams();
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

    private async void OnOpenTickerNews(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: WishlistStockCard card } || String.IsNullOrWhiteSpace(card.NewsUrl))
        {
            await DisplayAlertAsync("News", "No related news link is available for this ticker.", "OK");
            return;
        }

        await NewsNavigation.OpenAsync(card.NewsUrl);
    }

    private async void OnToggleTickerDetails(object? sender, EventArgs e)
    {
        if (selectedWishlist is not null && sender is Button { CommandParameter: WishlistStockCard card })
        {
            await Shell.Current.GoToAsync(
                $"{nameof(SymbolDetailPage)}?wishlistId={selectedWishlist.Id}&ticker={Uri.EscapeDataString(card.Ticker)}");
        }
    }

    private void OnTickerSearchChanged(object? sender, TextChangedEventArgs e)
    {
        RefreshSuggestions();
    }

    private void OnSuggestionSelected(object? sender, EventArgs e)
    {
        if (SuggestionsPicker.SelectedItem is string ticker)
        {
            TickerEntry.Text = ticker;
        }
    }

    private void RefreshSuggestions()
    {
        var query = NormalizeTicker(TickerEntry.Text);
        suggestions.Clear();
        if (query.Length == 0)
        {
            SuggestionsPicker.IsVisible = false;
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

        SuggestionsPicker.IsVisible = suggestions.Count > 0;
        SuggestionsPicker.SelectedIndex = -1;
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
    private string statusText = "Watching";
    private string eligibilityReason = "Waiting for VWAP/EMA/MACD/volume conditions.";
    private string newsText = "No related news";
    private string tradeText = "No open trade";
    private string? newsUrl;
    private Color statusColor = Color.FromArgb("#667085");
    private bool hasMovement;
    private bool hasNewsLink;

    public WishlistStockCard(MobileWishlistItemResponse item)
    {
        Ticker = item.Ticker;
        displayTitle = item.DisplayTitle;
        detailText = item.DetailText;
    }

    public WishlistStockCard(MobileWishlistDeskRowResponse row)
    {
        Ticker = row.Ticker;
        displayTitle = row.Item.DisplayTitle;
        detailText = row.Item.DetailText;
        UpdateFromDesk(row);
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

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public Color StatusColor
    {
        get => statusColor;
        private set => SetField(ref statusColor, value);
    }

    public string EligibilityReason
    {
        get => eligibilityReason;
        private set => SetField(ref eligibilityReason, value);
    }

    public string NewsText
    {
        get => newsText;
        private set => SetField(ref newsText, value);
    }

    public string TradeText
    {
        get => tradeText;
        private set => SetField(ref tradeText, value);
    }

    public string? NewsUrl
    {
        get => newsUrl;
        private set => SetField(ref newsUrl, value);
    }

    public bool HasMovement
    {
        get => hasMovement;
        private set => SetField(ref hasMovement, value);
    }

    public bool HasNewsLink
    {
        get => hasNewsLink;
        private set => SetField(ref hasNewsLink, value);
    }

    public Color MovementColor => Color.FromArgb("#667085");

    public void UpdateStatic(MobileWishlistItemResponse item)
    {
        DisplayTitle = item.DisplayTitle;
        DetailText = item.DetailText;
    }

    public void UpdateFromDesk(MobileWishlistDeskRowResponse row)
    {
        DisplayTitle = row.Item.DisplayTitle;
        DetailText = row.Item.DetailText;
        PriceText = FirstNonEmpty(row.DisplayPrice, Format(row.MidPrice), "Last --");
        BidAskText = $"Bid {FirstNonEmpty(row.DisplayBid, Format(row.BidPrice), "--")} / Ask {FirstNonEmpty(row.DisplayAsk, Format(row.AskPrice), "--")}";
        MovementText = row.QuoteTimestamp is { } stamp ? $"as of {stamp.LocalDateTime:HH:mm}" : String.Empty;
        HasMovement = row.QuoteTimestamp is not null;
        ApplyStatus(row.StatusText, row.EligibilityReason);
        NewsText = row.LatestNews?.DisplayHeadline ?? "No related news";
        NewsUrl = row.LatestNews?.Url;
        HasNewsLink = !String.IsNullOrWhiteSpace(NewsUrl);
        TradeText = row.Trade is null
            ? "No open trade"
            : $"{row.Trade.SourceLabel} {row.Trade.Quantity:0.####} sh | {row.Trade.PlText} | {row.Trade.ProtectionSummary}";
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

    public void ApplySignal(MobileWishlistSignalResponse signal)
    {
        ApplyStatus("Eligible", signal.Reason);
    }

    public void ClearSignal()
    {
        if (StatusText.Equals("In trade", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ApplyStatus("Watching", "Waiting for VWAP/EMA/MACD/volume conditions.");
    }

    private void ApplyStatus(string status, string reason)
    {
        StatusText = String.IsNullOrWhiteSpace(status) ? "Watching" : status;
        EligibilityReason = String.IsNullOrWhiteSpace(reason)
            ? "Waiting for VWAP/EMA/MACD/volume conditions."
            : reason;
        StatusColor = StatusText.Equals("Eligible", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb("#067647")
            : StatusText.Equals("In trade", StringComparison.OrdinalIgnoreCase)
                ? Color.FromArgb("#175CD3")
                : Color.FromArgb("#667085");
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
