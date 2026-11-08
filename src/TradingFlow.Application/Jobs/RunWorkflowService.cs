using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingFlow.Application.Candidates;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Application.Jobs;

public interface IWorkflowRunConfiguration
{
    Task<IReadOnlyList<string>> WriteAsync(StartBacktestRequest request, UniversePreviewResponse universe, string uniqueName, CancellationToken token);
    Task<IReadOnlyList<string>> WriteAsync(StartPaperRunRequest request, UniversePreviewResponse universe, string uniqueName, CancellationToken token);
}

/// <summary>One durable command boundary for browser and device run requests.</summary>
public sealed class RunWorkflowService(
    IDurableJobRepository jobs, IUniversePreviewRepository previews, IStrategyIdentityResolver strategies,
    IWorkflowRunConfiguration configuration, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<RunJobResponse> StartAsync(StartBacktestRequest request, CancellationToken token = default)
    {
        Validate(request.ContractVersion, request.IdempotencyKey, request.RunName);
        if (request.TestWindowStartUtc == default || request.TestWindowEndUtc <= request.TestWindowStartUtc ||
            request.TestWindowEndUtc > clock.GetUtcNow() || request.StartingCapital <= 0 ||
            request.AccountRiskBudgetPct is <= 0 or > 100 || request.MaxPositionNotionalPct is <= 0 or > 100 ||
            request.MaxConcurrentPositions is <= 0 or > 1000 || request.RetentionPolicy != "full" ||
            request.CachePolicy is not ("use_cache" or "refresh"))
            throw new InvalidOperationException("Backtest dates, portfolio limits, cache policy or retention are invalid.");
        return StartCoreAsync("backtest", request.IdempotencyKey, request.RunName, request.UniverseSnapshotId, request.Strategies,
            JsonSerializer.Serialize(request, Json), (universe, name) => configuration.WriteAsync(request, universe, name, token), token);
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

    private async Task<RunJobResponse> StartCoreAsync(string mode, string key, string name, Guid snapshotId,
        IReadOnlyList<StrategyReference> identities, string json, Func<UniversePreviewResponse, string, Task<IReadOnlyList<string>>> write, CancellationToken token)
    {
        if (identities is null || identities.Count == 0 || identities.Any(item => item is null) ||
            identities.Distinct().Count() != identities.Count) throw new InvalidOperationException("Select distinct exact strategy identities.");
        var kind = mode == "backtest" ? "backtest" : "paper";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("workflow-job:" + key.Trim())).AsSpan(0, 16));
        var existing = await jobs.GetJobAsync(id, token);
        if (existing is not null) return RequireReplay(existing, hash, key);
        var stored = await previews.GetAsync(snapshotId, token) ?? throw new InvalidOperationException("Universe preview is missing.");
        var universe = UniverseDiscoveryService.ReadPreview(stored, snapshotId);
        if (universe.ExpiresAtUtc <= clock.GetUtcNow() || universe.Members.Count == 0 ||
            universe.Members.Any(item => !item.Sources.Any(source => !source.IsDiagnostic && source.ExpiresAtUtc > clock.GetUtcNow())))
            throw new InvalidOperationException("Universe preview expired or contains diagnostic-only membership.");
        foreach (var identity in identities) await strategies.RequireSwingStrategyAsync(identity, mode, token);
        // Every attempt has isolated immutable files. Only the deterministic job ID can win enqueue.
        var files = await write(universe, $"{name.Trim()}_{Guid.NewGuid():N}");
        var captured = DurableFileJobRequest.Capture(files) with { Workflow = new(new(mode, snapshotId, kind == "paper" ? id : null, identities), hash) };
        var now = clock.GetUtcNow();
        var job = new PersistedJob {
            Id = id, JobType = kind, RunName = name.Trim(), ConfigPath = files[0], RequestJson = captured.Serialize(),
            Status = "queued", CreatedAt = now,
            SnapshotJson = JsonSerializer.Serialize(new { jobId = id, runName = name.Trim(), configPath = files[0], status = "queued",
                createdAt = now, currentStage = "queued", completedTickerCount = 0, totalTickerCount = universe.Members.Count,
                events = Array.Empty<string>() }, Json)
        };
        if (!await jobs.TryEnqueueAsync(job, 16, token))
        {
            existing = await jobs.GetJobAsync(id, token);
            if (existing is not null) return RequireReplay(existing, hash, key);
            throw new InvalidOperationException("The durable run queue is full.");
        }
        return Project(job);
    }

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
        return new(Project(job), events, result, groups);
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

    public static RunJobResponse Project(PersistedJob job)
    {
        RunProvenance? provenance = null;
        try { provenance = DurableFileJobRequest.Deserialize(job.RequestJson).Workflow?.Provenance; }
        catch (Exception exception) when (exception is JsonException or InvalidDataException) { }
        using var snapshot = JsonDocument.Parse(job.SnapshotJson ?? "{}");
        var root = snapshot.RootElement;
        string stage = root.TryGetProperty("currentStage", out var value) ? value.GetString() ?? job.Status : job.Status;
        int Number(string key) => root.TryGetProperty(key, out var number) && number.TryGetInt32(out var result) ? result : 0;
        return new(ContractVersions.VersionOne, job.Id, job.JobType, job.RunName, job.Status, provenance, job.CreatedAt, job.StartedAt,
            job.FinishedAt, job.CancellationRequestedAtUtc, stage, Number("completedTickerCount"), Number("totalTickerCount"), 0,
            job.ResultReference is null ? null : $"/api/v1/workflow/jobs/{job.Id}/details",
            job.ErrorMessage is null ? null : new("run_failed", job.ErrorMessage, false, job.FinishedAt ?? job.CreatedAt));
    }

    private static void Validate(string contract, string key, string name)
    {
        if (contract != ContractVersions.VersionOne || String.IsNullOrWhiteSpace(key) || key.Trim().Length > 160 ||
            String.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(c => !Char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new InvalidOperationException("A supported contract, retry key and alphanumeric run name are required.");
    }
}
