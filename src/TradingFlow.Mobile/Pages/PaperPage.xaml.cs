using TradingFlow.Contracts.V1;
using System.Collections.ObjectModel;
using TradingFlow.Mobile.Services;
using TradingFlow.Mobile.Resources.Styles;

namespace TradingFlow.Mobile.Pages;

public partial class PaperPage : ContentPage, IQueryAttributable
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
    private StartPaperRunRequest? pendingRequest;
    private string? preparationKey;
    public void ApplyQueryAttributes(IDictionary<string,object> query)
    { if(query.TryGetValue("preparationKey",out var key)) { preparationKey=Uri.UnescapeDataString(key.ToString()??""); pendingRequest=null; } }
    private bool isLoading;
    private CancellationTokenSource? pageLifetime;
    private bool suppressPersist;
    private bool refreshingForm;
    private bool updatingJobs;
    private bool formRestored;

    public PaperPage()
    {
        InitializeComponent();
        suppressPersist = true;
        JobsView.ItemsSource = jobs;
        NewsView.ItemsSource = newsItems;
        AutomationSessionsView.ItemsSource = automationSessions;
        PositionsView.ItemsSource = positions;
        PaperModePicker.ItemsSource = new[] { "Paper shadow", "Paper experiment" };
        OrderExpirationPicker.ItemsSource = new[] { "day", "gtc" };
        EntryOrderTypePicker.ItemsSource = new[] { "market", "limit" };
        PaperModePicker.SelectedIndex = 0;
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
        pageLifetime?.Cancel(); pageLifetime?.Dispose(); pageLifetime = new();
        api.SessionChanged += OnSessionChanged;
        refreshTimer.Start();
        await LoadAsync();
        if(preparationKey is not null)
        {
            try {var review=await api.Workflow.AcceptedPreparationAsync(preparationKey,pageLifetime.Token);
                StatusLabel.Text=$"Preparation {review.UniverseSnapshotId}: {String.Join(", ",review.Symbols)}. Expires {review.ExpiresAtUtc.ToLocalTime():g}. Source selectors are replaced by this accepted membership.";
                WishlistPicker.IsEnabled=false;ScreenerEntry.IsEnabled=false;
            } catch(Exception ex) {StatusLabel.Text=ex.Message;}
        }
    }

    protected override void OnDisappearing()
    {
        refreshTimer.Stop(); pageLifetime?.Cancel();
        api.SessionChanged -= OnSessionChanged;
        base.OnDisappearing();
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        pageLifetime?.Cancel(); refreshTimer.Stop(); catalog = null; pendingRequest = null; preparationKey = null;
        jobs.Clear(); selectedJob = null; RenderSelectedJob();
        StatusLabel.Text = "Connection changed. Reopen this page after signing in.";
    }

    private async Task LoadAsync(bool showBusy = true)
    {
        if (isLoading)
        {
            return;
        }

        var token = pageLifetime?.Token ?? new CancellationToken(true);
        if (token.IsCancellationRequested) return;
        isLoading = true;
        BusyIndicator.IsVisible = showBusy;
        BusyIndicator.IsRunning = showBusy;
        try
        {
            var healthy = await api.CheckHealthAsync(token);
            token.ThrowIfCancellationRequested();
            StatusLabel.Text = healthy
                ? $"Connected: {api.BaseUrl}"
                : $"Backend unreachable: {api.BaseUrl}";
            StatusLabel.TextColor = healthy ? ThemePalette.Positive : ThemePalette.Negative;

            if (showBusy || catalog is null) {
            refreshingForm = true;
            var previousConfig = (ConfigPicker.SelectedItem as MobileRunConfigOption)?.Path;
            var previousStrategy = (StrategyPicker.SelectedItem as MobileStrategyOption)?.Path;
            catalog = await api.GetCatalogAsync(token);
            token.ThrowIfCancellationRequested();
            ConfigPicker.ItemsSource = catalog?.PaperConfigs.ToList();
            RestoreFormState();
            ApplyPaperModeSelection();
            SelectStrategyByPath(Preferences.Get("PaperStrategyPath", string.Empty));
            ConfigPicker.SelectedIndex = ConfigPicker.SelectedIndex < 0 && ConfigPicker.Items.Count > 0 ? 0 : ConfigPicker.SelectedIndex;
            StrategyPicker.SelectedIndex = StrategyPicker.SelectedIndex < 0 && StrategyPicker.Items.Count > 0 ? 0 : StrategyPicker.SelectedIndex;
            RunButton.IsEnabled = StrategyPicker.Items.Count > 0;
            if (StrategyPicker.Items.Count == 0)
            {
                StatusLabel.Text = PaperModePicker.SelectedIndex == 1
                    ? "Connected. No paper-experiment strategy is registered."
                    : "Connected. No strategy is authorized for paper shadow.";
                StatusLabel.TextColor = ThemePalette.Warning;
            }

            var previousWishlistId = (WishlistPicker.SelectedItem as MobileWishlistResponse)?.Id;
            wishlists = await api.GetWishlistsAsync(token) ?? Array.Empty<MobileWishlistResponse>();
            token.ThrowIfCancellationRequested();
            suppressPersist = true;
            WishlistPicker.ItemsSource = wishlists.ToList();
            WishlistPicker.SelectedItem = wishlists.FirstOrDefault(wishlist => wishlist.Id == previousWishlistId)
                ?? wishlists.FirstOrDefault(wishlist => wishlist.Id.ToString().Equals(Preferences.Get("PaperWishlistId", string.Empty), StringComparison.OrdinalIgnoreCase))
                ?? wishlists.FirstOrDefault(wishlist => wishlist.IsDefault)
                ?? wishlists.FirstOrDefault();
            suppressPersist = false;

            if (previousConfig is not null) SelectConfigByPath(previousConfig);
            if (previousStrategy is not null) SelectStrategyByPath(previousStrategy);
            refreshingForm = false;
            }
            var latestJobs = await api.GetPaperJobsAsync(token) ?? Array.Empty<BacktestJobSnapshot>();
            token.ThrowIfCancellationRequested();
            var latestAutomationSessions = await api.GetAutomationSessionsAsync(token) ?? Array.Empty<MobileAutomationSessionSnapshot>();
            token.ThrowIfCancellationRequested();
            lastRun = latestJobs.FirstOrDefault();
            RenderLastRun();

            updatingJobs = true;
            jobs.Clear();
            foreach (var job in latestJobs.Where(IsActiveJob).Take(12))
            {
                jobs.Add(job);
            }

            JobsView.SelectedItem = jobs.FirstOrDefault(item => item.JobId == selectedJob?.JobId);
            updatingJobs = false;
            automationSessions.Clear();
            foreach (var session in latestAutomationSessions.Take(12))
            {
                automationSessions.Add(session);
            }

            if (selectedJob is { } selection) await RefreshSelectedAsync(selection.JobId, token);
            token.ThrowIfCancellationRequested();
            RenderSelectedJob();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            StatusLabel.Text = $"Error: {exception.Message}";
            StatusLabel.TextColor = ThemePalette.Negative;
            await DisplayAlertAsync("Paper", exception.Message, "OK");
        }
        finally
        {
            refreshingForm = false; updatingJobs = false;
            isLoading = false;
            BusyIndicator.IsRunning = false;
            BusyIndicator.IsVisible = false;
            RefreshRoot.IsRefreshing = false;
        }
    }

    private void ApplyPaperModeSelection()
    {
        var experimentMode = PaperModePicker.SelectedIndex == 1;
        var selectedPath = (StrategyPicker.SelectedItem as MobileStrategyOption)?.Path
            ?? Preferences.Get("PaperStrategyPath", string.Empty);
        StrategyPicker.ItemsSource = experimentMode
            ? catalog?.PaperExperimentStrategies.ToList()
            : catalog?.PaperShadowStrategies.ToList();
        SelectStrategyByPath(selectedPath);
        if (StrategyPicker.SelectedIndex < 0)
        {
            StrategyPicker.SelectedIndex = StrategyPicker.Items.Count > 0 ? 0 : -1;
        }
        RunButton.IsEnabled = StrategyPicker.Items.Count > 0;
    }

    private void OnPaperModeChanged(object? sender, EventArgs e)
    {
        ApplyPaperModeSelection();
        SaveFormState();
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
            if (pendingRequest is null)
            {
                var sources = new List<UniverseSourceSelectionRequest>();
                if (WishlistPicker.SelectedItem is MobileWishlistResponse wishlist)
                    sources.Add(new("wishlist", wishlist.Id.ToString(), wishlist.Id));
                if (!String.IsNullOrWhiteSpace(ScreenerEntry.Text))
                    sources.Add(new("finviz", ScreenerEntry.Text.Trim(), Query: ScreenerEntry.Text.Trim()));
                Guid universeId;
                if(preparationKey is not null) {
                    var accepted=await api.Workflow.AcceptedPreparationAsync(preparationKey);
                    if(DateTimeOffset.UtcNow>=accepted.ExpiresAtUtc)throw new InvalidOperationException("Preparation expired. Collect a new morning review.");
                    universeId=accepted.UniverseSnapshotId;
                } else universeId=(await api.Workflow.PreviewAsync(new(ContractVersions.VersionOne,"swing",null,sources))).UniverseSnapshotId;
                pendingRequest = new(ContractVersions.VersionOne,
                    String.IsNullOrWhiteSpace(RunNameEntry.Text) ? $"paper_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}" : RunNameEntry.Text.Trim(),
                    universeId, new(strategy.ArtifactStrategyId, strategy.SemanticVersion, strategy.ContentSha256),
                    NewsCheck.IsChecked, AllowExtendedHoursTradingCheck.IsChecked, EntryOrderTypePicker.SelectedItem?.ToString() ?? "limit",
                    OrderExpirationPicker.SelectedItem?.ToString() ?? "day", "strategy_validated", Guid.NewGuid().ToString("N"),
                    PaperModePicker.SelectedIndex == 1 ? "paper_experiment" : "paper_shadow", config.FileName);
            }
            var job = await api.StartPaperRunAsync(pendingRequest);
            pendingRequest = null;            if (job is not null)
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
        if (updatingJobs) return;
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

    private async Task RefreshSelectedAsync(Guid id, CancellationToken token)
    {
        var detail = await api.GetPaperJobAsync(id, token);
        if (!token.IsCancellationRequested && selectedJob?.JobId == id) selectedJob = detail ?? selectedJob;
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
        var candidateLink = new Button { Text = "Open this run's candidates and audit" };
        var candidateRunId = selectedJob.JobId;
        candidateLink.Clicked += async (_, _) => await Shell.Current.GoToAsync(nameof(CandidatesPage) + "?runId=" + candidateRunId);
        EventsStack.Add(candidateLink);
        DetailStatus.Text = $"{selectedJob.Status} - {selectedJob.ProgressText}";
        ResultLabel.Text = selectedJob.ResultSummary;
        foreach (var item in selectedJob.Events.Reverse().Take(8))
        {
            EventsStack.Add(new Label
            {
                Text = item,
                FontSize = 12,
                TextColor = ThemePalette.TextSecondary
            });
        }

        _ = LoadPositionsAsync(selectedJob.JobId);
    }

    private async Task LoadPositionsAsync(Guid jobId)
    {
        try
        {
            var token = pageLifetime?.Token ?? new CancellationToken(true);
            var latest = await api.GetPaperPositionsAsync(jobId, token) ?? Array.Empty<MobilePaperPositionResponse>();
            if (token.IsCancellationRequested || selectedJob?.JobId != jobId)
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

    private void OnFormChanged(object? sender, EventArgs e)
    {
        if (!suppressPersist && !refreshingForm) pendingRequest = null;
        if (AllowExtendedHoursTradingCheck.IsChecked)
        {
            SelectByValue(OrderExpirationPicker, "day");
            SelectByValue(EntryOrderTypePicker, "limit");
        }

        SaveFormState();
    }

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
            PaperModePicker.SelectedIndex = Preferences.Get("PaperExecutionMode", "shadow") == "experiment" ? 1 : 0;
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
        if (!suppressPersist && !refreshingForm) pendingRequest = null;
        if (suppressPersist || refreshingForm)
        {
            return;
        }

        Preferences.Set("PaperRunName", RunNameEntry.Text ?? string.Empty);
        Preferences.Set("PaperExecutionMode", PaperModePicker.SelectedIndex == 1 ? "experiment" : "shadow");
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
        if (index >= 0 && picker.SelectedIndex != index)
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
