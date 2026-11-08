using System.Collections.ObjectModel;
using TradingFlow.Mobile.Controls;
using TradingFlow.Mobile.Services;
using TradingFlow.Mobile.Resources.Styles;
using TradingFlow.Contracts.Presentation;

namespace TradingFlow.Mobile.Pages;

public partial class NewsPage : ContentPage
{
    private readonly ObservableCollection<MobileNewsItem> newsItems = new();
    private readonly IDispatcherTimer refreshTimer;
    private readonly MarketFilterForm filters = new();
    private readonly LatestMarketRead reads = new(() => AppServices.Api.SessionVersion);
    private bool providerBusy;
    private int page = 1;
    private long groupsSession = -1;
    private CancellationTokenSource? assessmentRead;

    public NewsPage()
    {
        InitializeComponent();
        NewsView.ItemsSource = newsItems;
        FilterHost.Children.Add(filters);
        filters.Choice("hours", "Stored lookback", [new("4", "4 hours"), new("1", "1 hour"), new("12", "12 hours"), new("24", "24 hours"), new("72", "72 hours")]);
        filters.EntryField("provider", "Provider");
        filters.EntryField("source", "Source");
        filters.Choice("classification", "Evidence classification", [new("", "Any"), new("company", "Company"), new("sec filing", "SEC filing"), new("fed", "Fed"), new("macro", "Macro"), new("market", "Market")]);
        filters.Choice("sentiment", "Sentiment", [new("all", "Any"), new("positive", "Positive"), new("negative", "Negative"), new("neutral", "Neutral")]);
        filters.EntryField("search", "Headline / summary search");
        filters.Toggle("linkedOnly", "Has article link");
        filters.Choice("scope", "Private symbol scope", [new("all", "All market"), new("groups", "My groups"), new("positions", "Held positions")], true);
        filters.GroupField();
        filters.Choice("pageSize", "Records per page", [new("50", "50"), new("100", "100"), new("200", "200")]);
        filters.Changed += async (_, _) => { page = 1; await LoadAsync(false); };
        refreshTimer = Dispatcher.CreateTimer();
        refreshTimer.Interval = TimeSpan.FromSeconds(20);
        refreshTimer.Tick += async (_, _) => await LoadAsync(false);
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        reads.Activate();
        AppServices.Api.SessionChanged += OnSessionChanged;
        SetSession();
        refreshTimer.Start();
        await LoadAsync();
    }
    protected override void OnDisappearing()
    {
        CancelAssessmentRead();
        refreshTimer.Stop();
        AppServices.Api.SessionChanged -= OnSessionChanged;
        reads.Deactivate();
        SetBusy(false);
        base.OnDisappearing();
    }
    // Clear private selections and old-session stories before reading the new feed.
    private void SetSession()
    {
        CancelAssessmentRead();
        AssessmentSamplesHost.Children.Clear();
        AssessmentSamplesHost.IsVisible = false;
        AssessmentSamplesButton.IsVisible = AppServices.Api.CurrentUserName is not null;
        AssessmentSamplesButton.IsEnabled = true;
        groupsSession = -1; page = 1;
        ProviderStatusLabel.IsVisible = false;
        newsItems.Clear();
        EmptyTitleLabel.Text = "Loading news"; EmptyDetailLabel.Text = "Waiting for a news response.";
        filters.SetAuthenticated(false); // Remove private selection before changing operator or backend.
        filters.SetAuthenticated(AppServices.Api.CurrentUserName is not null);
        FetchProvidersButton.IsVisible = AppServices.Api.CurrentUserName is not null;
        PreviousButton.IsEnabled = NextButton.IsEnabled = false;
        PageLabel.Text = "Page 1";
    }
    private void OnSessionChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(async () => {
        if (!reads.Active) return;
        reads.Cancel(); SetSession(); await LoadAsync();
    });
    private void SetBusy(bool busy)
    {
        BusyIndicator.IsRunning = BusyIndicator.IsVisible = busy;
        if (!busy) RefreshRoot.IsRefreshing = false;
    }
    // Render only the current feed and keep unavailable reads distinct from successful empty results.
    private async Task LoadAsync(bool showBusy = true)
    {
        if (!reads.Active || providerBusy) return; // Provider completion reads the latest controls.
        var read = reads.Begin();
        SetBusy(showBusy);
        var query = filters.Snapshot();
        query["ticker"] = TickerEntry.Text?.Trim().ToUpperInvariant() ?? "";
        query["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try {
            if (AppServices.Api.CurrentUserName is not null && groupsSession != read.Session) {
                var available = await AppServices.Api.GetWishlistsAsync(read.Token) ?? throw new InvalidOperationException("No group response returned.");
                if (!reads.IsCurrent(read)) return;
                filters.SetGroups(available.Select(x => (x.Id, x.Name))); groupsSession = read.Session;
            }
            var feed = await AppServices.Api.GetFilteredNewsAsync(query, read.Token) ?? throw new InvalidOperationException("No news response returned.");
            if (!reads.IsCurrent(read)) return;
            newsItems.Clear(); foreach (var item in feed.Items) newsItems.Add(item);
            EmptyTitleLabel.Text = feed.Enabled ? "No matching news" : "News unavailable";
            EmptyDetailLabel.Text = feed.Enabled ? "Try another ticker or filter." : feed.Message ?? "The feed is unavailable.";
            page = feed.Page;
            long? last = feed.TotalMatching is { } total ? Math.Max(1, (long)Math.Ceiling(total / (double)Math.Max(1, feed.PageSize))) : null;
            PageLabel.Text = last is { } known ? $"Page {page} of {known}" : $"Page {page} | total pages unknown";
            PreviousButton.IsEnabled = feed.Enabled && page > 1;
            NextButton.IsEnabled = feed.Enabled && last.HasValue && page < last.Value;
            StatusLabel.Text = feed.Enabled ? $"{feed.Items.Count} displayed | {feed.TotalMatching?.ToString() ?? "unknown"} matching stored records before article combining. {feed.Message}" : feed.Message ?? "News unavailable.";
            StatusLabel.TextColor = feed.Enabled ? ThemePalette.Positive : ThemePalette.Negative;
        } catch (OperationCanceledException) when (read.Token.IsCancellationRequested || !reads.IsCurrent(read)) { }
        catch (Exception exception) {
            if (!reads.IsCurrent(read)) return;
            newsItems.Clear(); PreviousButton.IsEnabled = NextButton.IsEnabled = false;
            EmptyTitleLabel.Text = "News unavailable"; EmptyDetailLabel.Text = "Refresh to try again or check your backend in More → Settings.";
            StatusLabel.Text = $"News unavailable: {exception.Message}"; StatusLabel.TextColor = ThemePalette.Negative;
        } finally { if (reads.IsCurrent(read)) SetBusy(false); }
    }
    private async void OnRefresh(object? sender, EventArgs e) => await LoadAsync();
    private async void OnRefreshClicked(object? sender, EventArgs e) => await LoadAsync();
    private async void OnTickerChanged(object? sender, TextChangedEventArgs e) { page = 1; await LoadAsync(false); }
    // Open secondary filters locally; individual edits retain the existing latest-read behavior.
    private void OnToggleFilters(object? sender, EventArgs e)
    {
        FilterHost.IsVisible = !FilterHost.IsVisible;
        if (sender is Button button) button.Text = FilterHost.IsVisible ? "Filters −" : "Filters +";
    }
    private async void OnPreviousPage(object? sender, EventArgs e) { page = Math.Max(1, page - 1); await LoadAsync(); }
    private async void OnNextPage(object? sender, EventArgs e) { page++; await LoadAsync(); }
    private async void OnFetchClicked(object? sender, EventArgs e)
    {
        if (!reads.Active || providerBusy || AppServices.Api.CurrentUserName is null) return;
        var read = reads.Begin(); providerBusy = true; FetchProvidersButton.IsEnabled = false; SetBusy(true);
        ProviderStatusLabel.IsVisible = false;
        try { await AppServices.Api.RefreshRollingNewsFeedAsync(read.Token); }
        catch (OperationCanceledException) when (read.Token.IsCancellationRequested || !reads.IsCurrent(read)) { }
        catch (Exception exception) { if (reads.IsCurrent(read)) { ProviderStatusLabel.Text = $"Provider refresh failed: {exception.Message}"; ProviderStatusLabel.TextColor = ThemePalette.Negative; ProviderStatusLabel.IsVisible = true; } }
        finally {
            providerBusy = false; FetchProvidersButton.IsEnabled = true;
            if (reads.Active) await LoadAsync(false);
        }
    }
    /// <summary>Cancels only the explicit assessment read when the page, account or backend changes.</summary>
    private void CancelAssessmentRead()
    {
        assessmentRead?.Cancel(); assessmentRead?.Dispose(); assessmentRead = null;
    }

