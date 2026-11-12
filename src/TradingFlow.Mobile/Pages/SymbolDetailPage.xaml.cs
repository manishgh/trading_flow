using TradingFlow.Mobile.Services;
using TradingFlow.Contracts.V1;
using TradingFlow.Contracts.Transport;
using System.Text.Json;

namespace TradingFlow.Mobile.Pages;

public partial class SymbolDetailPage : ContentPage, IQueryAttributable
{
    private Guid wishlistId;
    private string ticker = String.Empty;
    private bool isLoading;
    private bool openingTicket;
    private readonly LatestMarketRead intelligenceReads = new(() => AppServices.Api.SessionVersion);
    private decimal? latestAskPrice;
    private IReadOnlyList<StockStrategyChoice> strategyChoices = [];
    private CancellationTokenSource? strategyRequest;
    private long strategyGeneration;
    private bool loadingStrategyChoices;
    private bool pageVisible;
    private Guid? currentAssessmentJob;
    private long lastSeenSessionVersion;
    private readonly Dictionary<string, StockStrategyAssessmentSubmission> strategySubmissions = new(StringComparer.Ordinal);

    /// <summary>Initializes controls and records the session whose page-owned assessment state may be reused.</summary>
    public SymbolDetailPage()
    {
        InitializeComponent();
        lastSeenSessionVersion = AppServices.Api.SessionVersion;
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

    /// <summary>Clears assessments from any session change while hidden before loading this session's observations and strategy choices.</summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        pageVisible = true; intelligenceReads.Activate();
        AppServices.Api.SessionChanged += OnStrategySessionChanged;
        if (lastSeenSessionVersion != AppServices.Api.SessionVersion)
            OnStrategySessionChanged(AppServices.Api, EventArgs.Empty);
        await LoadAsync();
        if (pageVisible) await LoadStrategiesAsync();
    }

    /// <summary>Stops page-owned reads while hidden; the next appearance compares the saved session version before reusing any state.</summary>
    protected override void OnDisappearing()
    {
        pageVisible = false; intelligenceReads.Deactivate();
        AppServices.Api.SessionChanged -= OnStrategySessionChanged;
        CancelStrategyRequest();
        base.OnDisappearing();
    }

    // Fence quote and model observations to the visible stock and current account before enabling review.
    private async Task LoadAsync()
    {
        if (isLoading || !intelligenceReads.Active || String.IsNullOrWhiteSpace(ticker)) return;
        isLoading = true; var read = intelligenceReads.Begin(); var symbol = ticker; var group = wishlistId;
        BusyIndicator.IsVisible = BusyIndicator.IsRunning = true; ErrorLabel.IsVisible = false;
        latestAskPrice = null; ReviewOrderButton.IsEnabled = false;
        try
        {
            var response = await AppServices.Api.GetSymbolIntelligenceAsync(group, symbol, read.Token)
                ?? throw new InvalidOperationException("No stock response returned.");
            if (!intelligenceReads.IsCurrent(read) || ticker != symbol || wishlistId != group) return;
            Render(response);
        }
        catch (OperationCanceledException) when (read.Token.IsCancellationRequested || !intelligenceReads.IsCurrent(read)) { }
        catch (Exception exception)
        {
            if (!intelligenceReads.IsCurrent(read)) return;
            QuotePriceLabel.Text = "Unavailable"; QuoteLabel.Text = QuoteAgeLabel.Text = PositionLabel.Text = String.Empty;
            ModelStatusLabel.Text = "Evidence unavailable"; ModelUnavailableLabel.IsVisible = false;
            ModelGeneratedLabel.Text = ModelIdentityLabel.Text = String.Empty; SwingSection.IsVisible = ReadinessSection.IsVisible = false;
            ErrorLabel.Text = $"Stock unavailable: {exception.Message}"; ErrorLabel.IsVisible = true;
        }
        finally
        {
            isLoading = false;
            if (intelligenceReads.IsCurrent(read)) BusyIndicator.IsRunning = BusyIndicator.IsVisible = false;
            else if (intelligenceReads.Active && pageVisible && read.Session == AppServices.Api.SessionVersion)
                Dispatcher.Dispatch(async () => await LoadAsync());
        }
    }

