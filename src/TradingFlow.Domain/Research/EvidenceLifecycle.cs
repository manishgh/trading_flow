using System.Collections.ObjectModel;

namespace TradingFlow.Domain.Research;

public enum EvidenceTransportKind
{
    Http = 1,
    WebSocket = 2,
    FileImport = 3,
    Broker = 4
}

public sealed class EvidenceSourceObservation
{
    public EvidenceSourceObservation(
        string observationId,
        string collectionJobId,
        string collectionPlanHash,
        string collectionAttemptHash,
        string ingestionRequestId,
        string pageIdentity,
        string provider,
        string endpoint,
        EvidenceTransportKind transportKind,
        int? transportStatusCode,
        IReadOnlyList<string> requestedSymbols,
        DateTimeOffset? requestedStartUtc,
        DateTimeOffset? requestedEndUtc,
        string dataFeed,
        string adjustment,
        string currency,
        DateOnly? asOfDate,
        string? providerRecordId,
        DateTimeOffset? providerCreatedAtUtc,
        DateTimeOffset? providerUpdatedAtUtc,
        DateTimeOffset receivedAtUtc,
        EvidenceArtifactReference artifact,
        string runId,
        string configHash,
        string codeVersion,
        IReadOnlyDictionary<string, string>? responseHeaders = null,
        IReadOnlyDictionary<string, string>? requestAttributes = null)
    {
        ObservationId = EvidenceValue.NormalizeRequired(observationId, nameof(observationId));
        CollectionJobId = EvidenceValue.NormalizeRequired(collectionJobId, nameof(collectionJobId));
        CollectionPlanHash = EvidenceValue.NormalizeSha256(collectionPlanHash);
        CollectionAttemptHash = EvidenceValue.NormalizeSha256(collectionAttemptHash);
        if (!CollectionJobId.Equals($"evidence-{CollectionAttemptHash[..24]}", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Collection job ID must be derived from the collection-attempt hash.",
                nameof(collectionJobId));
        }

        IngestionRequestId = EvidenceValue.NormalizeRequired(ingestionRequestId, nameof(ingestionRequestId));
        PageIdentity = EvidenceValue.NormalizeRequired(pageIdentity, nameof(pageIdentity));
        Provider = EvidenceValue.NormalizeRequired(provider, nameof(provider)).ToLowerInvariant();
        Endpoint = EvidenceValue.NormalizeNonSecretEndpoint(endpoint, nameof(endpoint));
        if (!Enum.IsDefined(transportKind))
        {
            throw new ArgumentOutOfRangeException(nameof(transportKind));
        }

        TransportKind = transportKind;
        if (transportStatusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(transportStatusCode));
        }

        TransportStatusCode = transportStatusCode;
        if (TransportKind == EvidenceTransportKind.Http && TransportStatusCode is null)
        {
            throw new ArgumentException(
                "HTTP observations require the actual response status.",
                nameof(transportStatusCode));
        }

        RequestedSymbols = new ReadOnlyCollection<string>(
            (requestedSymbols ?? throw new ArgumentNullException(nameof(requestedSymbols)))
            .Select(symbol => EvidenceValue.NormalizeRequired(symbol, nameof(requestedSymbols)).ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(symbol => symbol, StringComparer.Ordinal)
            .ToArray());
        EnsureOptionalRange(requestedStartUtc, requestedEndUtc);
        RequestedStartUtc = requestedStartUtc;
        RequestedEndUtc = requestedEndUtc;
        DataFeed = EvidenceValue.NormalizeRequired(dataFeed, nameof(dataFeed)).ToLowerInvariant();
        Adjustment = EvidenceValue.NormalizeRequired(adjustment, nameof(adjustment)).ToLowerInvariant();
        Currency = EvidenceValue.NormalizeRequired(currency, nameof(currency)).ToUpperInvariant();
        AsOfDate = asOfDate;
        ProviderRecordId = String.IsNullOrWhiteSpace(providerRecordId) ? null : providerRecordId.Trim();
        EnsureOptionalUtc(providerCreatedAtUtc, nameof(providerCreatedAtUtc));
        EnsureOptionalUtc(providerUpdatedAtUtc, nameof(providerUpdatedAtUtc));
        ProviderCreatedAtUtc = providerCreatedAtUtc;
        ProviderUpdatedAtUtc = providerUpdatedAtUtc;
        EvidenceValue.EnsureUtc(receivedAtUtc, nameof(receivedAtUtc));
        ReceivedAtUtc = receivedAtUtc;
        Artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
        RunId = EvidenceValue.NormalizeRequired(runId, nameof(runId));
        ConfigHash = EvidenceValue.NormalizeSha256(configHash);
        CodeVersion = EvidenceValue.NormalizeRequired(codeVersion, nameof(codeVersion));
        ResponseHeaders = EvidenceValue.CopySafeResponseHeaders(responseHeaders);
        RequestAttributes = EvidenceValue.CopyNonSecretMetadata(requestAttributes);
    }

