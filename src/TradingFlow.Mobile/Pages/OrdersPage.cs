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
    private readonly VerticalStackLayout positionRows = new() { Spacing = 10 };
    private readonly VerticalStackLayout orderRows = new() { Spacing = 10, IsVisible = false };
    private readonly Button positionsTab = new() { Text = "Positions" };
    private readonly Button ordersTab = new() { Text = "Orders" };
    private readonly Label cashValue = new() { FontSize = 25, FontAttributes = FontAttributes.Bold };
    private readonly Label investedValue = new() { FontSize = 25, FontAttributes = FontAttributes.Bold };
    private readonly Grid accountSummary = new() { IsVisible = false, ColumnSpacing = 20,
        ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Star }, new() { Width = GridLength.Star } } };

    // Keep account status and the local view switch fixed above independently scrollable holdings or orders.
    public OrdersPage()
    {
        Title = "Trading";
        var tabs = new Grid { ColumnDefinitions = new ColumnDefinitionCollection {
            new() { Width = GridLength.Star }, new() { Width = GridLength.Star } }, ColumnSpacing = 8 };
        tabs.Add(positionsTab); tabs.Add(ordersTab, 1);
        positionsTab.Clicked += (_, _) => SelectSection(false);
        ordersTab.Clicked += (_, _) => SelectSection(true);
        accountSummary.Add(new VerticalStackLayout { Spacing = 4, Children = {
            new Label { Text = "Cash · USD", Style = (Style)Application.Current!.Resources["SubHeadline"] }, cashValue } });
        accountSummary.Add(new VerticalStackLayout { Spacing = 4, Children = {
            new Label { Text = "Invested · USD", Style = (Style)Application.Current!.Resources["SubHeadline"] }, investedValue } }, 1);
        var header = new VerticalStackLayout { Padding = new Thickness(20, 12), Spacing = 14, Children = { status, accountSummary, tabs } };
        var layout = new Grid { RowDefinitions = new RowDefinitionCollection {
            new() { Height = GridLength.Auto }, new() { Height = GridLength.Star } } };
        layout.Add(header); layout.Add(new ScrollView { Content = rows }, 0, 1); Content = layout;
        var refresh = new ToolbarItem { Text = "Refresh" };
        refresh.Clicked += async (_, _) => await RunAsync(RefreshAsync);
        ToolbarItems.Add(refresh);
        SelectSection(false);
        timer = Dispatcher.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(6);
        timer.Tick += async (_, _) => await RunAsync(RefreshAsync);
    }
    // Hide any previously displayed account summary until a fresh current-session read completes.
    protected override async void OnAppearing()
    {
        base.OnAppearing(); reads.Activate(); accountSummary.IsVisible = false;
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
    // Remove all old-account display rows while retaining the existing warning about earlier issued commands.
    private void ChangeSession()
    {
        reads.Cancel();
        previousOutcomesRequireReconciliation |= cancellations.Count != 0 || closes.Count != 0;
        cancellations.Clear(); cancellationOutcomes.Clear(); closes.Clear(); closeOutcomes.Clear();
        sessionVersion = AppServices.Api.SessionVersion;
        rows.Clear(); positionRows.Clear(); orderRows.Clear(); accountSummary.IsVisible = false;
        positionsTab.Text = "Positions"; ordersTab.Text = "Orders";
        status.Text = "Connection changed. Sign in to read the current account.";
        if (previousOutcomesRequireReconciliation) rows.Add(Text("Earlier commands may have reached the previous account. Reconcile their journal before issuing another exit or cancellation. They are not replayed in this session."));
    }
    private void RequireCurrent(LatestMarketRead.Read read)
    {
        if (!reads.IsCurrent(read)) throw new OperationCanceledException(read.Token);
    }
    // Serialize the existing account commands and preserve recovery controls after a failed read.
    private async Task RunAsync(Func<LatestMarketRead.Read, Task> action)
    {
        if (busy || !reads.Active) return;
        busy = true; var read = reads.Begin(); rows.IsEnabled = false; accountSummary.IsVisible = false;
        try { await action(read); }
        catch (OperationCanceledException) when (read.Token.IsCancellationRequested || !reads.IsCurrent(read)) { }
        catch (Exception exception) {
            if (reads.IsCurrent(read)) {
                positionsTab.Text = "Positions"; ordersTab.Text = "Orders";
                positionRows.Clear(); orderRows.Clear(); accountSummary.IsVisible = false;
                status.Text = exception.Message + (cancellations.Count + closes.Count > 0 ? " Issued commands remain unchanged; check or retry the same command." : "");
                rows.Clear(); AddRecoveryControls(); rows.Add(Action("Refresh current account", RefreshAsync));
            }
        } finally {
            busy = false;
            if (reads.IsCurrent(read)) rows.IsEnabled = true;
            else if (reads.Active) Dispatcher.Dispatch(async () => await RunAsync(RefreshAsync));
        }
    }
    // Route command actions through the existing serialized read and session guard.
    private Button Action(string text, Func<LatestMarketRead.Read, Task> action)
    {
        var button = new Button { Text = text, MinimumHeightRequest = 44, Style = (Style)Application.Current!.Resources["QuietButton"] };
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
    // Read the same bound account and journal once, reconcile retained commands, then render compact local sections.
    private async Task RefreshAsync(LatestMarketRead.Read read)
    {
        status.Text = "Reading paper account…"; accountSummary.IsVisible = false;
        var account = await AppServices.Api.Workflow.PortfolioAsync(read.Token);
        var journal = await AppServices.Api.Workflow.OrderJournalAsync(read.Token); RequireCurrent(read);
        // Journal terminal states release local retained controls; a POST acknowledgment alone is not terminal.
        foreach (var id in cancellations.Keys.ToArray())
            if (journal.Any(x => x.OrderIntentId == id && NativeExitReview.IsTerminal(x.State))) { cancellations.Remove(id); cancellationOutcomes.Remove(id); }
        foreach (var (symbol, outcome) in closeOutcomes.ToArray())
            if (!String.IsNullOrWhiteSpace(outcome.ClientOrderId) && journal.Any(x => x.ClientOrderId == outcome.ClientOrderId && NativeExitReview.IsTerminal(x.State))) { closes.Remove(symbol); closeOutcomes.Remove(symbol); }
        rows.Clear();
        var fresh = account.Available && DateTimeOffset.UtcNow - account.ReadAtUtc is { TotalSeconds: >= 0 and < 30 };
        accountSummary.IsVisible = fresh && (account.Cash.HasValue || account.Invested.HasValue);
        cashValue.Text = fresh ? Money(account.Cash) : String.Empty;
        investedValue.Text = fresh ? Money(account.Invested) : String.Empty;
        status.Text = fresh ? $"Paper account · read {account.ReadAtUtc.LocalDateTime:dd MMM HH:mm:ss}" : "Paper account · holdings unavailable or stale";
        positionsTab.Text = "Positions" + (account.Available ? $" ({account.Positions.Count})" : "");
        ordersTab.Text = $"Orders ({journal.Length})";
        AddRecoveryControls();
        rows.Add(ActionBar(
            Action("Review candidate", _ => Shell.Current.GoToAsync(nameof(CandidatesPage))),
            Action("Account details", current => { RequireCurrent(current); return DisplayAlertAsync("Account evidence", account.Detail + "\nRead UTC " + account.ReadAtUtc.ToString("O"), "Close"); })));
        RenderPositions(account, fresh);
        RenderOrders(journal);
        rows.Add(positionRows); rows.Add(orderRows);
    }

    // Switch already-rendered sections without fetching or changing any order command.
    private void SelectSection(bool orders)
    {
        positionRows.IsVisible = !orders; orderRows.IsVisible = orders;
        positionsTab.Style = (Style)Application.Current!.Resources[orders ? "QuietButton" : "ActionButton"];
        ordersTab.Style = (Style)Application.Current.Resources[orders ? "ActionButton" : "QuietButton"];
    }

    // Show position values and reviewed exits; cost and account evidence remain available in Details.
    private void RenderPositions(PortfolioResponse account, bool fresh)
    {
        positionRows.Clear();
        if (!account.Available) positionRows.Add(Text("Holdings could not be read. This does not mean the account is empty."));
        else if (account.Positions.Count == 0) positionRows.Add(Text("No open positions in this paper account."));
        foreach (var position in account.Positions)
        {
            var card = Card($"{position.Symbol} · {position.Quantity:0.####} shares",
                $"Value {Money(position.MarketValue)} · Open P/L {Money(position.UnrealizedPl)}", out var body);
            var actions = new List<Button> { Action("Details", current => {
                RequireCurrent(current);
                return DisplayAlertAsync(position.Symbol + " holding",
                    $"Quantity {position.Quantity:0.####}\nAverage entry {Money(position.AverageEntry)}\nCost {Money(position.CostBasis)}\nMarket value {Money(position.MarketValue)}\nUnrealized P/L {Money(position.UnrealizedPl)}", "Close");
            }) };
            if (position.Quantity > 0m && position.Quantity != Decimal.Truncate(position.Quantity))
                body.Add(Text("Fractional shares remain when exiting a whole-share portion. No quantity is rounded."));
            if (closes.ContainsKey(position.Symbol)) body.Add(Text("Resolve the issued exit above before reviewing another exit."));
            else if (fresh && position.Quantity >= 1m) actions.Add(Action("Review exit", current => CloseAsync(position, current)));
            body.Add(ActionBar(actions.ToArray()));
            positionRows.Add(card);
        }
    }

    // Keep order state and quantity visible while opening technical evidence only when requested.
    private void RenderOrders(OrderJournalEntry[] journal)
    {
        orderRows.Clear();
        orderRows.Add(Text("Most recent 200 journal entries"));
        if (journal.Length == 0) orderRows.Add(Text("No journaled orders for this account."));
        foreach (var order in journal)
        {
            var card = Card($"{order.Symbol} · {order.Side} {order.Quantity:0.####}",
                $"{order.State} · filled {order.FilledQuantity?.ToString("0.####") ?? "unknown"}", out var body);
            var actions = new List<Button> { Action("Details", current => {
                RequireCurrent(current);
                return DisplayAlertAsync(order.Symbol + " order",
                    $"{order.OrderType} / {order.TimeInForce}\nLimit {Money(order.LimitPrice)}\nFilled {order.FilledQuantity?.ToString("0.####") ?? "unknown"} at {Money(order.FillPrice)}\nUpdated UTC {order.UpdatedAtUtc:O}\nClient order {order.ClientOrderId}\nBroker order {order.BrokerOrderId ?? "unavailable"}", "Close");
            }) };
            if (order.CandidateId is { } candidateId)
                actions.Add(Action("Evidence", _ => Shell.Current.GoToAsync(nameof(CandidatesPage) + "?runId=" + order.RunId + "&candidateId=" + candidateId)));
            if (order.OrderIntentId is { } id && order.AccountId is not null && order.BrokerOrderId is not null &&
                order.State.ToUpperInvariant() is "INTENT" or "SUBMITTED" or "ACKED" or "PARTIALLYFILLED" or "PARTIALLY_FILLED" && !cancellations.ContainsKey(id))
                actions.Add(Action("Cancel…", current => CancelAsync(order, current)));
            body.Add(ActionBar(actions.ToArray()));
            orderRows.Add(card);
        }
    }

    // Keep short card actions on one touch-friendly row rather than stacking full-width buttons.
    private static Grid ActionBar(params Button[] actions)
    {
        var row = new Grid { ColumnSpacing = 8 };
        foreach (var action in actions)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
            row.Add(action, row.ColumnDefinitions.Count - 1);
        }
        return row;
    }

    // Reuse the native card style for short summaries and their guarded actions.
    private static Border Card(string title, string summary, out VerticalStackLayout body)
    {
        body = new VerticalStackLayout { Spacing = 8 };
        body.Add(new Label { Text = title, FontSize = 16, FontAttributes = FontAttributes.Bold });
        body.Add(Text(summary));
        return new Border { Style = (Style)Application.Current!.Resources["CardBorder"], Content = body };
    }

    // US paper-account amounts remain USD; missing inputs are explicit rather than formatted as zero.
    private static string Money(decimal? value) => value?.ToString("C2", CultureInfo.GetCultureInfo("en-US")) ?? "unavailable";

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
