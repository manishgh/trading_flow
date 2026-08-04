namespace TradingFlow.Mobile.Pages;

public partial class LoginPage : ContentPage
{
    public LoginPage()
    {
        InitializeComponent();
    }

    private async void OnSignInClicked(object? sender, EventArgs e)
    {
        var userName = UserNameEntry.Text?.Trim() ?? String.Empty;
        var password = PasswordEntry.Text ?? String.Empty;
        if (userName.Length == 0 || password.Length == 0)
        {
            StatusLabel.Text = "Username and password are required.";
            return;
        }

        try
        {
            StatusLabel.Text = "Signing in...";
            var state = await AppServices.Api.LoginAsync(userName, password, RememberMeCheckBox.IsChecked);
            PasswordEntry.Text = String.Empty;
            StatusLabel.Text = state is null ? "Sign-in failed." : $"Signed in as {state.UserName}.";
            if (state is not null)
            {
                await Shell.Current.GoToAsync("..");
            }
        }
        catch (Exception exception)
        {
            PasswordEntry.Text = String.Empty;
            StatusLabel.Text = exception.Message.Contains("401", StringComparison.OrdinalIgnoreCase)
                ? "The username or password is invalid."
                : $"Sign-in failed: {exception.Message}";
        }
    }
}
