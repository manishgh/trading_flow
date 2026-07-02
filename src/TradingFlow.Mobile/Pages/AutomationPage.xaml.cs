using System.Collections.ObjectModel;
using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Pages;

public partial class AutomationPage : ContentPage
{
    private readonly TradingFlowApiClient api = AppServices.Api;
    private readonly NotificationAutomationHub hub = AppServices.AutomationHub;
    private readonly INotificationAccessHelper notificationAccess = AppServices.NotificationAccess;
    private readonly ObservableCollection<MobileAutomationSessionSnapshot> sessions = new();
    private readonly ObservableCollection<CapturedAutomationAlert> alerts = new();
    private MobileCatalogResponse? catalog;
    private MobileAutomationSessionSnapshot? selectedSession;
    private CapturedAutomationAlert? selectedAlert;

    public AutomationPage()
    {
        InitializeComponent();
        SessionsView.ItemsSource = sessions;
        AlertsView.ItemsSource = alerts;
        hub.Updated += OnHubUpdated;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        BusyIndicator.IsVisible = true;
        BusyIndicator.IsRunning = true;
        try
        {
            var healthy = await api.CheckHealthAsync();
            StatusLabel.Text = healthy
                ? $"Connected: {api.BaseUrl}"
                : $"Backend unreachable: {api.BaseUrl}";
            StatusLabel.TextColor = healthy ? Color.FromArgb("#067647") : Color.FromArgb("#B42318");

            catalog ??= await api.GetCatalogAsync();
            ConfigPicker.ItemsSource = catalog?.PaperConfigs.ToList();
            StrategyPicker.ItemsSource = (catalog?.Strategies ?? Array.Empty<MobileStrategyOption>())
                .Where(x => x.Direction.Equals("long", StringComparison.OrdinalIgnoreCase))
                .ToList();

            SelectSavedDefaults();
            NotificationAccessLabel.Text = notificationAccess.IsNotificationAccessEnabled()
                ? "Notification access is enabled."
                : "Notification access is not enabled yet.";
            Preferences.Set("TradingFlowAutomationAppName", "Stock Pulse");
            AutoForwardCheck.IsChecked = Preferences.Get("TradingFlowAutomationAutoForward", false);
            SelectSavedEntryMode();

            var latestSessions = await api.GetAutomationSessionsAsync() ?? Array.Empty<MobileAutomationSessionSnapshot>();
            sessions.Clear();
            foreach (var session in latestSessions.Take(20))
            {
                sessions.Add(session);
            }

            ReloadAlerts();
        }
        catch (Exception exception)
        {
            StatusLabel.Text = $"Automation error: {exception.Message}";
            StatusLabel.TextColor = Color.FromArgb("#B42318");
            await DisplayAlertAsync("Automation", exception.Message, "OK");
        }
        finally
        {
            BusyIndicator.IsRunning = false;
            BusyIndicator.IsVisible = false;
            RefreshRoot.IsRefreshing = false;
        }
    }

    private void SelectSavedDefaults()
    {
        var savedConfig = Preferences.Get("TradingFlowAutomationConfigPath", string.Empty);
        var savedStrategy = Preferences.Get("TradingFlowAutomationStrategyPath", string.Empty);

        if (ConfigPicker.ItemsSource is IEnumerable<MobileRunConfigOption> configs)
        {
            ConfigPicker.SelectedItem = configs.FirstOrDefault(x => x.Path.Equals(savedConfig, StringComparison.OrdinalIgnoreCase))
                ?? configs.FirstOrDefault();
        }

        if (StrategyPicker.ItemsSource is IEnumerable<MobileStrategyOption> strategies)
        {
            StrategyPicker.SelectedItem = strategies.FirstOrDefault(x => x.Path.Equals(savedStrategy, StringComparison.OrdinalIgnoreCase))
                ?? strategies.FirstOrDefault();
        }
    }

