using System.Collections.ObjectModel;

namespace TradingFlow.Mobile.Pages;

public partial class MorePage : ContentPage
{
    private readonly ObservableCollection<MoreToolItem> tools =
    [
        new("Sign in", "Authenticate for trading, research, and operational APIs.", nameof(LoginPage)),
        new("Automation", "Stock Pulse capture and paper automation sessions.", nameof(AutomationPage)),
        new("Warmup", "Prepared market-state operations.", nameof(WarmupPage)),
        new("Settings", "Backend endpoint and application preferences.", nameof(SettingsPage))
    ];

    public MorePage()
    {
        InitializeComponent();
        ToolsView.ItemsSource = tools;
    }

    private async void OnToolSelected(object? sender, SelectionChangedEventArgs e)
    {
        var selected = e.CurrentSelection.FirstOrDefault() as MoreToolItem;
        if (sender is CollectionView collectionView)
        {
            collectionView.SelectedItem = null;
        }

        if (selected is not null)
        {
            await Shell.Current.GoToAsync(selected.Route);
        }
    }

    private sealed record MoreToolItem(string Title, string Description, string Route);
}
