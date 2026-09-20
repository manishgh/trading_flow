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

/// <summary>
/// Creates a run-scoped, immutable discovery snapshot. Provider and wishlist
/// adapters supply observations; this service owns point-in-time validation,
/// durable source provenance and the canonical response hash.
/// </summary>
public sealed class UniverseDiscoveryService(
    IUniverseSourceResolver sources,
    IDiscoveryRepository repository,
    IUniversePreviewRepository previews,
    IApplicationEventRepository events,
    TimeProvider timeProvider) : IUniverseDiscoveryService
{
    private static readonly TimeSpan MaximumClientClockSkew = TimeSpan.FromMinutes(2);

    public async Task<UniversePreviewResponse> PreviewAsync(
        UniversePreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireVersion(request.ContractVersion);
        if (!request.Horizon.Equals("swing", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("TradingFlow supports swing strategy universes only.");
        }
        if (request.Sources.Count == 0)
        {
            throw new InvalidOperationException("At least one universe source is required.");
        }

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var requestedAsOf = request.AsOfUtc.ToUniversalTime();
        if (requestedAsOf > now + MaximumClientClockSkew)
        {
            throw new InvalidOperationException("Universe as-of time cannot be in the future.");
        }

        var snapshotId = Guid.NewGuid();
        foreach (var selection in request.Sources
                     .OrderBy(source => source.SourceKind, StringComparer.Ordinal)
                     .ThenBy(source => source.SourceKey, StringComparer.Ordinal))
        {
            var capture = await sources.ResolveAsync(selection, "swing", cancellationToken);
            ValidateCapture(capture, now);
            await repository.CommitSnapshotAsync(
                new DiscoverySnapshotRequest(
                    snapshotId,
                    DeterministicGuid(String.Join('\u001f',
                        snapshotId,
                        capture.SourceKind,
                        capture.SourceKey,
                        capture.ObservedAtUtc.UtcTicks,
                        CanonicalSymbols(capture.Symbols))),
                    capture.SourceKind,
                    capture.SourceKey,
                    "swing",
                    capture.ObservedAtUtc.ToUniversalTime(),
                    capture.ExpiresAtUtc.ToUniversalTime(),
                    0,
                    capture.Symbols,
                    capture.IsDiagnostic,
                    capture.ProviderTimestampUtc?.ToUniversalTime(),
                    capture.ProviderReference),
                cancellationToken);
        }

        var members = await repository.GetActiveAsync(snapshotId, now, cancellationToken);
        if (members.Count == 0)
        {
            throw new InvalidOperationException("The selected sources produced no unexpired swing candidates.");
        }

        var response = ToResponse(snapshotId, now, members);
        var requestHash = Sha256(JsonSerializer.Serialize(new
        {
            horizon = "swing",
            sources = request.Sources
                .OrderBy(source => source.SourceKind, StringComparer.Ordinal)
                .ThenBy(source => source.SourceKey, StringComparer.Ordinal)
        }));
        await previews.SaveAsync(new UniversePreviewRecord
        {
            UniverseSnapshotId = snapshotId,
            Horizon = "swing",
            RequestSha256 = requestHash,
            ContentSha256 = response.ContentSha256,
            ResolvedAtUtc = response.ResolvedAtUtc,
            ExpiresAtUtc = response.ExpiresAtUtc,
            PreviewJson = JsonSerializer.Serialize(response)
        }, cancellationToken);
        await events.AppendAsync(
            $"universes/{snapshotId:N}",
            "universe.preview.created",
            now,
            JsonSerializer.Serialize(response),
            cancellationToken);
        return response;
    }

    public static UniversePreviewResponse ToResponse(
        Guid snapshotId,
        DateTimeOffset resolvedAtUtc,
        IReadOnlyList<ActiveDiscoveryAggregate> aggregates)
    {
        var members = aggregates
            .OrderBy(item => item.Symbol, StringComparer.Ordinal)
            .Select(item => new UniverseMemberResponse(
                item.Symbol,
                "discovered",
                item.Sources
                    .OrderBy(source => source.SourceKind, StringComparer.Ordinal)
                    .ThenBy(source => source.SourceKey, StringComparer.Ordinal)
                    .Select(source => new UniverseSourceEvidenceResponse(
                        source.SourceKind,
                        source.SourceKey,
                        source.ObservedAtUtc,
                        source.ExpiresAtUtc,
                        source.ContentSha256,
                        source.IsDiagnostic,
                        source.RawReference))
                    .ToArray()))
            .ToArray();
        var expiresAt = members.SelectMany(member => member.Sources)
            .Select(source => source.ExpiresAtUtc)
            .DefaultIfEmpty(resolvedAtUtc)
            .Min();
        var hashPayload = JsonSerializer.Serialize(new
        {
            horizon = "swing",
            resolvedAtUtc,
            expiresAtUtc = expiresAt,
            members
        });
        return new UniversePreviewResponse(
            ContractVersions.VersionOne,
            snapshotId,
            "swing",
            resolvedAtUtc,
            expiresAt,
            Sha256(hashPayload),
            members,
            []);
    }

    private static void ValidateCapture(UniverseSourceCapture capture, DateTimeOffset now)
    {
        if (!DiscoverySourceKinds.All.Contains(capture.SourceKind) ||
            String.IsNullOrWhiteSpace(capture.SourceKey))
        {
            throw new InvalidOperationException("Universe source kind and key are required.");
        }
        if (capture.ObservedAtUtc.Offset != TimeSpan.Zero || capture.ExpiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Universe source timestamps must be UTC.");
        }
        if (capture.ObservedAtUtc > now || capture.ProviderTimestampUtc > now)
        {
            throw new InvalidOperationException("A universe source cannot contain future observations.");
        }
        if (capture.ExpiresAtUtc <= now || capture.ExpiresAtUtc <= capture.ObservedAtUtc)
        {
            throw new InvalidOperationException("Universe source evidence is expired.");
        }
        if (capture.Symbols.Count == 0)
        {
            throw new InvalidOperationException("Universe source returned no symbols.");
        }
    }

    internal static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static Guid DeterministicGuid(string value) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    private static string CanonicalSymbols(IEnumerable<DiscoverySymbolObservation> values) =>
        String.Join(',', values
            .OrderBy(value => value.Symbol, StringComparer.Ordinal)
            .Select(value => $"{value.Symbol.Trim().ToUpperInvariant()}:{value.MetadataJson}"));

    private static void RequireVersion(string value)
    {
        if (!String.Equals(value, ContractVersions.VersionOne, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported contract version '{value}'.");
        }
    }
}

/// <summary>
/// Starts the deterministic candidate lifecycle from an immutable discovery
/// snapshot. An exact retry returns the same run and candidate identities.
/// </summary>
public sealed class CandidateWorkflowService(
    IUniversePreviewRepository previews,
    ICandidateRepository candidates,
    ICandidateQueryService queries,
    IStrategyIdentityResolver strategies,
    TimeProvider timeProvider) : ICandidateWorkflow
{
    public async Task<CandidateRunResponse> StartAsync(
        CreateCandidateRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!String.Equals(request.ContractVersion, ContractVersions.VersionOne, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported contract version '{request.ContractVersion}'.");
        }
        if (request.UniverseSnapshotId == Guid.Empty || request.Strategies.Count == 0 ||
            String.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160)
        {
            throw new InvalidOperationException("Idempotency key, universe snapshot and at least one exact strategy identity are required.");
        }

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var asOf = request.AsOfUtc.ToUniversalTime();
        if (asOf > now)
        {
            throw new InvalidOperationException("Candidate run as-of time cannot be in the future.");
        }
        foreach (var strategy in request.Strategies)
        {
            ValidateStrategyReference(strategy);
            await strategies.RequireSwingStrategyAsync(strategy, request.Mode, cancellationToken);
        }

        var previewRecord = await previews.GetAsync(request.UniverseSnapshotId, cancellationToken);
        if (previewRecord is null)
        {
            throw new InvalidOperationException("Universe snapshot is missing or expired.");
        }
        if (previewRecord.ExpiresAtUtc <= now)
        {
            throw new InvalidOperationException("Universe snapshot is missing or expired.");
        }
        var preview = JsonSerializer.Deserialize<UniversePreviewResponse>(previewRecord.PreviewJson)
            ?? throw new InvalidOperationException("Universe snapshot payload is invalid.");
        if (!UniverseDiscoveryService.Sha256(JsonSerializer.Serialize(new
            {
                horizon = preview.Horizon,
                resolvedAtUtc = preview.ResolvedAtUtc,
                expiresAtUtc = preview.ExpiresAtUtc,
                members = preview.Members
            })).Equals(previewRecord.ContentSha256, StringComparison.Ordinal) ||
            !preview.ContentSha256.Equals(previewRecord.ContentSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Universe snapshot content failed immutable hash validation.");
        }
        if (preview.Members.Any(member => member.Sources.Count == 0 ||
                                         member.Sources.Any(source => source.ObservedAtUtc > asOf || source.ExpiresAtUtc <= now)))
        {
            throw new InvalidOperationException("Universe snapshot contains future or expired source evidence.");
        }
        if (preview.Members.All(member => member.Sources.All(source => source.IsDiagnostic)))
        {
            throw new InvalidOperationException("Diagnostic-only discovery evidence cannot start a trading candidate run.");
        }

        var canonicalRequest = JsonSerializer.Serialize(new
        {
            mode = request.Mode.Trim().ToLowerInvariant(),
            request.UniverseSnapshotId,
            asOf,
            strategies = request.Strategies
                .OrderBy(strategy => strategy.StrategyId, StringComparer.Ordinal)
                .ThenBy(strategy => strategy.SemanticVersion, StringComparer.Ordinal)
                .ThenBy(strategy => strategy.ContentSha256, StringComparer.Ordinal)
        });
        var requestHash = UniverseDiscoveryService.Sha256(canonicalRequest);
        var proposedRunId = UniverseDiscoveryService.DeterministicGuid($"candidate-run\u001f{request.IdempotencyKey}");
        var reservation = await previews.ReserveCandidateRunAsync(new CandidateRunRequestRecord
        {
            IdempotencyKey = request.IdempotencyKey.Trim(),
            RequestSha256 = requestHash,
            CandidateRunId = proposedRunId,
            UniverseSnapshotId = request.UniverseSnapshotId,
            Mode = request.Mode.Trim().ToLowerInvariant(),
            StrategyIdentitiesJson = JsonSerializer.Serialize(request.Strategies),
            CreatedAtUtc = now
        }, cancellationToken);
        var runId = reservation.CandidateRunId;
        var run = new ProductionRun
        {
            RunId = runId,
            UniverseSnapshotId = request.UniverseSnapshotId,
            DecisionRunId = runId,
            Profile = request.Mode.Trim().ToLowerInvariant(),
            Status = "running",
            StartedAtUtc = now,
            SchemaVersion = 1,
            ConfigHash = requestHash,
            CodeVersion = typeof(CandidateWorkflowService).Assembly.GetName().Version?.ToString() ?? "development"
        };

        foreach (var member in preview.Members.OrderBy(item => item.Symbol, StringComparer.Ordinal))
        {
            var usableSources = member.Sources.Where(source => !source.IsDiagnostic).ToArray();
            if (usableSources.Length == 0)
            {
                continue;
            }
            foreach (var strategy in request.Strategies.OrderBy(item => item.StrategyId, StringComparer.Ordinal))
            {
                var setupKey = $"{request.UniverseSnapshotId:N}:{member.Symbol}:{strategy.StrategyId}:{strategy.SemanticVersion}:{strategy.ContentSha256}";
                var candidateId = UniverseDiscoveryService.DeterministicGuid($"candidate\u001f{runId:N}\u001f{setupKey}");
                await candidates.UpsertDiscoveryAsync(run, new CandidateRecord
                {
                    CandidateId = candidateId,
                    RunId = runId,
                    SchemaVersion = run.SchemaVersion,
                    ConfigHash = run.ConfigHash,
                    CodeVersion = run.CodeVersion,
                    Symbol = member.Symbol,
                    DiscoveredAtUtc = usableSources.Min(source => source.ObservedAtUtc),
                    RevalidatedAtUtc = now,
                    DiscoverySource = String.Join('+', usableSources.Select(source => source.SourceKind).Distinct(StringComparer.Ordinal)),
                    FinvizPreset = usableSources.FirstOrDefault(source => source.SourceKind == DiscoverySourceKinds.Finviz)?.SourceKey ?? String.Empty,
                    Horizon = "swing",
                    SelectedStrategy = strategy.StrategyId,
                    StrategySemanticVersion = strategy.SemanticVersion,
                    StrategyContentSha256 = strategy.ContentSha256.ToLowerInvariant(),
                    AdmissionProfileId = strategy.StrategyId,
                    AdmissionProfileVersion = strategy.SemanticVersion,
                    SetupKey = setupKey,
                    DiscoveryWindowStartUtc = usableSources.Min(source => source.ObservedAtUtc),
                    DiscoveryWindowEndUtc = asOf,
                    State = StrategyCandidateState.Discovered,
                    ExpiresAtUtc = usableSources.Min(source => source.ExpiresAtUtc),
                    SemanticDecisionSha256 = new string('0', 64),
                    SetupScoresJson = JsonSerializer.Serialize(new
                    {
                        universeSnapshotId = request.UniverseSnapshotId,
                        sources = usableSources.Select(source => new
                        {
                            source.SourceKind,
                            source.SourceKey,
                            source.ObservedAtUtc,
                            source.ExpiresAtUtc,
                            source.ContentSha256,
                            source.ProviderReference
                        })
                    }),
                    RejectReasonsJson = "[]"
                }, cancellationToken);
            }
        }

        var created = await queries.GetRunAsync(runId, cancellationToken)
            ?? throw new InvalidOperationException("Candidate run was not visible after durable creation.");
        return created;
    }

    private static void ValidateStrategyReference(StrategyReference strategy)
    {
        if (String.IsNullOrWhiteSpace(strategy.StrategyId) ||
            String.IsNullOrWhiteSpace(strategy.SemanticVersion) ||
            strategy.ContentSha256.Length != 64 ||
            !strategy.ContentSha256.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Every candidate strategy requires exact ID, semantic version and SHA-256 identity.");
        }
    }
}
