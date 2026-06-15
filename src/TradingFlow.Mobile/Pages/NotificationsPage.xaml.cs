using System.Collections.ObjectModel;
using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Pages;

public partial class NotificationsPage : ContentPage
{
    private readonly ObservableCollection<MobileNotificationItem> notifications = new();
    private readonly IDispatcherTimer refreshTimer;
    private bool isLoading;

    public NotificationsPage()
    {
        InitializeComponent();
        NotificationsView.ItemsSource = notifications;
        refreshTimer = Dispatcher.CreateTimer();
        refreshTimer.Interval = TimeSpan.FromSeconds(10);
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
            var healthy = await AppServices.Api.CheckHealthAsync();
            StatusLabel.Text = healthy
                ? $"Connected: {AppServices.Api.BaseUrl}"
                : $"Backend unreachable: {AppServices.Api.BaseUrl}";
            StatusLabel.TextColor = healthy ? Color.FromArgb("#067647") : Color.FromArgb("#B42318");

            var latest = await AppServices.Api.GetNotificationsAsync() ?? Array.Empty<MobileNotificationItem>();
            notifications.Clear();
            foreach (var item in latest)
            {
                notifications.Add(item);
            }
        }
        catch (Exception exception)
        {
            StatusLabel.Text = $"Error: {exception.Message}";
            StatusLabel.TextColor = Color.FromArgb("#B42318");
            await DisplayAlertAsync("Notifications", exception.Message, "OK");
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
}
