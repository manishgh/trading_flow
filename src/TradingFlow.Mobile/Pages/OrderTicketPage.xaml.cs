using System.Globalization;
using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Pages;

public partial class OrderTicketPage : ContentPage, IQueryAttributable
{
    private Guid wishlistId;
    private string ticker = String.Empty;
    private string? ticketToken;
    private bool isBusy;

    public OrderTicketPage()
    {
        InitializeComponent();
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
        if (query.TryGetValue("limitPrice", out var limitValue) &&
            Decimal.TryParse(Uri.UnescapeDataString(limitValue?.ToString() ?? String.Empty), NumberStyles.Number, CultureInfo.InvariantCulture, out var limit))
        {
            LimitEntry.Text = limit.ToString("0.####", CultureInfo.CurrentCulture);
        }
        TickerLabel.Text = $"{ticker} | PAPER";
    }

    private async void OnPreviewClicked(object? sender, EventArgs e)
    {
        if (!TryReadDecimal(QuantityEntry.Text, out var quantity) ||
            !TryReadDecimal(LimitEntry.Text, out var limit) ||
            !TryReadDecimal(StopEntry.Text, out var stop) ||
            !TryReadDecimal(TargetEntry.Text, out var target))
        {
            ShowError("Quantity, limit, stop, and target must be valid numbers.");
            return;
        }

        await RunAsync(async () =>
        {
            var preview = await AppServices.Api.PreviewManualOrderAsync(new MobileOrderPreviewRequest(
                ticker,
                quantity,
                limit,
                stop,
                target,
                HorizonPicker.SelectedItem?.ToString() ?? "intraday",
                ExtendedHoursCheckBox.IsChecked));
            if (preview is null)
            {
                throw new InvalidOperationException("The order review response was empty.");
            }
            RenderPreview(preview);
        });
    }

    private async void OnConfirmClicked(object? sender, EventArgs e)
    {
        if (String.IsNullOrWhiteSpace(ticketToken))
        {
            ShowError("Review the order again before confirming.");
            return;
        }

        ConfirmButton.IsEnabled = false;
        await RunAsync(async () =>
        {
            var confirmation = await AppServices.Api.ConfirmManualOrderAsync(ticketToken);
            if (confirmation is null)
            {
                throw new InvalidOperationException("The order confirmation response was empty.");
            }
            DraftSection.IsVisible = false;
            ReviewSection.IsVisible = false;
            ConfirmationSection.IsVisible = true;
            ConfirmationLabel.Text = $"{confirmation.Quantity:0.####} {confirmation.Ticker} at limit {confirmation.LimitPrice:C2}\nOrder {confirmation.OrderId}";
        });
    }

    private void OnEditClicked(object? sender, EventArgs e)
    {
        ticketToken = null;
        ReviewSection.IsVisible = false;
        DraftSection.IsVisible = true;
        ErrorLabel.IsVisible = false;
    }

    private void RenderPreview(MobileOrderTicketPreview preview)
    {
        ticketToken = preview.TicketToken;
        DraftSection.IsVisible = false;
        ReviewSection.IsVisible = true;
        ReviewStatusLabel.Text = preview.CanSubmit ? "Ready to confirm" : "Submission blocked";
        ReviewSummaryLabel.Text = $"{preview.Quantity:0.####} {preview.Ticker} at {preview.LimitPrice:C2} | notional {preview.Notional:C2}\nStop {preview.StopLossPrice:C2} | target {preview.TakeProfitPrice:C2}";
        MarketEvidenceLabel.Text = $"Bid {preview.BidPrice:C2} | Ask {preview.AskPrice:C2}\nSpread {preview.SpreadBps:0.##} bps | quote age {preview.QuoteAgeMilliseconds} ms\n{preview.Session} | {preview.TimeInForce} | expires {preview.ExpiresAtUtc:HH:mm:ss} UTC";
        PolicyLabel.Text = $"Policy {preview.Policy} | extended hours {(preview.AllowExtendedHoursTrading ? "requested" : "off")}";
        RejectionsLabel.Text = String.Join(Environment.NewLine, preview.Rejections.Select(reason => $"- {reason}"));
        RejectionsLabel.IsVisible = preview.Rejections.Count > 0;
        ConfirmButton.IsVisible = preview.CanSubmit;
        ConfirmButton.IsEnabled = preview.CanSubmit;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (isBusy) return;
        isBusy = true;
        BusyIndicator.IsVisible = true;
        BusyIndicator.IsRunning = true;
        ErrorLabel.IsVisible = false;
        try { await action(); }
        catch (Exception exception) { ShowError(exception.Message); }
        finally
        {
            isBusy = false;
            BusyIndicator.IsRunning = false;
            BusyIndicator.IsVisible = false;
        }
    }

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.IsVisible = true;
    }

    private static bool TryReadDecimal(string? value, out decimal result) =>
        Decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out result) ||
        Decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out result);
}