    public string ObservationId { get; }

    public string CollectionJobId { get; }

    public string CollectionPlanHash { get; }

    public string CollectionAttemptHash { get; }

    public string IngestionRequestId { get; }

    public string PageIdentity { get; }

    public string Provider { get; }

    public string Endpoint { get; }

    public EvidenceTransportKind TransportKind { get; }

    public int? TransportStatusCode { get; }

    public IReadOnlyList<string> RequestedSymbols { get; }

    public DateTimeOffset? RequestedStartUtc { get; }

    public DateTimeOffset? RequestedEndUtc { get; }

    public string DataFeed { get; }

    public string Adjustment { get; }

    public string Currency { get; }

    public DateOnly? AsOfDate { get; }

    public string? ProviderRecordId { get; }

    public DateTimeOffset? ProviderCreatedAtUtc { get; }

    public DateTimeOffset? ProviderUpdatedAtUtc { get; }

    public DateTimeOffset ReceivedAtUtc { get; }

    public EvidenceArtifactReference Artifact { get; }

    public string RunId { get; }

    public string ConfigHash { get; }

    public string CodeVersion { get; }

    public IReadOnlyDictionary<string, string> ResponseHeaders { get; }

    public IReadOnlyDictionary<string, string> RequestAttributes { get; }

    public EvidenceSourceReference ToReference() =>
        new(ObservationId, Artifact, ReceivedAtUtc);

    private static void EnsureOptionalUtc(DateTimeOffset? value, string parameterName)
    {
        if (value is { } timestamp)
        {
            EvidenceValue.EnsureUtc(timestamp, parameterName);
        }
    }

    private static void EnsureOptionalRange(
        DateTimeOffset? requestedStartUtc,
        DateTimeOffset? requestedEndUtc)
    {
        if (requestedStartUtc.HasValue != requestedEndUtc.HasValue)
        {
            throw new ArgumentException("Requested start and end must either both be present or both be absent.");
        }

        EnsureOptionalUtc(requestedStartUtc, nameof(requestedStartUtc));
        EnsureOptionalUtc(requestedEndUtc, nameof(requestedEndUtc));
        if (requestedStartUtc is { } start && requestedEndUtc is { } end && end <= start)
        {
            throw new ArgumentException("Requested end must follow requested start.");
        }
    }
}

/// <summary>
/// Required metadata carried by each normalized evidence record.
/// </summary>
public sealed class EvidenceRowProvenance
{
    public EvidenceRowProvenance(
        int schemaVersion,
        string runId,
        string configHash,
        string codeVersion,
        string dataFeed,
        DateTimeOffset sourceTimestampUtc,
        DateTimeOffset receivedAtUtc,
        EvidenceRowLineage lineage)
    {
        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        SchemaVersion = schemaVersion;
        RunId = EvidenceValue.NormalizeRequired(runId, nameof(runId));
        ConfigHash = EvidenceValue.NormalizeSha256(configHash);
        CodeVersion = EvidenceValue.NormalizeRequired(codeVersion, nameof(codeVersion));
        DataFeed = EvidenceValue.NormalizeRequired(dataFeed, nameof(dataFeed)).ToLowerInvariant();
        EvidenceValue.EnsureUtc(sourceTimestampUtc, nameof(sourceTimestampUtc));
        EvidenceValue.EnsureUtc(receivedAtUtc, nameof(receivedAtUtc));
        SourceTimestampUtc = sourceTimestampUtc;
        ReceivedAtUtc = receivedAtUtc;
        Lineage = lineage ?? throw new ArgumentNullException(nameof(lineage));
    }

    public int SchemaVersion { get; }

    public string RunId { get; }

    public string ConfigHash { get; }

    public string CodeVersion { get; }

    public string DataFeed { get; }

    public DateTimeOffset SourceTimestampUtc { get; }

    public DateTimeOffset ReceivedAtUtc { get; }

    public EvidenceRowLineage Lineage { get; }
}

