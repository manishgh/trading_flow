using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Application.Candidates;

public sealed record UniverseSourceCapture(
    string SourceKind,
    string SourceKey,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<DiscoverySymbolObservation> Symbols,
    bool IsDiagnostic = false,
    DateTimeOffset? ProviderTimestampUtc = null,
    string? ProviderReference = null);

public interface IUniverseSourceResolver
{
    Task<UniverseSourceCapture> ResolveAsync(
        UniverseSourceSelectionRequest source,
        string horizon,
        CancellationToken cancellationToken = default);
}

public interface IStrategyIdentityResolver
{
    /// <summary>Checks research selection separately from the daily-only operational candidate path.</summary>
    Task RequireBacktestStrategyAsync(StrategyReference strategy, CancellationToken cancellationToken = default) =>
        RequireSwingStrategyAsync(strategy, "backtest", cancellationToken);
    Task RequireSwingStrategyAsync(
        StrategyReference strategy,
        string mode,
        CancellationToken cancellationToken = default);
}

public interface IUniverseDiscoveryService
{
    Task<UniversePreviewResponse> PreviewAsync(
        UniversePreviewRequest request,
        CancellationToken cancellationToken = default);
}

public interface ICandidateWorkflow
{
    Task<CandidateRunResponse> StartAsync(
        CreateCandidateRunRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Captures immutable discovery evidence without backdating current membership.</summary>
public sealed class UniverseDiscoveryService(
    IUniverseSourceResolver sources,
    IDiscoveryRepository repository,
    IUniversePreviewRepository previews,
    IApplicationEventRepository events,
    TimeProvider timeProvider) : IUniverseDiscoveryService
{
    /// <summary>Captures current source membership for the requested research horizon without granting execution authority.</summary>
    public async Task<UniversePreviewResponse> PreviewAsync(UniversePreviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContractVersion != ContractVersions.VersionOne ||
            request.Horizon?.ToLowerInvariant() is not ("swing" or "day") ||
            request.Sources is null || request.Sources.Count == 0 || request.Sources.Any(source => source is null))
            throw new InvalidOperationException("A supported day or swing universe contract and at least one source are required.");

        var horizon = request.Horizon.ToLowerInvariant();

        var startedAt = timeProvider.GetUtcNow().ToUniversalTime();
        if (request.AsOfUtc is { } requested && (requested == default || requested > startedAt))
            throw new InvalidOperationException("Universe as-of time cannot be default or in the future.");

        // A null cutoff is an operational capture. An explicit cutoff never admits
        // newer observations, including a current wishlist read after that cutoff.
        var captures = new List<UniverseSourceCapture>();
        foreach (var selection in request.Sources.OrderBy(item => item.SourceKind, StringComparer.Ordinal)
                     .ThenBy(item => item.SourceKey, StringComparer.Ordinal))
            captures.Add(await sources.ResolveAsync(selection, horizon, cancellationToken));
        var resolvedAt = timeProvider.GetUtcNow().ToUniversalTime();
        var cutoff = request.AsOfUtc?.ToUniversalTime() ?? resolvedAt;
        foreach (var capture in captures)
            ValidateCapture(capture, cutoff, resolvedAt);
        if (captures.Select(item => (item.SourceKind, item.SourceKey)).Distinct().Count() != captures.Count)
            throw new InvalidOperationException("A universe source may only be selected once.");

        var snapshotId = Guid.NewGuid();
        foreach (var capture in captures)
        {
            await repository.CommitSnapshotAsync(new DiscoverySnapshotRequest(
                snapshotId,
                DeterministicGuid($"{snapshotId:N}\u001f{capture.SourceKind}\u001f{capture.SourceKey}"),
                capture.SourceKind, capture.SourceKey, horizon, capture.ObservedAtUtc, capture.ExpiresAtUtc,
                0, capture.Symbols, capture.IsDiagnostic, capture.ProviderTimestampUtc, capture.ProviderReference), cancellationToken);
        }
        var members = await repository.GetActiveAsync(snapshotId, cutoff, cancellationToken);
        if (members.Count == 0)
            throw new InvalidOperationException("The selected sources produced no unexpired stocks.");
        var response = ToResponse(snapshotId, resolvedAt, members, cutoff, horizon);
        await previews.SaveAsync(new UniversePreviewRecord
        {
            UniverseSnapshotId = snapshotId,
            Horizon = response.Horizon,
            RequestSha256 = Sha256(JsonSerializer.Serialize(new
            {
                horizon, asOfUtc = request.AsOfUtc?.ToUniversalTime(),
                sources = request.Sources.OrderBy(item => item.SourceKind, StringComparer.Ordinal).ThenBy(item => item.SourceKey, StringComparer.Ordinal)
            })),
            ContentSha256 = response.ContentSha256,
            ResolvedAtUtc = response.ResolvedAtUtc,
            ExpiresAtUtc = response.ExpiresAtUtc,
            PreviewJson = JsonSerializer.Serialize(response)
        }, cancellationToken);
        await events.AppendAsync($"universes/{snapshotId:N}", "universe.preview.created", resolvedAt,
            JsonSerializer.Serialize(response), cancellationToken);
        return response;
    }

    /// <summary>Preserves the source horizon in the immutable preview while retaining the existing swing default.</summary>
    public static UniversePreviewResponse ToResponse(Guid snapshotId, DateTimeOffset resolvedAtUtc,
        IReadOnlyList<ActiveDiscoveryAggregate> aggregates, DateTimeOffset? decisionCutoffUtc = null, string horizon = "swing")
    {
        var members = aggregates.OrderBy(item => item.Symbol, StringComparer.Ordinal)
            .Select(item => new UniverseMemberResponse(item.Symbol, "discovered",
                item.Sources.OrderBy(source => source.SourceKind, StringComparer.Ordinal).ThenBy(source => source.SourceKey, StringComparer.Ordinal)
                    .Select(source => new UniverseSourceEvidenceResponse(source.SourceKind, source.SourceKey,
                        source.ObservedAtUtc, source.ExpiresAtUtc, source.ContentSha256, source.IsDiagnostic, source.RawReference)).ToArray())).ToArray();
        var expiresAt = members.SelectMany(member => member.Sources).Select(source => source.ExpiresAtUtc).DefaultIfEmpty(resolvedAtUtc).Min();
        var response = new UniversePreviewResponse(ContractVersions.VersionOne, snapshotId, horizon, resolvedAtUtc,
            expiresAt, String.Empty, members, [], decisionCutoffUtc ?? resolvedAtUtc);
        return response with { ContentSha256 = ContentHash(response) };
    }

    public static string ContentHash(UniversePreviewResponse response) =>
        Sha256(JsonSerializer.Serialize(response with { ContentSha256 = String.Empty }));

    /// <summary>Rejects changed membership, clocks or horizon when restoring retained source evidence.</summary>
    public static UniversePreviewResponse ReadPreview(UniversePreviewRecord record, Guid snapshotId)
    {
        UniversePreviewResponse preview;
        try
        {
            preview = JsonSerializer.Deserialize<UniversePreviewResponse>(record.PreviewJson)
                ?? throw new InvalidOperationException("Universe snapshot payload is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Universe snapshot payload is invalid.", exception);
        }
        if (record.UniverseSnapshotId != snapshotId || preview.UniverseSnapshotId != snapshotId ||
            preview.ContractVersion != ContractVersions.VersionOne || record.Horizon is not ("swing" or "day") || preview.Horizon != record.Horizon ||
            preview.ResolvedAtUtc != record.ResolvedAtUtc || preview.ExpiresAtUtc != record.ExpiresAtUtc ||
            preview.DecisionCutoffUtc == default || preview.DecisionCutoffUtc > preview.ResolvedAtUtc ||
            preview.Members is null || preview.Members.Count == 0 || preview.Exclusions is null ||
            preview.ContentSha256 != record.ContentSha256 || ContentHash(preview) != record.ContentSha256)
            throw new InvalidOperationException("Universe snapshot identity or immutable content failed validation.");
        return preview;
    }

    private static void ValidateCapture(UniverseSourceCapture capture, DateTimeOffset cutoff, DateTimeOffset now)
    {
        if (!DiscoverySourceKinds.All.Contains(capture.SourceKind) || String.IsNullOrWhiteSpace(capture.SourceKey) ||
            capture.Symbols is null || capture.Symbols.Count == 0)
            throw new InvalidOperationException("Universe source identity and symbols are required.");
        if (capture.ObservedAtUtc == default || capture.ObservedAtUtc.Offset != TimeSpan.Zero ||
            capture.ExpiresAtUtc.Offset != TimeSpan.Zero || capture.ProviderTimestampUtc is { Offset: var offset } && offset != TimeSpan.Zero)
            throw new InvalidOperationException("Universe source timestamps must be UTC.");
        if (capture.ObservedAtUtc > cutoff || capture.ProviderTimestampUtc > cutoff)
            throw new InvalidOperationException("Universe source contains future observations relative to the decision cutoff.");
        if (capture.ExpiresAtUtc <= now || capture.ExpiresAtUtc <= capture.ObservedAtUtc)
            throw new InvalidOperationException("Universe source evidence is expired.");
    }
    internal static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static Guid DeterministicGuid(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}

/// <summary>Atomically creates a discovered candidate run; retries only read the durable outcome.</summary>
public sealed class CandidateWorkflowService(
    IUniversePreviewRepository previews,
    ICandidateQueryService queries,
    IStrategyIdentityResolver strategies,
    TimeProvider timeProvider) : ICandidateWorkflow
{
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web);

    public async Task<CandidateRunResponse> StartAsync(CreateCandidateRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContractVersion != ContractVersions.VersionOne || request.UniverseSnapshotId == Guid.Empty ||
            request.Strategies is null || request.Strategies.Count == 0 || String.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.IdempotencyKey.Trim().Length > 160 || String.IsNullOrWhiteSpace(request.Mode) || request.AsOfUtc == default)
            throw new InvalidOperationException("A supported contract, idempotency key, snapshot, cutoff and exact strategies are required.");
        var mode = request.Mode.Trim().ToLowerInvariant();
        if (mode is not ("backtest" or "paper_experiment" or "paper_shadow" or "live" or "diagnostic_replay"))
            throw new InvalidOperationException("Candidate mode is invalid.");
        var selected = request.Strategies.Select(NormalizeStrategy).OrderBy(item => item.StrategyId, StringComparer.Ordinal).ToArray();
        if (selected.Select(item => item.StrategyId).Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new InvalidOperationException("Each strategy may occur only once in a candidate run.");
        var key = request.IdempotencyKey.Trim();
        var asOf = request.AsOfUtc.ToUniversalTime();
        var hash = UniverseDiscoveryService.Sha256(JsonSerializer.Serialize(new { mode, request.UniverseSnapshotId, asOf, strategies = selected }));
        var existing = await previews.FindCandidateRunAsync(key, cancellationToken);
        if (existing is not null)
        {
            if (existing.RequestSha256 != hash) throw new IdempotencyKeyConflictException(key);
            var prior = await queries.GetRunAsync(existing.CandidateRunId, cancellationToken)
                ?? throw new InvalidOperationException("Persisted candidate start is incomplete; no new candidates were created.");
            var stored = await previews.GetAsync(request.UniverseSnapshotId, cancellationToken)
                ?? throw new InvalidOperationException("Persisted candidate start has no immutable universe evidence.");
            var previous = UniverseDiscoveryService.ReadPreview(stored, request.UniverseSnapshotId);
            var expectedIds = previous.Members.Where(member => member.Sources.Any(source => !source.IsDiagnostic))
                .SelectMany(member => selected.Select(strategy => CandidateId(existing.CandidateRunId, request.UniverseSnapshotId, member.Symbol, strategy)))
                .Order().ToArray();
            if (!expectedIds.SequenceEqual(prior.Candidates.Select(item => item.CandidateId).Order()) ||
                prior.Provenance.UniverseSnapshotId != request.UniverseSnapshotId)
                throw new InvalidOperationException("Persisted candidate start is incomplete or has inconsistent provenance.");
            return prior;
        }

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        if (asOf > now) throw new InvalidOperationException("Candidate run as-of time cannot be in the future.");
        foreach (var strategy in selected)
            await strategies.RequireSwingStrategyAsync(strategy, mode, cancellationToken);
        var record = await previews.GetAsync(request.UniverseSnapshotId, cancellationToken)
            ?? throw new InvalidOperationException("Universe snapshot is missing or expired.");
        var preview = UniverseDiscoveryService.ReadPreview(record, request.UniverseSnapshotId);
        if (preview.Horizon != "swing")
            throw new InvalidOperationException("Day research sources cannot start operational candidate runs.");
        now = timeProvider.GetUtcNow().ToUniversalTime();
        if (preview.ExpiresAtUtc <= now || preview.ResolvedAtUtc > now || preview.DecisionCutoffUtc > asOf ||
            preview.Members.Any(member => member.Sources is null || member.Sources.Count == 0 ||
                member.Sources.Any(source => source.ObservedAtUtc > asOf || source.ExpiresAtUtc <= now)))
            throw new InvalidOperationException("Universe snapshot contains future or expired source evidence.");
        var members = preview.Members.Where(member => member.Sources.Any(source => !source.IsDiagnostic)).ToArray();
        if (members.Length == 0) throw new InvalidOperationException("Diagnostic-only evidence cannot start a trading candidate run.");
        var runId = UniverseDiscoveryService.DeterministicGuid($"candidate-run\u001f{key}");
        var run = new ProductionRun
        {
            RunId = runId, UniverseSnapshotId = request.UniverseSnapshotId, DecisionRunId = runId,
            Profile = mode, Status = "running", StartedAtUtc = now, SchemaVersion = 1, ConfigHash = hash,
            CodeVersion = ResolveCodeIdentity()
        };
        var records = new List<CandidateRecord>();
        foreach (var member in members.OrderBy(item => item.Symbol, StringComparer.Ordinal))
        foreach (var strategy in selected)
        {
            var sources = member.Sources.Where(source => !source.IsDiagnostic).ToArray();
            records.Add(new CandidateRecord
            {
                CandidateId = CandidateId(runId, request.UniverseSnapshotId, member.Symbol, strategy), RunId = runId,
                SchemaVersion = run.SchemaVersion, ConfigHash = hash, CodeVersion = run.CodeVersion,
                Symbol = member.Symbol, DiscoveredAtUtc = sources.Min(source => source.ObservedAtUtc), RevalidatedAtUtc = now,
                DiscoverySource = String.Join('+', sources.Select(source => source.SourceKind).Distinct(StringComparer.Ordinal)),
                FinvizPreset = sources.FirstOrDefault(source => source.SourceKind == DiscoverySourceKinds.Finviz)?.SourceKey ?? String.Empty,
                Horizon = "swing", SelectedStrategy = strategy.StrategyId, StrategySemanticVersion = strategy.SemanticVersion,
                StrategyContentSha256 = strategy.ContentSha256, AdmissionProfileId = strategy.StrategyId,
                AdmissionProfileVersion = strategy.SemanticVersion, SetupKey = SetupKey(request.UniverseSnapshotId, member.Symbol, strategy),
                DiscoveryWindowStartUtc = sources.Min(source => source.ObservedAtUtc), DiscoveryWindowEndUtc = asOf,
                State = StrategyCandidateState.Discovered, ExpiresAtUtc = sources.Min(source => source.ExpiresAtUtc),
                SemanticDecisionSha256 = new string('0', 64),
                SetupScoresJson = JsonSerializer.Serialize(new { universeSnapshotId = request.UniverseSnapshotId, sources }, EvidenceJson),
                RejectReasonsJson = "[]"
            });
        }
        var created = await previews.CreateCandidateRunAsync(new CandidateRunRequestRecord
        {
            IdempotencyKey = key, RequestSha256 = hash, CandidateRunId = runId, UniverseSnapshotId = request.UniverseSnapshotId,
            Mode = mode, StrategyIdentitiesJson = JsonSerializer.Serialize(selected), CreatedAtUtc = now
        }, run, records, cancellationToken);
        return await queries.GetRunAsync(created.CandidateRunId, cancellationToken)
            ?? throw new InvalidOperationException("Candidate run was not visible after atomic creation.");
    }
    /// <summary>Returns the compiled source identity shared by durable observational candidate runs.</summary>
    public static string ResolveCodeIdentity()
    {
        var version = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(CandidateWorkflowService).Assembly)?.InformationalVersion;
        var hash = version?.Split('+').LastOrDefault();
        return hash is { Length: 40 or 64 } && hash.All(Uri.IsHexDigit) ? hash.ToLowerInvariant()
            : throw new InvalidOperationException("Candidate run requires a build with exact source identity.");
    }
    private static StrategyReference NormalizeStrategy(StrategyReference strategy)
    {
        if (strategy is null || String.IsNullOrWhiteSpace(strategy.StrategyId) || String.IsNullOrWhiteSpace(strategy.SemanticVersion) ||
            strategy.ContentSha256 is null || strategy.ContentSha256.Length != 64 || !strategy.ContentSha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Every strategy requires exact ID, semantic identity and SHA-256.");
        return strategy with { ContentSha256 = strategy.ContentSha256.ToLowerInvariant() };
    }
    private static string SetupKey(Guid snapshot, string symbol, StrategyReference strategy) =>
        $"{snapshot:N}:{symbol}:{strategy.StrategyId}:{strategy.SemanticVersion}:{strategy.ContentSha256}";
    private static Guid CandidateId(Guid run, Guid snapshot, string symbol, StrategyReference strategy) =>
        UniverseDiscoveryService.DeterministicGuid($"candidate\u001f{run:N}\u001f{SetupKey(snapshot, symbol, strategy)}");
}
