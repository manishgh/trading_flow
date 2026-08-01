using System.Collections.ObjectModel;
using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Pages;

public partial class NotificationsPage : ContentPage
{
    private static readonly Color NeutralAccentColor = Color.FromArgb("#176B87");
    private static readonly Color SignalColor = Color.FromArgb("#116D42");
    private static readonly Color ErrorColor = Color.FromArgb("#A12A2A");
    private static readonly Color InactiveColor = Color.FromArgb("#667085");

    private readonly ObservableCollection<ActivityRow> visibleRows = new();
    private readonly IDispatcherTimer refreshTimer;
    private IReadOnlyList<ActivityRow> allRows = Array.Empty<ActivityRow>();
    private string selectedFilter = "all";
    private bool isLoading;

    public NotificationsPage()
    {
        InitializeComponent();
        NotificationsView.ItemsSource = visibleRows;
        refreshTimer = Dispatcher.CreateTimer();
        refreshTimer.Interval = TimeSpan.FromSeconds(10);
        refreshTimer.Tick += async (_, _) => await LoadAsync(showBusy: false);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        UpdateFilterButtons();
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
            var healthTask = AppServices.Api.CheckHealthAsync();
            var notificationsTask = AppServices.Api.GetNotificationsAsync();
            var signalsTask = AppServices.Api.GetWishlistSignalsAsync(hours: 48);
            var ordersTask = AppServices.Api.GetOrderActivityAsync(limit: 200);
            await Task.WhenAll(healthTask, notificationsTask, signalsTask, ordersTask);

            var healthy = await healthTask;
            var notifications = await notificationsTask ?? Array.Empty<MobileNotificationItem>();
            var signals = await signalsTask ?? Array.Empty<MobileWishlistSignalResponse>();
            var orders = await ordersTask ?? Array.Empty<MobileOrderActivityResponse>();

            allRows = notifications.Select(ActivityRow.FromNotification)
                .Concat(signals.Select(ActivityRow.FromSignal))
                .Concat(orders.Select(ActivityRow.FromOrder))
                .OrderByDescending(item => item.Timestamp)
                .Take(500)
                .ToArray();
            ApplyFilter();

            StatusLabel.Text = healthy
                ? $"Connected · {allRows.Count} recent event(s)"
                : "Disconnected · activity may be incomplete";
            StatusLabel.TextColor = healthy ? SignalColor : ErrorColor;
        }
        catch (Exception exception)
        {
            StatusLabel.Text = $"Activity unavailable: {exception.Message}";
            StatusLabel.TextColor = ErrorColor;
            SemanticScreenReader.Default.Announce("Activity is unavailable.");
        }
        finally
        {
            isLoading = false;
            BusyIndicator.IsRunning = false;
            BusyIndicator.IsVisible = false;
            RefreshRoot.IsRefreshing = false;
        }
    }

    private void ApplyFilter()
    {
        var filtered = selectedFilter switch
        {
            "signal" => allRows.Where(item => item.Category == "signal"),
            "system" => allRows.Where(item => item.Category == "system"),
            "order" => allRows.Where(item => item.Category == "order"),
            _ => allRows
        };

        visibleRows.Clear();
        foreach (var item in filtered)
        {
            visibleRows.Add(item);
        }
    }

    private async void OnRefresh(object? sender, EventArgs e) => await LoadAsync();

    private async void OnRefreshClicked(object? sender, EventArgs e)
    {
        await LoadAsync();
        SemanticScreenReader.Default.Announce($"Activity refreshed. {visibleRows.Count} event(s).");
    }

    private void OnFilterSelected(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: string filter })
        {
            selectedFilter = filter;
            UpdateFilterButtons();
            ApplyFilter();
        }
    }

    private void UpdateFilterButtons()
    {
        AllButton.BackgroundColor = selectedFilter == "all" ? NeutralAccentColor : InactiveColor;
        SignalsButton.BackgroundColor = selectedFilter == "signal" ? NeutralAccentColor : InactiveColor;
        SystemButton.BackgroundColor = selectedFilter == "system" ? NeutralAccentColor : InactiveColor;
        OrdersButton.BackgroundColor = selectedFilter == "order" ? NeutralAccentColor : InactiveColor;
    }

    private async void OnOpenNews(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: ActivityRow row } && row.HasNewsLink)
        {
            await NewsNavigation.OpenAsync(row.NewsUrl);
        }
    }

    internal sealed record ActivityRow(
        string Category,
        DateTimeOffset Timestamp,
        string CategoryLabel,
        string Title,
        string Message,
        string Context,
        string? NewsUrl,
        Color AccentColor)
    {
        public string DisplayTimestamp => Timestamp.LocalDateTime.ToString("dd/MM HH:mm");
        public bool HasContext => !String.IsNullOrWhiteSpace(Context);
        public bool HasNewsLink => Uri.TryCreate(NewsUrl, UriKind.Absolute, out var uri) &&
                                   uri.Scheme is "http" or "https";

        public static ActivityRow FromNotification(MobileNotificationItem item) => new(
            "system",
            item.Timestamp,
            "SYSTEM",
            item.Title,
            item.Message,
            item.RunName ?? String.Empty,
            null,
            item.Severity.Equals("error", StringComparison.OrdinalIgnoreCase) ? ErrorColor : NeutralAccentColor);

        public static ActivityRow FromSignal(MobileWishlistSignalResponse item) => new(
            "signal",
            item.DetectedAtUtc,
            "SIGNAL",
            $"{item.Ticker} · {item.SignalType}",
            item.Reason,
            String.IsNullOrWhiteSpace(item.NewsHeadline)
                ? item.PriceText
                : $"{item.PriceText} · {item.NewsHeadline}".Trim(' ', '·'),
            item.NewsUrl,
            SignalColor);

        public static ActivityRow FromOrder(MobileOrderActivityResponse item)
        {
            var isFailure = item.State.Equals("Rejected", StringComparison.OrdinalIgnoreCase) ||
                            item.State.Equals("Canceled", StringComparison.OrdinalIgnoreCase) ||
                            item.State.Equals("Expired", StringComparison.OrdinalIgnoreCase);
            var isFilled = item.State.Equals("Filled", StringComparison.OrdinalIgnoreCase);
            var filled = item.FilledQuantity is { } quantity
                ? $"Filled {quantity:0.####}" + (item.FillPrice is { } price ? $" at {price:C2}" : String.Empty)
                : "No fill reported";

            return new ActivityRow(
                "order",
                item.UpdatedAtUtc,
                "ORDER",
                $"{item.Symbol} - {item.State}",
                $"{item.Side.ToUpperInvariant()} {item.RequestedQuantity:0.####} {item.OrderType} / {item.TimeInForce}",
                $"{filled} - {item.StrategyId}",
                null,
                isFailure ? ErrorColor : isFilled ? SignalColor : NeutralAccentColor);
        }
    }
}