public enum EvidenceCollectionState
{
    Planned = 1,
    Collecting = 2,
    Normalizing = 3,
    Validating = 4,
    Committed = 5,
    Incomplete = 6,
    Quarantined = 7,
    Cancelled = 8
}

public sealed record EvidenceCollectionRequest
{
    public EvidenceCollectionRequest(
        string requestId,
        string provider,
        string endpoint,
        IReadOnlyList<string> symbols,
        DateTimeOffset? requestedStartUtc,
        DateTimeOffset? requestedEndUtc,
        string dataFeed,
        string adjustment,
        string currency,
        DateOnly? asOfDate,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        RequestId = EvidenceValue.NormalizeRequired(requestId, nameof(requestId));
        Provider = EvidenceValue.NormalizeRequired(provider, nameof(provider)).ToLowerInvariant();
        Endpoint = EvidenceValue.NormalizeNonSecretEndpoint(endpoint, nameof(endpoint));
        Symbols = new ReadOnlyCollection<string>(
            (symbols ?? throw new ArgumentNullException(nameof(symbols)))
            .Select(symbol => EvidenceValue.NormalizeRequired(symbol, nameof(symbols)).ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(symbol => symbol, StringComparer.Ordinal)
            .ToArray());
        if (requestedStartUtc.HasValue != requestedEndUtc.HasValue)
        {
            throw new ArgumentException("Requested start and end must both be present or both be absent.");
        }

        if (requestedStartUtc is { } start)
        {
            EvidenceValue.EnsureUtc(start, nameof(requestedStartUtc));
            EvidenceValue.EnsureUtc(requestedEndUtc!.Value, nameof(requestedEndUtc));
            if (requestedEndUtc.Value <= start)
            {
                throw new ArgumentException("Requested end must follow requested start.");
            }
        }

        RequestedStartUtc = requestedStartUtc;
        RequestedEndUtc = requestedEndUtc;
        DataFeed = EvidenceValue.NormalizeRequired(dataFeed, nameof(dataFeed)).ToLowerInvariant();
        Adjustment = EvidenceValue.NormalizeRequired(adjustment, nameof(adjustment)).ToLowerInvariant();
        Currency = EvidenceValue.NormalizeRequired(currency, nameof(currency)).ToUpperInvariant();
        AsOfDate = asOfDate;
        Parameters = EvidenceValue.CopyNonSecretMetadata(parameters);
    }

    public string RequestId { get; }

    public string Provider { get; }

    public string Endpoint { get; }

    public IReadOnlyList<string> Symbols { get; }

    public DateTimeOffset? RequestedStartUtc { get; }

    public DateTimeOffset? RequestedEndUtc { get; }

    public string DataFeed { get; }

    public string Adjustment { get; }

    public string Currency { get; }

    public DateOnly? AsOfDate { get; }

    public IReadOnlyDictionary<string, string> Parameters { get; }
}

public sealed class EvidenceCollectionPlan
{
    public EvidenceCollectionPlan(
        DateTimeOffset createdAtUtc,
        string runId,
        string configHash,
        string codeVersion,
        string collectionPolicyVersion,
        string partitionPolicyVersion,
        IReadOnlyList<EvidenceCollectionRequest> requests)
    {
        EvidenceValue.EnsureUtc(createdAtUtc, nameof(createdAtUtc));
        CreatedAtUtc = createdAtUtc;
        RunId = EvidenceValue.NormalizeRequired(runId, nameof(runId));
        ConfigHash = EvidenceValue.NormalizeSha256(configHash);
        CodeVersion = EvidenceValue.NormalizeRequired(codeVersion, nameof(codeVersion));
        CollectionPolicyVersion = EvidenceValue.NormalizeRequired(
            collectionPolicyVersion,
            nameof(collectionPolicyVersion));
        PartitionPolicyVersion = EvidenceValue.NormalizeRequired(
            partitionPolicyVersion,
            nameof(partitionPolicyVersion));
        var requestArray = (requests ?? throw new ArgumentNullException(nameof(requests)))
            .Select(request => request ?? throw new ArgumentException("Requests cannot contain null.", nameof(requests)))
            .OrderBy(request => request.RequestId, StringComparer.Ordinal)
            .ToArray();
        if (requestArray.Length == 0)
        {
            throw new ArgumentException("A collection plan requires at least one request.", nameof(requests));
        }

        if (requestArray.Select(request => request.RequestId).Distinct(StringComparer.Ordinal).Count() != requestArray.Length)
        {
            throw new ArgumentException("Collection request identifiers must be unique.", nameof(requests));
        }

        Requests = new ReadOnlyCollection<EvidenceCollectionRequest>(requestArray);
        LogicalPlanHash = EvidenceCanonicalJson.ComputeSha256(new
        {
            ConfigHash,
            CodeVersion,
            CollectionPolicyVersion,
            PartitionPolicyVersion,
            Requests
        });
        CollectionAttemptHash = EvidenceCanonicalJson.ComputeSha256(new
        {
            LogicalPlanHash,
            CreatedAtUtc,
            RunId
        });
        JobId = $"evidence-{CollectionAttemptHash[..24]}";
    }

