using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Pages;

public partial class SymbolDetailPage : ContentPage, IQueryAttributable
{
    private Guid wishlistId;
    private string ticker = String.Empty;
    private string mode = "unified";
    private bool isLoading;

    public SymbolDetailPage()
    {
        InitializeComponent();
        UpdateModeButtons();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("wishlistId", out var wishlistValue))
        {
            Guid.TryParse(Uri.UnescapeDataString(wishlistValue?.ToString() ?? String.Empty), out wishlistId);
        }
        if (query.TryGetValue("ticker", out var tickerValue))
        {
            ticker = Uri.UnescapeDataString(tickerValue?.ToString() ?? String.Empty).Trim().ToUpperInvariant();
        }
        TickerLabel.Text = String.IsNullOrWhiteSpace(ticker) ? "Symbol" : ticker;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (isLoading || wishlistId == Guid.Empty || String.IsNullOrWhiteSpace(ticker))
        {
            return;
        }

        isLoading = true;
        BusyIndicator.IsVisible = true;
        BusyIndicator.IsRunning = true;
        ErrorLabel.IsVisible = false;
        try
        {
            var response = await AppServices.Api.GetSymbolIntelligenceAsync(wishlistId, ticker, mode);
            if (response is null)
            {
                throw new InvalidOperationException("Symbol intelligence response was empty.");
            }
            Render(response);
        }
        catch (Exception exception)
        {
            ErrorLabel.Text = $"Symbol detail unavailable: {exception.Message}";
            ErrorLabel.IsVisible = true;
        }
        finally
        {
            isLoading = false;
            BusyIndicator.IsRunning = false;
            BusyIndicator.IsVisible = false;
        }
    }

    private void Render(MobileSymbolIntelligenceResponse response)
    {
        UpdatedLabel.Text = $"Updated {response.FetchedAtUtc.LocalDateTime:dd/MM HH:mm:ss}";
        var decision = response.TradingFlowDecision;
        DecisionStatusLabel.Text = decision.EligibilityLabel;
        DecisionReasonLabel.Text = decision.EligibilityReason;
        QuoteLabel.Text = decision.QuoteText;
        QuoteAgeLabel.Text = decision.QuoteAgeText;
        PositionLabel.Text = decision.HasOpenTrade && decision.Trade is not null
            ? $"Open position | {decision.Trade.Quantity:0.####} shares | {decision.Trade.PlText}"
            : "No open tracked position";

        var model = response.ModelIntelligence;
        ModelStatusLabel.Text = model.StatusText;
        ModelGeneratedLabel.Text = model.GeneratedText;
        ModelIdentityLabel.Text = model.ModelText;
        ModelUnavailableLabel.Text = model.AvailabilityReason ?? String.Empty;
        ModelUnavailableLabel.IsVisible = model.AvailabilityStatus != "available";

        SwingSection.IsVisible = model.Swing is not null;
        if (model.Swing is { } swing)
        {
            SwingSignalLabel.Text = $"Swing | {swing.Signal}";
            SwingProbabilityLabel.Text = swing.ProbabilityText;
            SwingContextLabel.Text = swing.ContextText;
        }

        IntradaySection.IsVisible = model.Intraday is not null;
        if (model.Intraday is { } intraday)
        {
            IntradaySignalLabel.Text = $"Intraday | {intraday.Signal}";
            IntradayProbabilityLabel.Text = intraday.ProbabilityText;
            IntradayTechnicalLabel.Text = intraday.TechnicalText;
        }

        ReadinessSection.IsVisible = model.ReadinessReasons.Count > 0;
        ReadinessReasonsLabel.Text = String.Join(Environment.NewLine, model.ReadinessReasons.Select(reason => $"- {reason}"));
    }

    private async void OnModeSelected(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: string selectedMode } && selectedMode != mode)
        {
            mode = selectedMode;
            UpdateModeButtons();
            await LoadAsync();
        }
    }

    private async void OnRefreshClicked(object? sender, EventArgs e) => await LoadAsync();

    private void UpdateModeButtons()
    {
        var active = Color.FromArgb("#176B87");
        var inactive = Color.FromArgb("#667085");
        UnifiedButton.BackgroundColor = mode == "unified" ? active : inactive;
        SwingButton.BackgroundColor = mode == "swing" ? active : inactive;
        IntradayButton.BackgroundColor = mode == "intraday" ? active : inactive;
    }
}
