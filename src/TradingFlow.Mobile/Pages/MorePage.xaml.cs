using System.Collections.ObjectModel;

namespace TradingFlow.Mobile.Pages;

public partial class MorePage : ContentPage
{
    private readonly ObservableCollection<MoreToolItem> tools =
    [
        new("Account / sign in", "Access your saved workflows and paper account.", nameof(LoginPage)),
        new("Simulated trading", "Authorized paper strategy runs and their controls.", nameof(PaperPage)),
        new("Strategy positions", "Holdings attached to paper strategy runs.", nameof(RunningTradesPage)),
        new("Earnings", "Reporting calendar and assessments.", nameof(EarningsPage)),
        new("Stock groups", "Followed stocks and saved discovery sources.", nameof(WishlistsPage)),
        new("Daily review", "Preparation workflows and upcoming sessions.", nameof(PreparationPage)),
        new("Candidates", "Review exact strategy checks and evidence.", nameof(CandidatesPage)),
        new("Activity", "Candidate alerts and notification history.", nameof(NotificationsPage)),
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