    private void SelectSavedEntryMode()
    {
        var savedEntryMode = Preferences.Get("TradingFlowAutomationEntryMode", "immediate_paper");
        ValidateStrategyCheck.IsChecked = savedEntryMode.Equals("validate_strategy", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveEntryMode()
    {
        return ValidateStrategyCheck.IsChecked ? "validate_strategy" : "immediate_paper";
    }

    private void ReloadAlerts()
    {
        alerts.Clear();
        foreach (var alert in hub.GetAlerts())
        {
            alerts.Add(alert);
        }

        if (selectedAlert is null || alerts.All(alert => alert.AlertId != selectedAlert.AlertId))
        {
            SelectedAlertLabel.Text = alerts.Count == 0
                ? "No alerts captured yet. Enable notification access, then wait for Stock Pulse to send one alert."
                : "Select a Stock Pulse alert below. The app extracts the ticker, then sends paper entry to TradingFlow; exits stay strategy-managed.";
        }
    }

    private void OnHubUpdated(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(ReloadAlerts);
    }

    private async void OnOpenNotificationSettings(object? sender, EventArgs e)
    {
        await notificationAccess.OpenNotificationAccessSettingsAsync();
    }

    private async void OnSaveDefaults(object? sender, EventArgs e)
    {
        if (ConfigPicker.SelectedItem is not MobileRunConfigOption config ||
            StrategyPicker.SelectedItem is not MobileStrategyOption strategy)
        {
            await DisplayAlertAsync("Automation", "Select a paper config and strategy first.", "OK");
            return;
        }

        Preferences.Set("TradingFlowAutomationConfigPath", config.Path);
        Preferences.Set("TradingFlowAutomationStrategyPath", strategy.Path);
        Preferences.Set("TradingFlowAutomationPackage", string.Empty);
        Preferences.Set("TradingFlowAutomationAppName", "Stock Pulse");
        Preferences.Set("TradingFlowAutomationAutoForward", AutoForwardCheck.IsChecked);
        Preferences.Set("TradingFlowAutomationEntryMode", ResolveEntryMode());
        var mode = AutoForwardCheck.IsChecked ? "Auto-forward is ON for Stock Pulse." : "Auto-forward is OFF. Use Start Selected to test manually.";
        await DisplayAlertAsync("Automation", $"Defaults saved. {mode}", "OK");
    }

    private void OnAutomationOptionChanged(object? sender, CheckedChangedEventArgs e)
    {
        Preferences.Set("TradingFlowAutomationAutoForward", AutoForwardCheck.IsChecked);
        Preferences.Set("TradingFlowAutomationEntryMode", ResolveEntryMode());
    }

    private async void OnRunNow(object? sender, EventArgs e)
    {
        if (ConfigPicker.SelectedItem is not MobileRunConfigOption config ||
            StrategyPicker.SelectedItem is not MobileStrategyOption strategy)
        {
            await DisplayAlertAsync("Automation", "Select a paper config and strategy first.", "OK");
            return;
        }

        var ticker = ManualTickerEntry.Text?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(ticker))
        {
            await DisplayAlertAsync("Automation", "Enter a ticker.", "OK");
            return;
        }

        try
        {
            var session = await api.StartAutomationEntryAsync(new MobileAutomationStartRequest(
                config.Path,
                strategy.Path,
                ticker,
                $"mobile_manual_{ticker}_{DateTimeOffset.Now:yyyyMMdd_HHmmss}",
                "manual_run",
                null,
                "Manual run now",
                $"User started {ticker} from Android app.",
                ResolveEntryMode()));

            if (session is not null)
            {
                sessions.Insert(0, session);
                await DisplayAlertAsync("Automation", $"Started automation for {ticker}.", "OK");
                await Task.Delay(1500);
                await LoadAsync();
            }
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Automation", exception.Message, "OK");
        }
    }

    private async void OnRefresh(object? sender, EventArgs e) => await LoadAsync();
    private async void OnRefreshClicked(object? sender, EventArgs e) => await LoadAsync();

    private void OnSessionSelected(object? sender, SelectionChangedEventArgs e)
    {
        selectedSession = e.CurrentSelection.FirstOrDefault() as MobileAutomationSessionSnapshot;
        DetailCard.IsVisible = selectedSession is not null;
        EventsStack.Clear();
        if (selectedSession is null)
        {
            return;
        }

        DetailTitle.Text = $"{selectedSession.Ticker} - {selectedSession.Source}";
        DetailStatus.Text = $"{selectedSession.Status} - {selectedSession.CurrentStage}";
        foreach (var item in selectedSession.Events.Reverse().Take(10))
        {
            EventsStack.Add(new Label
            {
                Text = item,
                FontSize = 12,
                TextColor = Color.FromArgb("#475467")
            });
        }
    }

    private async void OnCancelSession(object? sender, EventArgs e)
    {
        if (selectedSession is null)
        {
            return;
        }

        await api.CancelAutomationSessionAsync(selectedSession.SessionId);
        await LoadAsync();
    }

    private void OnAlertSelected(object? sender, SelectionChangedEventArgs e)
    {
        selectedAlert = e.CurrentSelection.FirstOrDefault() as CapturedAutomationAlert;
        SelectedAlertLabel.Text = selectedAlert is null
            ? "Select a Stock Pulse alert below. TradingFlow extracts the ticker; auto-start ignores other apps."
            : $"Selected {selectedAlert.ParsedTickerText} from {selectedAlert.AppName}. Start Paper uses the selected strategy.";
    }

    private async void OnForwardAlert(object? sender, EventArgs e)
    {
        if (selectedAlert is null)
        {
            await DisplayAlertAsync("Automation", "Select a captured alert first.", "OK");
            return;
        }

        if (string.IsNullOrWhiteSpace(selectedAlert.Ticker))
        {
            await DisplayAlertAsync("Automation", "This alert does not contain a parsed ticker.", "OK");
            return;
        }

        var forwarded = await hub.ForwardAsync(selectedAlert.AlertId);
        if (!forwarded)
        {
            await DisplayAlertAsync("Automation", "Alert could not be forwarded.", "OK");
        }

        ReloadAlerts();
        await Task.Delay(1500);
        await LoadAsync();
    }

    private void OnClearAlerts(object? sender, EventArgs e)
    {
        hub.Clear();
        selectedAlert = null;
        AlertsView.SelectedItem = null;
        SelectedAlertLabel.Text = "No alerts captured yet. Enable notification access, then wait for Stock Pulse to send one alert.";
    }

}