    public string JobId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public string RunId { get; }

    public string ConfigHash { get; }

    public string CodeVersion { get; }

    public string CollectionPolicyVersion { get; }

    public string PartitionPolicyVersion { get; }

    public IReadOnlyList<EvidenceCollectionRequest> Requests { get; }

    public string LogicalPlanHash { get; }

    public string CollectionAttemptHash { get; }
}

public static class EvidenceCollectionTransitions
{
    private static readonly IReadOnlyDictionary<EvidenceCollectionState, IReadOnlySet<EvidenceCollectionState>>
        Allowed = new ReadOnlyDictionary<EvidenceCollectionState, IReadOnlySet<EvidenceCollectionState>>(
            new Dictionary<EvidenceCollectionState, IReadOnlySet<EvidenceCollectionState>>
            {
                [EvidenceCollectionState.Planned] = NewSet(
                    EvidenceCollectionState.Collecting,
                    EvidenceCollectionState.Cancelled),
                [EvidenceCollectionState.Collecting] = NewSet(
                    EvidenceCollectionState.Normalizing,
                    EvidenceCollectionState.Incomplete,
                    EvidenceCollectionState.Quarantined,
                    EvidenceCollectionState.Cancelled),
                [EvidenceCollectionState.Normalizing] = NewSet(
                    EvidenceCollectionState.Validating,
                    EvidenceCollectionState.Incomplete,
                    EvidenceCollectionState.Quarantined,
                    EvidenceCollectionState.Cancelled),
                [EvidenceCollectionState.Validating] = NewSet(
                    EvidenceCollectionState.Committed,
                    EvidenceCollectionState.Incomplete,
                    EvidenceCollectionState.Quarantined,
                    EvidenceCollectionState.Cancelled),
                [EvidenceCollectionState.Incomplete] = NewSet(
                    EvidenceCollectionState.Collecting,
                    EvidenceCollectionState.Quarantined,
                    EvidenceCollectionState.Cancelled),
                [EvidenceCollectionState.Committed] = NewSet(),
                [EvidenceCollectionState.Quarantined] = NewSet(),
                [EvidenceCollectionState.Cancelled] = NewSet()
            });

    public static bool CanTransition(EvidenceCollectionState current, EvidenceCollectionState next) =>
        Enum.IsDefined(current) && Enum.IsDefined(next) && Allowed[current].Contains(next);

    public static void EnsureAllowed(EvidenceCollectionState current, EvidenceCollectionState next)
    {
        if (!CanTransition(current, next))
        {
            throw new InvalidOperationException(
                $"Evidence collection cannot transition from {current} to {next}.");
        }
    }

