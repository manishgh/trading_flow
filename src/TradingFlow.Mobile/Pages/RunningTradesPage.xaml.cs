using System.Collections.ObjectModel;
using TradingFlow.Mobile.Services;
using TradingFlow.Mobile.Resources.Styles;

namespace TradingFlow.Mobile.Pages;

public partial class RunningTradesPage : ContentPage
{
    private static readonly Color ProfitColor = ThemePalette.Positive;
    private static readonly Color LossColor = ThemePalette.Negative;
    private static readonly Color NeutralColor = ThemePalette.TextSecondary;
    private static readonly Color AccentColor = ThemePalette.AccentStrong;
    private static readonly Color InactiveColor = ThemePalette.TextSecondary;

    private readonly TradingFlowApiClient api = AppServices.Api;
    private readonly ObservableCollection<RunningTradeRow> rows = new();
    private readonly IDispatcherTimer refreshTimer;
    private string selectedSource = "all";
    private bool isLoading;
    private readonly LatestMarketRead reads = new(() => AppServices.Api.SessionVersion);

    public RunningTradesPage()
    {
        InitializeComponent();
        var paper = new ToolbarItem { Text = "Paper runs" };
        paper.Clicked += async (_, _) => await Shell.Current.GoToAsync(nameof(PaperPage));
        ToolbarItems.Add(paper);
        TradesView.ItemsSource = rows;
        refreshTimer = Dispatcher.CreateTimer();
        refreshTimer.Interval = TimeSpan.FromSeconds(6);
        refreshTimer.Tick += async (_, _) => await LoadAsync(showBusy: false);
    }

