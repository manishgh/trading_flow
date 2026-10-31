using TradingFlow.Contracts.V1;
using System.Collections.ObjectModel;
using TradingFlow.Mobile.Services;
using TradingFlow.Mobile.Resources.Styles;
using System.Globalization;
using TradingFlow.Contracts.Presentation;

namespace TradingFlow.Mobile.Pages;

public partial class BacktestsPage : ContentPage
{
    private readonly TradingFlowApiClient api = AppServices.Api;
    private readonly ObservableCollection<BacktestJobSnapshot> jobs = new();
    private readonly IDispatcherTimer refreshTimer;
    private MobileCatalogResponse? catalog;
    private BacktestJobSnapshot? selectedJob;
    private StartBacktestRequest? pendingRequest;
    private readonly HashSet<string> selectedStrategyPaths = new(StringComparer.OrdinalIgnoreCase);
    private long formRevision;
    private long? previewedRevision;
    private bool isLoading;
    private CancellationTokenSource? pageLifetime;
    private bool suppressPersist;
    private bool refreshingForm;
    private bool updatingJobs;
    private bool formRestored;
    private bool planBusy;
    private readonly Dictionary<long, string> planStartKeys = new();
    private long lastSeenSessionVersion;

    /// <summary>Leaves run values unset until an explicit saved choice or the selected server profile supplies them.</summary>
    public BacktestsPage()
    {
        InitializeComponent();
        suppressPersist = true;
        JobsView.ItemsSource = jobs;
        CachePolicyPicker.ItemsSource = new[] { "reuse", "refresh" };
        CachePolicyPicker.SelectedIndex = -1;
        lastSeenSessionVersion = api.SessionVersion;
        suppressPersist = false;
        refreshTimer = Dispatcher.CreateTimer();
        refreshTimer.Interval = TimeSpan.FromSeconds(8);
        refreshTimer.Tick += async (_, _) => await LoadAsync(showBusy: false);
    }

