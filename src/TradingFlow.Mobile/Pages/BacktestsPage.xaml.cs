using TradingFlow.Contracts.V1;
using System.Collections.ObjectModel;
using TradingFlow.Mobile.Services;
using TradingFlow.Mobile.Resources.Styles;

namespace TradingFlow.Mobile.Pages;

public partial class BacktestsPage : ContentPage
{
    private readonly TradingFlowApiClient api = AppServices.Api;
    private readonly ObservableCollection<BacktestJobSnapshot> jobs = new();
    private readonly IDispatcherTimer refreshTimer;
    private MobileCatalogResponse? catalog;
    private IReadOnlyList<MobileWishlistResponse> wishlists = Array.Empty<MobileWishlistResponse>();
    private BacktestJobSnapshot? selectedJob;
    private StartBacktestRequest? pendingRequest;
    private bool isLoading;
    private CancellationTokenSource? pageLifetime;
    private bool suppressPersist;
    private bool refreshingForm;
    private bool updatingJobs;
    private bool formRestored;

    public BacktestsPage()
    {
        InitializeComponent();
        suppressPersist = true;
        JobsView.ItemsSource = jobs;
        CachePolicyPicker.ItemsSource = new[] { "reuse", "refresh" };
        CachePolicyPicker.SelectedIndex = 0;
        suppressPersist = false;
        refreshTimer = Dispatcher.CreateTimer();
        refreshTimer.Interval = TimeSpan.FromSeconds(8);
        refreshTimer.Tick += async (_, _) => await LoadAsync(showBusy: false);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        pageLifetime?.Cancel(); pageLifetime?.Dispose(); pageLifetime = new();
        api.SessionChanged += OnSessionChanged;
        refreshTimer.Start();
        await LoadAsync();
    }

    protected override void OnDisappearing()
    {
        refreshTimer.Stop(); pageLifetime?.Cancel();
        api.SessionChanged -= OnSessionChanged;
        base.OnDisappearing();
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        pageLifetime?.Cancel(); refreshTimer.Stop(); catalog = null; pendingRequest = null;
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
            ConfigPicker.ItemsSource = catalog?.BacktestConfigs.ToList();
            StrategyPicker.ItemsSource = catalog?.Strategies.ToList();
            ConfigPicker.SelectedIndex = ConfigPicker.SelectedIndex < 0 && ConfigPicker.Items.Count > 0 ? 0 : ConfigPicker.SelectedIndex;
            StrategyPicker.SelectedIndex = StrategyPicker.SelectedIndex < 0 && StrategyPicker.Items.Count > 0 ? 0 : StrategyPicker.SelectedIndex;

            var previousWishlistId = (WishlistPicker.SelectedItem as MobileWishlistResponse)?.Id;
            wishlists = await api.GetWishlistsAsync(token) ?? Array.Empty<MobileWishlistResponse>();
            token.ThrowIfCancellationRequested();
            suppressPersist = true;
            WishlistPicker.ItemsSource = wishlists.ToList();
            WishlistPicker.SelectedItem = wishlists.FirstOrDefault(wishlist => wishlist.Id == previousWishlistId)
                ?? wishlists.FirstOrDefault(wishlist => wishlist.Id.ToString().Equals(Preferences.Get("BacktestWishlistId", string.Empty), StringComparison.OrdinalIgnoreCase))
                ?? wishlists.FirstOrDefault(wishlist => wishlist.IsDefault)
                ?? wishlists.FirstOrDefault();
            suppressPersist = false;

            RestoreFormState();

            if (previousConfig is not null) SelectConfigByPath(previousConfig);
            if (previousStrategy is not null) SelectStrategyByPath(previousStrategy);
            refreshingForm = false;
            }
            var latestJobs = await api.GetBacktestJobsAsync(token) ?? Array.Empty<BacktestJobSnapshot>();
            token.ThrowIfCancellationRequested();
            updatingJobs = true;
            jobs.Clear();
            foreach (var job in latestJobs.Take(20))
            {
                jobs.Add(job);
            }

