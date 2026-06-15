using System.Collections.ObjectModel;
using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Pages;

public partial class PaperPage : ContentPage
{
    private readonly TradingFlowApiClient api = AppServices.Api;
    private readonly ObservableCollection<BacktestJobSnapshot> jobs = new();
    private readonly ObservableCollection<MobileNewsItem> newsItems = new();
    private readonly IDispatcherTimer refreshTimer;
    private MobileCatalogResponse? catalog;
    private BacktestJobSnapshot? selectedJob;
    private bool isLoading;

    public PaperPage()
    {
        InitializeComponent();
        JobsView.ItemsSource = jobs;
        NewsView.ItemsSource = newsItems;
        OrderExpirationPicker.ItemsSource = new[] { "day", "gtc" };
        EntryOrderTypePicker.ItemsSource = new[] { "market", "limit" };
        OrderExpirationPicker.SelectedIndex = 0;
        EntryOrderTypePicker.SelectedIndex = 0;
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

            var latestJobs = await api.GetPaperJobsAsync() ?? Array.Empty<BacktestJobSnapshot>();
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
            var tickers = ParseTickers(TickersEntry.Text);
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
                ParseTickers(TickersEntry.Text),
                String.IsNullOrWhiteSpace(ScreenerEntry.Text) ? null : ScreenerEntry.Text.Trim(),
                ExtendedHoursCheck.IsChecked,
                NewsCheck.IsChecked,
                OrderExpirationPicker.SelectedItem?.ToString() ?? "day",
                EntryOrderTypePicker.SelectedItem?.ToString() ?? "market");
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

    private void RenderSelectedJob()
    {
        DetailCard.IsVisible = selectedJob is not null;
        EventsStack.Clear();
        if (selectedJob is null)
        {
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
    }

    private async void OnCancelSelectedJob(object? sender, EventArgs e)
    {
        if (selectedJob is null)
        {
            return;
        }

        await api.CancelPaperJobAsync(selectedJob.JobId);
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

    private static IReadOnlyList<string> ParseTickers(string? value)
    {
        return (value ?? String.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ticker => ticker.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
