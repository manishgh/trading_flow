using TradingFlow.Contracts.V1;
using System.Globalization;
using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Pages;

public partial class OrderTicketPage : ContentPage, IQueryAttributable
{
    private Guid wishlistId;
    private string ticker = String.Empty;
    private OrderPreviewResponse? reviewed;
    private Guid candidateId;
    private Guid runId;
    private PreviewOrderRequest? pending;
    private string commandKey = Guid.NewGuid().ToString("N");
    private bool isBusy;
    private CancellationTokenSource? lifetime;

    public OrderTicketPage()
    {
        InitializeComponent();
        void Changed() { if (!isBusy) { pending = null; commandKey = Guid.NewGuid().ToString("N"); } }
        foreach (var entry in new[] { QuantityEntry, LimitEntry, StopEntry, TargetEntry })
            entry.TextChanged += (_, _) => Changed();
        ExtendedHoursCheckBox.CheckedChanged += (_, _) => Changed();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing(); lifetime?.Dispose(); lifetime = new();
        AppServices.Api.SessionChanged += OnSessionChanged;
    }
    protected override void OnDisappearing()
    {
        lifetime?.Cancel(); AppServices.Api.SessionChanged -= OnSessionChanged;
        base.OnDisappearing();
    }
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        lifetime?.Cancel(); pending = null; reviewed = null;
        ConfirmButton.IsEnabled = false; ShowError("Connection changed. Reopen and review this order.");
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
        if (query.TryGetValue("candidateId", out var candidateValue)) Guid.TryParse(candidateValue?.ToString(), out candidateId);
        if (query.TryGetValue("runId", out var runValue)) Guid.TryParse(runValue?.ToString(), out runId);
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