    /// <summary>Displays server-computed historical samples separately from live articles, with actual assessment clocks.</summary>
    private async void OnAssessmentSamplesClicked(object? sender, EventArgs e)
    {
        if (!reads.Active || AppServices.Api.CurrentUserName is null) return;
        CancelAssessmentRead();
        var request = assessmentRead = new CancellationTokenSource();
        var token = request.Token;
        var session = AppServices.Api.SessionVersion;
        AssessmentSamplesButton.IsEnabled = false;
        AssessmentSamplesHost.Children.Clear(); AssessmentSamplesHost.IsVisible = true;
        try
        {
            var result = await AppServices.Api.GetCompanyNewsAssessmentSamplesAsync(token);
            if (token.IsCancellationRequested || !reads.Active || session != AppServices.Api.SessionVersion) return;
            if (result is null) throw new InvalidOperationException("No assessment response returned.");
            AssessmentSamplesHost.Children.Add(new Label { Text = result.Message, LineBreakMode = LineBreakMode.WordWrap });
            if (result.Summary is { } summary)
                AssessmentSamplesHost.Children.Add(new Label { Text = String.Join("\n", CompanyNewsAssessmentPresentation.Lines(summary)), FontSize = 12 });
            foreach (var sample in result.Samples)
                AssessmentSamplesHost.Children.Add(new Label {
                    Text = $"{sample.Symbol}: {sample.Headline}\n{sample.CompanyDirection} · {sample.Category}\n" +
                        (sample.CompanyName is null ? "" : $"Company name: {sample.CompanyName} · {sample.CompanyNameProvider} capture UTC: {sample.CompanyNameObservedAtUtc:O}. This capture does not prove the name was known at the old trade time.\n") +
                        $"Text tone: {sample.TextTone ?? "Not assessed"}; model confidence: {sample.ModelConfidence?.ToString("P1") ?? "Not assessed"}\n" +
                        $"{sample.Explanation}\nText support: {sample.TextSupport}\nEvidence: {sample.EvidenceText}\n" +
                        $"Published UTC: {sample.PublishedAtUtc?.ToString("O") ?? "Unknown"}\nActually assessed UTC: {sample.AssessedAtUtc:O}",
                    LineBreakMode = LineBreakMode.WordWrap, FontSize = 13 });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (reads.Active && session == AppServices.Api.SessionVersion && !token.IsCancellationRequested)
                AssessmentSamplesHost.Children.Add(new Label { Text = "Assessment samples unavailable: " + exception.Message });
        }
        finally
        {
            if (ReferenceEquals(assessmentRead, request)) { assessmentRead = null; request.Dispose(); AssessmentSamplesButton.IsEnabled = true; }
        }
    }

    private async void OnNewsSelected(object? sender, SelectionChangedEventArgs e)
    {
        var selected = e.CurrentSelection.FirstOrDefault() as MobileNewsItem;
        if (sender is CollectionView collectionView) collectionView.SelectedItem = null;
        await NewsNavigation.OpenAsync(selected);
    }
}
