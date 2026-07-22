using System.Collections.ObjectModel;
using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Pages;

public partial class PaperPage : ContentPage
{
    private readonly TradingFlowApiClient api = AppServices.Api;
    private readonly ObservableCollection<BacktestJobSnapshot> jobs = new();
    private readonly ObservableCollection<MobileNewsItem> newsItems = new();
    private readonly ObservableCollection<MobileAutomationSessionSnapshot> automationSessions = new();
    private readonly ObservableCollection<MobilePaperPositionResponse> positions = new();
    private readonly IDispatcherTimer refreshTimer;
    private MobileCatalogResponse? catalog;
    private IReadOnlyList<MobileWishlistResponse> wishlists = Array.Empty<MobileWishlistResponse>();
    private BacktestJobSnapshot? selectedJob;
    private BacktestJobSnapshot? lastRun;
    private bool isLoading;
    private bool suppressPersist;
    private bool formRestored;

    public PaperPage()
    {
        InitializeComponent();
        suppressPersist = true;
        JobsView.ItemsSource = jobs;
        NewsView.ItemsSource = newsItems;
        AutomationSessionsView.ItemsSource = automationSessions;
        PositionsView.ItemsSource = positions;
        OrderExpirationPicker.ItemsSource = new[] { "day", "gtc" };
        EntryOrderTypePicker.ItemsSource = new[] { "market", "limit" };
        OrderExpirationPicker.SelectedIndex = 0;
        EntryOrderTypePicker.SelectedIndex = 0;
        suppressPersist = false;
        refreshTimer = Dispatcher.CreateTimer();
        refreshTimer.Interval = TimeSpan.FromSeconds(6);
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
            StatusLabel.Text = healthy
                ? $"Connected: {api.BaseUrl}"
                : $"Backend unreachable: {api.BaseUrl}";
            StatusLabel.TextColor = healthy ? Color.FromArgb("#067647") : Color.FromArgb("#B42318");

            catalog ??= await api.GetCatalogAsync();
            ConfigPicker.ItemsSource = catalog?.PaperConfigs.ToList();
            StrategyPicker.ItemsSource = catalog?.Strategies.ToList();
            ConfigPicker.SelectedIndex = ConfigPicker.SelectedIndex < 0 && ConfigPicker.Items.Count > 0 ? 0 : ConfigPicker.SelectedIndex;
            StrategyPicker.SelectedIndex = StrategyPicker.SelectedIndex < 0 && StrategyPicker.Items.Count > 0 ? 0 : StrategyPicker.SelectedIndex;

            var previousWishlistId = (WishlistPicker.SelectedItem as MobileWishlistResponse)?.Id;
            wishlists = await api.GetWishlistsAsync() ?? Array.Empty<MobileWishlistResponse>();
            suppressPersist = true;
            WishlistPicker.ItemsSource = wishlists.ToList();
            WishlistPicker.SelectedItem = wishlists.FirstOrDefault(wishlist => wishlist.Id == previousWishlistId)
                ?? wishlists.FirstOrDefault(wishlist => wishlist.Id.ToString().Equals(Preferences.Get("PaperWishlistId", string.Empty), StringComparison.OrdinalIgnoreCase))
                ?? wishlists.FirstOrDefault(wishlist => wishlist.IsDefault)
                ?? wishlists.FirstOrDefault();
            suppressPersist = false;

            RestoreFormState();

            var latestJobs = await api.GetPaperJobsAsync() ?? Array.Empty<BacktestJobSnapshot>();
            var latestAutomationSessions = await api.GetAutomationSessionsAsync() ?? Array.Empty<MobileAutomationSessionSnapshot>();
            lastRun = latestJobs.FirstOrDefault();
            RenderLastRun();

            jobs.Clear();
            foreach (var job in latestJobs.Where(IsActiveJob).Take(12))
            {
                jobs.Add(job);
            }

            automationSessions.Clear();
            foreach (var session in latestAutomationSessions.Take(12))
            {
                automationSessions.Add(session);
            }

            selectedJob = selectedJob is null
                ? null
                : latestJobs.FirstOrDefault(job => job.JobId == selectedJob.JobId);
            RenderSelectedJob();
        }
        catch (Exception exception)
        {
            StatusLabel.Text = $"Error: {exception.Message}";
            StatusLabel.TextColor = Color.FromArgb("#B42318");
            await DisplayAlertAsync("Paper", exception.Message, "OK");
        }
        finally
        {
            isLoading = false;
            BusyIndicator.IsRunning = false;
            BusyIndicator.IsVisible = false;
            RefreshRoot.IsRefreshing = false;
        }
    }

    private async void OnRefresh(object? sender, EventArgs e) => await LoadAsync();

    private async void OnRefreshClicked(object? sender, EventArgs e) => await LoadAsync();

    private async void OnTestNews(object? sender, EventArgs e)
    {
        if (ConfigPicker.SelectedItem is not MobileRunConfigOption config)
        {
            await DisplayAlertAsync("News", "Select a paper environment first.", "OK");
            return;
        }

        try
        {
            NewsStatusLabel.Text = "Loading catalyst news...";
            var tickers = GetSelectedWishlistTickers();
            var feed = await api.GetNewsFeedAsync(config.Path, tickers, 48);
            newsItems.Clear();

            if (feed is null)
            {
                NewsStatusLabel.Text = "No news response returned.";
                return;
            }

            NewsStatusLabel.Text = feed.Enabled
                ? $"Provider {feed.Provider}: {feed.Items.Count} catalyst item(s)."
                : feed.Message ?? $"Provider {feed.Provider}: news disabled.";
            foreach (var item in feed.Items)
            {
                newsItems.Add(item);
            }
        }
        catch (Exception exception)
        {
            NewsStatusLabel.Text = $"News error: {exception.Message}";
            await DisplayAlertAsync("News", exception.Message, "OK");
        }
    }

    private async void OnStartPaperRun(object? sender, EventArgs e)
    {
        if (ConfigPicker.SelectedItem is not MobileRunConfigOption config ||
            StrategyPicker.SelectedItem is not MobileStrategyOption strategy)
        {
            await DisplayAlertAsync("Paper", "Select a paper environment and strategy.", "OK");
            return;
        }

        try
        {
            RunButton.IsEnabled = false;
            var request = new MobilePaperRunRequest(
                config.Path,
                strategy.Path,
                String.IsNullOrWhiteSpace(RunNameEntry.Text)
                    ? $"paper_{DateTimeOffset.Now:yyyyMMdd_HHmmss}"
                    : RunNameEntry.Text.Trim(),
                Array.Empty<string>(),
                String.IsNullOrWhiteSpace(ScreenerEntry.Text) ? null : ScreenerEntry.Text.Trim(),
                AllowExtendedHoursTradingCheck.IsChecked,
                NewsCheck.IsChecked,
                OrderExpirationPicker.SelectedItem?.ToString() ?? "day",
                EntryOrderTypePicker.SelectedItem?.ToString() ?? "market",
                (WishlistPicker.SelectedItem as MobileWishlistResponse)?.Id);
            var job = await api.StartPaperRunAsync(request);
            if (job is not null)
            {
                jobs.Insert(0, job);
                selectedJob = job;
                RenderSelectedJob();
                await DisplayAlertAsync("Paper", $"Started {job.RunName}.", "OK");
            }
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Paper", exception.Message, "OK");
        }
        finally
        {
            RunButton.IsEnabled = true;
        }
    }

    private void OnJobSelected(object? sender, SelectionChangedEventArgs e)
    {
        selectedJob = e.CurrentSelection.FirstOrDefault() as BacktestJobSnapshot;
        RenderSelectedJob();
    }

    private void OnJobDetailsClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: BacktestJobSnapshot job })
        {
            selectedJob = job;
            JobsView.SelectedItem = job;
            RenderSelectedJob();
        }
    }

    private void OnOpenLastRunDetails(object? sender, EventArgs e)
    {
        if (lastRun is null)
        {
            return;
        }

        selectedJob = lastRun;
        JobsView.SelectedItem = jobs.FirstOrDefault(job => job.JobId == lastRun.JobId);
        RenderSelectedJob();
    }

    private void RenderLastRun()
    {
        LastRunCard.IsVisible = lastRun is not null;
        if (lastRun is null)
        {
            LastRunNameLabel.Text = string.Empty;
            LastRunStatusLabel.Text = string.Empty;
            return;
        }

        LastRunNameLabel.Text = lastRun.RunName;
        LastRunStatusLabel.Text = $"{lastRun.Status} - {lastRun.LatestEvent}";
    }

    private void RenderSelectedJob()
    {
        DetailCard.IsVisible = selectedJob is not null;
        EventsStack.Clear();
        if (selectedJob is null)
        {
            positions.Clear();
            return;
        }

        DetailTitle.Text = selectedJob.RunName;
        DetailStatus.Text = $"{selectedJob.Status} - {selectedJob.ProgressText}";
        ResultLabel.Text = selectedJob.ResultSummary;
        foreach (var item in selectedJob.Events.Reverse().Take(8))
        {
            EventsStack.Add(new Label
            {
                Text = item,
                FontSize = 12,
                TextColor = Color.FromArgb("#475467")
            });
        }

        _ = LoadPositionsAsync(selectedJob.JobId);
    }

    private async Task LoadPositionsAsync(Guid jobId)
    {
        try
        {
            var latest = await api.GetPaperPositionsAsync(jobId) ?? Array.Empty<MobilePaperPositionResponse>();
            if (selectedJob?.JobId != jobId)
            {
                return; // Selection changed while loading.
            }

            positions.Clear();
            foreach (var position in latest)
            {
                positions.Add(position);
            }

            PositionsEmptyLabel.IsVisible = positions.Count == 0;
        }
        catch
        {
            // Positions are best-effort; leave the last known list intact on transient errors.
        }
    }

    private async void OnCancelSelectedJob(object? sender, EventArgs e)
    {
        if (selectedJob is null)
        {
            return;
        }

        await api.CancelPaperJobAsync(selectedJob.JobId);
        selectedJob = null;
        JobsView.SelectedItem = null;
        RenderSelectedJob();
        await LoadAsync();
    }

    private async void OnCancelBrokerOrders(object? sender, EventArgs e)
    {
        if (selectedJob is null)
        {
            return;
        }

        await api.CancelBrokerOrdersAsync(selectedJob.JobId);
        await DisplayAlertAsync("Paper", "Broker-order cancellation request sent.", "OK");
        await LoadAsync();
    }

    private void OnToggleAdvanced(object? sender, EventArgs e)
    {
        AdvancedPanel.IsVisible = !AdvancedPanel.IsVisible;
        AdvancedToggle.Text = AdvancedPanel.IsVisible ? "Hide advanced options" : "Show advanced options";
    }

    private void OnWishlistPicked(object? sender, EventArgs e)
    {
        SaveFormState();
    }

    private void OnFormChanged(object? sender, EventArgs e) => SaveFormState();

    private void RestoreFormState()
    {
        if (formRestored)
        {
            return;
        }

        suppressPersist = true;
        try
        {
            RunNameEntry.Text = Preferences.Get("PaperRunName", string.Empty);
            ScreenerEntry.Text = Preferences.Get("PaperScreener", string.Empty);
            AllowExtendedHoursTradingCheck.IsChecked = Preferences.Get("AllowExtendedHoursTrading", false);
            NewsCheck.IsChecked = Preferences.Get("PaperNews", false);

            SelectByValue(OrderExpirationPicker, Preferences.Get("PaperOrderExpiration", "day"));
            SelectByValue(EntryOrderTypePicker, Preferences.Get("PaperEntryOrderType", "market"));
            SelectConfigByPath(Preferences.Get("PaperConfigPath", string.Empty));
            SelectStrategyByPath(Preferences.Get("PaperStrategyPath", string.Empty));
        }
        finally
        {
            suppressPersist = false;
            formRestored = true;
        }
    }

    private void SaveFormState()
    {
        if (suppressPersist)
        {
            return;
        }

        Preferences.Set("PaperRunName", RunNameEntry.Text ?? string.Empty);
        Preferences.Set("PaperScreener", ScreenerEntry.Text ?? string.Empty);
        Preferences.Set("AllowExtendedHoursTrading", AllowExtendedHoursTradingCheck.IsChecked);
        Preferences.Set("PaperNews", NewsCheck.IsChecked);
        Preferences.Set("PaperOrderExpiration", OrderExpirationPicker.SelectedItem?.ToString() ?? "day");
        Preferences.Set("PaperEntryOrderType", EntryOrderTypePicker.SelectedItem?.ToString() ?? "market");
        if (ConfigPicker.SelectedItem is MobileRunConfigOption config)
        {
            Preferences.Set("PaperConfigPath", config.Path);
        }

        if (StrategyPicker.SelectedItem is MobileStrategyOption strategy)
        {
            Preferences.Set("PaperStrategyPath", strategy.Path);
        }

        if (WishlistPicker.SelectedItem is MobileWishlistResponse wishlist)
        {
            Preferences.Set("PaperWishlistId", wishlist.Id.ToString());
        }
    }

    private void SelectConfigByPath(string path)
    {
        if (String.IsNullOrWhiteSpace(path) || ConfigPicker.ItemsSource is not IEnumerable<MobileRunConfigOption> configs)
        {
            return;
        }

        var match = configs.FirstOrDefault(config => config.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            ConfigPicker.SelectedItem = match;
        }
    }

    private void SelectStrategyByPath(string path)
    {
        if (String.IsNullOrWhiteSpace(path) || StrategyPicker.ItemsSource is not IEnumerable<MobileStrategyOption> strategies)
        {
            return;
        }

        var match = strategies.FirstOrDefault(strategy => strategy.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            StrategyPicker.SelectedItem = match;
        }
    }

    private static void SelectByValue(Picker picker, string value)
    {
        if (picker.ItemsSource is not IEnumerable<string> options)
        {
            return;
        }

        var index = options.ToList().FindIndex(option => option.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            picker.SelectedIndex = index;
        }
    }

    private IReadOnlyList<string> GetSelectedWishlistTickers()
    {
        return (WishlistPicker.SelectedItem as MobileWishlistResponse)?.Items
            .Where(item => item.Active)
            .Select(item => item.Ticker.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();
    }

    private static bool IsActiveJob(BacktestJobSnapshot job)
    {
        return job.Status.Equals("running", StringComparison.OrdinalIgnoreCase) ||
            job.Status.Equals("starting", StringComparison.OrdinalIgnoreCase) ||
            job.Status.Equals("queued", StringComparison.OrdinalIgnoreCase);
    }
}