            JobsView.SelectedItem = jobs.FirstOrDefault(item => item.JobId == selectedJob?.JobId);
            updatingJobs = false;
            if (selectedJob is { } selection) await RefreshSelectedAsync(selection.JobId, token);
            token.ThrowIfCancellationRequested();
            RenderSelectedJob();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            StatusLabel.Text = $"Error: {exception.Message}";
            StatusLabel.TextColor = ThemePalette.Negative;
            await DisplayAlertAsync("Backtests", exception.Message, "OK");
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

    private async void OnRefresh(object? sender, EventArgs e) => await LoadAsync();

    private async void OnRefreshClicked(object? sender, EventArgs e) => await LoadAsync();

    private async void OnStartBacktest(object? sender, EventArgs e)
    {
        if (ConfigPicker.SelectedItem is not MobileRunConfigOption config)
        {
            await DisplayAlertAsync("Backtests", "Select a backtest profile.", "OK");
            return;
        }

        if (WishlistPicker.SelectedItem is not MobileWishlistResponse wishlist)
        {
            await DisplayAlertAsync("Backtests", "Select a wishlist.", "OK");
            return;
        }

        var strategies = SelectStrategies();
        if (strategies.Length == 0)
        {
            await DisplayAlertAsync("Backtests", "Select at least one strategy.", "OK");
            return;
        }

        try
        {
            RunButton.IsEnabled = false;
            if (pendingRequest is null)
            {
                var preview = await api.Workflow.PreviewAsync(new(ContractVersions.VersionOne, "swing", null,
                    [new("wishlist", wishlist.Id.ToString(), wishlist.Id)]));
                var end = DateTimeOffset.UtcNow;
                pendingRequest = new(ContractVersions.VersionOne, $"bt_{end:yyyyMMdd_HHmmss}", preview.UniverseSnapshotId,
                    strategies.Select(item => new StrategyReference(item.ArtifactStrategyId, item.SemanticVersion, item.ContentSha256)).ToArray(),
                    end.AddDays(-ParseInt(LookbackEntry.Text, 60)), end, ParseDecimal(CapitalEntry.Text, 10000m),
                    ParseDecimal(RiskEntry.Text, 1m), ParseDecimal(MaxPositionEntry.Text, 25m), ParseInt(MaxConcurrentEntry.Text, 4),
                    CachePolicyPicker.SelectedItem?.ToString() == "refresh" ? "refresh" : "use_cache", "full", Guid.NewGuid().ToString("N"), config.FileName);
            }
            var job = await api.StartBacktestRunAsync(pendingRequest);
            pendingRequest = null;            if (job is not null)
            {
                jobs.Insert(0, job);
                selectedJob = job;
                RenderSelectedJob();
                await DisplayAlertAsync("Backtests", $"Started {job.RunName}.", "OK");
            }
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Backtests", exception.Message, "OK");
        }
        finally
        {
            RunButton.IsEnabled = true;
        }
    }

    private void OnToggleAdvanced(object? sender, EventArgs e)
    {
        AdvancedPanel.IsVisible = !AdvancedPanel.IsVisible;
        AdvancedToggle.Text = AdvancedPanel.IsVisible ? "Hide advanced parameters" : "Show advanced parameters";
    }

    private void OnWishlistPicked(object? sender, EventArgs e)
    {
        if (WishlistPicker.SelectedItem is not MobileWishlistResponse wishlist)
        {
            return;
        }

        SaveFormState();
    }

    private void OnFormChanged(object? sender, EventArgs e) { if (!suppressPersist && !refreshingForm) pendingRequest = null; SaveFormState(); }

