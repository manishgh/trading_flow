using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TradingFlow.Mobile.Services;
using TradingFlow.Mobile.Resources.Styles;

namespace TradingFlow.Mobile.Pages;

public partial class WishlistsPage : ContentPage
{
    private readonly TradingFlowApiClient api = AppServices.Api;
    private readonly ObservableCollection<WishlistStockCard> items = new();
    private readonly ObservableCollection<WishlistStockCard> visibleItems = new();
    private string watchScope = "all";
    private string watchSearch = String.Empty;
    private readonly ObservableCollection<string> suggestions = new();
    private readonly IDispatcherTimer refreshTimer;
    private IReadOnlyList<MobileWishlistResponse> wishlists = Array.Empty<MobileWishlistResponse>();
    private MobileWishlistResponse? selectedWishlist;
    private CancellationTokenSource? streamCts;
    private Guid? streamedWishlistId;
    private bool isLoading;
    private bool appearing;
    private readonly LatestMarketRead reads = new(() => AppServices.Api.SessionVersion);
    private long sessionVersion = -1, pageGeneration;
    private string emptyActionRoute = nameof(LoginPage);

    // Initialize a virtualized stocks list that reuses existing group reads and quote streams.
    public WishlistsPage()
    {
        InitializeComponent();
        // The source collection keeps every card so live quote updates keep
        // arriving for symbols that a filter is currently hiding. The view is a
        // projection of it, rebuilt whenever the filter changes.
        ItemsView.ItemsSource = visibleItems;
        SuggestionsPicker.ItemsSource = suggestions;
        refreshTimer = Dispatcher.CreateTimer();
        refreshTimer.Interval = TimeSpan.FromSeconds(12);
        refreshTimer.Tick += async (_, _) => await LoadAsync(showBusy: false);
    }

    // Resume only this visible account's stock list; a changed connection discards all former group state.
    protected override async void OnAppearing()
    {
        base.OnAppearing(); appearing = true; pageGeneration++; reads.Activate();
        api.SessionChanged += OnSessionChanged;
        if (sessionVersion != api.SessionVersion) ResetSession();
        refreshTimer.Start(); await LoadAsync();
    }

    // Cancel snapshots and streams before another page can change the selected group or account.
    protected override void OnDisappearing()
    {
        appearing = false; pageGeneration++; refreshTimer.Stop(); reads.Deactivate(); StopStreams();
        api.SessionChanged -= OnSessionChanged; SetBusy(false); base.OnDisappearing();
    }

    // Reject queued callbacks immediately by session version, then reload the visible connection.
    private void OnSessionChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(async () =>
    {
        if (!appearing) return;
        reads.Cancel(); ResetSession(); await LoadAsync();
    });

    // Remove all private rows, selections and suggestions from the previous account/backend.
    private void ResetSession()
    {
        StopStreams(); sessionVersion = api.SessionVersion; selectedWishlist = null; wishlists = [];
        WishlistPicker.ItemsSource = null; items.Clear(); visibleItems.Clear(); suggestions.Clear();
        TickerEntry.Text = String.Empty; WishlistDetailLabel.Text = StockCountLabel.Text = WatchCountLabel.Text = String.Empty;
        EmptyTitleLabel.Text = "Loading stocks"; EmptyDetailLabel.Text = "Reading saved stock groups.";
        EmptyActionButton.IsVisible = SuggestionsPicker.IsVisible = false;
        StatusLabel.Text = "Loading stocks…";
    }

    // Keep both foreground refresh and pull-to-refresh state consistent after cancellation.
    private void SetBusy(bool busy)
    {
        BusyIndicator.IsVisible = BusyIndicator.IsRunning = busy;
        if (!busy) RefreshRoot.IsRefreshing = false;
    }

