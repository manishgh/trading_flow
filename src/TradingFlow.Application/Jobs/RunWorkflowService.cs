using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingFlow.Application.Candidates;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Application.Jobs;

/// <summary>Opaque operation lifetime only; the Web owner retains the provider and read leases.</summary>
public interface IWorkflowEvidenceVerificationScope : IDisposable { }

public interface IWorkflowRunConfiguration
{
    /// <summary>Resolves the strategy/profile execution choice before it is frozen in a saved command.</summary>
    Task<StartBacktestRequest> NormalizeBacktestRequestAsync(StartBacktestRequest request, CancellationToken token) => Task.FromResult(request);
    /// <summary>Opens a fresh server-owned verification lifetime for one backtest operation, where assessment reuse applies.</summary>
    Task<IWorkflowEvidenceVerificationScope?> OpenEvidenceVerificationScopeAsync(StartBacktestRequest request, CancellationToken token);
    /// <summary>Identifies the current base profile so saved research cannot silently change execution settings.</summary>
    Task<string> GetBacktestConfigurationHashAsync(string? configurationKey, CancellationToken token);
    /// <summary>Freezes public cost and execution-policy facts for review and later saved-plan display.</summary>
    Task<BacktestResolvedSettingsResponse?> GetBacktestResolvedSettingsAsync(StartBacktestRequest request, CancellationToken token);
    /// <summary>Resolves the optional server evidence identity before saving or admitting research.</summary>
    Task<DecisionContextEvidenceSummary?> GetDecisionContextEvidenceAsync(StartBacktestRequest request, CancellationToken token,
        IWorkflowEvidenceVerificationScope? evidenceScope = null);
    /// <summary>Rechecks the parsed dependency graph while its exact captured bytes are held open for admission.</summary>
    Task ValidateCapturedBacktestInputsAsync(string configPath, IReadOnlyList<string> files, CancellationToken token,
        IWorkflowEvidenceVerificationScope? evidenceScope = null);
    Task<IReadOnlyList<string>> WriteAsync(StartBacktestRequest request, UniversePreviewResponse universe, string uniqueName, CancellationToken token,
        IWorkflowEvidenceVerificationScope? evidenceScope = null);
    Task<IReadOnlyList<string>> WriteAsync(StartPaperRunRequest request, UniversePreviewResponse universe, string uniqueName, CancellationToken token);
    /// <summary>Freezes comparison-wide effective source requirements without collecting provider data or rewriting child inputs.</summary>
    Task<TradingFlow.Domain.Backtesting.PreparedMarketDataRequestReference?> WritePreparedComparisonRequestAsync(
        string comparisonId, IReadOnlyList<PersistedJob> books, DateTimeOffset cutoffUtc, CancellationToken token);
    /// <summary>Deletes only an unaccepted attempt's generated configuration and strategy copies; source evidence remains read-only.</summary>
    Task DiscardUnacceptedAsync(string configPath, IReadOnlyList<string> capturedPaths, CancellationToken token);
}

