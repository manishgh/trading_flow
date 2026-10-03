using System.Globalization;
using TradingFlow.Contracts.V1;
using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Pages;

/// <summary>Account-wide positions and the durable order journal, independent of running jobs.</summary>
public sealed class OrdersPage : ContentPage
{
    private readonly VerticalStackLayout rows = new() { Padding = 16, Spacing = 12 };
    private readonly Label status = new() { Text = "Reading the bound paper account…", LineBreakMode = LineBreakMode.WordWrap };
    private readonly Dictionary<Guid, CancelOrderRequest> cancellations = [];
    private readonly Dictionary<string, ClosePositionRequest> closes = [];
    private CancellationTokenSource? lifetime;
    private readonly IDispatcherTimer timer;
    private bool busy;
    private long sessionVersion = -1;
    private string? projection;

    public OrdersPage()
    {
        Title = "Trading";
        Content = new ScrollView { Content = rows };
        rows.Add(status);
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
        base.OnAppearing();
        if (sessionVersion != AppServices.Api.SessionVersion) { SessionChanged(null, EventArgs.Empty); sessionVersion = AppServices.Api.SessionVersion; }
        lifetime?.Dispose(); lifetime = new();
        AppServices.Api.SessionChanged += SessionChanged;
        timer.Start(); await RunAsync(RefreshAsync);
    }
    protected override void OnDisappearing()
    {
        timer.Stop(); lifetime?.Cancel(); AppServices.Api.SessionChanged -= SessionChanged;
        base.OnDisappearing();
    }
    private void SessionChanged(object? sender, EventArgs e)
    {
        lifetime?.Cancel(); projection = null; cancellations.Clear(); closes.Clear(); timer.Stop();
        rows.Clear(); rows.Add(status); status.Text = "Connection changed. Sign in and reopen Trading.";
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (busy) return; busy = true;
        var token = lifetime?.Token ?? new CancellationToken(true);
        rows.IsEnabled = false;
        try { await action(token); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!token.IsCancellationRequested) status.Text = exception.Message + " Retry retains the same reviewed command."; }
        finally { busy = false; rows.IsEnabled = !token.IsCancellationRequested; }
    }
    private Button Action(string text, Func<CancellationToken, Task> action)
    {
        var button = new Button { Text = text, MinimumHeightRequest = 44 };
        button.Clicked += async (_, _) => await RunAsync(action);
        return button;
    }
    private static Label Text(string value) => new() { Text = value, LineBreakMode = LineBreakMode.WordWrap };
    private async Task RefreshAsync(CancellationToken token)
    {
        var account = await AppServices.Api.Workflow.PortfolioAsync(token);
        var journal = await AppServices.Api.Workflow.OrderJournalAsync(token);
        token.ThrowIfCancellationRequested();
        var next = System.Text.Json.JsonSerializer.Serialize(new { account, journal, pendingCancel = cancellations.Keys, pendingExit = closes.Keys });
        if (next == projection) return;
        projection = next;
        rows.Clear(); rows.Add(status);
        var fresh = account.Available && DateTimeOffset.UtcNow - account.ReadAtUtc < TimeSpan.FromSeconds(30);
        status.Text = (fresh ? "Paper account" : "Holdings unavailable or stale") + " · " + account.Detail + "\nRead " + account.ReadAtUtc.ToString("O");
        rows.Add(Action("Review an authorized paper candidate", _ => Shell.Current.GoToAsync(nameof(CandidatesPage))));
        rows.Add(Action("Refresh positions and orders", RefreshAsync));
        rows.Add(Text("Account positions"));
        if (!account.Available) rows.Add(Text("Position evidence is unavailable; this does not mean no positions."));
        else if (account.Positions.Count == 0) rows.Add(Text("No open positions in the bound paper account."));
        foreach (var position in account.Positions)
        {
            rows.Add(Text($"{position.Symbol} · {position.Quantity:0.####} shares\nAverage {position.AverageEntry:C2} · cost {position.CostBasis:C2} · value {position.MarketValue:C2}\nUnrealized P/L {position.UnrealizedPl:C2}"));
            if (position.Quantity > 0m && position.Quantity != Decimal.Truncate(position.Quantity))
                rows.Add(Text("This account position contains fractional shares. Reviewed exits currently require whole shares; no quantity is rounded."));
            if (fresh && position.Quantity > 0m && position.Quantity == Decimal.Truncate(position.Quantity))
                rows.Add(Action(closes.ContainsKey(position.Symbol) ? "Retry the reviewed exit" : "Review exit", t => CloseAsync(position, t)));
        }
        rows.Add(Text("Recent orders · most recent 200"));
        if (journal.Length == 0) rows.Add(Text("No journaled orders for the bound account."));
        foreach (var order in journal)
        {
            rows.Add(Text($"{order.Symbol} · {order.Side} {order.Quantity:0.####} · {order.State}\n{order.OrderType}/{order.TimeInForce} · limit {order.LimitPrice:C2} · filled {order.FilledQuantity:0.####}\nUpdated {order.UpdatedAtUtc:O}"));
            if (order.CandidateId is { } candidate)
                rows.Add(Action("Candidate audit and news", _ => Shell.Current.GoToAsync(nameof(CandidatesPage) + "?runId=" + order.RunId + "&candidateId=" + candidate)));
            if (order.OrderIntentId is { } id && order.AccountId is not null && order.BrokerOrderId is not null &&
                order.State is "Intent" or "Submitted" or "Acked" or "PartiallyFilled")
                rows.Add(Action(cancellations.ContainsKey(id) ? "Retry the same cancellation" : "Cancel working order", t => CancelAsync(order, t)));
        }
    }
    private async Task CancelAsync(OrderJournalEntry order, CancellationToken token)
    {
        var id = order.OrderIntentId!.Value;
        if (!cancellations.TryGetValue(id, out var request))
        {
            if (!await DisplayAlertAsync("Cancel working order", $"Cancel {order.Symbol}? It may fill before cancellation is acknowledged.", "Cancel order", "Keep order")) return;
            token.ThrowIfCancellationRequested();
            request = new(ContractVersions.VersionOne, order.AccountId!, id, "Operator reviewed Android cancellation", Guid.NewGuid().ToString("N"));
            cancellations[id] = request;
        }
        var result = await AppServices.Api.Workflow.CancelOrderAsync(request, token);
        token.ThrowIfCancellationRequested();
        if (result.State != "confirmation_pending") cancellations.Remove(id);
        status.Text = result.State + " · " + result.RejectionReason;
    }
    private async Task CloseAsync(PortfolioPositionResponse position, CancellationToken token)
    {
        if (!closes.TryGetValue(position.Symbol, out var request))
        {
            var context = await AppServices.Api.GetOrderContextAsync(token);
            if (String.IsNullOrWhiteSpace(context?.AccountId)) throw new InvalidOperationException("A bound broker account is required.");
            var input = await DisplayPromptAsync("Review limit exit", "Limit price for " + position.Quantity + " " + position.Symbol, "Review", "Back", keyboard: Keyboard.Numeric);
            if (input is null) return;
            if (!Decimal.TryParse(input, NumberStyles.Number, CultureInfo.CurrentCulture, out var limit) || limit <= 0m)
                throw new InvalidOperationException("Enter a positive limit price.");
            if (!await DisplayAlertAsync("Confirm paper exit", $"Sell {position.Quantity} {position.Symbol} at limit {limit:C2}, day, regular hours. Server rechecks position and risk.", "Confirm exit", "Back")) return;
            token.ThrowIfCancellationRequested();
            request = new(ContractVersions.VersionOne, context.AccountId, position.Symbol, position.Quantity,
                "Operator reviewed Android exit", false, limit, Guid.NewGuid().ToString("N"));
            closes[position.Symbol] = request;
        }
        var result = await AppServices.Api.Workflow.ClosePositionAsync(request, token);
        token.ThrowIfCancellationRequested();
        if (result.State != "confirmation_pending") closes.Remove(position.Symbol);
        status.Text = result.State + " · " + result.RejectionReason;
    }
}
