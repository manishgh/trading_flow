using TradingFlow.Mobile.Resources.Styles;
using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Pages;

public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        BackendUrlEntry.Text = AppServices.Api.BaseUrl;
        HapticsSwitch.IsToggled = AppServices.Haptics.IsEnabled;
    }

    private async void OnHapticsToggled(object? sender, ToggledEventArgs e)
    {
        AppServices.Haptics.IsEnabled = e.Value;

        // Emit one pulse when switching on so the operator feels what they enabled.
        if (e.Value)
        {
            await AppServices.Haptics.SignalAsync(HapticSignal.ReviewReady);
        }
    }

    /// <summary>Validates and saves the operator's explicit HTTPS origin before making a connection check.</summary>
    private async void OnSaveAndTest(object? sender, EventArgs e)
    {
        if (String.IsNullOrWhiteSpace(BackendUrlEntry.Text))
        {
            await DisplayAlertAsync("Settings", "Enter a backend URL.", "OK");
            return;
        }

        try { AppServices.Api.BaseUrl = BackendUrlEntry.Text; }
        catch (ArgumentException error)
        {
            await DisplayAlertAsync("Settings", error.Message, "OK");
            return;
        }
        HealthLabel.Text = "Checking backend...";
        var healthy = await AppServices.Api.CheckHealthAsync();
        HealthLabel.Text = healthy
            ? "Connected to TradingFlow."
            : "Could not reach TradingFlow. Check URL, network, and whether the web app is running.";
        HealthLabel.TextColor = healthy ? ThemePalette.Positive : ThemePalette.Negative;
    }

}
