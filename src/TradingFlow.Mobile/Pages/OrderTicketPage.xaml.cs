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
    private bool confirmationStarted;
    private bool confirmationUnresolved;
    private long sessionVersion = -1;
    private StrategyReference? plannedStrategy;
    private OrderTicketPlanResponse? pricePlan;
    private CancellationTokenSource? lifetime;

    // Connect draft edits to command invalidation without changing a request already in flight.
    public OrderTicketPage()
    {
        InitializeComponent();
        // Keep retries bound to the same draft until the operator edits an idle ticket.
        void Changed() { if (!isBusy) { pending = null; pricePlan = null; RiskSizeButton.IsEnabled = false; commandKey = Guid.NewGuid().ToString("N"); } }
        foreach (var entry in new[] { QuantityEntry, LimitEntry, StopEntry, TargetEntry })
            entry.TextChanged += (_, _) => Changed();
        ExtendedHoursCheckBox.CheckedChanged += (_, _) => Changed();
    }

    // Resume the same ticket after a system interruption and invalidate reviews only on a real session change.
    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (sessionVersion >= 0 && sessionVersion != AppServices.Api.SessionVersion) { OnSessionChanged(null, EventArgs.Empty); sessionVersion = AppServices.Api.SessionVersion; }
        sessionVersion = AppServices.Api.SessionVersion;
        lifetime?.Dispose(); lifetime = new();
        AppServices.Api.SessionChanged += OnSessionChanged;
    }
    protected override void OnDisappearing()
    {
        lifetime?.Cancel(); AppServices.Api.SessionChanged -= OnSessionChanged;
        base.OnDisappearing();
    }
    // Discard account-bound commands when authentication or the backend changes.
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        lifetime?.Cancel(); pending = null; reviewed = null; plannedStrategy = null; pricePlan = null; confirmationStarted = false; confirmationUnresolved = false; EditButton.IsEnabled = true;
        DraftSection.IsVisible = true; ReviewSection.IsVisible = false; ConfirmationSection.IsVisible = false;
        commandKey = Guid.NewGuid().ToString("N");
        CloseButton.IsEnabled = !isBusy;
        ConfirmButton.IsEnabled = false; ShowError("Connection changed. Reopen and review this order.");
    }
    // Expand optional planning and session controls without loading more data.
    private void OnAdvancedClicked(object? sender, EventArgs e)
    {
        AdvancedSection.IsVisible = !AdvancedSection.IsVisible;
        AdvancedButton.Text = AdvancedSection.IsVisible ? "Strategy sizing and options −" : "Strategy sizing and options +";
    }

    // Keep detailed provenance available while leaving risk, warnings and review expiry visible.
    private void OnEvidenceClicked(object? sender, EventArgs e)
    {
        EvidenceSection.IsVisible = !EvidenceSection.IsVisible;
        EvidenceButton.Text = EvidenceSection.IsVisible ? "Review evidence −" : "Review evidence +";
    }

    // Return to the invoking screen only when no request or uncertain confirmation can be lost.
    private async void OnCloseClicked(object? sender, EventArgs e)
    {
        if (CanDismiss()) await Shell.Current.GoToAsync("..");
    }

    // Apply the same dismissal rule to Android hardware and gesture Back navigation.
    protected override bool OnBackButtonPressed() => !CanDismiss() || base.OnBackButtonPressed();

    // Preserve the preview token and idempotency key until the existing command has a known outcome.
    private bool CanDismiss()
    {
        if (!isBusy && !confirmationUnresolved) return true;
        ShowError(confirmationUnresolved
            ? "The order outcome is unknown. Use Check same command before closing."
            : "Wait for the current request before closing.");
        return false;
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

    private async Task<StrategyReference?> SelectStrategyAsync(CancellationToken token)
    {
        var catalog = await AppServices.Api.Workflow.CatalogAsync("backtest", token);
        var choices = catalog.Strategies.ToArray();
        var labels = choices.Select(item => item.Name + " · " + item.Identity.SemanticVersion + " · " + item.Identity.ContentSha256[..12]).ToArray();
        token.ThrowIfCancellationRequested();
        var choice = await DisplayActionSheetAsync("Exact strategy", "Cancel", null, labels);
        token.ThrowIfCancellationRequested();
        var index = Array.IndexOf(labels, choice);
        return index < 0 ? null : choices[index].Identity;
    }

    private async void OnPlanClicked(object? sender, EventArgs e) => await RunAsync(async token =>
    {
        StrategyReference? strategy;
        if (candidateId != Guid.Empty && runId != Guid.Empty)
        {
            var run = await AppServices.Api.Workflow.CandidatesAsync(runId, token);
            if (run.Provenance.Mode is not ("paper" or "paper_shadow" or "paper_experiment"))
                throw new InvalidOperationException("Research candidates cannot open executable orders.");
            var candidate = run.Candidates.SingleOrDefault(x => x.CandidateId == candidateId)
                ?? throw new InvalidOperationException("The exact candidate is unavailable.");
            ticker = candidate.Symbol; strategy = candidate.Strategy;
        }
        else
        {
            var context = await AppServices.Api.GetOrderContextAsync(token);
            if (context?.OperatorOverrideAllowed != true) throw new InvalidOperationException("Open an authorized paper candidate.");
            strategy = await SelectStrategyAsync(token);
            if (strategy is null) return;
        }
        var limit = TryReadDecimal(LimitEntry.Text, out var entered) && entered > 0m ? entered : (decimal?)null;
        var plan = await AppServices.Api.Workflow.PlanOrderAsync(new(ContractVersions.VersionOne, ticker, strategy, limit), token);
        token.ThrowIfCancellationRequested();
        plannedStrategy = strategy; pricePlan = plan; pending = null; commandKey = Guid.NewGuid().ToString("N");
        TickerLabel.Text = $"{ticker} | PAPER";
        LimitEntry.Text = plan.Limit?.ToString("0.####", CultureInfo.CurrentCulture);
        StopEntry.Text = plan.Stop?.ToString("0.####", CultureInfo.CurrentCulture);
        TargetEntry.Text = plan.Target?.ToString("0.####", CultureInfo.CurrentCulture);
        PlanNotesLabel.Text = plan.StopNote + "\n" + plan.TargetNote;
        RiskSizeButton.IsEnabled = plan.RiskBudget is > 0m && plan.Stop is > 0m && plan.Limit > plan.Stop;
    });

    private void OnRiskSizeClicked(object? sender, EventArgs e)
    {
        if (isBusy || pricePlan is not { RiskBudget: > 0m, Limit: { } limit, Stop: { } stop } || limit <= stop) return;
        var shares = Decimal.Floor(pricePlan.RiskBudget.Value / (limit - stop));
        if (shares < 1m) { ShowError("The account risk budget does not allow one share at this stop distance."); return; }
        QuantityEntry.Text = shares.ToString(CultureInfo.CurrentCulture);
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
                    identity = plannedStrategy ?? await SelectStrategyAsync(token)
                        ?? throw new InvalidOperationException("Select an exact strategy before reviewing.");
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

    // Confirm or recheck the reviewed command; transport errors retain its original token and key.
    private async void OnConfirmClicked(object? sender, EventArgs e)
    {
        if (reviewed is null)
        {
            ShowError("Review the order again before confirming.");
            return;
        }

        if (isBusy) return;
        confirmationStarted = true; confirmationUnresolved = true; EditButton.IsEnabled = false;
        ConfirmButton.Text = "Check same command";
        ConfirmButton.IsEnabled = false;
        await RunAsync(async token =>
        {
            var confirmation = await AppServices.Api.Workflow.ConfirmOrderAsync(new(ContractVersions.VersionOne, reviewed!.PreviewToken, reviewed.IdempotencyKey), token);
            if (confirmation is null)
            {
                throw new InvalidOperationException("The order confirmation response was empty.");
            }
            token.ThrowIfCancellationRequested();
            confirmationUnresolved = confirmation.State == "confirmation_pending";
            DraftSection.IsVisible = false;
            ReviewSection.IsVisible = false;
            ConfirmationSection.IsVisible = true;
            ConfirmationLabel.Text = $"{confirmation.State}: {confirmation.RequestedQuantity:0.####} {confirmation.Symbol}\n{confirmation.RejectionReason ?? confirmation.BrokerOrderId ?? confirmation.ClientOrderId}";
            if (confirmation.State == "rejected") { confirmationStarted = false; EditButton.IsEnabled = true; ReviewSection.IsVisible = true; ConfirmButton.IsVisible = false; }
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
        if (isBusy || confirmationStarted) return;
        reviewed = null;
        pending = null;
        commandKey = Guid.NewGuid().ToString("N");
        ReviewSection.IsVisible = false;
        ConfirmationSection.IsVisible = false;
        DraftSection.IsVisible = true;
        ErrorLabel.IsVisible = false;
    }

    // Show essential price and risk review with expiry; provenance stays behind the evidence toggle.
    private void RenderPreview(OrderPreviewResponse preview)
    {
        reviewed = preview;
        DraftSection.IsVisible = false;
        ReviewSection.IsVisible = true;
        ReviewStatusLabel.Text = "Ready to confirm";
        ReviewSummaryLabel.Text = $"{preview.Quantity} {preview.Symbol} · {preview.OrderType}/{preview.TimeInForce} at {preview.LimitPrice:C2}\nStop {preview.StopLossPrice:C2} · target {preview.TakeProfitPrice:C2}\nExtended hours {pending?.AllowExtendedHoursTrading}\nNotional {preview.EstimatedNotional:C2} · maximum loss {preview.MaximumLoss:C2}";
        ExpiryLabel.Text = $"Review expires {preview.ExpiresAtUtc:O}";
        MarketEvidenceLabel.Text = $"Quote {preview.QuoteTimestampUtc:O}";
        EvidenceSection.IsVisible = false;
        EvidenceButton.Text = "Review evidence +";
        ConfirmButton.Text = "Confirm paper order";
        PolicyLabel.Text = $"Policy {preview.ExecutionPolicy} · {preview.Strategy.StrategyId} · {preview.Strategy.SemanticVersion}\n{preview.Strategy.ContentSha256}";
        RejectionsLabel.Text = String.Join(Environment.NewLine, preview.Warnings);
        RejectionsLabel.IsVisible = preview.Warnings.Count > 0;
        ConfirmButton.IsVisible = true;
        ConfirmButton.IsEnabled = true;
        _ = AppServices.Haptics.SignalAsync(HapticSignal.ReviewReady);
    }
    // Serialize UI requests and keep dismissal disabled until pending confirmation is reconciled.
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (isBusy) return;
        isBusy = true;
        CloseButton.IsEnabled = false;
        DraftSection.IsEnabled = false;
        BusyIndicator.IsVisible = true;
        BusyIndicator.IsRunning = true;
        ErrorLabel.IsVisible = false;
        try { await action(lifetime?.Token ?? new CancellationToken(true)); }
        catch (OperationCanceledException)
        {
            if (confirmationUnresolved && reviewed is not null)
            {
                ConfirmButton.IsEnabled = true;
                ShowError("The order outcome is unknown. Use Check same command when connected.");
            }
        }
        catch (Exception exception) { ShowError(exception.Message); if (reviewed is not null) ConfirmButton.IsEnabled = true; }
        finally
        {
            isBusy = false;
            CloseButton.IsEnabled = !confirmationUnresolved;
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
