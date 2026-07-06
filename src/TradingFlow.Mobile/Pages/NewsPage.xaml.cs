using System.Collections.ObjectModel;
using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Pages;

public partial class NewsPage : ContentPage
{
    private readonly ObservableCollection<MobileNewsItem> newsItems = new();
    private readonly IDispatcherTimer refreshTimer;
    private bool isLoading;

    public NewsPage()
    {
        InitializeComponent();
        NewsView.ItemsSource = newsItems;
        refreshTimer = Dispatcher.CreateTimer();
        refreshTimer.Interval = TimeSpan.FromSeconds(20);
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
            var ticker = TickerEntry.Text?.Trim().ToUpperInvariant();
            var feed = await AppServices.Api.GetRollingNewsFeedAsync(ticker, 4) ??
                new MobileNewsFeedResponse(false, "unknown", "No response returned.", Array.Empty<MobileNewsItem>());

            StatusLabel.Text = feed.Enabled
                ? $"{feed.Provider}: {feed.Items.Count} item(s). {feed.Message}"
                : feed.Message ?? "News feed disabled.";
            StatusLabel.TextColor = feed.Enabled ? Color.FromArgb("#067647") : Color.FromArgb("#B42318");

            newsItems.Clear();
            foreach (var item in feed.Items)
            {
                newsItems.Add(item);
            }
        }
        catch (Exception exception)
        {
            StatusLabel.Text = $"News error: {exception.Message}";
            StatusLabel.TextColor = Color.FromArgb("#B42318");
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

    private async void OnFetchClicked(object? sender, EventArgs e)
    {
        if (isLoading)
        {
            return;
        }

        try
        {
            BusyIndicator.IsVisible = true;
            BusyIndicator.IsRunning = true;
            StatusLabel.Text = "Fetching Finviz and Alpaca news...";
            await AppServices.Api.RefreshRollingNewsFeedAsync();
            await LoadAsync(showBusy: false);
        }
        catch (Exception exception)
        {
            StatusLabel.Text = $"Fetch error: {exception.Message}";
            StatusLabel.TextColor = Color.FromArgb("#B42318");
        }
        finally
        {
            BusyIndicator.IsRunning = false;
            BusyIndicator.IsVisible = false;
        }
    }

    private async void OnTickerChanged(object? sender, TextChangedEventArgs e)
    {
        if ((e.NewTextValue?.Length ?? 0) == 0 || e.NewTextValue!.Length >= 2)
        {
            await LoadAsync(showBusy: false);
        }
    }

    private async void OnNewsSelected(object? sender, SelectionChangedEventArgs e)
    {
        var selected = e.CurrentSelection.FirstOrDefault() as MobileNewsItem;
        if (sender is CollectionView collectionView)
        {
            collectionView.SelectedItem = null;
        }

        await NewsNavigation.OpenAsync(selected);
    }
}
