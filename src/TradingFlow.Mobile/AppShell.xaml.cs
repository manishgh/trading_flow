using TradingFlow.Mobile.Pages;

namespace TradingFlow.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(nameof(NewsPage), typeof(NewsPage));
        Routing.RegisterRoute(nameof(AutomationPage), typeof(AutomationPage));
        Routing.RegisterRoute(nameof(BacktestsPage), typeof(BacktestsPage));
        Routing.RegisterRoute(nameof(WarmupPage), typeof(WarmupPage));
        Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
        Routing.RegisterRoute(nameof(PaperPage), typeof(PaperPage));
        Routing.RegisterRoute(nameof(SymbolDetailPage), typeof(SymbolDetailPage));
    }
}