        await RunAsync(async token =>
        {
            if (pending is null)
            {
                var context = await AppServices.Api.GetOrderContextAsync(token) ?? throw new InvalidOperationException("Account context unavailable.");
                if (String.IsNullOrWhiteSpace(context.AccountId)) throw new InvalidOperationException("A bound broker account is required.");
                CandidateStateResponse? candidate = null;
                CandidateRunResponse? run = null;
                if (candidateId != Guid.Empty && runId != Guid.Empty)
                {
                    run = await AppServices.Api.Workflow.CandidatesAsync(runId, token);
                    candidate = run.Candidates.SingleOrDefault(item => item.CandidateId == candidateId)
                        ?? throw new InvalidOperationException("Candidate is no longer available in this run.");
                    ticker = candidate.Symbol;
                }
                StrategyReference identity;
                Guid universeId;
                if (candidate is not null)
                {
                    identity = candidate.Strategy;
                    universeId = run!.Provenance.UniverseSnapshotId;
                }
                else
                {
                    if (!context.OperatorOverrideAllowed) throw new InvalidOperationException("Select an authorized candidate. Operator override requires Administrator.");
                    if (wishlistId == Guid.Empty) throw new InvalidOperationException("Open an operator ticket from a wishlist.");
                    var catalog = await AppServices.Api.Workflow.CatalogAsync("backtest", token);
                    var choices = catalog.Strategies.ToArray();
                    if (choices.Length == 0) throw new InvalidOperationException("No exact strategy is available.");
                    var labels = choices.Select(item => item.Name + " · " + item.Identity.SemanticVersion + " · " + item.Identity.ContentSha256[..12]).ToArray();
                    var selected = await DisplayActionSheetAsync("Select exact strategy", "Cancel", null, labels);
                    var index = Array.IndexOf(labels, selected);
                    if (index < 0) return;
                    identity = choices[index].Identity;
                    var universe = await AppServices.Api.Workflow.PreviewAsync(new(ContractVersions.VersionOne, "swing", null,
                        [new("wishlist", wishlistId.ToString(), wishlistId)]), token);
                    universeId = universe.UniverseSnapshotId;
                }
                token.ThrowIfCancellationRequested();
                pending = new(ContractVersions.VersionOne, context.AccountId, candidate?.CandidateId, candidate?.Version,
                    run?.CandidateRunId, universeId, identity, ticker, "buy", quantity, "limit", "day", limit, stop, target,
                    ExtendedHoursCheckBox.IsChecked, candidate is null ? "operator_override" : "strategy_validated", commandKey);
            }
            var preview = await AppServices.Api.Workflow.PreviewOrderAsync(pending, token);
            if (preview is null)
            {
                throw new InvalidOperationException("The order review response was empty.");
            }
            token.ThrowIfCancellationRequested();
            RenderPreview(preview);
        });
    }

    private async void OnConfirmClicked(object? sender, EventArgs e)
    {
        if (reviewed is null)
        {
            ShowError("Review the order again before confirming.");
            return;
        }

        ConfirmButton.IsEnabled = false;
        await RunAsync(async token =>
        {
            var confirmation = await AppServices.Api.Workflow.ConfirmOrderAsync(new(ContractVersions.VersionOne, reviewed!.PreviewToken, reviewed.IdempotencyKey), token);
            if (confirmation is null)
            {
                throw new InvalidOperationException("The order confirmation response was empty.");
            }
            token.ThrowIfCancellationRequested();
            DraftSection.IsVisible = false;
            ReviewSection.IsVisible = false;
            ConfirmationSection.IsVisible = true;
            ConfirmationLabel.Text = $"{confirmation.State}: {confirmation.RequestedQuantity:0.####} {confirmation.Symbol}\n{confirmation.RejectionReason ?? confirmation.BrokerOrderId ?? confirmation.ClientOrderId}";
            if (confirmation.State == "confirmation_pending")
            {
                ReviewSection.IsVisible = true;
                ConfirmButton.IsEnabled = true;
                ConfirmButton.Text = "Check same command";
            }
            else await AppServices.Haptics.SignalAsync(confirmation.State == "rejected" ? HapticSignal.OrderRejected : HapticSignal.OrderAccepted);
        });
    }

    private void OnEditClicked(object? sender, EventArgs e)
    {
        reviewed = null;
        pending = null;
        commandKey = Guid.NewGuid().ToString("N");
        ReviewSection.IsVisible = false;
        DraftSection.IsVisible = true;
        ErrorLabel.IsVisible = false;
    }

    private void RenderPreview(OrderPreviewResponse preview)
    {
        reviewed = preview;
        DraftSection.IsVisible = false;
        ReviewSection.IsVisible = true;
        ReviewStatusLabel.Text = "Ready to confirm";
        ReviewSummaryLabel.Text = $"{preview.Quantity} {preview.Symbol} at {preview.LimitPrice:C2}\nNotional {preview.EstimatedNotional:C2} · maximum loss {preview.MaximumLoss:C2}";
        MarketEvidenceLabel.Text = $"Quote {preview.QuoteTimestampUtc:O}\nExpires {preview.ExpiresAtUtc:O}";
        PolicyLabel.Text = $"Policy {preview.ExecutionPolicy} · {preview.Strategy.StrategyId} · {preview.Strategy.SemanticVersion}";
        RejectionsLabel.Text = String.Join(Environment.NewLine, preview.Warnings);
        RejectionsLabel.IsVisible = preview.Warnings.Count > 0;
        ConfirmButton.IsVisible = true;
        ConfirmButton.IsEnabled = true;
        _ = AppServices.Haptics.SignalAsync(HapticSignal.ReviewReady);
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (isBusy) return;
        isBusy = true;
        DraftSection.IsEnabled = false;
        BusyIndicator.IsVisible = true;
        BusyIndicator.IsRunning = true;
        ErrorLabel.IsVisible = false;
        try { await action(lifetime?.Token ?? new CancellationToken(true)); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ShowError(exception.Message); if (reviewed is not null) ConfirmButton.IsEnabled = true; }
        finally
        {
            isBusy = false;
            DraftSection.IsEnabled = true;
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
