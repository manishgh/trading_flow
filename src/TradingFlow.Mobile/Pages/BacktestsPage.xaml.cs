using System.Collections.ObjectModel;
using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Pages;

public partial class BacktestsPage : ContentPage
{
    private readonly TradingFlowApiClient api = AppServices.Api;
    private readonly ObservableCollection<BacktestJobSnapshot> jobs = new();
    private readonly IDispatcherTimer refreshTimer;
    private MobileCatalogResponse? catalog;
    private IReadOnlyList<MobileWishlistResponse> wishlists = Array.Empty<MobileWishlistResponse>();
    private BacktestJobSnapshot? selectedJob;
    private bool isLoading;
    private bool suppressPersist;
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
            ConfigPicker.ItemsSource = catalog?.BacktestConfigs.ToList();
            StrategyPicker.ItemsSource = catalog?.Strategies.ToList();
            ConfigPicker.SelectedIndex = ConfigPicker.SelectedIndex < 0 && ConfigPicker.Items.Count > 0 ? 0 : ConfigPicker.SelectedIndex;
            StrategyPicker.SelectedIndex = StrategyPicker.SelectedIndex < 0 && StrategyPicker.Items.Count > 0 ? 0 : StrategyPicker.SelectedIndex;

            var previousWishlistId = (WishlistPicker.SelectedItem as MobileWishlistResponse)?.Id;
            wishlists = await api.GetWishlistsAsync() ?? Array.Empty<MobileWishlistResponse>();
            suppressPersist = true;
            WishlistPicker.ItemsSource = wishlists.ToList();
            WishlistPicker.SelectedItem = wishlists.FirstOrDefault(wishlist => wishlist.Id == previousWishlistId);
            suppressPersist = false;

            RestoreFormState();

            var latestJobs = await api.GetBacktestJobsAsync() ?? Array.Empty<BacktestJobSnapshot>();
            jobs.Clear();
            foreach (var job in latestJobs.Take(20))
            {
                jobs.Add(job);
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
            await DisplayAlertAsync("Backtests", exception.Message, "OK");
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

    private async void OnStartBacktest(object? sender, EventArgs e)
    {
        if (ConfigPicker.SelectedItem is not MobileRunConfigOption config)
        {
            await DisplayAlertAsync("Backtests", "Select a backtest config.", "OK");
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
            var request = new MobileBacktestRunRequest(
                config.Path,
                $"bt_{DateTimeOffset.Now:yyyyMMdd_HHmmss}",
                ParseInt(LookbackEntry.Text, 60),
                ParseTickers(TickersEntry.Text),
                strategies.Select(strategy => strategy.Path).ToArray(),
                ParseDecimal(CapitalEntry.Text, 10000m),
                ParseDecimal(RiskEntry.Text, 1.0m),
                ParseDecimal(MaxPositionEntry.Text, 25.0m),
                ParseInt(MaxConcurrentEntry.Text, 4),
                CachePolicyPicker.SelectedItem?.ToString() ?? "reuse");
            var job = await api.StartBacktestRunAsync(request);
            if (job is not null)
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

        var tickers = wishlist.Items
            .Where(item => item.Active)
            .Select(item => item.Ticker)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tickers.Length > 0)
        {
            TickersEntry.Text = String.Join(", ", tickers);
        }

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
            if (String.IsNullOrWhiteSpace(TickersEntry.Text))
            {
                TickersEntry.Text = Preferences.Get("BacktestTickers", "POET, MXL, RGTI, MU, MSFT");
            }

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
        if (suppressPersist)
        {
            return;
        }

        Preferences.Set("BacktestTickers", TickersEntry.Text ?? string.Empty);
        Preferences.Set("BacktestLookback", LookbackEntry.Text ?? "60");
        Preferences.Set("BacktestCapital", CapitalEntry.Text ?? "10000");
        Preferences.Set("BacktestRisk", RiskEntry.Text ?? "1");
        Preferences.Set("BacktestMaxPosition", MaxPositionEntry.Text ?? "25");
        Preferences.Set("BacktestMaxConcurrent", MaxConcurrentEntry.Text ?? "4");
        Preferences.Set("BacktestRunAll", RunAllStrategiesCheck.IsChecked);
        Preferences.Set("BacktestCachePolicy", CachePolicyPicker.SelectedItem?.ToString() ?? "reuse");
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

    private void OnJobSelected(object? sender, SelectionChangedEventArgs e)
    {
        selectedJob = e.CurrentSelection.FirstOrDefault() as BacktestJobSnapshot;
        RenderSelectedJob();
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
                TextColor = Color.FromArgb("#344054")
            });
        }

        foreach (var item in selectedJob.Events.Reverse().Take(10))
        {
            EventsStack.Add(new Label
            {
                Text = item,
                FontSize = 12,
                TextColor = Color.FromArgb("#475467")
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

    private static IReadOnlyList<string> ParseTickers(string? value)
    {
        return (value ?? String.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ticker => ticker.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