/// <summary>One durable command boundary for browser and device run requests.</summary>
public sealed partial class RunWorkflowService(
    IDurableJobRepository jobs, IUniversePreviewRepository previews, IStrategyIdentityResolver strategies,
    IWorkflowRunConfiguration configuration, TimeProvider clock, RunQueueLimits limits)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Admits the selected research book with its exact requested dates and a comparison-wide immutable input request.</summary>
    public async Task<RunJobResponse> StartAsync(StartBacktestRequest request, CancellationToken token = default)
    {
        ValidateBacktest(request, clock.GetUtcNow());
        using var scope = await configuration.OpenEvidenceVerificationScopeAsync(request, token);
        var evidence = await configuration.GetDecisionContextEvidenceAsync(request, token, scope);
        return await StartCoreAsync("backtest", request.IdempotencyKey, request.RunName, request.UniverseSnapshotId, request.Strategies,
            JsonSerializer.Serialize(request, Json), (universe, name) => configuration.WriteAsync(request, universe, name, token, scope), token,
            request.TestWindowEndUtc, evidence, scope);
    }

    /// <summary>Validates research choices before saving, preparing files or admitting any jobs.</summary>
    internal static void ValidateBacktest(StartBacktestRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.ContractVersion, request.IdempotencyKey, request.RunName);
        if (request.TestWindowStartUtc == default || request.TestWindowEndUtc <= request.TestWindowStartUtc ||
            request.TestWindowEndUtc > now || request.StartingCapital <= 0 ||
            request.AccountRiskBudgetPct is <= 0 or > 100 || request.MaxPositionNotionalPct is <= 0 or > 100 ||
            request.MaxConcurrentPositions is <= 0 or > 1000 || request.RetentionPolicy != "full" ||
            request.CachePolicy is not ("use_cache" or "refresh") || request.Strategies is null || request.Strategies.Count == 0 ||
            request.Strategies.Any(item => item is null) || request.Strategies.Distinct().Count() != request.Strategies.Count)
            throw new InvalidOperationException("Select valid backtest dates, distinct strategies, positive portfolio limits, cache policy and full retention.");
    }

    public Task<RunJobResponse> StartAsync(StartPaperRunRequest request, CancellationToken token = default)
    {
        Validate(request.ContractVersion, request.IdempotencyKey, request.RunName);
        if (request.Mode is not ("paper_shadow" or "paper_experiment") || request.ExecutionPolicy != "strategy_validated" ||
            request.EntryOrderType is not ("limit" or "market") || request.TimeInForce is not ("day" or "gtc") ||
            (request.AllowExtendedHoursTrading && (request.EntryOrderType != "limit" || request.TimeInForce != "day")))
            throw new InvalidOperationException("An explicit authorized paper mode and strategy-validated order policy are required.");
        return StartCoreAsync(request.Mode, request.IdempotencyKey, request.RunName, request.UniverseSnapshotId, [request.Strategy],
            JsonSerializer.Serialize(request, Json), (universe, name) => configuration.WriteAsync(request, universe, name, token), token);
    }

    /// <summary>Enqueues one prepared execution using the same configured budget as cohort comparisons.</summary>
    private async Task<RunJobResponse> StartCoreAsync(string mode, string key, string name, Guid snapshotId,
        IReadOnlyList<StrategyReference> identities, string json, Func<UniversePreviewResponse, string, Task<IReadOnlyList<string>>> write,
        CancellationToken token, DateTimeOffset? preparedCutoffUtc = null, DecisionContextEvidenceSummary? evidenceSummary = null,
        IWorkflowEvidenceVerificationScope? evidenceScope = null)
    {
        var job = await PrepareCoreAsync(mode, key, name, snapshotId, identities, json, write, token,
            evidenceSummary: evidenceSummary, evidenceScope: evidenceScope);
        try
        {
            var replay = await jobs.GetJobAsync(job.Id, token);
            if (replay is not null)
                return RequireReplay(replay, DurableFileJobRequest.Deserialize(job.RequestJson).Workflow!.RequestSha256, key);
            if (mode == "backtest")
                await AttachPreparedComparisonAsync("run-" + job.Id.ToString("N"), [job], preparedCutoffUtc ?? job.CreatedAt, token);
            var maximum = mode == "backtest" ? limits.Backtest : limits.Paper;
            if (!await jobs.TryEnqueueAsync(job, maximum, token))
            {
                var existing = await jobs.GetJobAsync(job.Id, token);
                if (existing is not null) return RequireReplay(existing, DurableFileJobRequest.Deserialize(job.RequestJson).Workflow!.RequestSha256, key);
                throw new InvalidOperationException("The durable run queue is full.");
            }
            return Project(job);
        }
        finally
        {
            if (mode == "backtest") await DiscardUnacceptedInputsAsync(job);
        }
    }

    /// <summary>Freezes one runnable file and its evidence without enqueuing; batch admission follows atomically.</summary>
    private async Task<PersistedJob> PrepareCoreAsync(string mode, string key, string name, Guid snapshotId,
        IReadOnlyList<StrategyReference> identities, string json, Func<UniversePreviewResponse, string, Task<IReadOnlyList<string>>> write,
        CancellationToken token, BacktestCohortExecution? cohort = null, UniversePreviewResponse? frozenResearchSource = null,
        DecisionContextEvidenceSummary? evidenceSummary = null, IWorkflowEvidenceVerificationScope? evidenceScope = null)
    {
        if (identities is null || identities.Count == 0 || identities.Any(item => item is null) ||
            identities.Distinct().Count() != identities.Count) throw new InvalidOperationException("Select distinct exact strategy identities.");
        var kind = mode == "backtest" ? "backtest" : "paper";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("workflow-job:" + key.Trim())).AsSpan(0, 16));
        var existing = await jobs.GetJobAsync(id, token);
        if (existing is not null)
        {
            RequireReplay(existing, hash, key);
            return existing;
        }
        var universe = frozenResearchSource;
        if (universe is null)
        {
            var stored = await previews.GetAsync(snapshotId, token) ?? throw new InvalidOperationException("Universe preview is missing.");
            universe = UniverseDiscoveryService.ReadPreview(stored, snapshotId);
        }
        if (universe.UniverseSnapshotId != snapshotId || (frozenResearchSource is not null && (mode != "backtest" || cohort is null)))
            throw new InvalidOperationException("Frozen membership is allowed only for its saved historical research plan.");
        if (mode != "backtest" && universe.Horizon != "swing")
            throw new InvalidOperationException("Day research source previews cannot start paper or live trading.");
        if (cohort is not null)
        {
            var selected = cohort.Symbols.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selected.Count == 0 || selected.Any(symbol => !universe.Members.Any(member => member.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("Cohort stocks must belong to the reviewed source snapshot.");
            universe = universe with { Members = universe.Members.Where(member => selected.Contains(member.Symbol)).ToArray() };
        }
        // Saved research retains the original observation; operational freshness and historical promotion remain separate.
        if ((frozenResearchSource is null && universe.ExpiresAtUtc <= clock.GetUtcNow()) || universe.Members.Count == 0 ||
            universe.Members.Any(item => !item.Sources.Any(source => !source.IsDiagnostic &&
                (frozenResearchSource is not null || source.ExpiresAtUtc > clock.GetUtcNow()))))
            throw new InvalidOperationException("Universe preview expired or contains diagnostic-only membership.");
        await RequireSelectedStrategiesAsync(identities, mode, token);
        // Every attempt has isolated immutable files. Only the deterministic job ID can win enqueue.
        var files = await write(universe, $"{name.Trim()}_{Guid.NewGuid():N}");
        DurableFileJobRequest captured;
        try
        {
            captured = DurableFileJobRequest.Capture(files) with { Workflow = new(new(mode, snapshotId, kind == "paper" ? id : null, identities), hash, cohort, evidenceSummary) };
            if (kind == "backtest")
            {
                using var lease = captured.AcquireVerifiedReadLease();
                await configuration.ValidateCapturedBacktestInputsAsync(files[0], files, token, evidenceScope);
            }
        }
        catch
        {
            if (mode == "backtest") await configuration.DiscardUnacceptedAsync(files[0], files, CancellationToken.None);
            throw;
        }
        var now = clock.GetUtcNow();
        var job = new PersistedJob {
            Id = id, JobType = kind, RunName = name.Trim(), ConfigPath = files[0], RequestJson = captured.Serialize(),
            Status = "queued", CreatedAt = now,
            SnapshotJson = JsonSerializer.Serialize(new { jobId = id, runName = name.Trim(), configPath = files[0], status = "queued",
                createdAt = now, currentStage = "queued", completedTickerCount = 0, totalTickerCount = universe.Members.Count,
                events = Array.Empty<string>() }, Json)
        };
        return job;
    }

    /// <summary>Checks exact selected artifacts before preview/save and again before admitting new execution inputs.</summary>
    internal async Task RequireSelectedStrategiesAsync(IReadOnlyList<StrategyReference> identities, string mode, CancellationToken token)
    {
        foreach (var identity in identities)
        {
            if (mode == "backtest") await strategies.RequireBacktestStrategyAsync(identity, token);
            else await strategies.RequireSwingStrategyAsync(identity, mode, token);
        }
    }

    /// <summary>Projects bounded progress, monthly results and committed public input evidence from the existing durable snapshot.</summary>
    public async Task<RunJobDetailsResponse?> DetailsAsync(Guid id, CancellationToken token = default)
    {
        var job = await jobs.GetJobAsync(id, token);
        if (job is null || job.JobType is not ("paper" or "backtest")) return null;
        using var document = JsonDocument.Parse(job.SnapshotJson ?? "{}");
        var root = document.RootElement;
        var events = root.TryGetProperty("events", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().TakeLast(100).Select(item => item.GetString() ?? "").ToArray() : [];
        RunResultSummary? result = root.TryGetProperty("result", out var value) && value.ValueKind == JsonValueKind.Object
            ? value.Deserialize<RunResultSummary>(Json) : null;
        var groups = root.TryGetProperty("strategyGroups", out var strategies) && strategies.ValueKind == JsonValueKind.Array
            ? strategies.EnumerateArray().Take(100).Select(item => item.Deserialize<RunStrategyProgress>(Json)!)
                .Where(item => item is not null).Select(item => item with { RecentEvents = (item.RecentEvents ?? []).TakeLast(20).ToArray() }).ToArray() : [];
        var monthly = root.TryGetProperty("monthlyResults", out var months) && months.ValueKind == JsonValueKind.Array
            ? months.Deserialize<MonthlyRunResult[]>(Json) ?? [] : [];
        var unavailable = root.TryGetProperty("monthlyUnavailableReason", out var reason) ? reason.GetString() : null;
        var portfolio = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("portfolioState", out var state)
            && state.ValueKind == JsonValueKind.Object ? state.Deserialize<BacktestPortfolioState>(Json) : null;
        var eligibility = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("historyEligibility", out var history)
            && history.ValueKind == JsonValueKind.Array ? history.Deserialize<BacktestHistoryEligibility[]>(Json) ?? [] : [];
        if (result is not null && portfolio is not null && portfolio.Sessions.LastOrDefault()?.Equity is null)
            result = result with { EndingCapital = null, NetProfit = null, TotalReturnPct = null,
                AverageDailyReturnPct = null, MaxDrawdownPct = null };
        return new(Project(job), events, result, groups, monthly, unavailable, ReadPreparedMarketData(root),
            RunPortfolioStateProjection.Project(portfolio), RunPortfolioStateProjection.ProjectEligibility(eligibility), Project(job).EvidenceSummary);
    }
    public async Task<RunJobResponse?> GetAsync(Guid id, CancellationToken token = default) =>
        await jobs.GetJobAsync(id, token) is { } job ? Project(job) : null;

    public async Task<IReadOnlyList<RunJobResponse>> ListAsync(string kind, CancellationToken token = default)
    {
        if (kind is not ("backtest" or "paper")) throw new InvalidOperationException("Unknown job kind.");
        return (await jobs.GetJobsAsync(kind, token)).OrderByDescending(item => item.CreatedAt).Take(100).Select(Project).ToArray();
    }

    public async Task<RunJobResponse?> CancelAsync(Guid id, CancelJobRequest request, CancellationToken token = default)
    {
        if (request.ContractVersion != ContractVersions.VersionOne || String.IsNullOrWhiteSpace(request.Reason))
            throw new InvalidOperationException("Cancellation reason is required.");
        var job = await jobs.GetJobAsync(id, token);
        if (job is null) return null;
        if (job.JobType is not ("backtest" or "paper")) throw new InvalidOperationException("Unsupported job kind.");
        await jobs.RequestCancellationAsync(id, clock.GetUtcNow(), token);
        return Project((await jobs.GetJobAsync(id, token))!);
    }

    private static RunJobResponse RequireReplay(PersistedJob job, string hash, string key)
    {
        if (DurableFileJobRequest.Deserialize(job.RequestJson).Workflow?.RequestSha256 != hash)
            throw new IdempotencyKeyConflictException(key);
        return Project(job);
    }

    /// <summary>Builds the shared job-list contract without exposing selected-data filesystem paths.</summary>
    public static RunJobResponse Project(PersistedJob job)
    {
        RunProvenance? provenance = null;
        BacktestCohortExecution? cohort = null;
        DecisionContextEvidenceSummary? evidenceSummary = null;
        try
        {
            var context = DurableFileJobRequest.Deserialize(job.RequestJson).Workflow;
            provenance = context?.Provenance;
            cohort = context?.Cohort;
            evidenceSummary = context?.EvidenceSummary;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException) { }
        using var snapshot = JsonDocument.Parse(job.SnapshotJson ?? "{}");
        var root = snapshot.RootElement;
        string stage = root.TryGetProperty("currentStage", out var value) ? value.GetString() ?? job.Status : job.Status;
        // Missing progress counts on queued jobs are zero, independently of whether a data snapshot has been selected.
        int Number(string key) => root.TryGetProperty(key, out var number) && number.TryGetInt32(out var result) ? result : 0;
        return new(ContractVersions.VersionOne, job.Id, job.JobType, job.RunName, job.Status, provenance, job.CreatedAt, job.StartedAt,
            job.FinishedAt, job.CancellationRequestedAtUtc, stage, Number("completedTickerCount"), Number("totalTickerCount"), 0,
            job.ResultReference is null ? null : $"/api/v1/workflow/jobs/{job.Id}/details",
            job.ErrorMessage is null ? null : new("run_failed", job.ErrorMessage, false, job.FinishedAt ?? job.CreatedAt), cohort,
            ReadPreparedMarketData(root), evidenceSummary);
    }

    /// <summary>Reads only the committed public summary from live or terminal snapshots; queued jobs have no selected hash yet.</summary>
    private static PreparedMarketDataSummaryResponse? ReadPreparedMarketData(JsonElement root)
    {
        if (root.TryGetProperty("preparedMarketData", out var prepared) && prepared.ValueKind == JsonValueKind.Object)
            return prepared.Deserialize<PreparedMarketDataSummaryResponse>(Json);
        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("preparedMarketData", out prepared) && prepared.ValueKind == JsonValueKind.Object)
            return prepared.Deserialize<PreparedMarketDataSummaryResponse>(Json);
        return null;
    }

    private static void Validate(string contract, string key, string name)
    {
        if (contract != ContractVersions.VersionOne || String.IsNullOrWhiteSpace(key) || key.Trim().Length > 160 ||
            String.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(c => !Char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new InvalidOperationException("A supported contract, retry key and alphanumeric run name are required.");
    }
}
