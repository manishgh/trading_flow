namespace TradingFlow.Mobile.Pages;

/// <summary>Public market screens remain reachable before authentication.</summary>
public sealed class WorkspacePage : ContentPage
{
    public WorkspacePage()
    {
        Title = "Workspace";
        var body = new VerticalStackLayout { Padding = 16, Spacing = 12 };
        body.Add(new Label { Text = "Workspace", Style = (Style)Application.Current!.Resources["Headline"] });
        body.Add(new Label { Text = "Market news and earnings are available without signing in.", LineBreakMode = LineBreakMode.WordWrap });
        Add("News", nameof(NewsPage));
        Add("Earnings", nameof(EarningsPage));
        body.Add(new Label { Text = "Sign in for daily preparation, candidate review and your account.", LineBreakMode = LineBreakMode.WordWrap });
        Add("Daily review", nameof(PreparationPage));
        Add("Candidates", nameof(CandidatesPage));
        Add("Sources", nameof(WishlistsPage));
        Add("Activity", nameof(NotificationsPage));
        Add("Sign in", nameof(LoginPage));
        Content = new ScrollView { Content = body };

        void Add(string title, string route)
        {
            var button = new Button { Text = title };
            button.Clicked += async (_, _) => await Shell.Current.GoToAsync(route);
            body.Add(button);
        }
    }
}