    // Fetch the existing saved groups and one selected desk; obsolete account/group replies cannot paint the list.
    private async Task LoadAsync(bool showBusy = true)
    {
        if (isLoading || !reads.Active) return;
        isLoading = true; var read = reads.Begin(); var generation = pageGeneration; SetBusy(showBusy);
        EmptyActionButton.IsVisible = false;
        try
        {
            if (String.IsNullOrWhiteSpace(api.BaseUrl)) throw new InvalidOperationException("Select a backend in More → Settings.");
            var available = await api.GetWishlistsAsync(read.Token) ?? throw new InvalidOperationException("No stock group response returned.");
            if (!reads.IsCurrent(read)) return;
            var previousWishlistId = selectedWishlist?.Id;
            wishlists = available;
            selectedWishlist = wishlists.FirstOrDefault(group => group.Id == previousWishlistId)
                ?? wishlists.FirstOrDefault(group => group.IsDefault) ?? wishlists.FirstOrDefault();
            WishlistPicker.ItemsSource = wishlists.ToList(); WishlistPicker.SelectedItem = selectedWishlist;
            await LoadDeskAsync(false, read);
            if (!reads.IsCurrent(read)) return;
            RefreshSuggestions(); EnsureStreams();
            StatusLabel.Text = "Quotes shown with their source time"; StatusLabel.TextColor = ThemePalette.TextSecondary;
        }
        catch (OperationCanceledException) when (read.Token.IsCancellationRequested || !reads.IsCurrent(read)) { }
        catch (Exception exception)
        {
            if (!reads.IsCurrent(read)) return;
            ShowReadFailure(exception);
        }
        finally
        {
            isLoading = false;
            if (reads.IsCurrent(read)) SetBusy(false);
            else if (reads.Active && (read.Session != api.SessionVersion || generation != pageGeneration))
                Dispatcher.Dispatch(async () => await LoadAsync());
        }
    }