    private static IReadOnlySet<EvidenceCollectionState> NewSet(
        params EvidenceCollectionState[] values) =>
        new HashSet<EvidenceCollectionState>(values);
}

public sealed record EvidenceCollectionCheckpoint
{
    public EvidenceCollectionCheckpoint(
        string jobId,
        EvidenceCollectionState state,
        DateTimeOffset updatedAtUtc,
        IReadOnlyList<string>? completedRequestIds = null,
        string? failureReason = null)
    {
        JobId = EvidenceValue.NormalizeRequired(jobId, nameof(jobId));
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        State = state;
        EvidenceValue.EnsureUtc(updatedAtUtc, nameof(updatedAtUtc));
        UpdatedAtUtc = updatedAtUtc;
        CompletedRequestIds = new ReadOnlyCollection<string>(
            (completedRequestIds ?? Array.Empty<string>())
            .Select(requestId => EvidenceValue.NormalizeRequired(requestId, nameof(completedRequestIds)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(requestId => requestId, StringComparer.Ordinal)
            .ToArray());
        FailureReason = String.IsNullOrWhiteSpace(failureReason) ? null : failureReason.Trim();

        if (state is EvidenceCollectionState.Incomplete or EvidenceCollectionState.Quarantined &&
            FailureReason is null)
        {
            throw new ArgumentException("Incomplete and quarantined checkpoints require a reason.", nameof(failureReason));
        }
    }

    public string JobId { get; }

    public EvidenceCollectionState State { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public IReadOnlyList<string> CompletedRequestIds { get; }

    public string? FailureReason { get; }
}

public sealed record EvidenceRequestCursorCheckpoint
{
    public EvidenceRequestCursorCheckpoint(
        string jobId,
        string requestId,
        int pageOrdinal,
        string? nextPageToken,
        IReadOnlyList<string>? consumedPageTokenHashes,
        string observationId,
        int attemptCount,
        bool exhausted,
        DateTimeOffset updatedAtUtc)
    {
        JobId = EvidenceValue.NormalizeRequired(jobId, nameof(jobId));
        RequestId = EvidenceValue.NormalizeRequired(requestId, nameof(requestId));
        if (pageOrdinal <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageOrdinal));
        }

        PageOrdinal = pageOrdinal;
        NextPageToken = String.IsNullOrWhiteSpace(nextPageToken) ? null : nextPageToken.Trim();
        ConsumedPageTokenHashes = new ReadOnlyCollection<string>(
            (consumedPageTokenHashes ?? Array.Empty<string>())
            .Select(EvidenceValue.NormalizeSha256)
            .ToArray());
        if (ConsumedPageTokenHashes.Distinct(StringComparer.Ordinal).Count() != ConsumedPageTokenHashes.Count)
        {
            throw new ArgumentException(
                "Consumed page-token history cannot contain a cycle.",
                nameof(consumedPageTokenHashes));
        }
        if (ConsumedPageTokenHashes.Count != PageOrdinal - 1)
        {
            throw new ArgumentException(
                "Page one consumes no cursor token; every later page must add exactly one consumed token.",
                nameof(consumedPageTokenHashes));
        }

        if (NextPageToken is not null &&
            ConsumedPageTokenHashes.Contains(
                EvidenceValue.ComputeSha256(NextPageToken),
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Next-page token has already been consumed.",
                nameof(nextPageToken));
        }

        TokenHistoryHash = EvidenceValue.ComputeSha256(
            String.Join("\n", ConsumedPageTokenHashes));
        ObservationId = EvidenceValue.NormalizeRequired(observationId, nameof(observationId));
        if (attemptCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptCount));
        }

        AttemptCount = attemptCount;
        Exhausted = exhausted;
        if (Exhausted && NextPageToken is not null)
        {
            throw new ArgumentException("An exhausted cursor cannot retain a next-page token.");
        }

        EvidenceValue.EnsureUtc(updatedAtUtc, nameof(updatedAtUtc));
        UpdatedAtUtc = updatedAtUtc;
    }

    public string JobId { get; }

    public string RequestId { get; }

    public int PageOrdinal { get; }

    public string? NextPageToken { get; }

    public IReadOnlyList<string> ConsumedPageTokenHashes { get; }

    public string TokenHistoryHash { get; }

    public string ObservationId { get; }

    public int AttemptCount { get; }

    public bool Exhausted { get; }

    public DateTimeOffset UpdatedAtUtc { get; }
}

public enum EvidenceQuarantineScope
{
    Observation = 1,
    Row = 2,
    Partition = 3,
    Dataset = 4,
    ResearchRun = 5
}

public enum EvidenceQuarantineStatus
{
    Open = 1,
    RetryScheduled = 2,
    Resolved = 3,
    Terminal = 4
}

public sealed record EvidenceQuarantineRecord
{
    public EvidenceQuarantineRecord(
        string quarantineId,
        EvidenceQuarantineScope scope,
        string reasonCode,
        string logicalKey,
        EvidenceRowLineage lineage,
        EvidenceArtifactReference diagnosticArtifact,
        bool retryable,
        DateTimeOffset detectedAtUtc)
    {
        QuarantineId = EvidenceValue.NormalizeRequired(quarantineId, nameof(quarantineId));
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        Scope = scope;
        ReasonCode = EvidenceValue.NormalizeRequired(reasonCode, nameof(reasonCode));
        LogicalKey = EvidenceValue.NormalizeRequired(logicalKey, nameof(logicalKey));
        Lineage = lineage ?? throw new ArgumentNullException(nameof(lineage));
        DiagnosticArtifact = diagnosticArtifact ?? throw new ArgumentNullException(nameof(diagnosticArtifact));
        Retryable = retryable;
        EvidenceValue.EnsureUtc(detectedAtUtc, nameof(detectedAtUtc));
        DetectedAtUtc = detectedAtUtc;
    }

