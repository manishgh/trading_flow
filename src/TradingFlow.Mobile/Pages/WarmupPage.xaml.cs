using System.Collections.ObjectModel;
using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Pages;

public partial class WarmupPage : ContentPage
{
    private readonly TradingFlowApiClient api = AppServices.Api;
    private readonly ObservableCollection<WarmupTickerIntent> watchlist = new();
    private readonly ObservableCollection<WarmupRunRecord> runs = new();
    private WarmupTickerIntent? selected;
    private bool isLoading;

    public WarmupPage()
    {
        InitializeComponent();
        WatchlistView.ItemsSource = watchlist;
        RunsView.ItemsSource = runs;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
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
            StatusLabel.Text = healthy ? $"Connected: {api.BaseUrl}" : $"Backend unreachable: {api.BaseUrl}";
            StatusLabel.TextColor = healthy ? Color.FromArgb("#067647") : Color.FromArgb("#B42318");

            var latestWatchlist = await api.GetWarmupWatchlistAsync() ?? Array.Empty<WarmupTickerIntent>();
            watchlist.Clear();
            foreach (var item in latestWatchlist.OrderBy(x => x.Ticker))
            {
                watchlist.Add(item);
            }

            var latestRuns = await api.GetWarmupRunsAsync() ?? Array.Empty<WarmupRunRecord>();
            runs.Clear();
            foreach (var run in latestRuns.Take(20))
            {
                runs.Add(run);
            }

            selected = selected is null
                ? null
                : latestWatchlist.FirstOrDefault(x => x.Ticker.Equals(selected.Ticker, StringComparison.OrdinalIgnoreCase));
            RenderSelected();
        }
        catch (Exception exception)
        {
            StatusLabel.Text = $"Warmup error: {exception.Message}";
            StatusLabel.TextColor = Color.FromArgb("#B42318");
            await DisplayAlertAsync("Warmup", exception.Message, "OK");
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

    private async void OnAddWarmup(object? sender, EventArgs e)
    {
        var tickers = ParseCsv(TickersEntry.Text);
        if (tickers.Count == 0)
        {
            await DisplayAlertAsync("Warmup", "Add at least one ticker.", "OK");
            return;
        }

        try
        {
            AddButton.IsEnabled = false;
            var accepted = await api.AddWarmupWatchlistAsync(new WarmupWatchRequest(
                tickers,
                String.IsNullOrWhiteSpace(ReasonEntry.Text) ? "possible trend tomorrow" : ReasonEntry.Text.Trim(),
                "mobile",
                ParseInt(WarmupDaysEntry.Text, 60),
                ParseInt(NewsDaysEntry.Text, 14),
                ParseCsv(TimeframesEntry.Text),
                IncludeNewsCheck.IsChecked,
                RunNowCheck.IsChecked));
            await DisplayAlertAsync("Warmup", accepted is null ? "Warmup submitted." : $"Accepted {accepted.Accepted} ticker(s).", "OK");
            await LoadAsync();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Warmup", exception.Message, "OK");
        }
        finally
        {
            AddButton.IsEnabled = true;
        }
    }

    private async void OnRunAll(object? sender, EventArgs e)
    {
        await api.RunWarmupNowAsync(new WarmupRunNowRequest(null, "mobile-run-all"));
        await DisplayAlertAsync("Warmup", "Warmup run queued.", "OK");
        await LoadAsync();
    }

    private void OnWatchlistSelected(object? sender, SelectionChangedEventArgs e)
    {
        selected = e.CurrentSelection.FirstOrDefault() as WarmupTickerIntent;
        RenderSelected();
    }

    private async void OnRunSelected(object? sender, EventArgs e)
    {
        if (selected is null)
        {
            return;
        }

        await api.RunWarmupNowAsync(new WarmupRunNowRequest([selected.Ticker], "mobile-run-selected"));
        await DisplayAlertAsync("Warmup", $"Warmup queued for {selected.Ticker}.", "OK");
        await LoadAsync();
    }

    private async void OnRemoveSelected(object? sender, EventArgs e)
    {
        if (selected is null)
        {
            return;
        }

        await api.RemoveWarmupTickerAsync(selected.Ticker);
        selected = null;
        await LoadAsync();
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
}
