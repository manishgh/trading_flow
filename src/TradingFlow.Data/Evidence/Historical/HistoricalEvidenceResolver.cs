using System.Collections.Immutable;

namespace TradingFlow.Data.Evidence.Historical;

/// <summary>Resolves evidence availability only. It does not classify news or qualify a trade.</summary>
public static class HistoricalEvidenceResolver
{
    public static HistoricalAvailabilityResolution Resolve(HistoricalEvidenceManifest manifest,
        HistoricalFactVersion fact, DateTimeOffset decisionAtUtc,
        HistoricalAvailabilityMode mode = HistoricalAvailabilityMode.StrictOriginalDelivery,
        HistoricalReconstructionPolicy? policy = null)
    {
        HistoricalEvidenceManifestStore.RequireUtc(decisionAtUtc);
        HistoricalEvidenceManifestStore.Validate(manifest);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!manifest.Facts.Contains(fact)) throw new InvalidDataException("Fact is not part of the supplied manifest.");
        return ResolveBound(manifest, fact, decisionAtUtc, mode, policy);
    }

    internal static HistoricalAvailabilityResolution ResolveBound(HistoricalEvidenceManifest manifest,
        HistoricalFactVersion fact, DateTimeOffset decision, HistoricalAvailabilityMode mode,
        HistoricalReconstructionPolicy? policy, IReadOnlySet<string>? originalSourceIds = null, string? policyHash = null)
    {
        HistoricalAvailabilityResolution Result(bool available, string status, string reason,
            DateTimeOffset? at = null, string? contract = null) =>
            new(fact.FactId, mode, available, status, reason, at, fact.ActualReceivedAtUtc, contract,
                contract is null ? null : policyHash ?? PolicySha256(policy ?? HistoricalReconstructionPolicy.ConservativeV1));
        if (fact.QualityReason is not null) return Result(false, "unknown", fact.QualityReason);
        if (fact.Basis == HistoricalEvidenceBasis.CurrentSnapshot)
            return Result(false, "current_only", "current_snapshot_does_not_establish_historical_membership_or_identity");
        if (mode == HistoricalAvailabilityMode.StrictOriginalDelivery)
        {
            originalSourceIds ??= manifest.Observations.Where(source => source.OriginalDeliveryObserved &&
                !String.IsNullOrWhiteSpace(source.OriginalDeliveryEvidence)).Select(source => source.ObservationId).ToHashSet(StringComparer.Ordinal);
            if (fact.SourceObservationIds.IsDefaultOrEmpty || fact.SourceObservationIds.Any(id => !originalSourceIds.Contains(id)))
                return Result(false, "unknown", "original_delivery_and_revision_lineage_not_proven");
            return Result(fact.ActualReceivedAtUtc <= decision, "observed_original_delivery",
                fact.ActualReceivedAtUtc <= decision ? "original_delivery_available" : "original_delivery_after_decision", fact.ActualReceivedAtUtc);
        }
        if (mode == HistoricalAvailabilityMode.ObservedCapture)
            return Result(fact.ActualReceivedAtUtc <= decision, "observed_capture",
                fact.ActualReceivedAtUtc <= decision ? "captured_version_available_since_actual_receipt_only" : "actual_receipt_after_decision", fact.ActualReceivedAtUtc);

        policy ??= HistoricalReconstructionPolicy.ConservativeV1;
        if (String.IsNullOrWhiteSpace(policy.ContractId) || policy.ProviderUpdateLag < TimeSpan.Zero ||
            policy.ProcessingAllowance < TimeSpan.Zero || policy.OfficialArchiveLag < TimeSpan.Zero)
            throw new InvalidDataException("Reconstruction requires an identified nonnegative frozen delay policy.");
        DateTimeOffset threshold;
        if (mode == HistoricalAvailabilityMode.ConservativeProviderUpdateDiagnostic)
        {
            if (fact.Kind != HistoricalFactKind.NewsVersion || fact.Basis != HistoricalEvidenceBasis.CapturedProviderVersion)
                return Result(false, "unknown", "provider_update_reconstruction_is_news_only");
            if (fact.PublishedAtUtc is not { } published || fact.UpdatedAtUtc is not { } updated || updated < published)
                return Result(false, "unknown", "news_publication_or_update_time_unverified");
            threshold = updated.Add(policy.ProviderUpdateLag).Add(policy.ProcessingAllowance);
        }
        else
        {
            if (fact.Kind != HistoricalFactKind.OfficialFilingAcceptance || fact.Basis != HistoricalEvidenceBasis.OfficialAcceptedArchive ||
                fact.PublishedAtUtc is not { } accepted)
                return Result(false, "unknown", "official_archive_acceptance_not_proven");
            threshold = accepted.Add(policy.OfficialArchiveLag).Add(policy.ProcessingAllowance);
        }
        if (manifest.CalendarObservationId is null || manifest.Sessions.IsDefaultOrEmpty)
            return Result(false, "unknown", "independent_exchange_calendar_unavailable");
        var left = 0;
        var right = manifest.Sessions.Length;
        while (left < right)
        {
            var middle = left + (right - left) / 2;
            if (manifest.Sessions[middle].OpenUtc <= threshold) left = middle + 1;
            else right = middle;
        }
        var next = left < manifest.Sessions.Length ? manifest.Sessions[left] : null;
        if (next is null) return Result(false, "unknown", "next_session_outside_captured_calendar");
        return Result(next.OpenUtc <= decision, "reconstructed_diagnostic",
            next.OpenUtc <= decision ? "explicit_simulation_assumption_not_original_delivery" : "reconstructed_availability_after_decision",
            next.OpenUtc, policy.ContractId);
    }

    public static string PolicySha256(HistoricalReconstructionPolicy policy) =>
        HistoricalEvidenceManifestStore.Hash(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(policy));
}