    // US stock prices use an explicit USD formatter rather than the device currency.
    private static string Usd(decimal? value) => value is > 0m ? value.Value.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US")) : "unavailable";

    // Reveal recorded strategy evidence without rerunning checks or submitting an order.
    private void OnStrategyEvidenceClicked(object? sender, EventArgs e) => StrategyEvidencePanel.IsVisible = !StrategyEvidencePanel.IsVisible;

    // Keep optional prediction provenance available without filling the stock screen with model metadata.
    private void OnModelEvidenceClicked(object? sender, EventArgs e) => ModelEvidencePanel.IsVisible = !ModelEvidencePanel.IsVisible;

    /// <summary>Displays quote, holding and optional model facts; strategy conditions are read separately from the shared decision evidence.</summary>
    private void Render(MobileSymbolIntelligenceResponse response)
    {
        UpdatedLabel.Text = $"Updated {response.FetchedAtUtc.LocalDateTime:dd/MM HH:mm:ss}";
        var decision = response.TradingFlowDecision;
        DecisionStatusLabel.Text = String.Empty;
        DecisionReasonLabel.Text = String.Empty;
        QuotePriceLabel.Text = decision.MidPrice is > 0m ? decision.MidPrice.Value.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US")) : "Unavailable";
        QuoteLabel.Text = $"Bid {Usd(decision.BidPrice)} · Ask {Usd(decision.AskPrice)}";
        QuoteAgeLabel.Text = decision.QuoteTimestamp is { } stamp ? $"Quote {stamp.LocalDateTime:dd MMM yyyy HH:mm:ss}" : "Quote time unavailable";
        PositionLabel.Text = decision.HasOpenTrade && decision.Trade is not null
            ? $"Open position | {decision.Trade.Quantity:0.####} shares | {decision.Trade.PlText}"
            : "No open tracked position";
        latestAskPrice = decision.AskPrice;
        ReviewOrderButton.IsEnabled = latestAskPrice is > 0m;

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

        ReadinessSection.IsVisible = model.ReadinessReasons.Count > 0;
        ReadinessReasonsLabel.Text = String.Join(Environment.NewLine, model.ReadinessReasons.Select(reason => $"- {TradingFlow.Contracts.Evidence.PredictorEvidenceDisplay.FormatReason(reason)}"));
    }

    /// <summary>Refreshes quote/model observations and the selected recorded strategy evidence.</summary>
    private async void OnRefreshClicked(object? sender, EventArgs e)
    {
        await LoadAsync();
        await LoadStrategiesAsync(refreshEvaluation: true);
    }

    /// <summary>Invalidates page-owned strategy requests and increments the response generation.</summary>
    private void CancelStrategyRequest()
    {
        Interlocked.Increment(ref strategyGeneration);
        strategyRequest?.Cancel();
        strategyRequest?.Dispose();
        strategyRequest = null;
        currentAssessmentJob = null;
        CancelStrategyButton.IsVisible = false;
    }

    /// <summary>Requests worker cancellation without letting a page/session transition publish an unobserved exception.</summary>
    private static async Task CancelCurrentJobAsync(Guid job)
    {
        try { await AppServices.Api.Workflow.CancelStockStrategyAssessmentAsync(job); }
        catch (Exception) { /* A disconnected page cannot confirm cancellation; the durable server status remains authoritative. */ }
    }

    /// <summary>Cancels this page's observational job; it cannot cancel or submit a broker order.</summary>
    private async void OnCancelStrategyClicked(object? sender, EventArgs args)
    {
        if (currentAssessmentJob is not { } job) return;
        CancelStrategyButton.IsEnabled = false;
        await CancelCurrentJobAsync(job);
        CancelStrategyButton.IsEnabled = true;
    }

