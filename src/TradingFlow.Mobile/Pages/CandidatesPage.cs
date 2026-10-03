using TradingFlow.Contracts.Transport;
using TradingFlow.Contracts.V1;
using TradingFlow.Mobile.Services;

namespace TradingFlow.Mobile.Pages;

public sealed class CandidatesPage : ContentPage, IQueryAttributable
{
    private readonly Entry runEntry = new() { Placeholder = "Candidate run identifier" };
    private readonly Label status = new() { Text = "Open a candidate run or capture wishlist membership." };
    private readonly VerticalStackLayout cards = new() { Spacing = 10 };
    private readonly Dictionary<Guid, VerticalStackLayout> rows = new();
    private readonly CandidateStateStore state = new();
    private CancellationTokenSource? lifetime;
    private Guid? requestedCandidate;
    private string? requestedArticle;
    private bool appearing;
    private CreateCandidateRunRequest? pendingCapture;
    private bool capturing;

    public CandidatesPage()
    {
        Title = "Candidates";
        var open = new Button { Text = "Open run" };
        open.Clicked += async (_, _) => await SelectAsync();
        var capture = new Button { Text = "Capture wishlist candidates" };
        capture.Clicked += async (_, _) => await CaptureAsync();
        Content = new ScrollView { Content = new VerticalStackLayout {
            Padding = 16, Spacing = 12, Children = { runEntry, open, capture, status, cards }
        }};
    }
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("runId", out var run)) runEntry.Text = Uri.UnescapeDataString(run?.ToString() ?? "");
        if (query.TryGetValue("candidateId", out var id) && Guid.TryParse(id?.ToString(), out var parsed)) requestedCandidate = parsed;
        if (query.TryGetValue("articleId", out var article)) requestedArticle = Uri.UnescapeDataString(article?.ToString() ?? "");
        if (appearing) _ = SelectAsync();
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing(); appearing = true; lifetime ??= new();
        AppServices.Api.SessionChanged += OnSessionChanged;
        if (String.IsNullOrWhiteSpace(runEntry.Text)) runEntry.Text = Preferences.Get("CandidateRun:" + AppServices.Api.BaseUrl, "");
        if (!String.IsNullOrWhiteSpace(runEntry.Text)) await SelectAsync();
    }
    protected override void OnDisappearing()
    {
        appearing = false; Stop();
        AppServices.Api.SessionChanged -= OnSessionChanged;
        base.OnDisappearing();
    }
    private void Stop() { lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; }
    private void OnSessionChanged(object? sender, EventArgs e) { Stop(); pendingCapture = null; status.Text = "Connection changed. Sign in and reopen the run."; }
    private async Task SelectAsync()
    {
        Stop();
        if (!Guid.TryParse(runEntry.Text, out var id)) { status.Text = "Enter a valid candidate run identifier."; return; }
        lifetime = new(); var token = lifetime.Token; var generation = state.Select(id);
        var target = new CandidateLink(id, requestedCandidate, requestedArticle, AppServices.Api.BaseUrl);
        cards.Clear(); rows.Clear(); status.Text = "Loading durable candidate state…";
        try
        {
            await RefreshAsync(generation, token);
            Preferences.Set("CandidateRun:" + AppServices.Api.BaseUrl, id.ToString());
            if (target.CandidateId is { } candidate) { await ShowAuditAsync(candidate, token); requestedCandidate = null; requestedArticle = null; }
            _ = PollAsync(generation, token);
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        { status.Text = "Sign in to open this candidate."; CandidateNavigation.Pending = target; await Shell.Current.GoToAsync(nameof(LoginPage)); }
        catch (Exception exception) { status.Text = exception.Message; }
    }
    private async Task PollAsync(long generation, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), token); await RefreshAsync(generation, token); }
            catch (OperationCanceledException) { break; }
            catch (Exception exception) { if (!token.IsCancellationRequested) status.Text = "Reconnecting: " + exception.Message; }
        }
    }
    private async Task RefreshAsync(long generation, CancellationToken token)
    {
        var previous = state.Candidates.ToDictionary(item => item.CandidateId);
        CandidateRunResponse snapshot;
        try { snapshot = await AppServices.Api.Workflow.CandidatesAsync(state.RunId, token); }
        catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            if (!token.IsCancellationRequested) status.Text = "Candidate run is not available yet. Queued paper runs appear when execution starts.";
            return;
        }
        if (token.IsCancellationRequested || !state.Replace(generation, snapshot)) return;
        status.Text = snapshot.Status + " · " + snapshot.Candidates.Count + " candidates";
        foreach (var id in rows.Keys.Except(snapshot.Candidates.Select(item => item.CandidateId)).ToArray()) { cards.Remove(rows[id]); rows.Remove(id); }
        foreach (var candidate in snapshot.Candidates)
        {
            if (previous.TryGetValue(candidate.CandidateId, out var before) && before.State != candidate.State &&
                candidate.State.Equals("triggered", StringComparison.OrdinalIgnoreCase))
            {
                var audit = await AppServices.Api.Workflow.AuditAsync(candidate.CandidateId, token);
                token.ThrowIfCancellationRequested();
                LocalSignalNotificationService.ShowSignal(candidate.Symbol, candidate.Readiness,
                    "Open the exact candidate and its attached evidence.", state.RunId, candidate.CandidateId, audit.CatalystEvidence?.ProviderArticleId);
            }
            if (!rows.TryGetValue(candidate.CandidateId, out var card))
            {
                card = new VerticalStackLayout { Spacing = 5 };
                var title = new Label { FontAttributes = FontAttributes.Bold };
                var reasons = new Label { LineBreakMode = LineBreakMode.WordWrap };
                var audit = new Button { Text = "Audit and attached news" };
                audit.Clicked += async (_, _) => await ShowAuditSafelyAsync(candidate.CandidateId);
                var order = new Button { Text = "Review candidate order" };
                order.Clicked += async (_, _) => await Shell.Current.GoToAsync(nameof(OrderTicketPage) + "?runId=" + state.RunId + "&candidateId=" + candidate.CandidateId + "&ticker=" + Uri.EscapeDataString(candidate.Symbol));
                card.Children.Add(title); card.Children.Add(reasons); card.Children.Add(audit); card.Children.Add(order);
                rows[candidate.CandidateId] = card; cards.Add(card);
            }
            ((Label)card.Children[0]).Text = candidate.Symbol + " · " + candidate.Readiness.Replace('_', ' ');
            ((Label)card.Children[1]).Text = String.Join("\n", candidate.Rejections.Select(item => item.Message));
            ((Button)card.Children[3]).IsVisible = snapshot.Provenance.Mode is "paper" or "paper_shadow" or "paper_experiment" && candidate.State.Equals("triggered", StringComparison.OrdinalIgnoreCase);
        }
    }
    private async Task ShowAuditSafelyAsync(Guid id)
    {
        try { await ShowAuditAsync(id, lifetime?.Token ?? CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { status.Text = exception.Message; }
    }
    private async Task ShowAuditAsync(Guid id, CancellationToken token)
    {
        var audit = await AppServices.Api.Workflow.AuditAsync(id, token);
        token.ThrowIfCancellationRequested();
        var content = new VerticalStackLayout { Padding = 16, Spacing = 10 };
        content.Add(new Label { Text = audit.Candidate.Symbol + " · " + audit.Candidate.Readiness, FontAttributes = FontAttributes.Bold });
        content.Add(new Label { Text = audit.Candidate.Strategy.StrategyId + " · " + audit.Candidate.Strategy.SemanticVersion + "\n" + audit.Candidate.Strategy.ContentSha256 });
        content.Add(new Label { Text = String.Join("\n", audit.Transitions.Select(item => item.OccurredAtUtc.ToString("O") + " · " + item.NewState + " · " + item.ReasonCode)) });
        content.Add(new Label { Text = String.Join("\n", audit.GateEvaluations.Select(item => item.GateName + ": " + (item.Passed ? "passed" : item.RejectionCode))) });
        content.Add(new Label { Text = audit.MarketEvidence is null ? "Market inputs incomplete." : "Completed candle: " + audit.MarketEvidence.LatestCompletedCandleAtUtc?.ToString("O") });
        content.Add(new Label { Text = audit.ExecutionEvidence is null ? "No execution evidence." : audit.ExecutionEvidence.State + " · filled " + audit.ExecutionEvidence.FilledQuantity });
        var news = audit.CatalystEvidence;
        var matches = requestedArticle is null || news?.ProviderArticleId == requestedArticle;
        content.Add(new Label { Text = !matches ? "The notification's attached article is no longer available." : news?.Headline ?? "No attached headline." });
        if (matches && Uri.TryCreate(news?.Url, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
        {
            var open = new Button { Text = "Open attached article" };
            open.Clicked += async (_, _) => await NewsNavigation.OpenAsync(url.AbsoluteUri);
            content.Add(open);
        }
        requestedArticle = null;
        await Navigation.PushAsync(new ContentPage { Title = "Candidate audit", Content = new ScrollView { Content = content } });
    }
    private async Task CaptureAsync()
    {
        if (capturing) return; capturing = true;
        var token = lifetime?.Token ?? new CancellationToken(true);
        try
        {
            if (pendingCapture is null) {
            var lists = await AppServices.Api.GetWishlistsAsync(token) ?? [];
            var selected = await DisplayActionSheetAsync("Wishlist", "Cancel", null, lists.Select(item => item.Name + " · " + item.Id.ToString()[..8]).ToArray());
            token.ThrowIfCancellationRequested();
            var list = lists.FirstOrDefault(item => selected == item.Name + " · " + item.Id.ToString()[..8]); if (list is null) return;
            var catalog = await AppServices.Api.Workflow.CatalogAsync("backtest", token);
            var names = catalog.Strategies.Select(item => item.Name + " · " + item.Identity.SemanticVersion).ToArray();
            var choice = await DisplayActionSheetAsync("Research strategy", "Cancel", null, names);
            token.ThrowIfCancellationRequested();
            var index = Array.IndexOf(names, choice); if (index < 0) return;
            var preview = await AppServices.Api.Workflow.PreviewAsync(new(ContractVersions.VersionOne, "swing", null, [new("wishlist", list.Id.ToString(), list.Id)]), token);
            pendingCapture = new(ContractVersions.VersionOne, Guid.NewGuid().ToString("N"), "backtest",
                preview.UniverseSnapshotId, [catalog.Strategies[index].Identity], preview.DecisionCutoffUtc);
            }
            var run = await AppServices.Api.Workflow.StartCandidatesAsync(pendingCapture, token);
            token.ThrowIfCancellationRequested();
            pendingCapture = null;
            runEntry.Text = run.CandidateRunId.ToString(); await SelectAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { status.Text = exception.Message; }
        finally { capturing = false; }
    }
}
