using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace TradingFlow.Data.Evidence.Historical;

/// <summary>Packages retained archives offline; incomplete pagination and undocumented query clocks stay unknown.</summary>
public static class HistoricalDiagnosticNewsManifestBuilder
{
    private sealed record Page(HistoricalSourceObservation Source, string Provider, string QueryKey,
        ImmutableArray<string> Symbols, DateTimeOffset? Start, DateTimeOffset? End, string? Token, string? Next,
        string? Failure);

    /// <summary>Freezes exact archived revisions and query proofs using the existing historical availability resolver.</summary>
    public static HistoricalDiagnosticNewsManifest Build(string researchRoot,
        IEnumerable<HistoricalEvidenceManifest> archives, HistoricalDiagnosticQuerySemantics? querySemantics = null,
        DateTimeOffset? builtAtUtc = null, IEnumerable<string>? selectedSymbols = null)
    {
        if (!Path.IsPathFullyQualified(researchRoot)) throw new InvalidDataException("Diagnostic research root must be absolute.");
        var symbols = (selectedSymbols ?? []).Select(item => item.Trim().ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal).ToImmutableArray();
        var scopedArchives = ScopeArticles(archives.ToArray(), symbols);
        var rows = scopedArchives.Select(archive => {
            HistoricalEvidenceManifestStore.Validate(archive);
            var relative = Path.GetRelativePath(researchRoot, archive.CollectionRoot);
            // BoundPath also rejects junctions and path traversal before reading retained sources.
            HistoricalEvidenceManifestStore.BoundPath(researchRoot, Path.Combine(relative, "diagnostic-path-check"));
            return new HistoricalDiagnosticNewsArchive(relative, archive);
        }).ToImmutableArray();
        var policy = HistoricalReconstructionPolicy.ConservativeV1;
        VerifySemantics(researchRoot, querySemantics);
        return new(HistoricalDiagnosticNewsManifest.CurrentSchema, builtAtUtc ?? DateTimeOffset.UtcNow, Path.GetFullPath(researchRoot),
            policy.ContractId, HistoricalEvidenceResolver.PolicySha256(policy), rows, querySemantics,
            BuildCoverage(rows, querySemantics), BuildRevisions(rows), symbols);
    }

