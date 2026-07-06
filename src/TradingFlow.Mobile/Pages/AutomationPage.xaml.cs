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
    private readonly List<ModeChoice> modeChoices = new();
    private readonly IDispatcherTimer refreshTimer;
    private MobileCatalogResponse? catalog;
    private IReadOnlyList<MobileStrategyOption> longStrategies = Array.Empty<MobileStrategyOption>();
    private MobileAutomationSessionSnapshot? selectedSession;

    public AutomationPage()
    {
        InitializeComponent();
        SessionsView.ItemsSource = sessions;
        AlertsView.ItemsSource = alerts;
        hub.Updated += OnHubUpdated;
        // Refresh only the live sessions on a timer so the selected mode is never reset.
        refreshTimer = Dispatcher.CreateTimer();
        refreshTimer.Interval = TimeSpan.FromSeconds(15);
        refreshTimer.Tick += async (_, _) => await RefreshSessionsAsync();
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

    private async Task RefreshSessionsAsync()
    {
        try
        {
            var latestSessions = await api.GetAutomationSessionsAsync() ?? Array.Empty<MobileAutomationSessionSnapshot>();
            var selectedId = selectedSession?.SessionId;
            sessions.Clear();
            foreach (var session in latestSessions.Take(20))
            {
                sessions.Add(session);
            }

            if (selectedId is { } id)
            {
                SessionsView.SelectedItem = sessions.FirstOrDefault(session => session.SessionId == id);
            }
        }
        catch
        {
            // Best-effort background refresh; the manual Refresh button surfaces errors.
        }
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
            longStrategies = (catalog?.Strategies ?? Array.Empty<MobileStrategyOption>())
                .Where(x => x.Direction.Equals("long", StringComparison.OrdinalIgnoreCase))
                .ToList();
            SelectDefaultConfig();
            BuildModeChoices();
            RestoreSavedMode();

            NotificationAccessLabel.Text = notificationAccess.IsNotificationAccessEnabled()
                ? "Notification access is enabled."
                : "Notification access is not enabled yet.";

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

    // The paper profile (broker/env) is chosen silently so it never clutters the UI.
    private void SelectDefaultConfig()
    {
        if (catalog?.PaperConfigs is not { Count: > 0 } configs)
        {
            return;
        }

        var saved = Preferences.Get("TradingFlowAutomationConfigPath", string.Empty);
        var config = configs.FirstOrDefault(x => x.Path.Equals(saved, StringComparison.OrdinalIgnoreCase)) ?? configs[0];
        Preferences.Set("TradingFlowAutomationConfigPath", config.Path);
        Preferences.Set("TradingFlowAutomationPackage", string.Empty);
        Preferences.Set("TradingFlowAutomationAppName", "Stock Pulse");
    }

    // One combobox: Off (disable), Direct enter (auto-forward), or a strategy (validate).
    private void BuildModeChoices()
    {
        modeChoices.Clear();
        modeChoices.Add(new ModeChoice("Off — capture alerts only", ModeKind.Disabled, null));
        modeChoices.Add(new ModeChoice("Direct enter (auto-forward)", ModeKind.Direct, null));
        foreach (var strategy in longStrategies)
        {
            modeChoices.Add(new ModeChoice($"{strategy.StrategyName} (validate)", ModeKind.Strategy, strategy));
        }

        ModePicker.ItemsSource = modeChoices.ToList();
    }

    private void RestoreSavedMode()
    {
        var autoForward = Preferences.Get("TradingFlowAutomationAutoForward", false);
        var entryMode = Preferences.Get("TradingFlowAutomationEntryMode", "immediate_paper");
        var strategyPath = Preferences.Get("TradingFlowAutomationStrategyPath", string.Empty);

        ModeChoice? choice;
        if (!autoForward)
        {
            choice = modeChoices.FirstOrDefault(x => x.Kind == ModeKind.Disabled);
        }
        else if (entryMode.Equals("validate_strategy", StringComparison.OrdinalIgnoreCase))
        {
            choice = modeChoices.FirstOrDefault(x =>
                x.Strategy is not null && x.Strategy.Path.Equals(strategyPath, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            choice = modeChoices.FirstOrDefault(x => x.Kind == ModeKind.Direct);
        }

        ModePicker.SelectedItem = choice ?? modeChoices.FirstOrDefault();
    }

    private ModeChoice? SelectedMode => ModePicker.SelectedItem as ModeChoice ?? modeChoices.FirstOrDefault();

    private void OnModeChanged(object? sender, EventArgs e)
    {
        UpdateModeHint();
        PersistMode();
    }

    private void PersistMode()
    {
        var mode = SelectedMode;
        if (mode is null)
        {
            return;
        }

        Preferences.Set("TradingFlowAutomationAutoForward", mode.Kind != ModeKind.Disabled);
        Preferences.Set("TradingFlowAutomationEntryMode", mode.Kind == ModeKind.Strategy ? "validate_strategy" : "immediate_paper");

        var strategyPath = mode.Strategy?.Path ?? longStrategies.FirstOrDefault()?.Path;
        if (!string.IsNullOrWhiteSpace(strategyPath))
        {
            Preferences.Set("TradingFlowAutomationStrategyPath", strategyPath);
        }
    }

    private void UpdateModeHint()
    {
        var mode = SelectedMode;
        EntryModeHintLabel.Text = mode?.Kind switch
        {
            ModeKind.Disabled => "Automation off. Alerts are captured but nothing trades.",
            ModeKind.Direct => longStrategies.FirstOrDefault() is { } exit
                ? $"Enters immediately on each alert; {exit.StrategyName} manages the exit."
                : "Enters immediately on each alert; the strategy engine manages the exit.",
            ModeKind.Strategy => $"Validates {mode.Strategy!.StrategyName} entry on each alert, then it manages the exit.",
            _ => string.Empty
        };
    }

    private void ReloadAlerts()
    {
        alerts.Clear();
        foreach (var alert in hub.GetAlerts())
        {
            alerts.Add(alert);
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

    private void OnClearAlerts(object? sender, EventArgs e)
    {
        hub.Clear();
    }

    private enum ModeKind
    {
        Disabled,
        Direct,
        Strategy
    }

    private sealed record ModeChoice(string Display, ModeKind Kind, MobileStrategyOption? Strategy)
    {
        public override string ToString() => Display;
    }
}
