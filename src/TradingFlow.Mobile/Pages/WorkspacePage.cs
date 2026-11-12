using TradingFlow.Mobile.Controls;

namespace TradingFlow.Mobile.Pages;

/// <summary>Offers the daily desk and public market destinations without loading trading data.</summary>
public sealed class WorkspacePage : ContentPage
{
    // Keep the mobile landing screen compact and route every action into its existing workflow.
    public WorkspacePage()
    {
        Title = "Desk";
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitionCollection {
            new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } } };
        heading.Add(new Label { Text = "Daily focus", Style = (Style)Application.Current!.Resources["PageTitle"] });
        heading.Add(new EnvironmentBadgeView(), 1);
        var cards = new Grid { ColumnDefinitions = new ColumnDefinitionCollection {
            new() { Width = GridLength.Star }, new() { Width = GridLength.Star } },
            RowDefinitions = new RowDefinitionCollection { new() { Height = GridLength.Auto }, new() { Height = GridLength.Auto } },
            ColumnSpacing = 12, RowSpacing = 12 };
        cards.Add(Destination("Daily review", "Prepare today's stock groups", nameof(PreparationPage)), 0, 0);
        cards.Add(Destination("Candidates", "Review strategy checks", nameof(CandidatesPage)), 1, 0);
        cards.Add(Destination("Earnings", "Reporting calendar", nameof(EarningsPage)), 0, 1);
        cards.Add(Destination("Stock groups", "Manage followed stocks", nameof(WishlistsPage)), 1, 1);
        var activity = new Button { Text = "Activity and alerts", Style = (Style)Application.Current.Resources["QuietButton"] };
        activity.Clicked += async (_, _) => await Shell.Current.GoToAsync(nameof(NotificationsPage));
        Content = new ScrollView { Content = new VerticalStackLayout { Padding = 20, Spacing = 20, Children = {
            heading,
            new Label { Text = "Your daily review and markets", Style = (Style)Application.Current.Resources["SubHeadline"] },
            cards, activity,
            new Label { Text = "News and earnings are public. Sign in from More for your account and saved workflows.",
                Style = (Style)Application.Current.Resources["SubHeadline"], LineBreakMode = LineBreakMode.WordWrap }
        } } };
    }

    // Pair a short destination title with one line of context and an accessible native button.
    private static View Destination(string title, string description, string route)
    {
        var open = new Button { Text = title, FontSize = 16, MinimumHeightRequest = 72,
            Style = (Style)Application.Current!.Resources["QuietButton"] };
        SemanticProperties.SetDescription(open, title + ". " + description);
        open.Clicked += async (_, _) => await Shell.Current.GoToAsync(route);
        return new VerticalStackLayout { Spacing = 6, Children = { open,
            new Label { Text = description, FontSize = 12, LineBreakMode = LineBreakMode.WordWrap,
                Style = (Style)Application.Current.Resources["SubHeadline"] } } };
    }
}