    private void RestoreFormState()
    {
        if (formRestored)
        {
            return;
        }

        suppressPersist = true;
        try
        {
            LookbackEntry.Text = Preferences.Get("BacktestLookback", "60");
            CapitalEntry.Text = Preferences.Get("BacktestCapital", "10000");
            RiskEntry.Text = Preferences.Get("BacktestRisk", "1");
            MaxPositionEntry.Text = Preferences.Get("BacktestMaxPosition", "25");
            MaxConcurrentEntry.Text = Preferences.Get("BacktestMaxConcurrent", "4");
            RunAllStrategiesCheck.IsChecked = Preferences.Get("BacktestRunAll", false);
            SelectByValue(CachePolicyPicker, Preferences.Get("BacktestCachePolicy", "reuse"));
            SelectConfigByPath(Preferences.Get("BacktestConfigPath", string.Empty));
            SelectStrategyByPath(Preferences.Get("BacktestStrategyPath", string.Empty));
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

        Preferences.Set("BacktestLookback", LookbackEntry.Text ?? "60");
        Preferences.Set("BacktestCapital", CapitalEntry.Text ?? "10000");
        Preferences.Set("BacktestRisk", RiskEntry.Text ?? "1");
        Preferences.Set("BacktestMaxPosition", MaxPositionEntry.Text ?? "25");
        Preferences.Set("BacktestMaxConcurrent", MaxConcurrentEntry.Text ?? "4");
        Preferences.Set("BacktestRunAll", RunAllStrategiesCheck.IsChecked);
        Preferences.Set("BacktestCachePolicy", CachePolicyPicker.SelectedItem?.ToString() ?? "reuse");
        if (WishlistPicker.SelectedItem is MobileWishlistResponse wishlist)
        {
            Preferences.Set("BacktestWishlistId", wishlist.Id.ToString());
        }

        if (ConfigPicker.SelectedItem is MobileRunConfigOption config)
        {
            Preferences.Set("BacktestConfigPath", config.Path);
        }

        if (StrategyPicker.SelectedItem is MobileStrategyOption strategy)
        {
            Preferences.Set("BacktestStrategyPath", strategy.Path);
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

    private MobileStrategyOption[] SelectStrategies()
    {
        if (catalog is null)
        {
            return Array.Empty<MobileStrategyOption>();
        }

        if (RunAllStrategiesCheck.IsChecked)
        {
            return catalog.Strategies.ToArray();
        }

        return StrategyPicker.SelectedItem is MobileStrategyOption strategy
            ? new[] { strategy }
            : Array.Empty<MobileStrategyOption>();
    }

    private async void OnJobSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (updatingJobs) return;
        selectedJob = e.CurrentSelection.FirstOrDefault() as BacktestJobSnapshot;
        if (selectedJob is { } selection) { try { await RefreshSelectedAsync(selection.JobId, pageLifetime?.Token ?? new CancellationToken(true)); } catch (OperationCanceledException) { } catch (HttpRequestException) { } }
        RenderSelectedJob();
    }

    private async Task RefreshSelectedAsync(Guid id, CancellationToken token)
    {
        var detail = await api.GetBacktestJobAsync(id, token);
        if (!token.IsCancellationRequested && selectedJob?.JobId == id) selectedJob = detail ?? selectedJob;
    }
    private void RenderSelectedJob()
    {
        DetailCard.IsVisible = selectedJob is not null;
        EventsStack.Clear();
        StrategyGroupsStack.Clear();
        if (selectedJob is null)
        {
            return;
        }

        DetailTitle.Text = selectedJob.RunName;
        DetailStatus.Text = $"{selectedJob.Status} - {selectedJob.ProgressText}";
        ResultLabel.Text = selectedJob.ResultSummary;

        foreach (var group in selectedJob.StrategyGroups ?? Array.Empty<BacktestStrategyRunGroup>())
        {
            StrategyGroupsStack.Add(new Label
            {
                Text = $"{group.StrategyName}: {group.Status} {group.CompletedTickerCount}/{group.TotalTickerCount}",
                FontSize = 12,
                TextColor = ThemePalette.TextPrimary
            });
        }

        foreach (var item in selectedJob.Events.Reverse().Take(10))
        {
            EventsStack.Add(new Label
            {
                Text = item,
                FontSize = 12,
                TextColor = ThemePalette.TextSecondary
            });
        }
    }

    private async void OnCancelSelectedJob(object? sender, EventArgs e)
    {
        if (selectedJob is null)
        {
            return;
        }

        await api.CancelBacktestJobAsync(selectedJob.JobId);
        await LoadAsync();
    }

    private static int ParseInt(string? value, int fallback)
    {
        return Int32.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private static decimal ParseDecimal(string? value, decimal fallback)
    {
        return Decimal.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }
}
