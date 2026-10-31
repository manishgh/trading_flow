using System.Collections.ObjectModel;
using TradingFlow.Mobile.Services;
using TradingFlow.Mobile.Resources.Styles;
using TradingFlow.Contracts.V1;

namespace TradingFlow.Mobile.Pages;

public partial class WarmupPage : ContentPage
{
    private readonly TradingFlowApiClient api = AppServices.Api;
    private readonly ObservableCollection<WarmupTickerIntent> watchlist = new();
    private readonly ObservableCollection<WarmupRunRecord> runs = new();
    private WarmupTickerIntent? selected;
    private bool isLoading;
    private WarmupRunRecord? archiveRun;
    private WarmupRunNowRequest? pendingRunNow;
    private WarmupWatchRequest? pendingWatch;
    private bool queueing;
    private long lastSeenSessionVersion;
    private CancellationTokenSource? pageLifetime;

    /// <summary>Initializes read models and remembers which connection owns retained retry identities.</summary>
    public WarmupPage()
    {
        InitializeComponent();
        WatchlistView.ItemsSource = watchlist;
        RunsView.ItemsSource = runs;
        lastSeenSessionVersion = api.SessionVersion;
    }

    /// <summary>Clears prior-account commands missed while hidden, then creates a cancellable lifetime for this appearance.</summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (lastSeenSessionVersion != api.SessionVersion) OnSessionChanged(this, EventArgs.Empty);
        pageLifetime?.Cancel(); pageLifetime?.Dispose(); pageLifetime = new();
        isLoading = false; queueing = false; AddButton.IsEnabled = true;
        api.SessionChanged += OnSessionChanged;
        await LoadAsync();
    }

    /// <summary>Stops hidden-page calls; a later appearance checks session changes missed after unsubscribing.</summary>
    protected override void OnDisappearing()
    {
        pageLifetime?.Cancel();
        api.SessionChanged -= OnSessionChanged;
        base.OnDisappearing();
    }

    /// <summary>Removes previous-account rows, selections and uncertain command keys immediately when the connection changes.</summary>
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        lastSeenSessionVersion = api.SessionVersion;
        pageLifetime?.Cancel();
        pendingWatch = null; pendingRunNow = null; selected = null; archiveRun = null;
        watchlist.Clear(); runs.Clear(); JobsStack.Clear(); RenderSelected();
        RetryArchiveButton.IsEnabled = false; AddButton.IsEnabled = false;
        StatusLabel.Text = "Connection changed. Reopen this page after signing in.";
    }

    /// <summary>Accepts UI updates only from the current signed-in connection and visible appearance.</summary>
    private bool IsCurrentOwner(long version, CancellationToken token) =>
        !token.IsCancellationRequested && version == api.SessionVersion && version == lastSeenSessionVersion;

    /// <summary>Stops delayed responses before they can restore a hidden page or a previous account's state.</summary>
    private void RequireCurrentOwner(long version, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrentOwner(version, token)) throw new OperationCanceledException("The warmup page's signed-in connection changed.");
    }

    /// <summary>Refreshes durable queue state, cache results and archive status as separate facts.</summary>
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
            StatusLabel.Text = healthy ? $"Connected: {api.BaseUrl}" : $"Backend unreachable: {api.BaseUrl}";
            StatusLabel.TextColor = healthy ? ThemePalette.Positive : ThemePalette.Negative;

            var latestWatchlist = await api.GetWarmupWatchlistAsync(token) ?? Array.Empty<WarmupTickerIntent>();
            RequireCurrentOwner(version, token);
            watchlist.Clear();
            foreach (var item in latestWatchlist.OrderBy(x => x.Ticker))
            {
                watchlist.Add(item);
            }

            var latestRuns = await api.GetWarmupRunsAsync(token) ?? Array.Empty<WarmupRunRecord>();
            RequireCurrentOwner(version, token);
            runs.Clear();
            foreach (var run in latestRuns.Take(20))
            {
                runs.Add(run);
            }
            var latestJobs = await api.Workflow.WarmupJobsAsync(token);
            RequireCurrentOwner(version, token);
            RenderJobs(latestJobs);

            selected = selected is null
                ? null
                : latestWatchlist.FirstOrDefault(x => x.Ticker.Equals(selected.Ticker, StringComparison.OrdinalIgnoreCase));
            RenderSelected();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!IsCurrentOwner(version, token)) return;
            StatusLabel.Text = $"Warmup error: {exception.Message}";
            StatusLabel.TextColor = ThemePalette.Negative;
            await DisplayAlertAsync("Warmup", exception.Message, "OK");
        }
        finally
        {
            if (IsCurrentOwner(version, token))
            {
                isLoading = false;
                BusyIndicator.IsRunning = false;
                BusyIndicator.IsVisible = false;
                RefreshRoot.IsRefreshing = false;
            }
        }
    }

    /// <summary>Refreshes only the currently visible account's preparation state.</summary>
    private async void OnRefresh(object? sender, EventArgs e) => await LoadAsync();

    /// <summary>Retains the same accepted add-and-run command after an uncertain response until its inputs change.</summary>
    private async void OnAddWarmup(object? sender, EventArgs e)
    {
        var token = pageLifetime?.Token ?? new CancellationToken(true);
        var version = api.SessionVersion;
        if (!IsCurrentOwner(version, token)) return;
        var tickers = ParseCsv(TickersEntry.Text);
        if (tickers.Count == 0)
        {
            await DisplayAlertAsync("Warmup", "Add at least one ticker.", "OK");
            return;
        }

        try
        {
            AddButton.IsEnabled = false;
            pendingWatch ??= new WarmupWatchRequest(
                tickers,
                String.IsNullOrWhiteSpace(ReasonEntry.Text) ? "possible trend tomorrow" : ReasonEntry.Text.Trim(),
                "mobile",
                ParseInt(WarmupDaysEntry.Text, 60),
                ParseInt(NewsDaysEntry.Text, 14),
                ParseCsv(TimeframesEntry.Text),
                IncludeNewsCheck.IsChecked,
                RunNowCheck.IsChecked,
                Guid.NewGuid().ToString("N"));
            var command = pendingWatch;
            var accepted = await api.AddWarmupWatchlistAsync(command, token);
            RequireCurrentOwner(version, token);
            if (ReferenceEquals(command, pendingWatch)) pendingWatch = null;
            await DisplayAlertAsync("Warmup", accepted is null ? "Warmup submitted." : $"Accepted {accepted.Accepted} ticker(s).", "OK");
            if (IsCurrentOwner(version, token)) await LoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!IsCurrentOwner(version, token)) return;
            await DisplayAlertAsync("Warmup", exception.Message, "OK");
        }
        finally
        {
            if (IsCurrentOwner(version, token)) AddButton.IsEnabled = true;
        }
    }

    /// <summary>Any operator edit creates a new command; transport retries without edits keep their original key.</summary>
    private void OnWatchInputsChanged(object? sender, EventArgs e) => pendingWatch = null;

    /// <summary>Queues the active warmup list using an explicit retry key.</summary>
    private async void OnRunAll(object? sender, EventArgs e)
    {
        await QueueWarmupAsync(null, "mobile-run-all");
    }

    /// <summary>Keeps the selected ticker as a local view choice, not an automatically submitted command.</summary>
    private void OnWatchlistSelected(object? sender, SelectionChangedEventArgs e)
    {
        selected = e.CurrentSelection.FirstOrDefault() as WarmupTickerIntent;
        RenderSelected();
    }

    /// <summary>Queues only the selected stock without creating another source list.</summary>
    private async void OnRunSelected(object? sender, EventArgs e)
    {
        if (selected is null)
        {
            return;
        }

        await QueueWarmupAsync([selected.Ticker], "mobile-run-selected");
    }

    /// <summary>Removes only the captured account's selected warmup intent and suppresses late acknowledgements after owner changes.</summary>
    private async void OnRemoveSelected(object? sender, EventArgs e)
    {
        var token = pageLifetime?.Token ?? new CancellationToken(true);
        var version = api.SessionVersion;
        if (selected is not { } item || !IsCurrentOwner(version, token)) return;
        try
        {
            await api.RemoveWarmupTickerAsync(item.Ticker, token);
            RequireCurrentOwner(version, token);
            if (selected?.Ticker == item.Ticker) selected = null;
            await LoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (IsCurrentOwner(version, token)) await DisplayAlertAsync("Warmup", error.Message, "OK"); }
    }

    private void RenderSelected()
    {
        SelectedCard.IsVisible = selected is not null;
        if (selected is null)
        {
            return;
        }

        SelectedTitle.Text = selected.Ticker;
        SelectedDetail.Text = $"{selected.DetailText}\n{selected.LastWarmText}";
    }

    private static IReadOnlyList<string> ParseCsv(string? value)
    {
        return (value ?? String.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int ParseInt(string? value, int fallback)
    {
        return Int32.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    /// <summary>Keeps transport retries on one command and displays queue rejection instead of crashing an async handler.</summary>
    private async Task QueueWarmupAsync(IReadOnlyCollection<string>? symbols, string reason)
    {
        var token = pageLifetime?.Token ?? new CancellationToken(true);
        var version = api.SessionVersion;
        if (queueing || !IsCurrentOwner(version, token)) return;
        queueing = true;
        try
        {
            if (pendingRunNow is null || pendingRunNow.Reason != reason ||
                !(pendingRunNow.Tickers ?? []).SequenceEqual(symbols ?? []))
                pendingRunNow = new(symbols, reason, Guid.NewGuid().ToString("N"));
            var command = pendingRunNow;
            var accepted = await api.RunWarmupNowAsync(command, token);
            RequireCurrentOwner(version, token);
            if (ReferenceEquals(command, pendingRunNow)) pendingRunNow = null;
            await DisplayAlertAsync("Warmup", $"Warmup accepted: {accepted?.RunId}", "OK");
            if (IsCurrentOwner(version, token)) await LoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (IsCurrentOwner(version, token)) await DisplayAlertAsync("Warmup", error.Message, "OK"); }
        finally { if (IsCurrentOwner(version, token)) queueing = false; }
    }

    /// <summary>Shows persisted accepted work and cancellation controls, including jobs with no cache result yet.</summary>
    private void RenderJobs(IReadOnlyList<WarmupQueueStatus> jobs)
    {
        JobsStack.Clear();
        foreach (var job in jobs.OrderByDescending(item => item.QueuedAtUtc).Take(20))
        {
            var row = new VerticalStackLayout { Spacing = 4 };
            row.Add(new Label { Text = $"{job.RunId}: {job.Status}\n{String.Join(", ", job.Tickers)} {job.Error}", FontSize = 12 });
            if (job.Status is "queued" or "running")
            {
                var cancel = new Button { Text = "Cancel job", CommandParameter = job.RunId };
                cancel.Clicked += OnCancelWarmupJob;
                row.Add(cancel);
            }
            JobsStack.Add(row);
        }
    }

    /// <summary>Cancels the exact selected persisted job and refreshes the server's outcome.</summary>
    private async void OnCancelWarmupJob(object? sender, EventArgs e)
    {
        var token = pageLifetime?.Token ?? new CancellationToken(true);
        var version = api.SessionVersion;
        if (!IsCurrentOwner(version, token)) return;
        if (sender is not Button { CommandParameter: string id } button) return;
        button.IsEnabled = false;
        try {
            await api.Workflow.CancelWarmupJobAsync(id, token);
            RequireCurrentOwner(version, token);
            await LoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (IsCurrentOwner(version, token)) await DisplayAlertAsync("Warmup", error.Message, "OK"); }
        finally { if (IsCurrentOwner(version, token)) button.IsEnabled = true; }
    }

    /// <summary>Enables archive retry only for a selected run with captured data awaiting delivery.</summary>
    private void OnRunSelectedForArchive(object? sender, SelectionChangedEventArgs e)
    {
        archiveRun = e.CurrentSelection.FirstOrDefault() as WarmupRunRecord;
        RetryArchiveButton.IsEnabled = archiveRun?.Tickers.Any(ticker => ticker.ArchiveStatus is "remote_pending" or "local_failed") == true;
    }

    /// <summary>Retries captured delivery and restores the same owner's selected run button after a failure if delivery remains pending.</summary>
    private async void OnRetryArchives(object? sender, EventArgs e)
    {
        var token = pageLifetime?.Token ?? new CancellationToken(true);
        var version = api.SessionVersion;
        if (archiveRun is not { } run || !IsCurrentOwner(version, token)) return;
        RetryArchiveButton.IsEnabled = false;
        try
        {
            var result = await api.Workflow.RetryWarmupArchivesAsync(run.RunId, token);
            RequireCurrentOwner(version, token);
            await DisplayAlertAsync("Warmup", String.Join("; ", result.Tickers.Select(ticker => $"{ticker.Ticker}: {ticker.ArchiveStatus.Replace('_', ' ')} {ticker.Error}")), "OK");
            if (IsCurrentOwner(version, token)) await LoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (IsCurrentOwner(version, token)) await DisplayAlertAsync("Warmup", error.Message, "OK"); }
        finally
        {
            if (IsCurrentOwner(version, token) && archiveRun is { } selectedRun && selectedRun.RunId == run.RunId)
                RetryArchiveButton.IsEnabled = selectedRun.Tickers.Any(ticker => ticker.ArchiveStatus is "remote_pending" or "local_failed");
        }
    }
}