public static class HistoricalAvailabilityAudit
{
    public static ImmutableArray<HistoricalAvailabilityAuditRow> Build(HistoricalEvidenceManifest manifest,
        IEnumerable<DateTimeOffset> decisions, HistoricalAvailabilityMode mode,
        HistoricalReconstructionPolicy? policy = null)
    {
        HistoricalEvidenceManifestStore.Validate(manifest);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var rows = ImmutableArray.CreateBuilder<HistoricalAvailabilityAuditRow>();
        var originalSources = manifest.Observations.Where(source => source.OriginalDeliveryObserved &&
            !String.IsNullOrWhiteSpace(source.OriginalDeliveryEvidence)).Select(source => source.ObservationId).ToHashSet(StringComparer.Ordinal);
        var policyHash = mode is HistoricalAvailabilityMode.ConservativeProviderUpdateDiagnostic or HistoricalAvailabilityMode.OfficialArchivePublicationDiagnostic
            ? HistoricalEvidenceResolver.PolicySha256(policy ?? HistoricalReconstructionPolicy.ConservativeV1) : null;
        foreach (var decision in decisions.Distinct().Order())
        {
            HistoricalEvidenceManifestStore.RequireUtc(decision);
            var counts = new Dictionary<(HistoricalFactKind Kind, string Status, string Reason), int>();
            foreach (var fact in manifest.Facts)
            {
                var result = HistoricalEvidenceResolver.ResolveBound(manifest, fact, decision, mode, policy, originalSources, policyHash);
                var key = (fact.Kind, result.Status, result.Reason);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
            foreach (var item in counts.OrderBy(pair => pair.Key.Kind).ThenBy(pair => pair.Key.Status, StringComparer.Ordinal)
                         .ThenBy(pair => pair.Key.Reason, StringComparer.Ordinal))
                rows.Add(new(decision, item.Key.Kind, mode, item.Key.Status, item.Key.Reason, item.Value, policyHash));
        }
        return rows.ToImmutable();
    }
}
