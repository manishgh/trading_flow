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
    private readonly ObservableCollection<InstalledNotificationApp> appMatches = new();
    private MobileCatalogResponse? catalog;
    private MobileAutomationSessionSnapshot? selectedSession;
    private CapturedAutomationAlert? selectedAlert;

    public AutomationPage()
    {
        InitializeComponent();
        SessionsView.ItemsSource = sessions;
        AlertsView.ItemsSource = alerts;
        AppMatchPicker.ItemsSource = appMatches;
        hub.Updated += OnHubUpdated;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            catalog ??= await api.GetCatalogAsync();
            ConfigPicker.ItemsSource = catalog?.PaperConfigs.ToList();
            StrategyPicker.ItemsSource = (catalog?.Strategies ?? Array.Empty<MobileStrategyOption>())
                .Where(x => x.Direction.Equals("long", StringComparison.OrdinalIgnoreCase))
                .ToList();

            SelectSavedDefaults();
            NotificationAccessLabel.Text = notificationAccess.IsNotificationAccessEnabled()
                ? "Notification access is enabled."
                : "Notification access is not enabled yet.";
            PackageEntry.Text = Preferences.Get("TradingFlowAutomationPackage", string.Empty);
            SourceAppLabel.Text = Preferences.Get("TradingFlowAutomationAppName", string.Empty) is { Length: > 0 } appName
                ? appName
                : "None selected";
            SourceAppStatusLabel.Text = BuildSourceAppStatus();
            AutoForwardCheck.IsChecked = Preferences.Get("TradingFlowAutomationAutoForward", false);

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
            await DisplayAlertAsync("Automation", exception.Message, "OK");
        }
        finally
        {
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

        if (AutoForwardCheck.IsChecked && string.IsNullOrWhiteSpace(PackageEntry.Text))
        {
            await DisplayAlertAsync("Automation", "Find or select a source app before enabling auto-start.", "OK");
            return;
        }

        Preferences.Set("TradingFlowAutomationConfigPath", config.Path);
        Preferences.Set("TradingFlowAutomationStrategyPath", strategy.Path);
        Preferences.Set("TradingFlowAutomationPackage", PackageEntry.Text?.Trim() ?? string.Empty);
        Preferences.Set("TradingFlowAutomationAppName", SourceAppLabel.Text == "None selected" ? string.Empty : SourceAppLabel.Text);
        Preferences.Set("TradingFlowAutomationAutoForward", AutoForwardCheck.IsChecked);
        var mode = AutoForwardCheck.IsChecked ? "Auto-forward is ON." : "Auto-forward is OFF. Use Forward Selected to test manually.";
        await DisplayAlertAsync("Automation", $"Defaults saved. {mode}", "OK");
    }

    private async void OnFindSourceApp(object? sender, EventArgs e)
    {
        var query = SourceAppSearchEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            await DisplayAlertAsync("Automation", "Type the app name first, for example Stock Pulse.", "OK");
            return;
        }

        appMatches.Clear();
        var matches = await notificationAccess.FindInstalledAppsAsync(query);
        foreach (var match in matches)
        {
            appMatches.Add(match);
        }

        if (appMatches.Count == 0)
        {
            SourceAppStatusLabel.Text = $"No installed app matched '{query}'. If Android hides it, wait for one notification and use the captured alert app.";
            return;
        }

        AppMatchPicker.SelectedIndex = 0;
        SourceAppStatusLabel.Text = notificationAccess.IsNotificationAccessEnabled()
            ? $"Found {appMatches.Count} match(es). Choose one and tap Use Matched App."
            : $"Found {appMatches.Count} match(es), but notification access is not enabled yet.";
    }

    private async void OnUseMatchedApp(object? sender, EventArgs e)
    {
        if (AppMatchPicker.SelectedItem is not InstalledNotificationApp app)
        {
            await DisplayAlertAsync("Automation", "Find and select an installed source app first.", "OK");
            return;
        }

        SaveSourceApp(app.AppName, app.PackageName);
        await DisplayAlertAsync(
            "Automation",
            notificationAccess.IsNotificationAccessEnabled()
                ? $"TradingFlow can capture notifications from {app.AppName}."
                : $"{app.AppName} is selected. Enable notification access before alerts can be captured.",
            "OK");
    }

    private void OnClearSourceApp(object? sender, EventArgs e)
    {
        SaveSourceApp(string.Empty, string.Empty);
        AutoForwardCheck.IsChecked = false;
        Preferences.Set("TradingFlowAutomationAutoForward", false);
        SourceAppStatusLabel.Text = "Source app filter cleared. New notifications from any app may appear for discovery.";
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
                $"User started {ticker} from Android app."));

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
            ? "Select an alert below. TradingFlow extracts the ticker; auto-start only listens to the saved source app."
            : $"Selected {selectedAlert.ParsedTickerText} from {selectedAlert.AppName}. Start Paper uses the selected strategy.";
    }

    private async void OnUseSelectedApp(object? sender, EventArgs e)
    {
        if (selectedAlert is null)
        {
            await DisplayAlertAsync("Automation", "Select a captured alert from Stock Pulse first.", "OK");
            return;
        }

        SaveSourceApp(selectedAlert.AppName, selectedAlert.PackageName);
        SelectedAlertLabel.Text = $"Using {selectedAlert.AppName}. Auto-start will match package {selectedAlert.PackageName}.";
        await DisplayAlertAsync("Automation", $"Using {selectedAlert.AppName}. Select mode/playbook and Save Automation Defaults.", "OK");
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
        SelectedAlertLabel.Text = "No alerts captured yet. Enable notification access, then wait for the source app to send one alert.";
    }

    private void SaveSourceApp(string appName, string packageName)
    {
        PackageEntry.Text = packageName;
        SourceAppLabel.Text = string.IsNullOrWhiteSpace(appName) ? "None selected" : appName;
        Preferences.Set("TradingFlowAutomationPackage", packageName);
        Preferences.Set("TradingFlowAutomationAppName", appName);
        SourceAppStatusLabel.Text = BuildSourceAppStatus();
    }

    private string BuildSourceAppStatus()
    {
        var packageName = Preferences.Get("TradingFlowAutomationPackage", string.Empty);
        var appName = Preferences.Get("TradingFlowAutomationAppName", string.Empty);
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return "No source app verified yet. Alerts are captured for discovery until you save a filter.";
        }

        return notificationAccess.IsNotificationAccessEnabled()
            ? $"Ready: capturing parsed alerts from {appName} only."
            : $"Selected {appName}, but notification access must be enabled.";
    }
}