    // Distinguish protected account access from a missing backend or failed read without claiming an empty group.
    private void ShowReadFailure(Exception exception)
    {
        items.Clear(); ApplyWatchFilter(); StopStreams();
        var signIn = exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden };
        var missingBackend = String.IsNullOrWhiteSpace(api.BaseUrl);
        EmptyTitleLabel.Text = signIn ? "Sign in to see your stocks" : missingBackend ? "Connect your trading desk" : "Stocks unavailable";
        EmptyDetailLabel.Text = signIn ? "Your saved stock groups belong to your account."
            : "Check the saved backend in Settings, then refresh.";
        emptyActionRoute = signIn ? nameof(LoginPage) : nameof(SettingsPage);
        EmptyActionButton.Text = signIn ? "Sign in" : "Open settings"; EmptyActionButton.IsVisible = true;
        StatusLabel.Text = signIn ? "Sign-in required" : exception.Message;
        StatusLabel.TextColor = signIn ? ThemePalette.TextSecondary : ThemePalette.Negative;
    }

    // Use the normal sign-in/settings routes; this action does not modify credentials or backend preferences.
    private async void OnEmptyActionClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync(emptyActionRoute);

    // Bind a desk snapshot to the exact selected group and current read before applying it.
    private async Task LoadDeskAsync(bool notifyNewSignals, LatestMarketRead.Read read)
    {
        if (selectedWishlist is null)
        {
            RenderWishlist(); EmptyTitleLabel.Text = "No stock group selected";
            EmptyDetailLabel.Text = "Choose a saved group to follow its stocks."; return;
        }
        var id = selectedWishlist.Id;
        var desk = await api.GetWishlistDeskAsync(id, signalMinutes: 20, newsHours: 4, cancellationToken: read.Token)
            ?? throw new InvalidOperationException("No stock quotes response returned.");
        if (!reads.IsCurrent(read) || selectedWishlist?.Id != id) return;
        RenderDesk(desk); ProcessSignals(desk.RecentSignals, notifyNewSignals);
        EmptyActionButton.IsVisible = false;
        EmptyTitleLabel.Text = "No matching stocks"; EmptyDetailLabel.Text = "Change your search or use Manage to add a ticker.";
    }

    /// <summary>Shows existing alerts as history; polling never republishes an old generic alert as a fresh strategy notification.</summary>
    private void ProcessSignals(IReadOnlyList<MobileWishlistSignalResponse> latest, bool notifyNew)
    {
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

    /// <summary>Applies the search box and scope chips to the visible card list.</summary>
    private void ApplyWatchFilter()
    {
        var term = watchSearch.Trim();
        var matches = items.Where(card =>
        {
            if (term.Length > 0 &&
                !card.Ticker.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                !card.DisplayTitle.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return watchScope switch
            {
                "signal" => card.StatusText.Contains("Historical alert", StringComparison.OrdinalIgnoreCase),
                "trade" => card.HasTrade,
                "news" => card.HasNewsLink,
                _ => true
            };
        }).ToList();

        visibleItems.Clear();
        foreach (var card in matches)
        {
            visibleItems.Add(card);
        }

        if (WatchCountLabel is not null)
        {
            WatchCountLabel.Text = $"{matches.Count} stocks" + (matches.Count == items.Count ? "" : $" · {items.Count} in group") +
                (watchScope == "all" ? "" : " · " + (watchScope == "signal" ? "Past alerts" : watchScope == "trade" ? "In trade" : "News"));
        }

        UpdateWatchScopeButtons();
    }

    // Keep selection contrast consistent with the same semantic action styles in both themes.
    private void UpdateWatchScopeButtons()
    {
        var buttons = new (Button Button, string Scope)[]
        {
            (WatchAllButton, "all"),
            (WatchSignalButton, "signal"),
            (WatchTradeButton, "trade"),
            (WatchNewsButton, "news")
        };

        foreach (var (button, scope) in buttons)
        {
            if (button is null)
            {
                continue;
            }

            var selected = watchScope == scope;
            button.Style = (Style)Application.Current!.Resources[selected ? "ActionButton" : "QuietButton"];
        }
    }

    private void OnWatchFilterChanged(object? sender, TextChangedEventArgs e)
    {
        watchSearch = e.NewTextValue ?? String.Empty;
        ApplyWatchFilter();
    }

    private void OnWatchScopeSelected(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: string scope } && watchScope != scope)
        {
            watchScope = scope;
            ApplyWatchFilter();
        }
    }

    private void RenderWishlist()
    {
        if (selectedWishlist is null)
        {
            items.Clear();
            ApplyWatchFilter();
            WishlistDetailLabel.Text = "No group";
            StockCountLabel.Text = String.Empty;
            return;
        }

        WishlistDetailLabel.Text = selectedWishlist.DetailText;
        StockCountLabel.Text = $"{selectedWishlist.ActiveItemCount} active";

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

        ApplyWatchFilter();
    }

    private void RenderDesk(MobileWishlistDeskResponse desk)
    {
        selectedWishlist = desk.Wishlist;
        WishlistDetailLabel.Text = selectedWishlist.DetailText;
        StockCountLabel.Text = $"{desk.Rows.Count} active | {desk.TotalText}";

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

        ApplyWatchFilter();
    }

    private void EnsureStreams()
    {
        // An in-flight refresh can complete after navigation has stopped this page.
        if (!appearing || selectedWishlist is null)
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

    // Start only one pair of streams for the selected group and capture the account generation.
    private void StartStreams(Guid wishlistId)
    {
        StopStreams();
        var cts = new CancellationTokenSource();
        streamCts = cts;
        streamedWishlistId = wishlistId;
        var session = api.SessionVersion;
        _ = RunQuoteStreamAsync(wishlistId, session, cts.Token);
        _ = RunActivityStreamAsync(wishlistId, session, cts.Token);
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

    // Recheck ownership inside each queued UI callback, including callbacks delivered after cancellation.
    private async Task RunQuoteStreamAsync(Guid wishlistId, long session, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await api.StreamWishlistQuotesAsync(wishlistId, updates =>
                {
                    MainThread.BeginInvokeOnMainThread(() => { if (CanApplyStream(wishlistId, session, cancellationToken)) ApplyQuoteUpdates(updates); });
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

    // Keep historical alerts scoped to the same visible group/account as their originating stream.
    private async Task RunActivityStreamAsync(Guid wishlistId, long session, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await api.StreamWishlistActivityAsync(wishlistId, update =>
                {
                    MainThread.BeginInvokeOnMainThread(() => { if (CanApplyStream(wishlistId, session, cancellationToken)) ApplyActivityUpdate(update); });
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

    // An old group, connection or canceled stream can never update the current list.
    private bool CanApplyStream(Guid group, long session, CancellationToken token) =>
        appearing && session == api.SessionVersion && selectedWishlist?.Id == group && streamedWishlistId == group && !token.IsCancellationRequested;

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

    // Change the group immediately and cancel any earlier snapshot before starting its replacement.
    private async void OnWishlistChanged(object? sender, EventArgs e)
    {
        if (!reads.Active || WishlistPicker.SelectedItem is not MobileWishlistResponse group || selectedWishlist?.Id == group.Id) return;
        selectedWishlist = group; StopStreams(); items.Clear(); RenderWishlist(); RefreshSuggestions();
        var read = reads.Begin(); SetBusy(true);
        try
        {
            await LoadDeskAsync(false, read);
            if (reads.IsCurrent(read)) EnsureStreams();
        }
        catch (OperationCanceledException) when (read.Token.IsCancellationRequested || !reads.IsCurrent(read)) { }
        catch (Exception exception)
        {
            if (reads.IsCurrent(read)) ShowReadFailure(exception);
        }
        finally { if (reads.IsCurrent(read)) SetBusy(false); }
    }

    // Put group maintenance behind one explicit action instead of mixing forms into the stock list.
    private void OnManageClicked(object? sender, EventArgs e)
    {
        ManagePanel.IsVisible = !ManagePanel.IsVisible;
        if (sender is Button button) button.Text = ManagePanel.IsVisible ? "Done" : "Manage";
    }

    // Open the existing stock detail workflow; tapping a row never starts an evaluation or order.
    private async void OnStockSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not WishlistStockCard card || selectedWishlist is null) return;
        ItemsView.SelectedItem = null;
        await Shell.Current.GoToAsync($"{nameof(SymbolDetailPage)}?wishlistId={selectedWishlist.Id}&ticker={Uri.EscapeDataString(card.Ticker)}");
    }

    private async void OnAddTicker(object? sender, EventArgs e)
    {
        await AddTickerAsync(TickerEntry.Text);
    }

    // Preserve the original add operation but discard its UI result if the selected account/group changed.
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

        var groupId = selectedWishlist.Id; var session = api.SessionVersion;
        try
        {
            await api.AddWishlistTickerAsync(groupId, new MobileWishlistItemRequest(normalizedTicker, null, null));
            if (!appearing || session != api.SessionVersion || selectedWishlist?.Id != groupId) return;
            TickerEntry.Text = string.Empty;
            await LoadAsync();
        }
        catch (Exception exception)
        {
            if (appearing && session == api.SessionVersion && selectedWishlist?.Id == groupId)
                await DisplayAlertAsync("Wishlists", exception.Message, "OK");
        }
    }

    // Preserve the existing direct article action as a secondary swipe action on compact stock rows.
    private async void OnOpenTickerNews(object? sender, EventArgs e)
    {
        var card = sender switch { Button button => button.CommandParameter as WishlistStockCard,
            SwipeItem swipe => swipe.CommandParameter as WishlistStockCard, _ => null };
        if (card is null || String.IsNullOrWhiteSpace(card.NewsUrl))
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
    private string companyName = String.Empty;
    private bool hasTrade;
    private string detailText;
    private string priceText = "—";
    private string bidAskText = "Streaming quotes...";
    private string movementText = "Quote unavailable";
    private string statusText = "Select a strategy";
    private string eligibilityReason = "Select a strategy in stock details to collect and evaluate its conditions.";
    private string newsText = "No related news";
    private string tradeText = "No open trade";
    private string? newsUrl;
    private Color statusColor = ThemePalette.TextSecondary;
    private bool hasMovement;
    private bool hasNewsLink;

    // Preserve ticker identity and the supplied company name before a quote snapshot arrives.
    public WishlistStockCard(MobileWishlistItemResponse item)
    {
        Ticker = item.Ticker;
        companyName = item.DisplayName ?? String.Empty;
        displayTitle = item.DisplayTitle;
        detailText = item.DetailText;
    }

    // Initialize a row from the existing desk snapshot without inferring price changes.
    public WishlistStockCard(MobileWishlistDeskRowResponse row)
    {
        Ticker = row.Ticker;
        companyName = row.Item.DisplayName ?? String.Empty;
        displayTitle = row.Item.DisplayTitle;
        detailText = row.Item.DetailText;
        UpdateFromDesk(row);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Ticker { get; }

    public string CompanyName { get => companyName; private set => SetField(ref companyName, value); }
    public bool HasTrade { get => hasTrade; private set => SetField(ref hasTrade, value); }

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

    public Color MovementColor => ThemePalette.TextSecondary;

    // Refresh the item name without replacing the instance receiving live quote updates.
    public void UpdateStatic(MobileWishlistItemResponse item)
    {
        CompanyName = item.DisplayName ?? String.Empty;
        DisplayTitle = item.DisplayTitle;
        DetailText = item.DetailText;
    }

    // Use structured holdings and real quote timestamps for compact list facts and filters.
    public void UpdateFromDesk(MobileWishlistDeskRowResponse row)
    {
        CompanyName = row.Item.DisplayName ?? String.Empty; HasTrade = row.Trade is not null;
        DisplayTitle = row.Item.DisplayTitle;
        DetailText = row.Item.DetailText;
        PriceText = FirstNonEmpty(Format(row.MidPrice), row.DisplayPrice, "—");
        BidAskText = $"Bid {FirstNonEmpty(row.DisplayBid, Format(row.BidPrice), "--")} / Ask {FirstNonEmpty(row.DisplayAsk, Format(row.AskPrice), "--")}";
        MovementText = QuoteTime(row.QuoteTimestamp);
        HasMovement = row.QuoteTimestamp is not null;
        ApplyStatus(row.StatusText, row.EligibilityReason);
        NewsText = row.LatestNews?.DisplayHeadline ?? "No related news";
        NewsUrl = row.LatestNews?.Url;
        HasNewsLink = !String.IsNullOrWhiteSpace(NewsUrl);
        TradeText = row.Trade is null
            ? "No open trade"
            : $"{row.Trade.SourceLabel} {row.Trade.Quantity:0.####} sh | {row.Trade.PlText} | {row.Trade.ProtectionSummary}";
    }

    // Apply only supplied quote fields; absent or old timestamps never look like a fresh price change.
    public void ApplyQuote(WishlistQuoteUpdate update)
    {
        PriceText = FirstNonEmpty(Format(update.MidPrice), update.MidText, "—");

        var bid = FirstNonEmpty(update.BidText, Format(update.BidPrice), "--");
        var ask = FirstNonEmpty(update.AskText, Format(update.AskPrice), "--");
        BidAskText = $"Bid {bid} / Ask {ask}";

        MovementText = QuoteTime(update.Timestamp);
        HasMovement = update.Timestamp is not null;
    }

    // Include the local date for older-day quotes so a closed market's observation is never mistaken for today.
    private static string QuoteTime(DateTimeOffset? timestamp)
    {
        if (timestamp is not { } source) return "Time unavailable";
        var local = source.LocalDateTime;
        return local.Date == DateTime.Now.Date ? $"As of {local:HH:mm}" : $"{local:dd MMM yyyy HH:mm}";
    }

    public void ApplySignal(MobileWishlistSignalResponse signal)
    {
        ApplyStatus("Historical alert", signal.Reason);
    }

    public void ClearSignal()
    {
        if (StatusText.Equals("In trade", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ApplyStatus("Select a strategy", "Select a strategy in stock details to collect and evaluate its conditions.");
    }

    private void ApplyStatus(string status, string reason)
    {
        StatusText = String.IsNullOrWhiteSpace(status) ? "Select a strategy" : status;
        EligibilityReason = String.IsNullOrWhiteSpace(reason)
            ? "Select a strategy in stock details to collect and evaluate its conditions."
            : reason;
        StatusColor = StatusText.Equals("Historical alert", StringComparison.OrdinalIgnoreCase)
            ? ThemePalette.Positive
            : StatusText.Equals("In trade", StringComparison.OrdinalIgnoreCase)
                ? ThemePalette.AccentStrong
                : ThemePalette.TextSecondary;
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

    // Listed US stock quotes remain USD regardless of the device display locale.
    private static string? Format(decimal? value)
    {
        return value is null or <= 0 ? null : value.Value.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
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
