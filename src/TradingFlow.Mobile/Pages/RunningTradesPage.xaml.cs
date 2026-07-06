using System.Collections.ObjectModel;
using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Pages;

public partial class RunningTradesPage : ContentPage
{
    private static readonly Color ProfitColor = Color.FromArgb("#067647");
    private static readonly Color LossColor = Color.FromArgb("#B42318");
    private static readonly Color NeutralColor = Color.FromArgb("#475467");
    private static readonly Color AccentColor = Color.FromArgb("#007ACC");
    private static readonly Color InactiveColor = Color.FromArgb("#667085");

    private readonly TradingFlowApiClient api = AppServices.Api;
    private readonly ObservableCollection<RunningTradeRow> rows = new();
    private readonly IDispatcherTimer refreshTimer;
    private string selectedSource = "all";
    private bool isLoading;

    public RunningTradesPage()
    {
        InitializeComponent();
        TradesView.ItemsSource = rows;
        refreshTimer = Dispatcher.CreateTimer();
        refreshTimer.Interval = TimeSpan.FromSeconds(6);
        refreshTimer.Tick += async (_, _) => await LoadAsync(showBusy: false);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        UpdateSourceButtons();
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
            StatusLabel.Text = healthy ? $"Connected: {api.BaseUrl}" : $"Backend unreachable: {api.BaseUrl}";
            StatusLabel.TextColor = healthy ? ProfitColor : LossColor;

            var response = await api.GetRunningTradesAsync(selectedSource);
            var trades = response?.Trades ?? Array.Empty<MobileRunningTrade>();

            rows.Clear();
            foreach (var trade in trades)
            {
                rows.Add(new RunningTradeRow(trade));
            }

            var total = response?.TotalUnrealizedPl ?? 0m;
            TotalLabel.Text = response?.TotalText ?? "Total P/L --";
            TotalLabel.TextColor = total > 0 ? ProfitColor : total < 0 ? LossColor : NeutralColor;
        }
        catch (Exception exception)
        {
            StatusLabel.Text = $"Trades error: {exception.Message}";
            StatusLabel.TextColor = LossColor;
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

    private async void OnSourceSelected(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: string source } && !source.Equals(selectedSource, StringComparison.OrdinalIgnoreCase))
        {
            selectedSource = source;
            UpdateSourceButtons();
            await LoadAsync();
        }
    }

    private void UpdateSourceButtons()
    {
        AllButton.BackgroundColor = selectedSource == "all" ? AccentColor : InactiveColor;
        WishlistButton.BackgroundColor = selectedSource == "wishlist" ? AccentColor : InactiveColor;
        StockPulseButton.BackgroundColor = selectedSource == "stockpulse" ? AccentColor : InactiveColor;
        ManualButton.BackgroundColor = selectedSource == "manual" ? AccentColor : InactiveColor;
    }

    private async void OnSell(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: RunningTradeRow row })
        {
            return;
        }

        var confirmed = await DisplayAlertAsync("Sell", $"Sell {row.Trade.Ticker} now?", "Sell", "Cancel");
        if (!confirmed)
        {
            return;
        }

        try
        {
            if (row.Trade.CloseKind == "paper_job" && row.Trade.JobId is { } jobId)
            {
                await api.ClosePaperPositionAsync(jobId, row.Trade.Ticker);
            }
            else if (row.Trade.CloseKind == "automation" && row.Trade.SessionId is { } sessionId)
            {
                await api.CloseAutomationPositionAsync(sessionId);
            }

            await LoadAsync();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Sell", exception.Message, "OK");
        }
    }

    // Wraps a trade with a UI Color so the P/L text can be tinted without a value converter.
    internal sealed class RunningTradeRow
    {
        public RunningTradeRow(MobileRunningTrade trade)
        {
            Trade = trade;
        }

        public MobileRunningTrade Trade { get; }

        public Color PlColor => Trade.UnrealizedPl > 0
            ? ProfitColor
            : Trade.UnrealizedPl < 0 ? LossColor : NeutralColor;
    }
}
