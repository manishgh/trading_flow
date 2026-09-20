using System.Text.Json;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Application.Candidates;

public interface ICandidateQueryService
{
    Task<CandidateRunResponse?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<CandidateStateResponse?> GetCandidateAsync(Guid candidateId, CancellationToken cancellationToken = default);
}

public interface IDecisionAuditQuery
{
    Task<CandidateAuditResponse?> GetAsync(Guid candidateId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Projects durable candidate truth into the transport-neutral API contract. It
/// does not reevaluate strategy rules, so web and Android cannot drift from the
/// decision kernel by calculating their own labels.
/// </summary>
public sealed class CandidateApplicationService(
    ICandidateRepository candidates,
    IGateEvaluationRepository gates,
    IOrderIntentRepository orders,
    ICandidateAuditEvidenceRepository evidence,
    IApplicationEventRepository events,
    TimeProvider timeProvider) : ICandidateQueryService, IDecisionAuditQuery
{
    public async Task<CandidateRunResponse?> GetRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty)
        {
            return null;
        }

        var run = await orders.GetRunAsync(runId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        var records = await candidates.ListByRunAsync(runId, cancellationToken);
        var openCandidateIds = await evidence.GetOpenPositionCandidateIdsAsync(runId, cancellationToken);
        var candidateDtos = records.Select(candidate => ProjectState(candidate, openCandidateIds.Contains(candidate.CandidateId))).ToArray();
        var strategyRefs = records
            .Select(ToStrategyReference)
            .Distinct()
            .ToArray();
        var universeSnapshotId = run.UniverseSnapshotId;
        var eventBounds = await events.GetBoundsAsync($"candidate-runs/{runId:N}", cancellationToken);
        return new CandidateRunResponse(
            ContractVersions.VersionOne,
            run.RunId,
            new RunProvenance(run.Profile, universeSnapshotId, run.DecisionRunId, strategyRefs),
            run.Status,
            run.StartedAtUtc,
            run.StartedAtUtc,
            run.FinishedAtUtc,
            eventBounds.Latest,
            candidateDtos);
    }

    public async Task<CandidateStateResponse?> GetCandidateAsync(
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        var candidate = await candidates.GetAsync(candidateId, cancellationToken);
        if (candidate is null)
        {
            return null;
        }

        var execution = await evidence.GetExecutionAsync(candidate, cancellationToken);
        return ProjectState(candidate, execution?.HasOpenPosition == true);
    }

    public async Task<CandidateAuditResponse?> GetAsync(
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        var candidate = await candidates.GetAsync(candidateId, cancellationToken);
        if (candidate is null)
        {
            return null;
        }

        var transitions = await candidates.GetTransitionsAsync(candidateId, cancellationToken);
        var gateRows = await gates.ListByCandidateAsync(candidateId, cancellationToken);
        var catalyst = await evidence.GetCatalystAsync(candidate, cancellationToken);
        var execution = await evidence.GetExecutionAsync(candidate, cancellationToken);
        var market = ReadMarketEvidence(candidate);
        return new CandidateAuditResponse(
            ContractVersions.VersionOne,
            ProjectState(candidate, execution?.HasOpenPosition == true),
            transitions.Select(transition => new CandidateTransitionResponse(
                transition.Sequence,
                transition.PreviousState.ToString(),
                transition.NewState.ToString(),
                transition.OccurredAtUtc,
                transition.ReasonCode,
                transition.Source,
                transition.SemanticDecisionSha256)).ToArray(),
            gateRows.Select(gate => new GateEvaluationResponse(
                gate.GateOrder,
                gate.GateName,
                gate.Passed,
                gate.RejectCode?.ToString(),
                gate.EvaluatedAtUtc,
                ReadStringMap(gate.InputsJson))).ToArray(),
            market,
            catalyst is null ? null : new CatalystEvidenceResponse(
                catalyst.CatalystResultId,
                catalyst.Provider,
                catalyst.ProviderArticleId,
                catalyst.ProviderPublishedAtUtc,
                catalyst.FirstReceivedAtUtc,
                catalyst.Category,
                catalyst.Direction,
                catalyst.CompositeScore,
                catalyst.Headline,
                catalyst.Url,
                catalyst.EvidenceSha256),
            execution is null ? null : new ExecutionEvidenceResponse(
                execution.OrderIntentId,
                execution.ClientOrderId,
                execution.BrokerOrderId,
                execution.State,
                execution.RequestedQuantity,
                execution.FilledQuantity,
                execution.AverageFillPrice,
                execution.UpdatedAtUtc,
                execution.RejectionCode),
            candidate.SemanticDecisionSha256,
            timeProvider.GetUtcNow());
    }

    public static CandidateStateResponse ProjectState(CandidateRecord candidate, bool hasOpenPosition) => new(
        candidate.CandidateId,
        candidate.Version,
        candidate.Symbol,
        ToStrategyReference(candidate),
        candidate.State.ToString(),
        ResolveReadiness(candidate.State, hasOpenPosition),
        candidate.State == StrategyCandidateState.Triggered ? "triggered" : null,
        candidate.DiscoveredAtUtc,
        candidate.RevalidatedAtUtc,
        candidate.ExpiresAtUtc,
        ReadLatestCompletedCandle(candidate.SetupScoresJson),
        candidate.SameTimeRvol,
        ReadInteger(candidate.SetupScoresJson, "relativeVolumeSampleCount"),
        ReadRejections(candidate),
        ReadSources(candidate.SetupScoresJson));

    private static StrategyReference ToStrategyReference(CandidateRecord candidate) => new(
        candidate.SelectedStrategy ?? String.Empty,
        candidate.StrategySemanticVersion,
        candidate.StrategyContentSha256);

    private static string ResolveReadiness(StrategyCandidateState state, bool hasOpenPosition) => state switch
    {
        StrategyCandidateState.Discovered => "watching",
        StrategyCandidateState.DataWarming => "warming",
        StrategyCandidateState.Qualified or
        StrategyCandidateState.Armed or
        StrategyCandidateState.Triggered => "eligible",
        StrategyCandidateState.Consumed when hasOpenPosition => "in_trade",
        StrategyCandidateState.Consumed => "consumed",
        StrategyCandidateState.Rejected or
        StrategyCandidateState.RiskBlocked or
        StrategyCandidateState.DataError => "rejected",
        StrategyCandidateState.Expired => "expired",
        _ => "watching"
    };

    private static MarketEvidenceResponse? ReadMarketEvidence(CandidateRecord candidate)
    {
        var latest = ReadLatestCompletedCandle(candidate.SetupScoresJson);
        if (latest is null && candidate.LastPrice is null && candidate.SpreadBps is null && candidate.SameTimeRvol is null)
        {
            return null;
        }

        var timeframe = ReadString(candidate.SetupScoresJson, "timeframe") ?? "unknown";
        var session = ReadString(candidate.SetupScoresJson, "session") ?? "unknown";
        var bid = ReadDecimal(candidate.SetupScoresJson, "bidPrice");
        var ask = ReadDecimal(candidate.SetupScoresJson, "askPrice");
        var asOf = candidate.RevalidatedAtUtc;
        var completed = latest ?? candidate.RevalidatedAtUtc;
        var payload = JsonSerializer.Serialize(new
        {
            asOf,
            completed,
            timeframe,
            session,
            bid,
            ask,
            candidate.LastPrice,
            candidate.SpreadBps,
            candidate.SameTimeRvol,
            samples = ReadInteger(candidate.SetupScoresJson, "relativeVolumeSampleCount")
        });
        return new MarketEvidenceResponse(
            asOf,
            completed,
            timeframe,
            session,
            bid,
            ask,
            candidate.LastPrice,
            candidate.SpreadBps,
            candidate.SameTimeRvol,
            ReadInteger(candidate.SetupScoresJson, "relativeVolumeSampleCount"),
            UniverseDiscoveryService.Sha256(payload));
    }

    private static DateTimeOffset? ReadLatestCompletedCandle(string json) =>
        DateTimeOffset.TryParse(ReadString(json, "latestCompletedCandleAtUtc"), out var value)
            ? value.ToUniversalTime()
            : null;

    private static string? ReadString(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(property, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static decimal? ReadDecimal(string json, string property) =>
        Decimal.TryParse(ReadStringOrNumber(json, property), out var value) ? value : null;

    private static int? ReadInteger(string json, string property) =>
        Int32.TryParse(ReadStringOrNumber(json, property), out var value) ? value : null;

    private static string? ReadStringOrNumber(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(property, out var value))
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<CandidateRejectionResponse> ReadRejections(CandidateRecord candidate)
    {
        if (String.IsNullOrWhiteSpace(candidate.RejectReasonsJson) || candidate.RejectReasonsJson == "[]")
        {
            return [];
        }

        try
        {
            var values = JsonSerializer.Deserialize<string[]>(candidate.RejectReasonsJson) ?? [];
            return values.Select(value => new CandidateRejectionResponse(
                value,
                value,
                candidate.RevalidatedAtUtc,
                new Dictionary<string, string?>())).ToArray();
        }
        catch (JsonException)
        {
            return [new CandidateRejectionResponse(
                "invalid_persisted_rejection_evidence",
                "Persisted rejection evidence could not be decoded.",
                candidate.RevalidatedAtUtc,
                new Dictionary<string, string?>())];
        }
    }

    private static IReadOnlyDictionary<string, string?> ReadStringMap(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string?> { ["value"] = document.RootElement.GetRawText() };
            }

            return document.RootElement.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.GetRawText(),
                StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string?> { ["evidence"] = "invalid_json" };
        }
    }

    private static IReadOnlyList<UniverseSourceEvidenceResponse> ReadSources(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("sources", out var sources) ||
                sources.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return sources.EnumerateArray().Select(source => new UniverseSourceEvidenceResponse(
                source.GetProperty("sourceKind").GetString() ?? String.Empty,
                source.GetProperty("sourceKey").GetString() ?? String.Empty,
                source.GetProperty("observedAtUtc").GetDateTimeOffset(),
                source.GetProperty("expiresAtUtc").GetDateTimeOffset(),
                source.GetProperty("contentSha256").GetString() ?? String.Empty,
                false,
                source.TryGetProperty("providerReference", out var reference) && reference.ValueKind == JsonValueKind.String
                    ? reference.GetString()
                    : null)).ToArray();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return [];
        }
    }
}
