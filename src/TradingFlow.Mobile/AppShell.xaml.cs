using TradingFlow.Mobile.Pages;

namespace TradingFlow.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(nameof(WishlistsPage), typeof(WishlistsPage));
        Routing.RegisterRoute(nameof(NotificationsPage), typeof(NotificationsPage));
        Routing.RegisterRoute(nameof(PreparationPage), typeof(PreparationPage));
        Loaded += async (_, _) => { Services.PreparationNotifications.Start(); Services.EarningsBeatNotifications.Start(); try { await Services.PreparationNotifications.OpenPendingAsync(); } catch { } };
        Routing.RegisterRoute(nameof(CandidatesPage), typeof(CandidatesPage));
        Loaded += async (_, _) => { try { await Services.CandidateNavigation.OpenPendingAsync(); } catch { } };
        Routing.RegisterRoute(nameof(NewsPage), typeof(NewsPage));
        Routing.RegisterRoute(nameof(EarningsPage), typeof(EarningsPage));
        Routing.RegisterRoute(nameof(AutomationPage), typeof(AutomationPage));
        Routing.RegisterRoute(nameof(BacktestsPage), typeof(BacktestsPage));
        Routing.RegisterRoute(nameof(WarmupPage), typeof(WarmupPage));
        Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
        Routing.RegisterRoute(nameof(PaperPage), typeof(PaperPage));
        Routing.RegisterRoute(nameof(SymbolDetailPage), typeof(SymbolDetailPage));
        Routing.RegisterRoute(nameof(OrderTicketPage), typeof(OrderTicketPage));
        Routing.RegisterRoute(nameof(OrdersPage), typeof(OrdersPage));
        Routing.RegisterRoute(nameof(RunningTradesPage), typeof(RunningTradesPage));
        Routing.RegisterRoute(nameof(LoginPage), typeof(LoginPage));
    }
}