    /// <summary>Invalidates old-account state missed while hidden, then starts a new cancellable page refresh.</summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (lastSeenSessionVersion != api.SessionVersion) OnSessionChanged(this, EventArgs.Empty);
        pageLifetime?.Cancel(); pageLifetime?.Dispose(); pageLifetime = new();
        planBusy = false; RunButton.IsEnabled = true;
        api.SessionChanged += OnSessionChanged;
        refreshTimer.Start();
        await LoadAsync();
    }

    /// <summary>Cancels page work and detaches active-session notifications; appearance checks for missed changes.</summary>
    protected override void OnDisappearing()
    {
        refreshTimer.Stop(); pageLifetime?.Cancel();
        api.SessionChanged -= OnSessionChanged;
        base.OnDisappearing();
    }

    /// <summary>Clears backend-bound choices and retry keys when the signed-in connection changes.</summary>
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        lastSeenSessionVersion = api.SessionVersion;
        pageLifetime?.Cancel(); refreshTimer.Stop(); catalog = null; pendingRequest = null;
        EvidencePicker.ItemsSource = null; EvidenceChoiceLabel.Text = String.Empty;
        previewedRevision = null; SavePlanButton.IsEnabled = false;
        selectedStrategyPaths.Clear();
        jobs.Clear(); selectedJob = null; RenderSelectedJob();
        SavedPlanPicker.ItemsSource = null; StartPlanButton.IsEnabled = false; PlanPreviewLabel.Text = String.Empty; planStartKeys.Clear();
        StatusLabel.Text = "Connection changed. Reopen this page after signing in.";
    }

    /// <summary>Refreshes catalog, saved plans and job state using this page's cancellation lifetime.</summary>
    private async Task LoadAsync(bool showBusy = true)
    {
        if (isLoading)
        {
            return;
        }

        var token = pageLifetime?.Token ?? new CancellationToken(true);
        if (token.IsCancellationRequested) return;
        isLoading = true;
        BusyIndicator.IsVisible = showBusy;
        BusyIndicator.IsRunning = showBusy;
        try
        {
            var healthy = await api.CheckHealthAsync(token);
            token.ThrowIfCancellationRequested();
            StatusLabel.Text = healthy
                ? $"Connected: {api.BaseUrl}"
                : $"Backend unreachable: {api.BaseUrl}";
            StatusLabel.TextColor = healthy ? ThemePalette.Positive : ThemePalette.Negative;

            if (showBusy || catalog is null) {
            refreshingForm = true;
            var previousConfig = (ConfigPicker.SelectedItem as MobileRunConfigOption)?.Path;
            var previousEvidence = (EvidencePicker.SelectedItem as EvidenceChoiceItem)?.Choice?.Reference;
            var loadedCatalog = await api.GetCatalogAsync(token);
            token.ThrowIfCancellationRequested();
            catalog = loadedCatalog;
            ConfigPicker.ItemsSource = catalog?.BacktestConfigs.ToList();
            var previousReplay = (ExecutionReplayPicker.SelectedItem as BacktestExecutionReplayChoice)?.PolicyId
                ?? Preferences.Get("BacktestExecutionReplayPolicyId", String.Empty);
            ExecutionReplayPicker.ItemsSource = (catalog?.ExecutionReplayChoices ?? []).ToList();
            ExecutionReplayPicker.SelectedItem = catalog?.ExecutionReplayChoices?.FirstOrDefault(choice => choice.PolicyId == previousReplay)
                ?? catalog?.ExecutionReplayChoices?.FirstOrDefault();
            ConfigPicker.SelectedIndex = ConfigPicker.SelectedIndex < 0 && ConfigPicker.Items.Count > 0 ? 0 : ConfigPicker.SelectedIndex;

            var previousSourceKey = (StockSourcePicker.SelectedItem as BacktestStockSource)?.Key;
            var loadedSources = await api.Workflow.BacktestSourcesAsync(token);
            token.ThrowIfCancellationRequested();
            suppressPersist = true;
            StockSourcePicker.ItemsSource = loadedSources.ToList();
            StockSourcePicker.SelectedItem = loadedSources.FirstOrDefault(source => source.Key == previousSourceKey)
                ?? loadedSources.FirstOrDefault(source => source.Key == Preferences.Get("BacktestStockSourceKey", String.Empty))
                ?? loadedSources.FirstOrDefault();
            suppressPersist = false;

            RestoreFormState();

            if (previousConfig is not null) SelectConfigByPath(previousConfig);
            RenderStrategyChoices();
            RenderEvidenceChoices(previousEvidence);
            ApplyProfileSettings(false);
            var selectedPlanId = (SavedPlanPicker.SelectedItem as BacktestPlanResponse)?.PlanId;
            var savedPlans = await api.Workflow.BacktestPlansAsync(token);
            token.ThrowIfCancellationRequested();
            SavedPlanPicker.ItemsSource = savedPlans;
            SavedPlanPicker.SelectedItem = savedPlans.FirstOrDefault(plan => plan.PlanId == selectedPlanId);
            refreshingForm = false;
            }
            var latestJobs = await api.GetBacktestJobsAsync(token) ?? Array.Empty<BacktestJobSnapshot>();
            token.ThrowIfCancellationRequested();
            updatingJobs = true;
            jobs.Clear();
            foreach (var job in latestJobs.Take(20))
            {
                jobs.Add(job);
            }

            JobsView.SelectedItem = jobs.FirstOrDefault(item => item.JobId == selectedJob?.JobId);
            updatingJobs = false;
            if (selectedJob is { } selection) await RefreshSelectedAsync(selection.JobId, token);
            token.ThrowIfCancellationRequested();
            RenderSelectedJob();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            StatusLabel.Text = $"Error: {exception.Message}";
            StatusLabel.TextColor = ThemePalette.Negative;
            await DisplayAlertAsync("Backtests", exception.Message, "OK");
        }
        finally
        {
            refreshingForm = false; updatingJobs = false;
            isLoading = false;
            BusyIndicator.IsRunning = false;
            BusyIndicator.IsVisible = false;
            RefreshRoot.IsRefreshing = false;
        }
    }

    private async void OnRefresh(object? sender, EventArgs e) => await LoadAsync();

    private async void OnRefreshClicked(object? sender, EventArgs e) => await LoadAsync();

    /// <summary>Previews stocks and independent portfolios without starting work.</summary>
    private async void OnStartBacktest(object? sender, EventArgs e) => await PreviewOrSaveAsync(false);

    /// <summary>Saves the exact previewed choices without creating jobs.</summary>
    private async void OnSaveBacktestPlan(object? sender, EventArgs e) => await PreviewOrSaveAsync(true);

    /// <summary>Submits per-plan exclusions and groups through Web's shared API, fencing edited or departed-page responses.</summary>
    private async Task PreviewOrSaveAsync(bool save)
    {
        if (planBusy) return;
        if (save && previewedRevision != formRevision) return;
        if (!save) { previewedRevision = null; SavePlanButton.IsEnabled = false; }
        var lifetime = pageLifetime;
        var token = lifetime?.Token ?? new CancellationToken(true);
        var revision = formRevision;
        if (token.IsCancellationRequested) return;
        if (ConfigPicker.SelectedItem is not MobileRunConfigOption config)
        {
            await DisplayAlertAsync("Backtests", "Select a backtest profile.", "OK");
            return;
        }

        if (StockSourcePicker.SelectedItem is not BacktestStockSource source)
        {
            await DisplayAlertAsync("Backtests", "Select a wishlist or saved Finviz list.", "OK");
            return;
        }

        var strategies = SelectStrategies();
        if (strategies.Length == 0)
        {
            await DisplayAlertAsync("Backtests", "Select at least one strategy.", "OK");
            return;
        }

        try
        {
            planBusy = true;
            RunButton.IsEnabled = false;
            SavePlanButton.IsEnabled = false;
            if (pendingRequest is null)
            {
                var end = DateTimeOffset.UtcNow;
                var window = ResolveTestWindow(end);
                var capital = RequireDecimal(CapitalEntry.Text, "Starting capital");
                var risk = RequireDecimal(RiskEntry.Text, "Account risk budget");
                var notional = RequireDecimal(MaxPositionEntry.Text, "Maximum position notional");
                var positions = RequireInt(MaxConcurrentEntry.Text, "Maximum open positions");
                var evidence = SelectedEvidence();
                var cache = CachePolicyPicker.SelectedItem?.ToString() == "refresh" ? "refresh" : "use_cache";
                var horizons = strategies.Select(item => item.Horizon).Distinct(StringComparer.Ordinal).ToArray();
                if (horizons.Length != 1)
                    throw new InvalidOperationException("Use separate saved plans for day and swing strategies.");
                var preview = await api.Workflow.PreviewAsync(new(ContractVersions.VersionOne, horizons[0], null,
                    [new(source.Kind, source.Key, source.WishlistId, source.Query)]), token);
                if (!CanApplyPlanResponse(lifetime, revision)) return;
                pendingRequest = new(ContractVersions.VersionOne, $"bt_{end:yyyyMMdd_HHmmss}", preview.UniverseSnapshotId,
                    strategies.Select(item => new StrategyReference(item.ArtifactStrategyId, item.SemanticVersion, item.ContentSha256)).ToArray(),
                    window.Start, window.End, capital, risk, notional, positions,
                    cache, "full", Guid.NewGuid().ToString("N"), config.FileName,
                    DecisionContextEvidence: evidence,
                    ExecutionReplayPolicyId: (ExecutionReplayPicker.SelectedItem as BacktestExecutionReplayChoice)?.PolicyId is { Length: > 0 } policy ? policy : null);
            }
            var manual = ParseManualCohorts(ManualCohortsEditor.Text);
            var planRequest = new SaveBacktestPlanRequest(pendingRequest with { ConfigurationSha256 = save ? pendingRequest.ConfigurationSha256 : null },
                manual.Count == 0 ? RequireInt(GroupSizeEntry.Text, "Stocks per group") : null,
                manual.Count == 0 ? null : manual,
                (ExcludedSymbolsEntry.Text ?? String.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            if (manual.Count > 0 && !String.IsNullOrWhiteSpace(GroupSizeEntry.Text))
                throw new InvalidOperationException("Leave stocks per group blank when entering your own groups.");
            var plan = save ? await api.Workflow.SaveBacktestPlanAsync(planRequest, token)
                : await api.Workflow.PreviewBacktestPlanAsync(planRequest, token);
            if (!CanApplyPlanResponse(lifetime, revision)) return;
            pendingRequest = plan.Run;
            ShowPlan(plan);
            if (!save) previewedRevision = revision;
            if (save)
            {
                var plans = await api.Workflow.BacktestPlansAsync(token);
                if (!CanApplyPlanResponse(lifetime, revision)) return;
                SavedPlanPicker.ItemsSource = plans;
                SavedPlanPicker.SelectedItem = plans.FirstOrDefault(item => item.PlanId == plan.PlanId);
                StatusLabel.Text = "Plan saved. No jobs have started.";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (CanApplyPlanResponse(lifetime, revision)) await DisplayAlertAsync("Backtests", exception.Message, "OK");
        }
        finally
        {
            if (ReferenceEquals(lifetime, pageLifetime))
            {
                planBusy = false;
                RunButton.IsEnabled = true;
                SavePlanButton.IsEnabled = previewedRevision == formRevision;
            }
        }
    }

    /// <summary>Rejects edited/departed-page replies and an old connection even before its UI event is delivered.</summary>
    private bool CanApplyPlanResponse(CancellationTokenSource? lifetime, long revision) =>
        lifetime is not null && ReferenceEquals(lifetime, pageLifetime) && !lifetime.IsCancellationRequested &&
        revision == formRevision && lastSeenSessionVersion == api.SessionVersion;

    private void OnToggleAdvanced(object? sender, EventArgs e)
    {
        AdvancedPanel.IsVisible = !AdvancedPanel.IsVisible;
        AdvancedToggle.Text = AdvancedPanel.IsVisible ? "Hide advanced parameters" : "Show advanced parameters";
    }

    /// <summary>Persists the explicitly selected source and invalidates the next plan without changing any source list.</summary>
    private void OnStockSourcePicked(object? sender, EventArgs e)
    {
        if (StockSourcePicker.SelectedItem is not BacktestStockSource)
        {
            return;
        }

        SaveFormState();
    }

    /// <summary>Invalidates only the next request when an operator edits its form.</summary>
    private void OnFormChanged(object? sender, EventArgs e) => SaveFormState();

    /// <summary>Restores explicit user choices; missing values stay blank until the selected server profile supplies them.</summary>
    private void RestoreFormState()
    {
        if (formRestored)
        {
            return;
        }

        suppressPersist = true;
        try
        {
            LookbackEntry.Text = Preferences.Get("BacktestLookback", String.Empty);
            CapitalEntry.Text = Preferences.Get("BacktestCapital", String.Empty);
            RiskEntry.Text = Preferences.Get("BacktestRisk", String.Empty);
            MaxPositionEntry.Text = Preferences.Get("BacktestMaxPosition", String.Empty);
            MaxConcurrentEntry.Text = Preferences.Get("BacktestMaxConcurrent", String.Empty);
            TestStartEntry.Text = Preferences.Get("BacktestTestStartUtc", String.Empty);
            TestEndEntry.Text = Preferences.Get("BacktestTestEndUtc", String.Empty);
            SelectByValue(CachePolicyPicker, Preferences.Get("BacktestCachePolicy", String.Empty));
            SelectConfigByPath(Preferences.Get("BacktestConfigPath", string.Empty));
            selectedStrategyPaths.UnionWith(Preferences.Get("BacktestStrategyPaths", String.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        finally
        {
            suppressPersist = false;
            formRestored = true;
        }
    }

    /// <summary>Saves form edits without starting jobs or inventing portfolio defaults.</summary>
    private void SaveFormState()
    {
        if (suppressPersist || refreshingForm)
        {
            return;
        }
        formRevision++;
        previewedRevision = null;
        SavePlanButton.IsEnabled = false;
        pendingRequest = null;
        SavedPlanPicker.SelectedItem = null;
        StartPlanButton.IsEnabled = false;
        PlanPreviewLabel.Text = String.Empty;

        Preferences.Set("BacktestLookback", LookbackEntry.Text ?? String.Empty);
        Preferences.Set("BacktestCapital", CapitalEntry.Text ?? String.Empty);
        Preferences.Set("BacktestRisk", RiskEntry.Text ?? String.Empty);
        Preferences.Set("BacktestMaxPosition", MaxPositionEntry.Text ?? String.Empty);
        Preferences.Set("BacktestMaxConcurrent", MaxConcurrentEntry.Text ?? String.Empty);
        Preferences.Set("BacktestTestStartUtc", TestStartEntry.Text ?? String.Empty);
        Preferences.Set("BacktestTestEndUtc", TestEndEntry.Text ?? String.Empty);
        Preferences.Set("BacktestCachePolicy", CachePolicyPicker.SelectedItem?.ToString() ?? String.Empty);
        Preferences.Set("BacktestExecutionReplayPolicyId", (ExecutionReplayPicker.SelectedItem as BacktestExecutionReplayChoice)?.PolicyId ?? String.Empty);
        if (StockSourcePicker.SelectedItem is BacktestStockSource source)
        {
            Preferences.Set("BacktestStockSourceKey", source.Key);
        }

        if (ConfigPicker.SelectedItem is MobileRunConfigOption config)
        {
            Preferences.Set("BacktestConfigPath", config.Path);
        }

        Preferences.Set("BacktestStrategyPaths", String.Join('\n', selectedStrategyPaths.Order(StringComparer.OrdinalIgnoreCase)));
    }

    private void SelectConfigByPath(string path)
    {
        if (String.IsNullOrWhiteSpace(path) || ConfigPicker.ItemsSource is not IEnumerable<MobileRunConfigOption> configs)
        {
            return;
        }

        var match = configs.FirstOrDefault(config => config.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            ConfigPicker.SelectedItem = match;
        }
    }

    /// <summary>Offers each exact catalog strategy independently, matching Web's multi-select comparison.</summary>
    private void RenderStrategyChoices()
    {
        StrategyChoices.Children.Clear();
        if (catalog is null) return;
        selectedStrategyPaths.IntersectWith(catalog.Strategies.Select(strategy => strategy.Path));
        foreach (var strategy in catalog.Strategies)
        {
            var check = new CheckBox { IsChecked = selectedStrategyPaths.Contains(strategy.Path) };
            // A changed checkbox edits the next plan; it never modifies a previously saved plan.
            check.CheckedChanged += (_, changed) => {
                if (changed.Value) selectedStrategyPaths.Add(strategy.Path);
                else selectedStrategyPaths.Remove(strategy.Path);
                SaveFormState();
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitionCollection {
                new() { Width = GridLength.Auto }, new() { Width = GridLength.Star } }, ColumnSpacing = 8 };
            row.Add(check);
            row.Add(new Label { Text = StrategyDescription(strategy),
                FontSize = 12, VerticalTextAlignment = TextAlignment.Center }, 1);
            StrategyChoices.Children.Add(row);
        }
    }

    /// <summary>Displays exact recipe lineage and native admission policy without interpreting trading rules.</summary>
    private static string StrategyDescription(MobileStrategyOption strategy)
    {
        var text = $"{strategy.StrategyName} {strategy.SemanticVersion} · {strategy.Lifecycle}\n{strategy.Horizon} · {strategy.Timeframe} / {strategy.ExecutionTimeframe}\nSHA-256 {strategy.ContentSha256}";
        if (strategy.DerivedFromIdentity is { } parent)
            text += $"\nParent: {parent.StrategyId} {parent.SemanticVersion} · {parent.ContentSha256}";
        if (strategy.AdmissionProfile is { } profile)
            text += $"\nAdmission: {profile.ProfileId} {profile.ProfileVersion}\nExecution: {profile.ExecutionTimingPolicy}";
        return text;
    }

    /// <summary>Refreshes approved choices, preserving only an exact ID/hash from the same active connection.</summary>
    private void RenderEvidenceChoices(DecisionContextEvidenceReference? previous)
    {
        var choices = new[] { new EvidenceChoiceItem(null) }.Concat(
            (catalog?.EvidenceChoices ?? []).Select(choice => new EvidenceChoiceItem(choice))).ToList();
        EvidencePicker.ItemsSource = choices;
        var retained = choices.FirstOrDefault(item => item.Choice is { Available: true } choice &&
            choice.Reference == previous && previous is not null);
        EvidencePicker.SelectedItem = retained ?? choices[0];
        if (previous is not null && retained is null) {
            pendingRequest = null; formRevision++; previewedRevision = null; SavePlanButton.IsEnabled = false;
        }
        ShowEvidenceChoice();
    }

    /// <summary>Invalidates the next plan on selection edits while leaving saved plans immutable.</summary>
    private void OnEvidenceChanged(object? sender, EventArgs e)
    {
        ShowEvidenceChoice();
        SaveFormState();
    }

    /// <summary>Shows unavailable reasons and frozen summary fields without interpreting their economic meaning.</summary>
    private void ShowEvidenceChoice()
    {
        var choice = (EvidencePicker.SelectedItem as EvidenceChoiceItem)?.Choice;
        var reason = choice?.UnavailableReason ?? String.Join("\n", (catalog?.EvidenceChoices ?? [])
            .Where(item => !item.Available).Select(item => $"{item.Name}: {item.UnavailableReason}"));
        EvidenceChoiceLabel.Text = String.Join("\n", new[] { reason }
            .Concat(DecisionContextEvidencePresentation.Lines(choice?.Summary)).Where(line => !String.IsNullOrWhiteSpace(line)));
        if (String.IsNullOrWhiteSpace(EvidenceChoiceLabel.Text) && (catalog?.EvidenceChoices?.Count ?? 0) == 0)
            EvidenceChoiceLabel.Text = "No approved decision-context evidence is available.";
    }

    /// <summary>Rejects unavailable choices before sending only their immutable server reference.</summary>
    private DecisionContextEvidenceReference? SelectedEvidence()
    {
        var choice = (EvidencePicker.SelectedItem as EvidenceChoiceItem)?.Choice;
        if (choice is null) return null;
        if (!choice.Available || choice.Reference is null)
            throw new InvalidOperationException(choice.UnavailableReason ?? "This evidence choice is unavailable. Refresh the choices.");
        return choice.Reference;
    }

    private sealed record EvidenceChoiceItem(DecisionContextEvidenceChoice? Choice)
    {
        /// <summary>Keeps unavailable evidence visible with the server's descriptive reason.</summary>
        public override string ToString() => Choice is null ? "No evidence selected" : Choice.Name +
            (Choice.Available ? String.Empty : $" · Unavailable: {Choice.UnavailableReason}");
    }

    /// <summary>Restores an explicit stored picker value only when it is still available.</summary>
    private static void SelectByValue(Picker picker, string value)
    {
        if (picker.ItemsSource is not IEnumerable<string> options)
        {
            return;
        }

        var index = options.ToList().FindIndex(option => option.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            picker.SelectedIndex = index;
        }
    }

    /// <summary>Parses an explicit UTC range without applying the phone's timezone; blank dates use the chosen lookback.</summary>
    private (DateTimeOffset Start, DateTimeOffset End) ResolveTestWindow(DateTimeOffset now)
    {
        if (String.IsNullOrWhiteSpace(TestStartEntry.Text) && String.IsNullOrWhiteSpace(TestEndEntry.Text))
            return (now.AddDays(-RequireInt(LookbackEntry.Text, "Lookback days")), now);
        if (!DateTimeOffset.TryParseExact(TestStartEntry.Text?.Trim(), "yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start) ||
            !DateTimeOffset.TryParseExact(TestEndEntry.Text?.Trim(), "yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var end))
            throw new InvalidOperationException("Enter both test dates as YYYY-MM-DDTHH:MMZ in UTC.");
        if (end <= start || end > now)
            throw new InvalidOperationException("To UTC must be after From UTC and cannot be in the future.");
        return (start, end);
    }

    /// <summary>Returns only the individually selected exact strategies, allowing any comparison subset.</summary>
    private MobileStrategyOption[] SelectStrategies() => catalog?.Strategies.Where(strategy => selectedStrategyPaths.Contains(strategy.Path)).ToArray() ?? [];

    private async void OnJobSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (updatingJobs) return;
        selectedJob = e.CurrentSelection.FirstOrDefault() as BacktestJobSnapshot;
        if (selectedJob is { } selection) { try { await RefreshSelectedAsync(selection.JobId, pageLifetime?.Token ?? new CancellationToken(true)); } catch (OperationCanceledException) { } catch (HttpRequestException) { } }
        RenderSelectedJob();
    }

    private async Task RefreshSelectedAsync(Guid id, CancellationToken token)
    {
        var detail = await api.GetBacktestJobAsync(id, token);
        if (!token.IsCancellationRequested && selectedJob?.JobId == id) selectedJob = detail ?? selectedJob;
    }
    /// <summary>Displays server monthly/terminal facts, preparation failures, committed candles and frozen context evidence.</summary>
    private void RenderSelectedJob()
    {
        DetailCard.IsVisible = selectedJob is not null;
        EventsStack.Clear();
        StrategyGroupsStack.Clear();
        PreparedDataLabel.Text = String.Empty;
        PreparedDataLabel.IsVisible = false;
        EvidenceDetailLabel.Text = String.Empty;
        EvidenceDetailLabel.IsVisible = false;
        if (selectedJob is null)
        {
            return;
        }

        DetailTitle.Text = selectedJob.Cohort is { } cohort
            ? $"{cohort.CohortName} · {cohort.Strategy.StrategyId} {cohort.Strategy.SemanticVersion}"
            : selectedJob.RunName;
        DetailStatus.Text = $"{selectedJob.Status} - {selectedJob.ProgressText}";
        if (selectedJob.Cohort is { } execution)
            DetailStatus.Text += $"\nSaved plan {execution.PlanId} · Starting capital USD {execution.StartingCapital.ToString("0.00", CultureInfo.InvariantCulture)}";
        if (!String.IsNullOrWhiteSpace(selectedJob.ErrorMessage))
            DetailStatus.Text += "\n" + selectedJob.ErrorMessage;
        if (selectedJob.PreparedMarketData is { } prepared)
        {
            PreparedDataLabel.Text = "Prepared candles\nSnapshot ID: " + prepared.SnapshotId +
                "\nSHA-256: " + prepared.SnapshotSha256 +
                "\nProvider: " + String.Join(", ", prepared.Providers) + " · Feed: " + String.Join(", ", prepared.DataFeeds) +
                "\nPrice basis: " + String.Join(", ", prepared.PriceAdjustments) +
                "\nRequested coverage (UTC): " + prepared.StartUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) +
                " to " + prepared.EndUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) +
                "\nPrepared at (UTC): " + prepared.PreparedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) +
                $"\nQueries reused: {prepared.ReusedQueryCount} · downloaded: {prepared.DownloadedQueryCount}\n" +
                prepared.HistoricalEvidenceNote;
            PreparedDataLabel.IsVisible = true;
        }
        ResultLabel.Text = selectedJob.ResultSummary;
        EvidenceDetailLabel.Text = String.Join("\n", DecisionContextEvidencePresentation.Lines(selectedJob.EvidenceSummary));
        EvidenceDetailLabel.IsVisible = selectedJob.EvidenceSummary is not null;
        if (!String.IsNullOrWhiteSpace(selectedJob.MonthlyUnavailableReason))
            ResultLabel.Text += "\n" + selectedJob.MonthlyUnavailableReason;
        RenderPortfolioState(selectedJob.PortfolioState);
        RenderZoneDecisions(selectedJob.ZoneDecisions);
        if (selectedJob.Status == "completed") RenderZoneEvaluationControls(selectedJob.JobId);
        foreach (var history in selectedJob.HistoryEligibility)
        {
            var historyLabel = history.Status switch
            {
                "history_eligible" => "Enough history for this strategy",
                "still_warming_at_study_end" => "Not enough history by test end",
                _ => history.Status.Replace('_', ' ')
            };
            StrategyGroupsStack.Add(new Label { Text = $"{history.Ticker} · {history.StrategyId} · {historyLabel}" +
                $"\nCompleted bars: start {history.AvailableAtStart}, cutoff {history.AvailableAtCutoff} / {history.RequiredBars} required" +
                $"\nFirst eligible decision UTC: {history.FirstEligibleDecisionUtc?.ToString("u") ?? "not reached"}", FontSize = 12 });
        }
        foreach (var month in selectedJob.MonthlyResults)
        {
            var performance = month.ReturnPct is { } value ? $"{value:0.00}%" : $"unavailable: {month.UnavailableReason}";
            StrategyGroupsStack.Add(new Label { Text = $"{month.Strategy.StrategyId} {month.Strategy.SemanticVersion} · {month.CohortName} · {month.Month}" +
                $"{(month.PartialMonth ? " (partial month)" : "")}\n{performance} · {ValuationLabel(month.ValuationBasis)}" +
                $"\nOpening equity: {month.OpeningEquity?.ToString("C2") ?? "Unavailable"} · Closing equity: {month.ClosingEquity?.ToString("C2") ?? "Unavailable"}" +
                $"\nDrawdown: {month.MaxDrawdownPct?.ToString("0.00") ?? "Unavailable"}% · Closed trades: {month.ClosedTrades?.ToString() ?? "Unavailable"}" +
                $"\nMonthly realized net: {month.RealizedNetProfit?.ToString("C2") ?? "Unavailable"} · Unrealized at month end: {month.UnrealizedProfit?.ToString("C2") ?? "Unavailable"}" +
                $"\nMonthly fees: {month.Fees?.ToString("C2") ?? "Unavailable"} · Gross exposure at close: {month.GrossExposurePct?.ToString("0.00") ?? "Unavailable"}%" +
                $"\nOpen positions: {month.OpenPositions?.ToString() ?? "Unavailable"} · Entries filled: {month.FilledCandidates?.ToString() ?? "Unavailable"} · Fills: {month.FillCount?.ToString() ?? "Unavailable"}" +
                $"\nStrategy hash: {month.Strategy.ContentSha256}", FontSize = 12 });
        }

        foreach (var group in selectedJob.StrategyGroups ?? Array.Empty<BacktestStrategyRunGroup>())
        {
            StrategyGroupsStack.Add(new Label
            {
                Text = $"{group.StrategyName}: {group.Status} {group.CompletedTickerCount}/{group.TotalTickerCount}",
                FontSize = 12,
                TextColor = ThemePalette.TextPrimary
            });
        }

        foreach (var item in selectedJob.Events.Reverse().Take(10))
        {
            EventsStack.Add(new Label
            {
                Text = item,
                FontSize = 12,
                TextColor = ThemePalette.TextSecondary
            });
        }
    }

    private async void OnCancelSelectedJob(object? sender, EventArgs e)
    {
        if (selectedJob is null)
        {
            return;
        }

        await api.CancelBacktestJobAsync(selectedJob.JobId);
        await LoadAsync();
    }

    /// <summary>Displays shared server evidence for zone hypotheses without adding mobile entry or execution rules.</summary>
    private void RenderZoneDecisions(IReadOnlyList<RunZoneDecisionDetail>? decisions)
    {
        if (decisions is null || decisions.Count == 0) return;
        StrategyGroupsStack.Add(new Label { Text = "Zone entry and SIP execution evidence � latest hypotheses; complete journals retain every event.", FontSize = 12 });
        foreach (var decision in decisions)
            StrategyGroupsStack.Add(new Label { Text = String.Join("\n", IntradayZoneDecisionPresentation.Lines(decision)),
                FontSize = 12, LineBreakMode = LineBreakMode.WordWrap });
    }

    /// <summary>Formats server-calculated terminal holdings and working actions without client accounting.</summary>
    // Fetch fifty source rows per page; rejected candles remain accessible without a candidate.
    private void RenderZoneEvaluationControls(Guid jobId)
    {
        var rows = new VerticalStackLayout { Spacing = 6 };
        var load = new Button { Text = "Show evaluated zone candles" };
        var previous = new Button { Text = "Previous candles", IsVisible = false };
        long offset = 0;
        async Task ShowPage(long requestedOffset)
        {
            load.IsEnabled = previous.IsEnabled = false;
            try
            {
                var page = await api.Workflow.ZoneEvaluationsAsync(jobId, requestedOffset, 50);
                rows.Clear();
                rows.Add(new Label { Text = $"{page.TotalCount} retained candle evaluations - offset {page.Offset}" +
                    (page.UnavailableReason is null ? "" : "\n" + page.UnavailableReason), FontSize = 12 });
                foreach (var row in page.Rows)
                    rows.Add(new Label { Text = String.Join("\n", ZoneEvaluationPresentation.Lines(row)), FontSize = 12,
                        LineBreakMode = LineBreakMode.WordWrap });
                offset = page.Offset;
                previous.IsVisible = offset > 0;
                load.IsVisible = page.NextOffset is not null;
                load.Text = "Next candles";
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException)
            { rows.Clear(); rows.Add(new Label { Text = exception.Message, FontSize = 12 }); }
            finally { load.IsEnabled = previous.IsEnabled = true; }
        }
        load.Clicked += async (_, _) => await ShowPage(load.Text == "Next candles" ? offset + 50 : 0);
        previous.Clicked += async (_, _) => await ShowPage(Math.Max(0, offset - 50));
        StrategyGroupsStack.Add(rows);
        StrategyGroupsStack.Add(previous);
        StrategyGroupsStack.Add(load);
    }
    /// <summary>Formats server-calculated terminal holdings and working actions without client accounting.</summary>
    private void RenderPortfolioState(RunPortfolioStateResponse? state)
    {
        if (state is null) return;
        StrategyGroupsStack.Add(new Label { Text = $"Portfolio at test end · cutoff UTC {state.CutoffUtc:u}\n{ValuationLabel(state.ValuationBasis)}" +
            $"\nAdmitted orders: {state.AdmittedOrderCount} · Entries filled: {state.FilledCandidateCount} · Fills: {state.FillCount}" +
            $"\nClosed trades: {state.ClosedTradeCount} · Open positions: {state.OpenPositions.Count}" +
            $"\nCash: {state.Cash?.ToString("C2") ?? "Unavailable"} · Position value: {state.PositionValue?.ToString("C2") ?? "Unavailable"}" +
            $"\nEquity: {state.Equity?.ToString("C2") ?? "Unavailable"} · Gross exposure: {state.GrossExposurePct?.ToString("0.00") ?? "Unavailable"}%" +
            $"\nRealized net: {state.RealizedNetProfit?.ToString("C2") ?? "Unavailable"} · Unrealized: {state.UnrealizedProfit?.ToString("C2") ?? "Unavailable"}" +
            $"\nActual fees: {state.Fees?.ToString("C2") ?? "Unavailable"}\n{state.UnavailableReason}", FontSize = 12 });
        foreach (var position in state.OpenPositions)
            StrategyGroupsStack.Add(new Label { Text = $"Open at test end: {position.Ticker} · signed quantity {position.SignedQuantity}" +
                $"\nEntry basis {position.EntryBasis:C2} · Mark {position.MarkPrice?.ToString("C2") ?? "Unavailable"} · mark timestamp UTC {position.MarkTimestampUtc?.ToString("u") ?? "Unavailable"}" +
                $"\nUnrealized {position.UnrealizedProfit?.ToString("C2") ?? "Unavailable"}", FontSize = 12 });
        foreach (var order in state.PendingOrders)
            StrategyGroupsStack.Add(new Label { Text = $"Waiting for execution: {order.Ticker} · {order.Side} {order.OrderType} · {order.RemainingQuantity} remaining · {order.Status}" +
                $"\nReserved {order.ReservedNotional:C2} · known UTC {order.KnownAtUtc:u}", FontSize = 12 });
        foreach (var exit in state.PendingExits)
            StrategyGroupsStack.Add(new Label { Text = $"Exit awaiting execution: {exit.Ticker} · {exit.Reason} · known UTC {exit.KnownAtUtc:u}", FontSize = 12 });
        if (state.IntradayDecisionCount > 0)
            StrategyGroupsStack.Add(new Label { Text = $"Day entry and protection evidence · latest {state.IntradayDecisions?.Count ?? 0} of {state.IntradayDecisionCount} hypotheses. Complete journals retain all events; planned prices do not establish fills.", FontSize = 12 });
        foreach (var decision in state.IntradayDecisions ?? [])
            StrategyGroupsStack.Add(new Label { Text = String.Join("\n", IntradayDecisionPresentation.Lines(decision)),
                FontSize = 12, LineBreakMode = LineBreakMode.WordWrap });
    }

    /// <summary>Names the server's valuation basis in ordinary words while the API retains its exact evidence identifier.</summary>
    private static string ValuationLabel(string basis) => basis.StartsWith("marked_to_market_", StringComparison.Ordinal)
        ? "Daily close equity, including open positions"
        : basis == "realized_closed_trade_equity" ? "Realized closed-trade equity" : "Valuation unavailable";

    /// <summary>Shows a saved plan's exact membership and strategy identities before starting.</summary>
    private void OnSavedPlanSelected(object? sender, EventArgs e)
    {
        previewedRevision = null; SavePlanButton.IsEnabled = false;
        StartPlanButton.IsEnabled = SavedPlanPicker.SelectedItem is BacktestPlanResponse;
        if (SavedPlanPicker.SelectedItem is BacktestPlanResponse plan) ShowPlan(plan);
    }

    /// <summary>Starts an already saved plan; transport retries reuse the same command identity.</summary>
    private async void OnStartSavedPlan(object? sender, EventArgs e)
    {
        if (planBusy || SavedPlanPicker.SelectedItem is not BacktestPlanResponse plan) return;
        var lifetime = pageLifetime;
        var token = lifetime?.Token ?? new CancellationToken(true);
        var revision = formRevision;
        if (token.IsCancellationRequested) return;
        planBusy = true; StartPlanButton.IsEnabled = false;
        try
        {
            if (!planStartKeys.TryGetValue(plan.PlanId, out var key))
                planStartKeys.Add(plan.PlanId, key = Guid.NewGuid().ToString("N"));
            var result = await api.Workflow.StartBacktestPlanAsync(plan.PlanId, new(ContractVersions.VersionOne, key),
                token);
            if (!CanApplyPlanResponse(lifetime, revision)) return;
            await LoadAsync();
            if (!CanApplyPlanResponse(lifetime, revision)) return;
            StatusLabel.Text = $"{result.Jobs.Count} independent strategy/group runs accepted.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (CanApplyPlanResponse(lifetime, revision)) await DisplayAlertAsync("Backtests", error.Message, "OK"); }
        finally
        {
            if (ReferenceEquals(lifetime, pageLifetime))
            { planBusy = false; StartPlanButton.IsEnabled = SavedPlanPicker.SelectedItem is BacktestPlanResponse; }
        }
    }

    /// <summary>Displays frozen membership, recipe lineage, resolved costs and evidence before save and after reopen.</summary>
    private void ShowPlan(BacktestPlanResponse plan)
    {
        PlanPreviewLabel.Text = $"{plan.Run.RunName}: {plan.ExecutionCount} independent portfolios.\n" +
            "Excluded stocks: " + (plan.ExcludedSymbols is { Count: > 0 } ? String.Join(", ", plan.ExcludedSymbols) : "None") + "\n" +
            String.Join("\n", plan.Cohorts.Select(group => $"{group.Name}: {String.Join(", ", group.Symbols)} · capital {group.StartingCapital:N2} per strategy")) +
            "\nStrategies: " + String.Join("; ", plan.Run.Strategies.Select(strategy => $"{strategy.StrategyId} {strategy.SemanticVersion} ({strategy.ContentSha256})")) +
            "\n" + plan.MembershipDescription;
        PlanPreviewLabel.Text += "\n" + String.Join("\n", BacktestSettingsPresentation.Lines(plan.ResolvedSettings));
        var evidence = DecisionContextEvidencePresentation.Lines(plan.EvidenceSummary);
        if (evidence.Count > 0) PlanPreviewLabel.Text += "\n" + String.Join("\n", evidence);
        else if (plan.Run.DecisionContextEvidence is { } reference)
            PlanPreviewLabel.Text += $"\nEvidence: {reference.Id} · Manifest SHA-256: {reference.ManifestSha256}. Summary unavailable.";
    }

    /// <summary>Reads operator rows into shared contracts; the server verifies source membership.</summary>
    private static IReadOnlyList<BacktestCohort> ParseManualCohorts(string? text)
    {
        return (text ?? String.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line =>
        {
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 3 || String.IsNullOrWhiteSpace(parts[0]))
                throw new InvalidOperationException("Enter each group as name | starting capital | comma-separated stocks.");
            return new BacktestCohort(parts[0], parts[2].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                RequireDecimal(parts[1], "Group capital"));
        }).ToArray();
    }

    /// <summary>Applies a chosen server profile rather than independent Android defaults.</summary>
    private void OnBacktestProfileChanged(object? sender, EventArgs e)
    {
        if (refreshingForm || suppressPersist) return;
        ApplyProfileSettings(true);
        OnFormChanged(sender, e);
    }

    /// <summary>Initializes blank values or explicitly replaces values when the operator changes profile.</summary>
    private void ApplyProfileSettings(bool replace)
    {
        if (ConfigPicker.SelectedItem is not MobileRunConfigOption { Settings: { } settings }) return;
        suppressPersist = true;
        try
        {
            if (replace || String.IsNullOrWhiteSpace(LookbackEntry.Text)) LookbackEntry.Text = settings.LookbackDays.ToString(CultureInfo.CurrentCulture);
            if (replace || String.IsNullOrWhiteSpace(CapitalEntry.Text)) CapitalEntry.Text = settings.StartingCapital.ToString(CultureInfo.CurrentCulture);
            if (replace || String.IsNullOrWhiteSpace(RiskEntry.Text)) RiskEntry.Text = settings.AccountRiskBudgetPct.ToString(CultureInfo.CurrentCulture);
            if (replace || String.IsNullOrWhiteSpace(MaxPositionEntry.Text)) MaxPositionEntry.Text = settings.MaxPositionNotionalPct.ToString(CultureInfo.CurrentCulture);
            if (replace || String.IsNullOrWhiteSpace(MaxConcurrentEntry.Text)) MaxConcurrentEntry.Text = settings.MaxConcurrentPositions.ToString(CultureInfo.CurrentCulture);
            if (replace || CachePolicyPicker.SelectedIndex < 0) CachePolicyPicker.SelectedIndex = settings.CachePolicy == "refresh" ? 1 : 0;
        }
        finally { suppressPersist = false; }
    }

    /// <summary>Rejects missing/invalid counts instead of silently selecting a different run.</summary>
    private static int RequireInt(string? value, string label) =>
        Int32.TryParse(value, out var parsed) && parsed > 0 ? parsed : throw new InvalidOperationException($"{label} must be a positive whole number.");

    /// <summary>Accepts a positive decimal in local or invariant notation, with no fallback value.</summary>
    private static decimal RequireDecimal(string? value, string label)
    {
        if ((Decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out var parsed) ||
             Decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out parsed)) && parsed > 0)
            return parsed;
        throw new InvalidOperationException($"{label} must be a positive number.");
    }
}