    /// <summary>Reads the bound account when visible, independently of local job activity.</summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        reads.Activate();
        api.SessionChanged += OnSessionChanged;
        UpdateSourceButtons();
        refreshTimer.Start();
        await LoadAsync();
    }

    /// <summary>Stops polling and prevents old backend responses from painting a hidden page.</summary>
    protected override void OnDisappearing()
    {
        refreshTimer.Stop();
        api.SessionChanged -= OnSessionChanged;
        reads.Deactivate();
        isLoading = false;
        base.OnDisappearing();
    }

    /// <summary>Clears the previous account immediately when authentication or the backend changes.</summary>
    private void OnSessionChanged(object? sender, EventArgs args) => MainThread.BeginInvokeOnMainThread(async () => {
        if (!reads.Active) return;
        reads.Cancel();
        rows.Clear();
        ShowUnavailable("Connection changed. Reading the current account.");
        await LoadAsync();
    });

    /// <summary>Uses the shared account response, retaining null numbers and full-account totals across filters.</summary>
    private async Task LoadAsync(bool showBusy = true)
    {
        if (!reads.Active || isLoading && !showBusy)
        {
            return;
        }

        var read = reads.Begin();
        var source = selectedSource;
        isLoading = true;
        BusyIndicator.IsVisible = showBusy;
        BusyIndicator.IsRunning = showBusy;
        try
        {
            var response = await api.GetRunningTradesAsync(source, read.Token)
                ?? throw new InvalidDataException("Account response was empty.");
            if (!reads.IsCurrent(read) || source != selectedSource) return;
            StatusLabel.Text = response.Detail + " · " + response.ReadAtUtc.ToString("O");
            StatusLabel.TextColor = response.Available ? ProfitColor : LossColor;

            rows.Clear();
            foreach (var trade in response.Trades)
            {
                rows.Add(new RunningTradeRow(trade));
            }

            var total = response.TotalUnrealizedPl;
            TotalLabel.Text = response.TotalText;
            TotalLabel.TextColor = total > 0 ? ProfitColor : total < 0 ? LossColor : NeutralColor;
            ExposureLabel.Text = response.GrossExposure is { } exposure ? $"Account gross exposure {exposure:C2}" : "Account gross exposure unavailable";
            SlotsLabel.Text = response.SlotsUsed is { } used ? $"Account positions {used} of {response.MaxConcurrentPositions?.ToString() ?? "unknown"} · source filter changes rows only" : "Account positions unknown";
            ProtectionLabel.Text = response.UnprotectedCount is { } unprotected
                ? $"No broker stop: {unprotected} · protection unknown: {response.UnknownProtectionCount}" : "Broker protection unknown";
            EmptyStateTitle.Text = response.Available ? "No positions match this source" : "Account holdings unavailable";
            EmptyStateDetail.Text = response.Available
                ? "The broker account was read successfully. Select All to see every holding."
                : "This does not mean the account has no positions.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!reads.IsCurrent(read)) return;
            rows.Clear();
            ShowUnavailable("Account holdings unavailable: " + exception.Message);
        }
        finally
        {
            if (reads.IsCurrent(read))
            {
                isLoading = false;
                BusyIndicator.IsRunning = false;
                BusyIndicator.IsVisible = false;
                RefreshRoot.IsRefreshing = false;
            }
        }
    }

    /// <summary>Withholds totals after a failed account read instead of reporting an empty or flat account.</summary>
    private void ShowUnavailable(string detail)
    {
        StatusLabel.Text = detail;
        StatusLabel.TextColor = LossColor;
        TotalLabel.Text = "Account P/L unavailable";
        ExposureLabel.Text = "Account gross exposure unavailable";
        SlotsLabel.Text = "Account positions unknown";
        ProtectionLabel.Text = "Broker protection unknown";
        EmptyStateTitle.Text = "Account holdings unavailable";
        EmptyStateDetail.Text = "This does not mean the account has no positions.";
    }

    private async void OnRefresh(object? sender, EventArgs e) => await LoadAsync();

    /// <summary>Changes the displayed source without changing the account totals or letting an old response restore old rows.</summary>
    private async void OnSourceSelected(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: string source } && !source.Equals(selectedSource, StringComparison.OrdinalIgnoreCase))
        {
            selectedSource = source;
            rows.Clear();
            UpdateSourceButtons();
            await LoadAsync();
        }
    }

    /// <summary>Marks the active row filter, including holdings that have no known local run.</summary>
    private void UpdateSourceButtons()
    {
        AllButton.BackgroundColor = selectedSource == "all" ? AccentColor : InactiveColor;
        WishlistButton.BackgroundColor = selectedSource == "wishlist" ? AccentColor : InactiveColor;
        StockPulseButton.BackgroundColor = selectedSource == "stockpulse" ? AccentColor : InactiveColor;
        ManualButton.BackgroundColor = selectedSource == "manual" ? AccentColor : InactiveColor;
        AccountButton.BackgroundColor = selectedSource == "account" ? AccentColor : InactiveColor;
    }

    /// <summary>Uses the existing tracked exit only for a matching owner; other holdings go to account-wide reviewed Orders.</summary>
    private async void OnSell(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: RunningTradeRow row })
        {
            return;
        }

        if (!row.Trade.CanReviewExit)
        {
            await Shell.Current.GoToAsync(nameof(OrdersPage));
            return;
        }

        await AppServices.Haptics.SignalAsync(HapticSignal.DestructiveConfirm);
        var confirmed = await DisplayAlertAsync(
            "Review paper exit",
            $"Close the tracked {row.Trade.Ticker} paper position? The server will validate and reconcile the request.",
            "Confirm Exit",
            "Keep Position");
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

            await AppServices.Haptics.SignalAsync(HapticSignal.OrderAccepted);
            await LoadAsync();
        }
        catch (Exception exception)
        {
            await AppServices.Haptics.SignalAsync(HapticSignal.OrderRejected);
            await DisplayAlertAsync("Exit rejected", exception.Message, "OK");
        }
    }

    // Wraps a trade with a UI Color so the P/L text can be tinted without a value converter.
    internal sealed class RunningTradeRow
    {
        /// <summary>Wraps the shared account row without replacing missing broker values.</summary>
        public RunningTradeRow(AccountPosition trade)
        {
            Trade = trade;
        }

        public AccountPosition Trade { get; }
        public string ReviewText => Trade.CanReviewExit ? "Review Exit" : "Review in Orders";

        public Color PlColor => Trade.UnrealizedPl > 0
            ? ProfitColor
            : Trade.UnrealizedPl < 0 ? LossColor : NeutralColor;
    }
}