    public string QuarantineId { get; }

    public EvidenceQuarantineScope Scope { get; }

    public string ReasonCode { get; }

    public string LogicalKey { get; }

    public EvidenceRowLineage Lineage { get; }

    public EvidenceArtifactReference DiagnosticArtifact { get; }

    public bool Retryable { get; }

    public DateTimeOffset DetectedAtUtc { get; }
}

public sealed record EvidenceQuarantineResolution
{
    public EvidenceQuarantineResolution(
        string quarantineId,
        EvidenceQuarantineStatus status,
        string decidedBy,
        string reason,
        DateTimeOffset decidedAtUtc)
    {
        QuarantineId = EvidenceValue.NormalizeRequired(quarantineId, nameof(quarantineId));
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Status = status;
        DecidedBy = EvidenceValue.NormalizeRequired(decidedBy, nameof(decidedBy));
        Reason = EvidenceValue.NormalizeRequired(reason, nameof(reason));
        EvidenceValue.EnsureUtc(decidedAtUtc, nameof(decidedAtUtc));
        DecidedAtUtc = decidedAtUtc;
        DecisionId = $"quarantine-decision-{EvidenceCanonicalJson.ComputeSha256(new
        {
            QuarantineId,
            Status,
            DecidedBy,
            Reason,
            DecidedAtUtc
        })[..24]}";
    }

    public string DecisionId { get; }

    public string QuarantineId { get; }

    public EvidenceQuarantineStatus Status { get; }

    public string DecidedBy { get; }

    public string Reason { get; }

    public DateTimeOffset DecidedAtUtc { get; }
}

public static class EvidenceQuarantineTransitions
{
    public static bool CanTransition(
        EvidenceQuarantineStatus current,
        EvidenceQuarantineStatus next) =>
        (current, next) switch
        {
            (EvidenceQuarantineStatus.Open, EvidenceQuarantineStatus.RetryScheduled) => true,
            (EvidenceQuarantineStatus.Open, EvidenceQuarantineStatus.Resolved) => true,
            (EvidenceQuarantineStatus.Open, EvidenceQuarantineStatus.Terminal) => true,
            (EvidenceQuarantineStatus.RetryScheduled, EvidenceQuarantineStatus.Open) => true,
            (EvidenceQuarantineStatus.RetryScheduled, EvidenceQuarantineStatus.Resolved) => true,
            (EvidenceQuarantineStatus.RetryScheduled, EvidenceQuarantineStatus.Terminal) => true,
            _ => false
        };

    public static void EnsureAllowed(
        EvidenceQuarantineStatus current,
        EvidenceQuarantineStatus next)
    {
        if (!CanTransition(current, next))
        {
            throw new InvalidOperationException(
                $"Evidence quarantine cannot transition from {current} to {next}.");
        }
    }
}

public sealed class EvidenceQuarantineEntry
{
    public EvidenceQuarantineEntry(
        EvidenceQuarantineRecord record,
        EvidenceQuarantineStatus status,
        IReadOnlyList<EvidenceQuarantineResolution>? decisions)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Status = status;
        Decisions = new ReadOnlyCollection<EvidenceQuarantineResolution>(
            (decisions ?? Array.Empty<EvidenceQuarantineResolution>())
            .Select(decision => decision
                ?? throw new ArgumentException("Quarantine decisions cannot contain null.", nameof(decisions)))
            .ToArray());
        var current = EvidenceQuarantineStatus.Open;
        foreach (var decision in Decisions)
        {
            if (!decision.QuarantineId.Equals(Record.QuarantineId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Quarantine decision belongs to a different record.",
                    nameof(decisions));
            }

            EvidenceQuarantineTransitions.EnsureAllowed(current, decision.Status);
            current = decision.Status;
        }

        if (current != Status)
        {
            throw new ArgumentException(
                "Quarantine status does not match its decision history.",
                nameof(status));
        }
    }

    public EvidenceQuarantineRecord Record { get; }

    public EvidenceQuarantineStatus Status { get; }

    public IReadOnlyList<EvidenceQuarantineResolution> Decisions { get; }
}
