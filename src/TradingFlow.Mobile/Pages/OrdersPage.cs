using System.Globalization;
using TradingFlow.Contracts.V1;
using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Pages;

/// <summary>Account-wide paper positions and journal; reviewed commands never use a direct broker route.</summary>
public sealed class OrdersPage : ContentPage
{
    private readonly VerticalStackLayout rows = new() { Padding = 16, Spacing = 12 };
    private readonly Label status = Text("Reading the bound paper account…");
    private readonly Dictionary<Guid, CancelOrderRequest> cancellations = [];
    private readonly Dictionary<Guid, OrderCommandResponse> cancellationOutcomes = [];
    private readonly Dictionary<string, ClosePositionRequest> closes = [];
    private readonly Dictionary<string, OrderCommandResponse> closeOutcomes = [];
    private readonly LatestMarketRead reads = new(() => AppServices.Api.SessionVersion);
    private readonly IDispatcherTimer timer;
    private bool busy, previousOutcomesRequireReconciliation;
    private long sessionVersion = -1;

    public OrdersPage()
    {
        Title = "Trading"; Content = new ScrollView { Content = rows }; rows.Add(status);
        var sources = new ToolbarItem { Text = "Watch / sources" };
        sources.Clicked += async (_, _) => await Shell.Current.GoToAsync(nameof(WishlistsPage));
        var runs = new ToolbarItem { Text = "Strategy positions" };
        runs.Clicked += async (_, _) => await Shell.Current.GoToAsync(nameof(RunningTradesPage));
        ToolbarItems.Add(sources); ToolbarItems.Add(runs);
        timer = Dispatcher.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(6);
        timer.Tick += async (_, _) => await RunAsync(RefreshAsync);
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing(); reads.Activate();
        if (sessionVersion != AppServices.Api.SessionVersion) ChangeSession();
        AppServices.Api.SessionChanged += SessionChanged;
        timer.Start(); await RunAsync(RefreshAsync);
    }
    protected override void OnDisappearing()
    {
        timer.Stop(); AppServices.Api.SessionChanged -= SessionChanged; reads.Deactivate(); base.OnDisappearing();
    }
    private void SessionChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(async () => {
        if (!reads.Active) return; ChangeSession(); await RunAsync(RefreshAsync);
    });
    private void ChangeSession()
    {
        reads.Cancel();
        previousOutcomesRequireReconciliation |= cancellations.Count != 0 || closes.Count != 0;
        cancellations.Clear(); cancellationOutcomes.Clear(); closes.Clear(); closeOutcomes.Clear();
        sessionVersion = AppServices.Api.SessionVersion;
        rows.Clear(); rows.Add(status);
        status.Text = "Connection changed. Sign in to read the current account.";
        if (previousOutcomesRequireReconciliation) rows.Add(Text("Earlier commands may have reached the previous account. Reconcile their journal before issuing another exit or cancellation. They are not replayed in this session."));
    }
    private void RequireCurrent(LatestMarketRead.Read read)
    {
        if (!reads.IsCurrent(read)) throw new OperationCanceledException(read.Token);
    }
    private async Task RunAsync(Func<LatestMarketRead.Read, Task> action)
    {
        if (busy || !reads.Active) return;
        busy = true; var read = reads.Begin(); rows.IsEnabled = false;
        try { await action(read); }
        catch (OperationCanceledException) when (read.Token.IsCancellationRequested || !reads.IsCurrent(read)) { }
        catch (Exception exception) {
            if (reads.IsCurrent(read)) {
                status.Text = exception.Message + (cancellations.Count + closes.Count > 0 ? " Issued commands remain unchanged; check or retry the same command." : "");
                rows.Clear(); rows.Add(status); AddRecoveryControls(); rows.Add(Action("Refresh current account", RefreshAsync));
            }
        } finally {
            busy = false;
            if (reads.IsCurrent(read)) rows.IsEnabled = true;
            else if (reads.Active) Dispatcher.Dispatch(async () => await RunAsync(RefreshAsync));
        }
    }
    private Button Action(string text, Func<LatestMarketRead.Read, Task> action)
    {
        var button = new Button { Text = text, MinimumHeightRequest = 44 };
        button.Clicked += async (_, _) => await RunAsync(action); return button;
    }
    private static Label Text(string value) => new() { Text = value, LineBreakMode = LineBreakMode.WordWrap };
    private void AddRecoveryControls()
    {
        if (previousOutcomesRequireReconciliation) rows.Add(Text("Earlier-session outcomes require reconciliation in the original account. Changing a connection does not cancel an order."));
        foreach (var (id, request) in cancellations.ToArray()) {
            cancellationOutcomes.TryGetValue(id, out var outcome);
            rows.Add(Text($"Issued cancellation: {request.OrderIntentId} | {request.AccountId}\nCommand {request.IdempotencyKey}\n{outcome?.State ?? "Outcome unknown"} · {outcome?.RejectionReason}"));
            rows.Add(Action("Check / retry exact cancellation", read => SendCancellationAsync(id, request, read)));
            if (NativeExitReview.CanEndUndispatched(outcome)) rows.Add(Action("End known undispatched cancellation", read => { RequireCurrent(read); cancellations.Remove(id); cancellationOutcomes.Remove(id); return RefreshAsync(read); }));
        }
        foreach (var (symbol, request) in closes.ToArray()) {
            closeOutcomes.TryGetValue(symbol, out var outcome);
            rows.Add(Text($"Issued exit: {ExitSummary(request)}\nCommand {request.IdempotencyKey}\n{outcome?.State ?? "Outcome unknown"} · {outcome?.RejectionReason}"));
            rows.Add(Action("Check / retry exact issued exit", read => SendExitAsync(symbol, request, read)));
            if (NativeExitReview.CanEndUndispatched(outcome)) rows.Add(Action("End known undispatched exit review", read => { RequireCurrent(read); closes.Remove(symbol); closeOutcomes.Remove(symbol); return RefreshAsync(read); }));
        }
    }
    private async Task RefreshAsync(LatestMarketRead.Read read)
    {
        var account = await AppServices.Api.Workflow.PortfolioAsync(read.Token);
        var journal = await AppServices.Api.Workflow.OrderJournalAsync(read.Token); RequireCurrent(read);
        // Journal terminal states release local retained controls; a POST acknowledgment alone is not terminal.
        foreach (var id in cancellations.Keys.ToArray())
            if (journal.Any(x => x.OrderIntentId == id && NativeExitReview.IsTerminal(x.State))) { cancellations.Remove(id); cancellationOutcomes.Remove(id); }
        foreach (var (symbol, outcome) in closeOutcomes.ToArray())
            if (!String.IsNullOrWhiteSpace(outcome.ClientOrderId) && journal.Any(x => x.ClientOrderId == outcome.ClientOrderId && NativeExitReview.IsTerminal(x.State))) { closes.Remove(symbol); closeOutcomes.Remove(symbol); }
        rows.Clear(); rows.Add(status);
        var fresh = account.Available && DateTimeOffset.UtcNow - account.ReadAtUtc is { TotalSeconds: >= 0 and < 30 };
        status.Text = (fresh ? "Paper account" : "Holdings unavailable or stale") + " · " + account.Detail + "\nRead " + account.ReadAtUtc.ToString("O");
        AddRecoveryControls();
        rows.Add(Action("Review an authorized paper candidate", _ => Shell.Current.GoToAsync(nameof(CandidatesPage))));
        rows.Add(Action("Refresh positions and orders", RefreshAsync)); rows.Add(Text("Account positions"));
        if (!account.Available) rows.Add(Text("Position evidence is unavailable; this does not mean no positions."));
        else if (account.Positions.Count == 0) rows.Add(Text("No open positions in the bound paper account."));
        foreach (var position in account.Positions) {
            rows.Add(Text($"{position.Symbol} · {position.Quantity:0.####} shares\nAverage {position.AverageEntry:C2} · cost {position.CostBasis:C2} · value {position.MarketValue:C2}\nUnrealized P/L {position.UnrealizedPl:C2}"));
            if (position.Quantity > 0m && position.Quantity != Decimal.Truncate(position.Quantity)) rows.Add(Text("This position contains fractional shares. A reviewed exit may sell an explicitly entered whole-share portion; fractional shares remain. No quantity is rounded."));
            if (closes.ContainsKey(position.Symbol)) rows.Add(Text("An issued exit is retained above. Resolve it before reviewing another exit for this symbol."));
            else if (fresh && position.Quantity >= 1m) rows.Add(Action("Review exit", current => CloseAsync(position, current)));
        }
        rows.Add(Text("Recent orders · most recent 200"));
        if (journal.Length == 0) rows.Add(Text("No journaled orders for the bound account."));
        foreach (var order in journal) {
            rows.Add(Text($"{order.Symbol} · {order.Side} {order.Quantity:0.####} · {order.State}\n{order.OrderType}/{order.TimeInForce} · limit {order.LimitPrice:C2} · filled {order.FilledQuantity:0.####}\nUpdated {order.UpdatedAtUtc:O}"));
            if (order.CandidateId is { } candidateId) rows.Add(Action("Candidate audit and news", _ => Shell.Current.GoToAsync(nameof(CandidatesPage) + "?runId=" + order.RunId + "&candidateId=" + candidateId)));
            if (order.OrderIntentId is { } id && order.AccountId is not null && order.BrokerOrderId is not null &&
                order.State.ToUpperInvariant() is "INTENT" or "SUBMITTED" or "ACKED" or "PARTIALLYFILLED" or "PARTIALLY_FILLED" && !cancellations.ContainsKey(id))
                rows.Add(Action("Review cancellation", current => CancelAsync(order, current)));
        }
    }
    private async Task CancelAsync(OrderJournalEntry order, LatestMarketRead.Read read)
    {
        var id = order.OrderIntentId!.Value;
        if (!await DisplayAlertAsync("Cancel working order", $"Cancel {order.Symbol}? It may fill before cancellation is acknowledged.", "Cancel order", "Keep order")) return;
        RequireCurrent(read);
        var request = new CancelOrderRequest(ContractVersions.VersionOne, order.AccountId!, id, "Operator reviewed Android cancellation", Guid.NewGuid().ToString("N"));
        cancellations[id] = request; await SendCancellationAsync(id, request, read);
    }
    private async Task SendCancellationAsync(Guid id, CancelOrderRequest request, LatestMarketRead.Read read)
    {
        RequireCurrent(read);
        var outcome = await AppServices.Api.Workflow.CancelOrderAsync(request, read.Token); RequireCurrent(read);
        cancellationOutcomes[id] = outcome;
        await RefreshAsync(read); RequireCurrent(read);
        status.Text = $"Cancellation response {outcome.State} · {outcome.RejectionReason}. The journal determines final acknowledgment.";
    }
    private static decimal Price(string? input, string name) => NativeExitReview.ParsePrice(input, CultureInfo.CurrentCulture, name);
    private static string ExitSummary(ClosePositionRequest request) => $"{request.AccountId} · sell {request.Quantity:0.####} {request.Symbol}\n{request.OrderType} / {request.TimeInForce} · limit {(request.LimitPrice is { } limit ? limit.ToString("0.####", CultureInfo.CurrentCulture) : "not used")} · trigger {(request.TriggerPrice is { } trigger ? trigger.ToString("0.####", CultureInfo.CurrentCulture) : "not used")}\nExtended-hours permission: {(request.AllowExtendedHoursTrading ? "allowed" : "regular session only")}";
    private async Task CloseAsync(PortfolioPositionResponse position, LatestMarketRead.Read read)
    {
        var context = await AppServices.Api.GetOrderContextAsync(read.Token); RequireCurrent(read);
        if (String.IsNullOrWhiteSpace(context?.AccountId)) throw new InvalidOperationException("A bound broker account is required.");
        var typeLabel = await DisplayActionSheetAsync("Paper exit type", "Back", null, "Limit", "Market", "Stop", "Stop-limit"); RequireCurrent(read);
        var type = typeLabel switch { "Limit" => "limit", "Market" => "market", "Stop" => "stop", "Stop-limit" => "stop_limit", _ => null };
        if (type is null) return;
        var tifLabel = await DisplayActionSheetAsync("Time in force", "Back", null, "Day", "Good till canceled", "Immediate or cancel", "Fill or kill"); RequireCurrent(read);
        var tif = tifLabel switch { "Day" => "day", "Good till canceled" => "gtc", "Immediate or cancel" => "ioc", "Fill or kill" => "fok", _ => null }; if (tif is null) return;
        var quantityInput = await DisplayPromptAsync("Exit quantity", $"Whole shares within the observed {position.Quantity:0.####} {position.Symbol}", "Continue", "Back", keyboard: Keyboard.Numeric, initialValue: position.Quantity == Decimal.Truncate(position.Quantity) ? position.Quantity.ToString("0", CultureInfo.CurrentCulture) : ""); RequireCurrent(read); if (quantityInput is null) return;
        var quantity = NativeExitReview.ParseQuantity(quantityInput, CultureInfo.CurrentCulture);
        decimal? limit = null, trigger = null;
        if (type is "limit" or "stop_limit") { var input = await DisplayPromptAsync("Exit limit", "Limit price", "Continue", "Back", keyboard: Keyboard.Numeric); RequireCurrent(read); if (input is null) return; limit = Price(input, "limit price"); }
        if (type is "stop" or "stop_limit") { var input = await DisplayPromptAsync("Exit trigger", "Stop trigger price", "Continue", "Back", keyboard: Keyboard.Numeric); RequireCurrent(read); if (input is null) return; trigger = Price(input, "trigger price"); }
        var extended = false;
        if (type == "limit" && tif == "day") {
            var permission = await DisplayActionSheetAsync("Session permission", "Back", null, "Regular session only", "Allow extended hours"); RequireCurrent(read); if (permission is null || permission == "Back") return; extended = permission == "Allow extended hours";
        }
        var request = NativeExitReview.Create(context.AccountId, position.Symbol, position.Quantity, quantity, type, tif, extended, limit, trigger, Guid.NewGuid().ToString("N"));
        if (!await DisplayAlertAsync("Confirm paper exit", ExitSummary(request) + "\nThe server rechecks the account, open quantity, market session and execution eligibility.", "Submit reviewed exit", "Back")) return;
        RequireCurrent(read); closes[position.Symbol] = request; await SendExitAsync(position.Symbol, request, read);
    }
    private async Task SendExitAsync(string symbol, ClosePositionRequest request, LatestMarketRead.Read read)
    {
        RequireCurrent(read);
        var outcome = await AppServices.Api.Workflow.ClosePositionAsync(request, read.Token); RequireCurrent(read);
        closeOutcomes[symbol] = outcome; await RefreshAsync(read); RequireCurrent(read);
        status.Text = $"Exit response {outcome.State} · {outcome.RejectionReason}. Check the position and journal for execution.";
    }
}
