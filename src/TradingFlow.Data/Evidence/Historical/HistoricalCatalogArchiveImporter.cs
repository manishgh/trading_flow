using System.Globalization;
using System.Text;
using System.Text.Json;
using TradingFlow.Data.Evidence.Collection;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Data.Evidence.Historical;

/// <summary>Bridges existing immutable catalog objects into the retained-archive importer; envelopes are explicitly derived metadata.</summary>
public static class HistoricalCatalogArchiveImporter
{
    /// <summary>Copies verified original raw bytes and frozen catalog lineage into a new output root without changing any original object.</summary>
    public static async Task<HistoricalEvidenceManifest> ImportAsync(IEvidenceCatalog catalog, IImmutableArtifactStore store,
        IEnumerable<string> datasetIds, string outputRoot, Uri marketDataRest, DateTimeOffset? builtAtUtc = null, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(outputRoot)) throw new InvalidDataException("Derived archive output must be absolute.");
        if (Directory.Exists(outputRoot) && Directory.EnumerateFileSystemEntries(outputRoot).Any())
            throw new IOException("Derived archive output must be new or empty.");
        Directory.CreateDirectory(Path.Combine(outputRoot, "receipts"));
        var imported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var datasetId in datasetIds.Distinct(StringComparer.Ordinal))
        {
            var dataset = await catalog.GetDatasetAsync(datasetId, cancellationToken)
                ?? throw new InvalidDataException("Catalog dataset not found: " + datasetId);
            if (dataset.Kind is not (EvidenceDatasetKind.NewsRevisions or EvidenceDatasetKind.NewsArticles or EvidenceDatasetKind.ExchangeSessions))
                throw new InvalidDataException("Diagnostic archive import supports news and exchange sessions only.");
            var plan = await catalog.GetCollectionPlanAsync(dataset.CollectionJobId, cancellationToken)
                ?? throw new InvalidDataException("Catalog collection plan missing.");
            if (plan.LogicalPlanHash != dataset.CollectionPlanHash) throw new InvalidDataException("Dataset and collection plan differ.");
            foreach (var partition in dataset.Partitions)
            {
                var verification = await store.VerifyAsync(partition.Artifact, cancellationToken);
                if (!verification.IsValid) throw new InvalidDataException("Normalized artifact bytes failed verification: " + verification.FailureReason);
            }
            foreach (var request in plan.Requests)
            {
                var cursor = await catalog.GetRequestCursorCheckpointAsync(plan.JobId, request.RequestId, cancellationToken);
                var observations = await catalog.FindSourceObservationsAsync(plan.JobId, request.RequestId, cancellationToken);
                var successes = observations.Where(item => item.TransportStatusCode == 200)
                    .OrderBy(PageOrdinal).ThenBy(item => item.ReceivedAtUtc).ToArray();
                string? token = null;
                var expectedPage = 1;
                foreach (var page in successes.GroupBy(PageOrdinal).OrderBy(group => group.Key))
                {
                    if (page.Key != expectedPage++) throw new InvalidDataException("Catalog request has a gap in its original page ordinals.");
                    if (page.Select(item => item.Artifact.Content.Sha256).Distinct().Count() != 1)
                        throw new InvalidDataException("Catalog request has conflicting successful raw pages.");
                    var selected = page.FirstOrDefault(item => item.ObservationId == cursor?.ObservationId) ?? page.First();
                    var verification = await store.VerifyAsync(selected.Artifact, cancellationToken);
                    if (!verification.IsValid) throw new InvalidDataException("Raw artifact bytes failed verification: " + verification.FailureReason);
                    await using var raw = await store.OpenReadAsync(selected.Artifact, cancellationToken);
                    using var copy = new MemoryStream();
                    await raw.CopyToAsync(copy, cancellationToken);
                    var bytes = copy.ToArray();
                    VerifyRequest(request, selected, token, marketDataRest);
                    var resource = Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpoint) ? endpoint.AbsolutePath : request.Endpoint;
                    var parameters = Parameters(request, token);
                    var payloadPath = Path.Combine("datasets", selected.Artifact.Content.Sha256 + ".json");
                    WriteExclusiveOrSame(Path.Combine(outputRoot, payloadPath), bytes);
                    var receipt = new {
                        Provider = selected.Provider, Resource = resource, Parameters = parameters,
                        HttpStatus = selected.TransportStatusCode, CapturedUtc = selected.ReceivedAtUtc,
                        Payload = payloadPath, PayloadSha256 = selected.Artifact.Content.Sha256,
                        HistoricalFirstReceiptClaim = false, DerivedMetadata = true,
                        Purpose = "Derived packaging envelope; original catalog observation and raw content identities are retained below. This is not an original provider receipt.",
                        OriginalCatalogObservation = selected, OriginalCollectionPlan = plan, OriginalCursor = cursor,
                        OriginalNormalizedDataset = dataset
                    };
                    if (imported.Add(selected.ObservationId))
                        WriteExclusiveOrSame(Path.Combine(outputRoot, "receipts", selected.ObservationId + ".json"),
                            JsonSerializer.SerializeToUtf8Bytes(receipt, HistoricalEvidenceManifestStore.JsonOptions));
                    using var body = JsonDocument.Parse(bytes);
                    token = resource == "/v1beta1/news" && body.RootElement.TryGetProperty("next_page_token", out var next) &&
                        next.ValueKind == JsonValueKind.String ? next.GetString() : null;
                }
            }
        }
        return HistoricalEvidenceManifestBuilder.BuildFromCollectionRoot(outputRoot, builtAtUtc);
    }

    /// <summary>Imports an existing raw archive calendar with its actual writer manifest, without inventing a news receipt.</summary>
    public static void ImportCapturedCalendar(string outputRoot, string originalManifestPath, string originalPayloadPath)
    {
        if (!Path.IsPathFullyQualified(outputRoot)) throw new InvalidDataException("Derived calendar output root must be absolute.");
        var manifestBytes = File.ReadAllBytes(originalManifestPath);
        var original = JsonSerializer.Deserialize<RawArchiveManifest>(manifestBytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Captured calendar manifest is empty.");
        var bytes = File.ReadAllBytes(originalPayloadPath);
        if (HistoricalEvidenceManifestStore.Hash(bytes) != original.Sha256 || bytes.LongLength != original.ByteLength ||
            Path.GetFileName(originalPayloadPath) != original.PayloadFileName || original.Http?.StatusCode != 200 ||
            original.Http.RequestUri?.AbsolutePath != "/v2/calendar")
            throw new InvalidDataException("Captured calendar bytes or HTTP receipt differ from the original manifest.");
        var path = Path.Combine("datasets", original.Sha256 + ".json");
        WriteExclusiveOrSame(Path.Combine(outputRoot, path), bytes);
        var receipt = new {
            Provider = original.Source, Resource = "/v2/calendar", Parameters = new { }, HttpStatus = 200,
            CapturedUtc = original.ReceivedAtUtc, Payload = path, PayloadSha256 = original.Sha256,
            HistoricalFirstReceiptClaim = false, DerivedMetadata = true,
            Purpose = "Derived packaging envelope, not an original provider receipt.",
            OriginalArchiveManifestSha256 = HistoricalEvidenceManifestStore.Hash(manifestBytes),
            OriginalArchiveManifestUtf8 = Encoding.UTF8.GetString(manifestBytes),
            OriginalArchiveManifestPath = originalManifestPath, OriginalArchivePayloadPath = originalPayloadPath
        };
        WriteExclusiveOrSame(Path.Combine(outputRoot, "receipts", "calendar-" + original.Sha256 + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(receipt, HistoricalEvidenceManifestStore.JsonOptions));
    }

    /// <summary>Checks the immutable observation belongs to the exact request and reuses its provider adapter to verify the actual request URI hash.</summary>
    private static void VerifyRequest(EvidenceCollectionRequest request, EvidenceSourceObservation observation, string? token, Uri marketDataRest)
    {
        if (observation.IngestionRequestId != request.RequestId || observation.Endpoint != request.Endpoint ||
            observation.RequestedStartUtc != request.RequestedStartUtc || observation.RequestedEndUtc != request.RequestedEndUtc ||
            !observation.RequestedSymbols.SequenceEqual(request.Symbols)) throw new InvalidDataException("Catalog raw page request identity differs.");
        if (!request.Endpoint.EndsWith("/v1beta1/news", StringComparison.Ordinal)) return;
        var adapter = new AlpacaNewsEvidenceRequestAdapter(marketDataRest);
        using var message = adapter.CreateRequest(request, token);
        var actualHash = HistoricalEvidenceManifestStore.Hash(Encoding.UTF8.GetBytes(message.RequestUri!.AbsoluteUri));
        if (!observation.RequestAttributes.TryGetValue("request_uri_sha256", out var expected) || actualHash != expected)
            throw new InvalidDataException("Catalog raw page URI does not match its retained token chain.");
    }

    /// <summary>Reconstructs the same nonsecret provider query fields as the existing request adapter.</summary>
    private static Dictionary<string, string> Parameters(EvidenceCollectionRequest request, string? token)
    {
        var fields = request.Parameters.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        if (request.Symbols.Count > 0) fields["symbols"] = String.Join(',', request.Symbols);
        if (request.RequestedStartUtc is { } start) fields["start"] = start.ToString("O", CultureInfo.InvariantCulture);
        if (request.RequestedEndUtc is { } end) fields["end"] = end.ToString("O", CultureInfo.InvariantCulture);
        fields.TryAdd("exclude_contentless", "false");
        if (token is not null) fields["page_token"] = token;
        return fields;
    }

    /// <summary>Requires a recorded ordinal instead of inferring request ordering from capture time.</summary>
    private static int PageOrdinal(EvidenceSourceObservation observation) =>
        observation.RequestAttributes.TryGetValue("page_ordinal", out var text) && Int32.TryParse(text, out var value) && value > 0
            ? value : throw new InvalidDataException("Catalog raw page has no valid ordinal.");

    /// <summary>Creates only derived packaging copies and never replaces differing existing bytes.</summary>
    private static void WriteExclusiveOrSame(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) throw new IOException("Derived archive bytes already differ: " + path);
            return;
        }
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
        stream.Flush(true);
    }
}
