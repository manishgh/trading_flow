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
    }

    private async void OnSaveAndTest(object? sender, EventArgs e)
    {
        if (String.IsNullOrWhiteSpace(BackendUrlEntry.Text))
        {
            await DisplayAlertAsync("Settings", "Enter a backend URL.", "OK");
            return;
        }

        AppServices.Api.BaseUrl = BackendUrlEntry.Text;
        HealthLabel.Text = "Checking backend...";
        var healthy = await AppServices.Api.CheckHealthAsync();
        HealthLabel.Text = healthy
            ? "Connected to TradingFlow."
            : "Could not reach TradingFlow. Check URL, network, and whether the web app is running.";
        HealthLabel.TextColor = healthy ? Color.FromArgb("#067647") : Color.FromArgb("#B42318");
    }

    private async void OnUsePhoneUrl(object? sender, EventArgs e)
    {
        AppServices.Api.UsePhysicalDeviceDefault();
        BackendUrlEntry.Text = AppServices.Api.BaseUrl;
        await TestCurrentUrlAsync();
    }

    private async void OnUseNgrokUrl(object? sender, EventArgs e)
    {
        AppServices.Api.UseNgrokDefault();
        BackendUrlEntry.Text = AppServices.Api.BaseUrl;
        await TestCurrentUrlAsync();
    }

    private async void OnUseEmulatorUrl(object? sender, EventArgs e)
    {
        AppServices.Api.UseAndroidEmulatorDefault();
        BackendUrlEntry.Text = AppServices.Api.BaseUrl;
        await TestCurrentUrlAsync();
    }

    private async Task TestCurrentUrlAsync()
    {
        HealthLabel.Text = $"Checking {AppServices.Api.BaseUrl}...";
        var healthy = await AppServices.Api.CheckHealthAsync();
        HealthLabel.Text = healthy
            ? $"Connected: {AppServices.Api.BaseUrl}"
            : $"Unreachable: {AppServices.Api.BaseUrl}";
        HealthLabel.TextColor = healthy ? Color.FromArgb("#067647") : Color.FromArgb("#B42318");
    }
}
