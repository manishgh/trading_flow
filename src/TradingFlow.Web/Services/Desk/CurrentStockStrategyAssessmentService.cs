using TradingFlow.Application.Candidates;
using TradingFlow.Application.Jobs;
using TradingFlow.Application.Preparation;
using TradingFlow.Backtesting;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Web.Services.Desk;

/// <summary>Queues observational strategy decisions through existing durable jobs; it has no broker or order dependency.</summary>
public sealed class CurrentStockStrategyAssessmentService(IDurableJobRepository jobs, RunQueueLimits limits,
    ConfigCatalogService profiles, StrategyArtifactCatalog artifacts, IStrategyIdentityResolver identities,
    IPreparationAuthorization authorization, DeskSources sources, IUniverseDiscoveryService discovery,
    IDiscoveryRepository discoveries, ICandidateRepository candidates, IStockStrategyEvidenceLoader evidence,
    DeskStrategyAssessmentService recorded, TimeProvider clock) : IDurableJobHandler
{
    public const string Type = "strategy_assessment";
    public string JobType => Type;
    public DurableJobRecoveryMode RecoveryMode => DurableJobRecoveryMode.ReplayFromStart;
    private sealed record Work(string Owner, string Stamp, string RequestSha256,
        StartStockStrategyAssessmentRequest Request, BacktestRunConfig Profile, string ProfileSha256);

    /// <summary>Freezes exact inputs and the effective profile, enforcing the existing bounded queue before work starts.</summary>
    public async Task<StockStrategyAssessmentJob> StartAsync(string owner, StartStockStrategyAssessmentRequest request, CancellationToken token)
    {
        Validate(request);
        var normalized = request with { IdempotencyKey = request.IdempotencyKey.Trim(), Source = request.Source.Trim(),
            Symbols = request.Symbols.Select(item => item.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() };
        var requestHash = PreparationJson.Hash(normalized);
        var id = StrategyDecisionRequestAssembler.CreateDeterministicId(new { type = Type, owner, normalized.IdempotencyKey });
        var existing = await jobs.GetJobAsync(id, token);
        if (existing is not null)
        {
            var prior = RequireOwner(existing, owner);
            if (prior.RequestSha256 != requestHash) throw new InvalidOperationException("This request key already belongs to different strategy assessment inputs.");
            return View(existing, prior);
        }
        var stamp = await authorization.StampAsync(owner, token)
            ?? throw new UnauthorizedAccessException("Sign in again before requesting strategy checks.");
        await identities.RequireSwingStrategyAsync(normalized.Strategy, "diagnostic_replay", token);
        await ResolveSourcesAsync(normalized.Source, token);
        var profile = profiles.DefaultPaperConfig()?.Config
            ?? throw new InvalidOperationException("No default paper profile is available to supply the existing data-feed and warm-up settings.");
        if (!profile.Provider.Equals("alpaca", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Current strategy checks require the profile's Alpaca market-data provider, not historical CSV or screener prices.");
        var work = new Work(owner, stamp, requestHash, normalized, profile, PreparationJson.Hash(profile));
        var job = new PersistedJob { Id = id, JobType = Type, RunName = normalized.Strategy.StrategyId,
            RequestJson = PreparationJson.Write(work), CreatedAt = clock.GetUtcNow() };
        if (!await jobs.TryEnqueueAsync(job, limits.Paper, token))
        {
            existing = await jobs.GetJobAsync(id, token);
            if (existing is not null && RequireOwner(existing, owner).RequestSha256 == requestHash)
                return View(existing, work);
            throw new InvalidOperationException("The configured observation queue is full. Wait for an active job to finish or cancel it.");
        }
        return View(job, work);
    }

    /// <summary>Returns only this operator's observation, with expired evidence clearly identified at read time.</summary>
    public async Task<StockStrategyAssessmentJob> ReadAsync(string owner, Guid id, CancellationToken token)
    {
        var job = await jobs.GetJobAsync(id, token) ?? throw new KeyNotFoundException("Strategy assessment job was not found.");
        return View(job, RequireOwner(job, owner));
    }

    /// <summary>Cancels collection through the durable worker's existing cancellation mechanism.</summary>
    public async Task<StockStrategyAssessmentJob> CancelAsync(string owner, Guid id, CancellationToken token)
    {
        var job = await jobs.GetJobAsync(id, token) ?? throw new KeyNotFoundException("Strategy assessment job was not found.");
        RequireOwner(job, owner);
        await jobs.RequestCancellationAsync(id, clock.GetUtcNow(), token);
        return await ReadAsync(owner, id, token);
    }

    /// <summary>No second in-memory queue or projection needs initialization.</summary>
    public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
    /// <summary>A recovered lease repeats collection under a new attempt identity; old evidence remains immutable.</summary>
    public Task RecoverAsync(PersistedJob job, CancellationToken token) => Task.CompletedTask;
    /// <summary>The durable repository already owns terminal status publication.</summary>
    public Task OnTerminalPublishedAsync(PersistedJob job, CancellationToken token) => Task.CompletedTask;

    /// <summary>Prepares data, then captures genuine current membership before invoking the one shared decision engine.</summary>
    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken token)
    {
        var work = PreparationJson.Read<Work>(context.Job.RequestJson);
        Validate(work.Request);
        if (work.ProfileSha256 != PreparationJson.Hash(work.Profile) ||
            work.RequestSha256 != PreparationJson.Hash(work.Request))
            throw new InvalidDataException("The frozen strategy request or effective profile hash does not match its stored contents.");
        await RequireAuthorizationAsync(work, token);
        await identities.RequireSwingStrategyAsync(work.Request.Strategy, "diagnostic_replay", token);
        var artifact = artifacts.RequireExact(new(work.Request.Strategy.StrategyId,
            work.Request.Strategy.SemanticVersion, work.Request.Strategy.ContentSha256));
        var runtime = new AuthorizedRuntimeStrategy(artifact.Identity, StrategySelectionMode.DiagnosticReplay,
            artifact.ResolvedStrategy, artifact.AdmissionProfile);
        var batch = await evidence.LoadAsync(work.Profile, runtime, work.Request.Symbols, token);
        await RequireAuthorizationAsync(work, token);
        var selectedSources = await ResolveSourcesAsync(work.Request.Source, token);
        var preview = await discovery.PreviewAsync(new(ContractVersions.VersionOne, "swing", null, selectedSources), token);
        var cutoff = clock.GetUtcNow();
        var members = await discoveries.GetActiveAsync(preview.UniverseSnapshotId, cutoff, token);
        // Recovery can observe newer membership/data. A new attempt run cannot overwrite or rebind its earlier evidence.
        var runId = StrategyDecisionRequestAssembler.CreateDeterministicId(new { context.Job.Id, context.Job.AttemptCount, context.LeaseToken });
        var run = new ProductionRun { RunId = runId, UniverseSnapshotId = preview.UniverseSnapshotId, DecisionRunId = runId,
            Profile = "diagnostic_replay", Status = "running", StartedAtUtc = cutoff, ConfigHash = work.ProfileSha256,
            CodeVersion = CandidateWorkflowService.ResolveCodeIdentity() };
        var decisions = new StrategyCandidateDecisionOrchestrator(new StrategyDecisionKernel(), candidates);
        var results = new List<StockStrategyAssessment>();
        foreach (var symbol in work.Request.Symbols)
        {
            token.ThrowIfCancellationRequested();
            var member = members.SingleOrDefault(item => item.Symbol == symbol);
            var stock = batch.Stocks.GetValueOrDefault(symbol);
            if (member is null || member.ExpiresAtUtc <= cutoff)
                results.Add(Missing(symbol, work.Request.Strategy, runId, "This stock is absent from the newly captured source membership."));
            else if (stock?.Missing is not null || stock?.Market is null || stock.Regime is null)
                results.Add(Missing(symbol, work.Request.Strategy, runId, stock?.Missing ?? "Prepared market or regime evidence is missing."));
            else if (StockStrategyEvidenceLoader.MissingAtDecision(runtime.Definition, stock, batch.Calendar, cutoff) is { } stale)
                results.Add(Missing(symbol, work.Request.Strategy, runId, stale));
            else
                results.Add(await EvaluateStockAsync(run, runtime, work.Profile, member, stock, cutoff, decisions, token));
            await context.SaveSnapshotAsync(PreparationJson.Write(new StockStrategyAssessmentResponse(
                ContractVersions.VersionOne, runId, work.Request.Strategy, cutoff, results.ToArray())), token);
        }
        await RequireAuthorizationAsync(work, token);
        var response = new StockStrategyAssessmentResponse(ContractVersions.VersionOne, runId, work.Request.Strategy, cutoff, results);
        return DurableJobCompletion.Completed(PreparationJson.Write(response), runId.ToString());
    }

    /// <summary>Assembles genuine source and provider evidence; only the shared kernel decides conditions, arming and triggering.</summary>
    private async Task<StockStrategyAssessment> EvaluateStockAsync(ProductionRun run, AuthorizedRuntimeStrategy strategy,
        BacktestRunConfig profile, ActiveDiscoveryAggregate member, StockStrategyEvidence stock, DateTimeOffset cutoff,
        IStrategyCandidateDecisionOrchestrator decisions, CancellationToken token)
    {
        var market = stock.Market!;
        try
        {
            var latest = market.BarsByTimeframe[strategy.Definition.Timeframe]
                .LastOrDefault(bar => bar.Timestamp.Add(TimeframeParser.Parse(strategy.Definition.Timeframe)) <= cutoff &&
                    (bar.KnownAtUtc is null || bar.KnownAtUtc <= cutoff));
            if (latest is null) return Missing(member.Symbol, ToReference(strategy), run.RunId, "No completed setup candle is available.");
            var source = StrategyDecisionRequestAssembler.CreateLiveDiscovery(member);
            var universe = new StrategyEligibilityEvidence(source.IsActive, "durable_discovery_universe",
                source.AggregateId.ToString("N"), source.ObservedAtUtc,
                PreparationJson.Write(new { source.AggregateId, source.AggregateVersion, source.Sources, source.EvidenceSha256 }));
            var request = StrategyDecisionRequestAssembler.Create(strategy, member.Symbol, market.BarsByTimeframe,
                market.SnapshotsByTimeframe, stock.News, cutoff, source, universe, stock.Regime!, StrategyCandidateState.Discovered,
                0, StrategyDecisionRequestAssembler.CreateSetupKey(strategy.Identity.StrategyId, strategy.Definition.Timeframe, latest.Timestamp),
                source.ObservedAtUtc, source.ExpiresAtUtc, profile.Engine.IndicatorWarmupBars, run.RunId,
                stock.ReferenceBars, stock.ReferenceSessions, stock.DrawdownContext);
            var evaluation = await decisions.EvaluateAsync(run, request, token);
            var response = await recorded.ReadAsync(run.RunId, ToReference(strategy), [member.Symbol], token);
            var projection = response.Stocks.Single();
            if (projection.State != "recorded") return projection;
            return projection with { State = projection.State == "recorded" ? "evaluated" : projection.State,
                Detail = "Selected strategy evaluated from collected provider evidence. Checks not reached are not marked passed. Passing conditions does not admit or submit an order.",
                ExpiresAtUtc = evaluation.Candidate.ExpiresAtUtc };
        }
        catch (InvalidOperationException exception)
        {
            return Missing(member.Symbol, ToReference(strategy), run.RunId, exception.Message);
        }
    }

    /// <summary>Resolves only existing operator sources; it neither creates wishlists nor registers a diagnostic stock list.</summary>
    private async Task<IReadOnlyList<UniverseSourceSelectionRequest>> ResolveSourcesAsync(string key, CancellationToken token)
    {
        if (key == "all")
        {
            var lists = await sources.FollowedListsAsync(token);
            if (lists.Count == 0) throw new InvalidOperationException("No wishlists are available for assessment.");
            return lists.Select(item => new UniverseSourceSelectionRequest("wishlist", item.Id.ToString(), item.Id)).ToArray();
        }
        var source = (await sources.CatalogAsync(token)).SingleOrDefault(item => item.Key == key)
            ?? throw new InvalidOperationException("This stock source no longer exists. Select another source.");
        return source.Query is not null ? [new("finviz", source.Key, null, source.Query)] :
            Guid.TryParse(source.Key.Replace("wishlist:", "", StringComparison.Ordinal), out var id)
                ? [new("wishlist", id.ToString(), id)] : throw new InvalidOperationException("Select a wishlist or Finviz source.");
    }

    /// <summary>Rechecks the existing operator security stamp before collection and before publication.</summary>
    private async Task RequireAuthorizationAsync(Work work, CancellationToken token)
    {
        if (await authorization.StampAsync(work.Owner, token) != work.Stamp)
            throw new UnauthorizedAccessException("The requesting login is no longer authorized. Sign in and request checks again.");
    }

    /// <summary>Prevents another operator or another durable job type from being read through this endpoint.</summary>
    private static Work RequireOwner(PersistedJob job, string owner)
    {
        if (job.JobType != Type) throw new KeyNotFoundException("Strategy assessment job was not found.");
        var work = PreparationJson.Read<Work>(job.RequestJson);
        if (work.Owner != owner) throw new UnauthorizedAccessException("This strategy assessment belongs to another operator.");
        return work;
    }

    /// <summary>Projects durable status without describing unfinished or expired evidence as a current successful evaluation.</summary>
    private StockStrategyAssessmentJob View(PersistedJob job, Work work)
    {
        var result = job.SnapshotJson is null ? null : PreparationJson.Read<StockStrategyAssessmentResponse>(job.SnapshotJson);
        var now = clock.GetUtcNow();
        if (result is not null) result = result with { ReadAtUtc = now, Stocks = result.Stocks.Select(item => item with {
            Expired = item.Expired || item.ExpiresAtUtc <= now }).ToArray() };
        var detail = job.ErrorMessage ?? (job.CancellationRequestedAtUtc is not null && job.Status is "queued" or "running"
            ? "Cancellation requested; waiting for collection to stop." : job.Status switch {
            "queued" => "Waiting to collect selected-strategy data.", "running" => "Collecting data and evaluating the selected strategy.",
            "completed" => "Evaluation finished. Inspect each stock's checks or missing-data reason.",
            "cancelled" => "Evaluation cancelled. Partial results are not a completed assessment.",
            _ => "Evaluation failed; no current successful assessment is available." });
        return new(ContractVersions.VersionOne, job.Id, job.Status, detail, work.Request.Strategy, work.ProfileSha256,
            job.CreatedAt, job.StartedAt, job.FinishedAt, result);
    }

    /// <summary>Identifies absent evidence without manufacturing a passed condition or an eligible candidate.</summary>
    private static StockStrategyAssessment Missing(string symbol, StrategyReference strategy, Guid run, string detail) =>
        new(symbol, strategy, run, null, "missing", detail, null, null, false, []);
    /// <summary>Preserves the selected immutable artifact identity on every response.</summary>
    private static StrategyReference ToReference(AuthorizedRuntimeStrategy strategy) =>
        new(strategy.Identity.StrategyId, strategy.Identity.SemanticVersion, strategy.Identity.ContentSha256);

    /// <summary>Bounds collection before any provider work and rejects ambiguous identities.</summary>
    private static void Validate(StartStockStrategyAssessmentRequest request)
    {
        if (request is null || request.ContractVersion != ContractVersions.VersionOne ||
            String.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128 ||
            String.IsNullOrWhiteSpace(request.Source) || request.Source.Length > 200 || request.Strategy is null ||
            String.IsNullOrWhiteSpace(request.Strategy.StrategyId) || String.IsNullOrWhiteSpace(request.Strategy.SemanticVersion) ||
            request.Strategy.ContentSha256 is not { Length: 64 } hash || !hash.All(Char.IsAsciiHexDigit) ||
            request.Symbols is null || request.Symbols.Count is < 1 or > DeskSources.DisplayLimit ||
            request.Symbols.Any(symbol => String.IsNullOrWhiteSpace(symbol) || symbol.Length > 20 ||
                symbol.Any(character => !Char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-')))
            throw new ArgumentException($"Select an exact strategy, existing source and between 1 and {DeskSources.DisplayLimit} valid stocks.");
    }
}