    /// <summary>Fences old responses and discards owner-scoped request keys/results when authentication or the backend changes.</summary>
    private void OnStrategySessionChanged(object? sender, EventArgs args)
    {
        CancelStrategyRequest(); intelligenceReads.Cancel(); latestAskPrice = null; ReviewOrderButton.IsEnabled = false;
        QuotePriceLabel.Text = "Unavailable"; QuoteLabel.Text = QuoteAgeLabel.Text = PositionLabel.Text = String.Empty;
        ModelStatusLabel.Text = "Reopen the stock to read this connection."; ModelUnavailableLabel.IsVisible = false;
        ModelGeneratedLabel.Text = ModelIdentityLabel.Text = String.Empty; SwingSection.IsVisible = ReadinessSection.IsVisible = false;
        BusyIndicator.IsRunning = BusyIndicator.IsVisible = false;
        lastSeenSessionVersion = AppServices.Api.SessionVersion;
        foreach (var submission in strategySubmissions.Values) submission.ForgetConnectionResult();
        strategySubmissions.Clear();
        strategyChoices = [];
        loadingStrategyChoices = true;
        StrategyPicker.ItemsSource = null;
        loadingStrategyChoices = false;
        StrategyChecks.Clear();
        StrategyStatusLabel.Text = "Connection changed. Sign in and select a strategy again.";
        StrategyTimeLabel.Text = String.Empty;
    }

    /// <summary>Loads exact strategy/run choices without creating a candidate or reusing the former generic score.</summary>
    private async Task LoadStrategiesAsync(bool refreshEvaluation = false)
    {
        if (!pageVisible) return;
        var previous = SelectedStrategy();
        CancelStrategyRequest();
        strategyRequest = new();
        var token = strategyRequest.Token;
        var generation = strategyGeneration;
        var session = AppServices.Api.SessionVersion;
        try
        {
            var choices = await AppServices.Api.Workflow.StockStrategiesAsync(token);
            if (token.IsCancellationRequested || generation != strategyGeneration || session != AppServices.Api.SessionVersion) return;
            strategyChoices = choices;
            loadingStrategyChoices = true;
            StrategyPicker.ItemsSource = new[] { "Select a strategy" }.Concat(choices.Select(item =>
                item.Name + " · " + item.Strategy.SemanticVersion + (item.CandidateRunId is null ? " · current data" :
                " · recorded · " + item.Mode + " · " + item.RunStartedAtUtc?.ToString("O")))).ToArray();
            var previousIndex = previous is null ? -1 : Array.FindIndex(choices,
                item => item.Strategy == previous.Strategy && item.CandidateRunId == previous.CandidateRunId);
            StrategyPicker.SelectedIndex = previousIndex + 1;
            loadingStrategyChoices = false;
            await LoadStrategyAssessmentAsync(refreshEvaluation);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (generation != strategyGeneration || session != AppServices.Api.SessionVersion) return;
            StrategyChecks.Clear();
            StrategyStatusLabel.Text = "Strategy list unavailable: " + exception.Message;
            StrategyTimeLabel.Text = String.Empty;
        }
        finally { loadingStrategyChoices = false; }
    }

    /// <summary>Returns the explicit picker selection; no default strategy is inferred from a wishlist or model.</summary>
    private StockStrategyChoice? SelectedStrategy()
    {
        var index = StrategyPicker.SelectedIndex - 1;
        return index >= 0 && index < strategyChoices.Count ? strategyChoices[index] : null;
    }

    /// <summary>Reloads only the recorded checks when the operator changes the exact strategy/run.</summary>
    private async void OnStrategyChanged(object? sender, EventArgs args)
    {
        if (!loadingStrategyChoices) await LoadStrategyAssessmentAsync();
    }

