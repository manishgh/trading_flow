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
    private TradingLimitProfileResponse? tradingLimits;
    private string? preparationKey;
    private long lastSeenSessionVersion;
    private bool startingRun;

    /// <summary>Binds an accepted preparation link to the currently signed-in connection rather than a prior hidden-page owner.</summary>
    public void ApplyQueryAttributes(IDictionary<string,object> query)
    {
        if (lastSeenSessionVersion != api.SessionVersion) OnSessionChanged(this, EventArgs.Empty);
        if (query.TryGetValue("preparationKey", out var key)) {
            preparationKey = Uri.UnescapeDataString(key.ToString() ?? "");
            pendingRequest = null;
        }
    }
    private bool isLoading;
    private CancellationTokenSource? pageLifetime;
    private bool suppressPersist;
    private bool refreshingForm;
    private bool updatingJobs;
    private bool formRestored;

    /// <summary>Initializes page controls and records which account owns any retained retry command.</summary>
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
        lastSeenSessionVersion = api.SessionVersion;
        suppressPersist = false;
        refreshTimer = Dispatcher.CreateTimer();
        refreshTimer.Interval = TimeSpan.FromSeconds(6);
        refreshTimer.Tick += async (_, _) => await LoadAsync(showBusy: false);
    }

    /// <summary>Detects connection changes missed while hidden, then loads only this appearance's account data.</summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (lastSeenSessionVersion != api.SessionVersion) OnSessionChanged(this, EventArgs.Empty);
        pageLifetime?.Cancel(); pageLifetime?.Dispose(); pageLifetime = new();
        isLoading = false; startingRun = false;
        var token = pageLifetime.Token;
        var version = api.SessionVersion;
        api.SessionChanged += OnSessionChanged;
        refreshTimer.Start();
        await LoadAsync();
        if (!IsCurrentOwner(version, token)) return;
        if (preparationKey is { } key)
        {
            try {
                var review = await api.Workflow.AcceptedPreparationAsync(key, token);
                RequireCurrentOwner(version, token);
                StatusLabel.Text=$"Preparation {review.UniverseSnapshotId}: {String.Join(", ",review.Symbols)}. Expires {review.ExpiresAtUtc.ToLocalTime():g}. Source selectors are replaced by this accepted membership.";
                WishlistPicker.IsEnabled=false;ScreenerEntry.IsEnabled=false;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (IsCurrentOwner(version, token)) StatusLabel.Text = ex.Message; }
        }
    }

    /// <summary>Cancels all calls for this appearance; the next appearance verifies changes missed while unsubscribed.</summary>
    protected override void OnDisappearing()
    {
        refreshTimer.Stop(); pageLifetime?.Cancel();
        api.SessionChanged -= OnSessionChanged;
        base.OnDisappearing();
    }

    /// <summary>Clears account-owned rows, settings and accepted preparation/retry identities before another connection can reuse them.</summary>
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        lastSeenSessionVersion = api.SessionVersion;
        pageLifetime?.Cancel(); refreshTimer.Stop(); catalog = null; pendingRequest = null; preparationKey = null;
        tradingLimits = null; DailyNewStockLimitEntry.Text = String.Empty; AccountOpenPositionLimitEntry.Text = String.Empty;
        wishlists = []; ConfigPicker.ItemsSource = null; StrategyPicker.ItemsSource = null; WishlistPicker.ItemsSource = null;
        jobs.Clear(); automationSessions.Clear(); newsItems.Clear(); positions.Clear(); selectedJob = null; lastRun = null;
        RenderSelectedJob(); RenderLastRun();
        WishlistPicker.IsEnabled = true; ScreenerEntry.IsEnabled = true;
        RunButton.IsEnabled = false; SaveTradingLimitsButton.IsEnabled = false;
        NewsStatusLabel.Text = String.Empty; TradingLimitRuleLabel.Text = String.Empty; TradingLimitStatusLabel.Text = String.Empty;
        StatusLabel.Text = "Connection changed. Reopen this page after signing in.";
    }

    /// <summary>Loads read models with a captured owner and lifetime so delayed responses cannot repopulate another account's page.</summary>
    private async Task LoadAsync(bool showBusy = true)
    {
        if (isLoading)
        {
            return;
        }

        var token = pageLifetime?.Token ?? new CancellationToken(true);
        var version = api.SessionVersion;
        if (!IsCurrentOwner(version, token)) return;
        isLoading = true;
        BusyIndicator.IsVisible = showBusy;
        BusyIndicator.IsRunning = showBusy;
        try
        {
            var healthy = await api.CheckHealthAsync(token);
            RequireCurrentOwner(version, token);
            StatusLabel.Text = healthy
                ? $"Connected: {api.BaseUrl}"
                : $"Backend unreachable: {api.BaseUrl}";
            StatusLabel.TextColor = healthy ? ThemePalette.Positive : ThemePalette.Negative;

            if (showBusy || catalog is null) {
            refreshingForm = true;
            var previousConfig = (ConfigPicker.SelectedItem as MobileRunConfigOption)?.Path;
            var previousStrategy = (StrategyPicker.SelectedItem as MobileStrategyOption)?.Path;
            var loadedCatalog = await api.GetCatalogAsync(token);
            RequireCurrentOwner(version, token);
            var loadedLimits = await api.Workflow.TradingLimitsAsync("paper", token);
            RequireCurrentOwner(version, token);
            catalog = loadedCatalog;
            tradingLimits = loadedLimits;
            DailyNewStockLimitEntry.Text = tradingLimits.MaxNewStocksPerDay.ToString();
            AccountOpenPositionLimitEntry.Text = tradingLimits.MaxOpenPositions.ToString();
            TradingLimitRuleLabel.Text = tradingLimits.CountingRule;
            TradingLimitStatusLabel.Text = "Loaded account limits. Saving does not start a run.";
            SaveTradingLimitsButton.IsEnabled = true;
            RequireCurrentOwner(version, token);
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
            var loadedWishlists = await api.GetWishlistsAsync(token) ?? Array.Empty<MobileWishlistResponse>();
            RequireCurrentOwner(version, token);
            wishlists = loadedWishlists;
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
            RequireCurrentOwner(version, token);
            var latestAutomationSessions = await api.GetAutomationSessionsAsync(token) ?? Array.Empty<MobileAutomationSessionSnapshot>();
            RequireCurrentOwner(version, token);
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
            RequireCurrentOwner(version, token);
            RenderSelectedJob();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!IsCurrentOwner(version, token)) return;
            StatusLabel.Text = $"Error: {exception.Message}";
            StatusLabel.TextColor = ThemePalette.Negative;
            await DisplayAlertAsync("Paper", exception.Message, "OK");
        }
        finally
        {
            if (IsCurrentOwner(version, token))
            {
                refreshingForm = false; updatingJobs = false;
                isLoading = false;
                BusyIndicator.IsRunning = false;
                BusyIndicator.IsVisible = false;
                RefreshRoot.IsRefreshing = false;
            }
        }
    }

    /// <summary>Accepts UI updates only for the captured account and still-visible page lifetime.</summary>
    private bool IsCurrentOwner(long version, CancellationToken token) =>
        !token.IsCancellationRequested && version == api.SessionVersion && version == lastSeenSessionVersion;

    /// <summary>Stops processing before assigning a delayed response from an earlier account or appearance.</summary>
    private void RequireCurrentOwner(long version, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrentOwner(version, token)) throw new OperationCanceledException("The paper page's signed-in connection changed.");
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

    /// <summary>Saves the common paper-account limits without submitting a run or an order.</summary>
    private async void OnSaveTradingLimits(object? sender, EventArgs e)
    {
        if (tradingLimits is null) return;
        var lifetime = pageLifetime;
        var token = lifetime?.Token ?? new CancellationToken(true);
        var version = api.SessionVersion;
        var profile = tradingLimits;
        if (!IsCurrentOwner(version, token)) return;
        if (!Int32.TryParse(DailyNewStockLimitEntry.Text, out var daily) || daily <= 0 ||
            !Int32.TryParse(AccountOpenPositionLimitEntry.Text, out var open) || open <= 0)
        {
            TradingLimitStatusLabel.Text = "Both limits must be positive whole numbers.";
            return;
        }
        SaveTradingLimitsButton.IsEnabled = false;
        try
        {
            var saved = await api.Workflow.SaveTradingLimitsAsync(new(ContractVersions.VersionOne, "paper", daily, open, profile.Revision), token);
            if (!IsCurrentOwner(version, token) || !ReferenceEquals(lifetime, pageLifetime)) return;
            tradingLimits = saved;
            TradingLimitRuleLabel.Text = tradingLimits.CountingRule;
            TradingLimitStatusLabel.Text = "Account limits saved. No run has started.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (IsCurrentOwner(version, token) && ReferenceEquals(lifetime, pageLifetime)) TradingLimitStatusLabel.Text = error.Message; }
        finally { if (IsCurrentOwner(version, token) && ReferenceEquals(lifetime, pageLifetime)) SaveTradingLimitsButton.IsEnabled = true; }
    }

    private async void OnRefresh(object? sender, EventArgs e) => await LoadAsync();

    private async void OnRefreshClicked(object? sender, EventArgs e) => await LoadAsync();

    /// <summary>Shows catalyst news only while the captured page and signed-in connection still own the response.</summary>
    private async void OnTestNews(object? sender, EventArgs e)
    {
        var token = pageLifetime?.Token ?? new CancellationToken(true);
        var version = api.SessionVersion;
        if (!IsCurrentOwner(version, token)) return;
        if (ConfigPicker.SelectedItem is not MobileRunConfigOption config)
        {
            await DisplayAlertAsync("News", "Select a paper environment first.", "OK");
            return;
        }

        try
        {
            NewsStatusLabel.Text = "Loading catalyst news...";
            var tickers = GetSelectedWishlistTickers();
            var feed = await api.GetNewsFeedAsync(config.Path, tickers, 48, token);
            RequireCurrentOwner(version, token);
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
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!IsCurrentOwner(version, token)) return;
            NewsStatusLabel.Text = $"News error: {exception.Message}";
            await DisplayAlertAsync("News", exception.Message, "OK");
        }
    }

    /// <summary>Retains an uncertain command for the same owner, and prevents late acceptance from restoring a prior account's run.</summary>
    private async void OnStartPaperRun(object? sender, EventArgs e)
    {
        var token = pageLifetime?.Token ?? new CancellationToken(true);
        var version = api.SessionVersion;
        if (startingRun || !IsCurrentOwner(version, token)) return;
        if (ConfigPicker.SelectedItem is not MobileRunConfigOption config ||
            StrategyPicker.SelectedItem is not MobileStrategyOption strategy)
        {
            await DisplayAlertAsync("Paper", "Select a paper environment and strategy.", "OK");
            return;
        }

        try
        {
            startingRun = true;
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
                    var accepted=await api.Workflow.AcceptedPreparationAsync(preparationKey, token);
                    RequireCurrentOwner(version, token);
                    if(DateTimeOffset.UtcNow>=accepted.ExpiresAtUtc)throw new InvalidOperationException("Preparation expired. Collect a new morning review.");
                    universeId=accepted.UniverseSnapshotId;
                } else {
                    var preview = await api.Workflow.PreviewAsync(new(ContractVersions.VersionOne,"swing",null,sources), token);
                    RequireCurrentOwner(version, token);
                    universeId = preview.UniverseSnapshotId;
                }
                pendingRequest = new(ContractVersions.VersionOne,
                    String.IsNullOrWhiteSpace(RunNameEntry.Text) ? $"paper_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}" : RunNameEntry.Text.Trim(),
                    universeId, new(strategy.ArtifactStrategyId, strategy.SemanticVersion, strategy.ContentSha256),
                    NewsCheck.IsChecked, AllowExtendedHoursTradingCheck.IsChecked, EntryOrderTypePicker.SelectedItem?.ToString() ?? "limit",
                    OrderExpirationPicker.SelectedItem?.ToString() ?? "day", "strategy_validated", Guid.NewGuid().ToString("N"),
                    PaperModePicker.SelectedIndex == 1 ? "paper_experiment" : "paper_shadow", config.FileName);
            }
            var command = pendingRequest;
            var job = await api.StartPaperRunAsync(command, token);
            RequireCurrentOwner(version, token);
            if (ReferenceEquals(command, pendingRequest)) pendingRequest = null;
            if (job is not null)
            {
                jobs.Insert(0, job);
                selectedJob = job;
                RenderSelectedJob();
                await DisplayAlertAsync("Paper", $"Started {job.RunName}.", "OK");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!IsCurrentOwner(version, token)) return;
            await DisplayAlertAsync("Paper", exception.Message, "OK");
        }
        finally
        {
            if (IsCurrentOwner(version, token)) { startingRun = false; RunButton.IsEnabled = StrategyPicker.Items.Count > 0; }
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

    /// <summary>Refreshes details without letting a prior account's response replace the current selection.</summary>
    private async Task RefreshSelectedAsync(Guid id, CancellationToken token)
    {
        var version = api.SessionVersion;
        var detail = await api.GetPaperJobAsync(id, token);
        if (IsCurrentOwner(version, token) && selectedJob?.JobId == id) selectedJob = detail ?? selectedJob;
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

    /// <summary>Reads selected-run positions only for the appearance and account that requested them.</summary>
    private async Task LoadPositionsAsync(Guid jobId)
    {
        var token = pageLifetime?.Token ?? new CancellationToken(true);
        var version = api.SessionVersion;
        if (!IsCurrentOwner(version, token)) return;
        try
        {
            var latest = await api.GetPaperPositionsAsync(jobId, token) ?? Array.Empty<MobilePaperPositionResponse>();
            if (!IsCurrentOwner(version, token) || selectedJob?.JobId != jobId)
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

    /// <summary>Cancels the captured run without clearing another account's later selection.</summary>
    private async void OnCancelSelectedJob(object? sender, EventArgs e)
    {
        var token = pageLifetime?.Token ?? new CancellationToken(true);
        var version = api.SessionVersion;
        if (selectedJob is not { } job || !IsCurrentOwner(version, token)) return;
        try
        {
            await api.CancelPaperJobAsync(job.JobId, token);
            RequireCurrentOwner(version, token);
            if (selectedJob?.JobId == job.JobId) { selectedJob = null; JobsView.SelectedItem = null; RenderSelectedJob(); }
            await LoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (IsCurrentOwner(version, token)) await DisplayAlertAsync("Paper", error.Message, "OK"); }
    }

    /// <summary>Requests cancellation for this account's selected run and suppresses acknowledgements after owner changes.</summary>
    private async void OnCancelBrokerOrders(object? sender, EventArgs e)
    {
        var token = pageLifetime?.Token ?? new CancellationToken(true);
        var version = api.SessionVersion;
        if (selectedJob is not { } job || !IsCurrentOwner(version, token)) return;
        try
        {
            await api.CancelBrokerOrdersAsync(job.JobId, token);
            RequireCurrentOwner(version, token);
            await DisplayAlertAsync("Paper", "Broker-order cancellation request sent.", "OK");
            if (IsCurrentOwner(version, token)) await LoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (IsCurrentOwner(version, token)) await DisplayAlertAsync("Paper", error.Message, "OK"); }
    }

    // Keep account settings available without placing editable limits above every strategy run.
    private void OnToggleLimits(object? sender, EventArgs e)
    {
        TradingLimitsCard.IsVisible = !TradingLimitsCard.IsVisible;
        LimitsToggle.Text = TradingLimitsCard.IsVisible ? "Account limits −" : "Account limits +";
    }

    // Disclose optional run inputs while retaining the same draft and existing start command.
    private void OnToggleAdvanced(object? sender, EventArgs e)
    {
        AdvancedPanel.IsVisible = !AdvancedPanel.IsVisible;
        AdvancedToggle.Text = AdvancedPanel.IsVisible ? "Run settings −" : "Run settings +";
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