    /// <summary>Limits package size to selected articles, retaining every revision including later membership removals and unscoped defects.</summary>
    internal static IEnumerable<HistoricalEvidenceManifest> ScopeArticles(HistoricalEvidenceManifest[] archives, ImmutableArray<string> symbols)
    {
        if (symbols.IsEmpty) return archives;
        var relevant = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in archives.SelectMany(item => item.Facts).Where(item => item.Kind == HistoricalFactKind.NewsVersion))
        {
            using var value = JsonDocument.Parse(fact.ValueJson);
            var tags = value.RootElement.TryGetProperty("symbols", out var field) && field.ValueKind == JsonValueKind.Array
                ? field.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String && !String.IsNullOrWhiteSpace(item.GetString()))
                    .Select(item => item.GetString()!.Trim().ToUpperInvariant()).ToArray() : [];
            if (tags.Any(symbols.Contains) || tags.Length == 0 && fact.QualityReason is not null) relevant.Add(fact.SourceRecordId);
        }
        return archives.Select(archive => archive with { Facts = archive.Facts.Where(fact =>
            fact.Kind == HistoricalFactKind.NewsVersion && relevant.Contains(fact.SourceRecordId)).ToImmutableArray() });
    }

    /// <summary>Retains every revision's separately assumed clock, including explicit unresolved source defects.</summary>
    internal static ImmutableArray<HistoricalDiagnosticNewsRevision> BuildRevisions(ImmutableArray<HistoricalDiagnosticNewsArchive> archives)
    {
        var revisions = ImmutableArray.CreateBuilder<HistoricalDiagnosticNewsRevision>();
        var calendar = SelectCalendar(archives);
        for (var index = 0; index < archives.Length; index++)
        {
            var manifest = archives[index].Evidence;
            var observations = manifest.Observations.ToDictionary(item => item.ObservationId, StringComparer.Ordinal);
            foreach (var fact in manifest.Facts.Where(item => item.Kind == HistoricalFactKind.NewsVersion))
            {
                var providers = fact.SourceObservationIds.Select(id => ProviderIdentity(observations[id].Provider)).Distinct(StringComparer.Ordinal).ToArray();
                var resolution = HistoricalEvidenceResolver.ResolveBound(calendar ?? manifest, fact, manifest.BuiltAtUtc,
                    HistoricalAvailabilityMode.ConservativeProviderUpdateDiagnostic, HistoricalReconstructionPolicy.ConservativeV1);
                // A defective revision with valid clocks can affect only decisions after its assumed arrival.
                // Missing/inverted clocks cannot be scoped safely and remain unavailable for the frozen package.
                var scoped = resolution;
                if (fact.QualityReason is not null && fact.PublishedAtUtc is { } published &&
                    fact.UpdatedAtUtc is { } updated && updated >= published)
                    scoped = HistoricalEvidenceResolver.ResolveBound(calendar ?? manifest, fact with { QualityReason = null }, manifest.BuiltAtUtc,
                        HistoricalAvailabilityMode.ConservativeProviderUpdateDiagnostic, HistoricalReconstructionPolicy.ConservativeV1);
                var reason = providers.Length != 1 ? "news_provider_identity_ambiguous" : fact.QualityReason ??
                    (resolution.AvailableAtUtc is null ? resolution.Reason : null);
                revisions.Add(new(index, fact.FactId, providers.FirstOrDefault() ?? "unknown", scoped.AvailableAtUtc, reason));
            }
        }
        return revisions.OrderBy(item => item.ArchiveIndex).ThenBy(item => item.FactId, StringComparer.Ordinal).ToImmutableArray();
    }

    /// <summary>Uses the latest retained calendar capture for every archive, rejecting contradictory overlapping sessions.</summary>
    private static HistoricalEvidenceManifest? SelectCalendar(ImmutableArray<HistoricalDiagnosticNewsArchive> archives)
    {
        var calendars = archives.Select(item => item.Evidence).Where(item => item.CalendarObservationId is not null && !item.Sessions.IsEmpty).ToArray();
        if (calendars.SelectMany(item => item.Sessions).GroupBy(item => item.ExchangeDate).Any(group => group.Distinct().Count() > 1))
            throw new InvalidDataException("Captured calendars disagree on an overlapping exchange session.");
        return calendars.OrderByDescending(item => item.Observations.Single(source => source.ObservationId == item.CalendarObservationId).CapturedAtUtc).FirstOrDefault();
    }

    /// <summary>Both legacy and catalog labels identify the same Alpaca article namespace.</summary>
    private static string ProviderIdentity(string provider) => provider is "alpaca-data" or "alpaca" ? "alpaca" : provider;

    /// <summary>Reconstructs token chains from exact receipt parameters; a page cap never proves absence.</summary>
    internal static ImmutableArray<HistoricalDiagnosticNewsCoverage> BuildCoverage(ImmutableArray<HistoricalDiagnosticNewsArchive> archives,
        HistoricalDiagnosticQuerySemantics? semantics)
    {
        var output = ImmutableArray.CreateBuilder<HistoricalDiagnosticNewsCoverage>();
        foreach (var archive in archives)
        {
            var pages = archive.Evidence.Observations.Where(source => source.Resource == "/v1beta1/news")
                .Select(source => ReadPage(archive.Evidence.CollectionRoot, source)).ToArray();
            foreach (var group in pages.GroupBy(page => (page.Provider, page.QueryKey)))
            {
                var first = group.First();
                var reason = semantics is null ? "provider_query_timestamp_semantics_unproved" : VerifyChain(group.ToArray());
                output.Add(new(first.Provider, first.Symbols, first.Start, first.End, reason is null, reason,
                    group.Select(page => page.Source.ObservationId).Distinct().Order(StringComparer.Ordinal).ToImmutableArray()));
            }
        }
        return output.ToImmutable();
    }

    /// <summary>Reads provider response and query identities without trusting a precomputed success flag.</summary>
    private static Page ReadPage(string root, HistoricalSourceObservation source)
    {
        var receiptBytes = HistoricalEvidenceManifestStore.ReadBound(root, source.ReceiptRelativePath);
        var payloadBytes = HistoricalEvidenceManifestStore.ReadBound(root, source.PayloadRelativePath);
        if (HistoricalEvidenceManifestStore.Hash(receiptBytes) != source.ReceiptSha256 ||
            HistoricalEvidenceManifestStore.Hash(payloadBytes) != source.PayloadSha256)
            throw new InvalidDataException("Diagnostic query source bytes changed.");
        using var receipt = JsonDocument.Parse(receiptBytes);
        var parameters = receipt.RootElement.GetProperty("Parameters");
        if (HistoricalEvidenceManifestStore.JsonHash(parameters) != source.RequestParametersSha256)
            throw new InvalidDataException("Diagnostic query parameters differ from the historical source.");
        var sorted = parameters.EnumerateObject().Where(item => item.Name != "page_token")
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToDictionary(item => item.Name, item => item.Value.Clone());
        var symbols = Text(parameters, "symbols")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal).ToImmutableArray() ?? [];
        var start = Clock(Text(parameters, "start"));
        var end = Clock(Text(parameters, "end"));
        var failure = source.HttpStatus != 200 ? "news_query_http_failure" : start is null || end is null || start >= end
            ? "news_query_window_unverified" : Text(parameters, "exclude_contentless") is not "false"
            ? "news_query_filters_may_exclude_articles" : null;
        if (parameters.EnumerateObject().Any(item => item.Name is not ("symbols" or "start" or "end" or "limit" or "sort" or
            "include_content" or "exclude_contentless" or "page_token"))) failure ??= "news_query_unsupported_filter";
        using var payload = JsonDocument.Parse(payloadBytes);
        var body = payload.RootElement;
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("news", out var news) || news.ValueKind != JsonValueKind.Array)
            failure ??= "news_query_payload_malformed";
        if (!body.TryGetProperty("next_page_token", out var next) || next.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            failure ??= "news_query_terminal_cursor_unproved";
        if (receipt.RootElement.TryGetProperty("DerivedMetadata", out var derived) && derived.ValueKind == JsonValueKind.True)
            failure ??= VerifyCatalogEnvelope(receipt.RootElement, source, next.ValueKind == JsonValueKind.Null);
        return new(source, ProviderIdentity(source.Provider), HistoricalEvidenceManifestStore.Hash(JsonSerializer.SerializeToUtf8Bytes(sorted)),
            symbols, start, end, Text(parameters, "page_token"), next.ValueKind == JsonValueKind.String ? next.GetString() : null, failure);
    }

    /// <summary>Derived catalog envelopes must retain the exhausted original cursor and its exact terminal observation.</summary>
    private static string? VerifyCatalogEnvelope(JsonElement receipt, HistoricalSourceObservation source, bool terminal)
    {
        if (!receipt.TryGetProperty("OriginalCatalogObservation", out var observation) ||
            !receipt.TryGetProperty("OriginalCursor", out var cursor) || cursor.ValueKind != JsonValueKind.Object ||
            !cursor.TryGetProperty("Exhausted", out var exhausted) || exhausted.ValueKind != JsonValueKind.True)
            return "catalog_query_exhausted_cursor_unproved";
        if (Clock(Text(observation, "ReceivedAtUtc")) != source.CapturedAtUtc ||
            Text(observation.GetProperty("Artifact").GetProperty("Content"), "Sha256") != source.PayloadSha256)
            throw new InvalidDataException("Derived catalog envelope changed an original actual receipt or raw content identity.");
        if (terminal && Text(cursor, "ObservationId") != Text(observation, "ObservationId"))
            return "catalog_query_terminal_observation_unproved";
        return null;
    }

    /// <summary>Requires an initial request, unique contiguous response sequence, and a terminal null cursor.</summary>
    private static string? VerifyChain(Page[] pages)
    {
        if (pages.Any(page => page.Failure is not null)) return pages.First(page => page.Failure is not null).Failure;
        var distinct = pages.GroupBy(page => (page.Token, page.Source.PayloadSha256)).Select(group => group.First()).ToArray();
        if (distinct.GroupBy(page => page.Token).Any(group => group.Count() != 1)) return "news_query_cursor_response_ambiguous";
        var initial = distinct.Where(page => page.Token is null).ToArray();
        if (initial.Length != 1) return "news_query_initial_page_unproved";
        var byToken = distinct.Where(page => page.Token is not null).ToDictionary(page => page.Token!, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = initial[0];
        var consumed = 1;
        while (current.Next is { } next)
        {
            if (!seen.Add(next)) return "news_query_cursor_cycle";
            if (!byToken.TryGetValue(next, out var following)) return "news_query_cursor_not_exhausted";
            current = following;
            consumed++;
        }
        return consumed == distinct.Length ? null : "news_query_orphan_pages";
    }

    /// <summary>Missing documentation remains unknown; supplied documentation is always bound by its actual bytes.</summary>
    internal static void VerifySemantics(string root, HistoricalDiagnosticQuerySemantics? semantics)
    {
        if (semantics is null) return;
        if (semantics.TimestampField is not ("created_at" or "updated_at") ||
            semantics.Basis is not ("observed_provider_query_behavior" or "documented_provider_contract") ||
            !HistoricalEvidenceManifestStore.IsHash(semantics.EvidenceSha256) ||
            HistoricalEvidenceManifestStore.Hash(HistoricalEvidenceManifestStore.ReadBound(root, semantics.EvidenceRelativePath)) != semantics.EvidenceSha256)
            throw new InvalidDataException("Diagnostic query timestamp semantics require bound provider documentation.");
    }

    /// <summary>Accepts literal string/numeric/bool query values while preserving their original parameter hash.</summary>
    internal static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property)
        ? property.ValueKind == JsonValueKind.String ? property.GetString() : property.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
            ? property.GetRawText() : null : null;

    /// <summary>Rejects local or unspecified clocks instead of silently interpreting them as UTC.</summary>
    internal static DateTimeOffset? Clock(string? value) => value is not null &&
        (value.EndsWith('Z') || value.EndsWith("+00:00", StringComparison.Ordinal)) &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) && time.Offset == TimeSpan.Zero && time != default ? time : null;
}