    /// <summary>Uses the same assessment contract as Web and discards responses from old selections or sessions.</summary>
    private async Task LoadStrategyAssessmentAsync(bool refreshEvaluation = false)
    {
        var choice = SelectedStrategy();
        CancelStrategyRequest();
        StrategyChecks.Clear();
        StrategyTimeLabel.Text = String.Empty;
        if (choice is null) { StrategyStatusLabel.Text = "Select a strategy"; return; }
        strategyRequest = new();
        var token = strategyRequest.Token;
        var generation = strategyGeneration;
        var session = AppServices.Api.SessionVersion;
        var symbol = ticker;
        StrategyStatusLabel.Text = "Loading selected-strategy checks…";
        try
        {
            var response = choice.CandidateRunId is null
                ? await CollectStrategyAssessmentAsync(choice, symbol, generation, session, refreshEvaluation, token)
                : await AppServices.Api.Workflow.StockStrategyAssessmentsAsync(choice.CandidateRunId, choice.Strategy, [symbol], token);
            if (token.IsCancellationRequested || generation != strategyGeneration || session != AppServices.Api.SessionVersion || ticker != symbol) return;
            if (response is null) return;
            if (response.Strategy != choice.Strategy || choice.CandidateRunId is not null && response.CandidateRunId != choice.CandidateRunId ||
                response.Stocks.Count != 1 || response.Stocks[0].Symbol != symbol)
                throw new InvalidDataException("Strategy evaluation does not match the selected stock and strategy.");
            var assessment = response.Stocks.Single();
            StrategyStatusLabel.Text = choice.Name + " · " + assessment.Strategy.SemanticVersion + "\n" +
                (assessment.Expired ? "Evaluation expired. Refresh to collect new evidence.\n" : "") + assessment.Detail;
            StrategyTimeLabel.Text = assessment.Strategy.ContentSha256 +
                (assessment.CandidateRunId is { } run ? "\nRun " + run : "") +
                (assessment.EvaluatedAtUtc is { } evaluated ? "\nEvaluated " + evaluated.ToString("O") : "") +
                (assessment.LatestCompletedCandleAtUtc is { } completed ? "\nCompleted candle " + completed.ToString("O") : "");
            foreach (var check in assessment.Checks)
            {
                var title = check.GateName.StartsWith("strategy:", StringComparison.Ordinal) ? check.GateName[9..] : check.GateName;
                StrategyChecks.Add(new Label { Text = title.Replace('_', ' ') + ": " + (check.Passed ? "passed" : "failed"),
                    FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.WordWrap });
                if (check.RejectionCode is { } reason)
                    StrategyChecks.Add(new Label { Text = reason, LineBreakMode = LineBreakMode.WordWrap });
                var values = new Label { Text = JsonSerializer.Serialize(check.Inputs, new JsonSerializerOptions { WriteIndented = true }),
                    IsVisible = false, FontSize = 11, LineBreakMode = LineBreakMode.WordWrap };
                var inputs = new Button { Text = "Inputs used" };
                // Toggle original evidence only; this control does not recalculate any rule.
                inputs.Clicked += (_, _) => values.IsVisible = !values.IsVisible;
                StrategyChecks.Add(inputs);
                StrategyChecks.Add(values);
            }
            if (!assessment.Expired && assessment.ExpiresAtUtc is { } expiry)
                _ = MarkStrategyExpiredAsync(expiry, generation, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (generation != strategyGeneration || session != AppServices.Api.SessionVersion) return;
            StrategyStatusLabel.Text = "Strategy evaluation unavailable: " + exception.Message;
        }
    }

    /// <summary>Expires a displayed assessment at its server-provided deadline even if the operator leaves the page idle.</summary>
    private async Task MarkStrategyExpiredAsync(DateTimeOffset expiry, long generation, CancellationToken token)
    {
        try
        {
            var delay = expiry - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            if (!token.IsCancellationRequested && pageVisible && generation == strategyGeneration)
                StrategyStatusLabel.Text = "Evaluation expired. Refresh to collect new strategy evidence.\n" + StrategyStatusLabel.Text;
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Runs one bounded current observation and displays durable progress until terminal; selection/session changes stop polling.</summary>
    private async Task<StockStrategyAssessmentResponse?> CollectStrategyAssessmentAsync(StockStrategyChoice choice,
        string symbol, long generation, long session, bool refreshEvaluation, CancellationToken token)
    {
        if (wishlistId == Guid.Empty) throw new InvalidOperationException("Open this stock from an existing wishlist to collect strategy evidence.");
        var source = "wishlist:" + wishlistId;
        var selection = JsonSerializer.Serialize(new { AppServices.Api.BaseUrl, SessionVersion = session, source, choice.Strategy, symbol });
        if (!strategySubmissions.TryGetValue(selection, out var submission) || refreshEvaluation && submission.IsTerminal)
        {
            submission = new(choice.Strategy, source, [symbol]);
            strategySubmissions[selection] = submission;
        }
        // If the server accepted a prior POST but its response was lost, ResolveAsync retries that exact request/key.
        var job = await submission.ResolveAsync(AppServices.Api.Workflow, token);
        if (generation != strategyGeneration || session != AppServices.Api.SessionVersion)
        {
            return null;
        }
        currentAssessmentJob = job.JobId;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (generation != strategyGeneration || session != AppServices.Api.SessionVersion) return null;
                if (job.Strategy != choice.Strategy) throw new InvalidDataException("The strategy job does not match the selected version.");
                StrategyStatusLabel.Text = choice.Name + " · " + job.Status + "\n" + job.Detail;
                StrategyTimeLabel.Text = choice.Strategy.ContentSha256 + "\nProfile " + job.ProfileSha256 + "\nJob " + job.JobId;
                CancelStrategyButton.IsVisible = job.Status is "queued" or "running";
                if (job.Status == "completed") return job.Result
                    ?? throw new InvalidDataException("The completed strategy job has no result.");
                if (job.Status is not ("queued" or "running")) return null;
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                job = await submission.ResolveAsync(AppServices.Api.Workflow, token);
            }
        }
        finally
        {
            if (generation == strategyGeneration) { currentAssessmentJob = null; CancelStrategyButton.IsVisible = false; }
        }
    }

    /// <summary>Opens the existing candidate workflow; this link does not start an evaluation or submit an order.</summary>
    private async void OnCandidateReviewClicked(object? sender, EventArgs args)
    {
        var run = SelectedStrategy()?.CandidateRunId;
        await Shell.Current.GoToAsync(nameof(CandidatesPage) + (run is { } id ? "?runId=" + id : "?wishlistId=" + wishlistId));
    }

    // Serialize ticket opening and keep context/navigation failures local to the same visible stock and session.
    private async void OnReviewOrderClicked(object? sender, EventArgs e)
    {
        if (openingTicket || isLoading || latestAskPrice is not > 0m) return;
        var session = AppServices.Api.SessionVersion;
        var ask = latestAskPrice.Value;
        var symbol = ticker;
        var group = wishlistId;
        openingTicket = true;
        ReviewOrderButton.IsEnabled = false;
        try
        {
            var context = await AppServices.Api.GetOrderContextAsync();
            if (!pageVisible || session != AppServices.Api.SessionVersion || ticker != symbol || wishlistId != group) return;
            if (context?.OperatorOverrideAllowed != true || group == Guid.Empty)
            {
                await Shell.Current.GoToAsync(nameof(CandidatesPage) + "?wishlistId=" + group);
                return;
            }
            await Shell.Current.GoToAsync(
                $"{nameof(OrderTicketPage)}?wishlistId={group}&ticker={Uri.EscapeDataString(symbol)}&limitPrice={ask.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
        catch (Exception exception)
        {
            if (pageVisible && session == AppServices.Api.SessionVersion && ticker == symbol && wishlistId == group)
            {
                ErrorLabel.Text = $"Unable to open buy review: {exception.Message}";
                ErrorLabel.IsVisible = true;
            }
        }
        finally
        {
            openingTicket = false;
            if (pageVisible && session == AppServices.Api.SessionVersion && ticker == symbol && wishlistId == group)
                ReviewOrderButton.IsEnabled = !isLoading && latestAskPrice is > 0m;
        }
    }
}
