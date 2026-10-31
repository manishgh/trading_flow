using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace TradingFlow.Data.Evidence.Historical;

/// <summary>Offline importer for retained collection receipts. No network, database or trading path.</summary>
public static class HistoricalEvidenceManifestBuilder
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>Discovers the retained receipt set once when preparing a new archive package.</summary>
    public static HistoricalEvidenceManifest BuildFromCollectionRoot(string collectionRoot, DateTimeOffset? builtAtUtc = null) =>
        BuildFromReceiptPaths(collectionRoot, null, builtAtUtc);

    /// <summary>Replays only explicitly frozen receipt paths; unrelated captures appended later cannot change earlier package decisions.</summary>
    internal static HistoricalEvidenceManifest BuildFromReceiptPaths(string collectionRoot,
        IEnumerable<string>? receiptRelativePaths, DateTimeOffset? builtAtUtc = null)
    {
        if (!Path.IsPathFullyQualified(collectionRoot)) throw new InvalidDataException("Collection root must be explicit and absolute.");
        collectionRoot = Path.GetFullPath(collectionRoot);
        var observations = new List<HistoricalSourceObservation>();
        var facts = new Dictionary<string, HistoricalFactVersion>(StringComparer.Ordinal);
        var sessions = new List<HistoricalSession>();
        var claims = new List<HistoricalClaimDisposition>();
        var selectedSymbols = new HashSet<string>(StringComparer.Ordinal);
        var receipts = (receiptRelativePaths is null
            ? Directory.EnumerateFiles(Path.Combine(collectionRoot, "receipts"), "*.json", System.IO.SearchOption.AllDirectories)
            : receiptRelativePaths.Select(relative => HistoricalEvidenceManifestStore.BoundPath(collectionRoot, relative)))
            .Order(StringComparer.Ordinal).ToArray();
        // The current-sector request scope selects earnings rows; it never establishes historical sector membership.
        foreach (var path in receipts)
        {
            using var receipt = JsonDocument.Parse(HistoricalEvidenceManifestStore.ReadBound(collectionRoot, Path.GetRelativePath(collectionRoot, path)));
            var row = receipt.RootElement;
            if (Text(row, "Resource") != "/export") continue;
            foreach (var symbol in Text(row.GetProperty("Parameters"), "t").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                selectedSymbols.Add(symbol);
        }
        string? calendarObservationId = null;
        foreach (var path in receipts)
        {
            var relative = Path.GetRelativePath(collectionRoot, path);
            var receiptBytes = HistoricalEvidenceManifestStore.ReadBound(collectionRoot, relative);
            using var receipt = JsonDocument.Parse(receiptBytes);
            var row = receipt.RootElement;
            var resource = Text(row, "Resource");
            var provider = Text(row, "Provider");
            if (!Wanted(resource, provider)) continue;
            var payloadPath = Text(row, "Payload");
            var payload = HistoricalEvidenceManifestStore.ReadBound(collectionRoot, payloadPath);
            var payloadHash = HistoricalEvidenceManifestStore.Hash(payload);
            if (payloadHash != Text(row, "PayloadSha256")) throw new InvalidDataException("Captured payload hash mismatch: " + relative);
            var captured = Utc(row, "CapturedUtc") ?? throw new InvalidDataException("Receipt lacks valid UTC capture time: " + relative);
            var original = row.TryGetProperty("HistoricalFirstReceiptClaim", out var originalValue) && originalValue.ValueKind == JsonValueKind.True;
            if (original || !String.IsNullOrWhiteSpace(OptionalText(row, "OriginalDeliveryEvidence")))
                throw new InvalidDataException("Raw archive receipts cannot certify original delivery; a typed independently verified original-receipt scheme is not implemented.");
            var receiptHash = HistoricalEvidenceManifestStore.Hash(receiptBytes);
            var source = new HistoricalSourceObservation(receiptHash, provider, resource,
                HistoricalEvidenceManifestStore.JsonHash(row.GetProperty("Parameters")), relative, receiptHash,
                payloadPath, payloadHash, payload.LongLength,
                row.TryGetProperty("HttpStatus", out var http) && http.ValueKind == JsonValueKind.Number && http.TryGetInt32(out var status) ? status : null,
                captured, false, null);
            observations.Add(source);
            if (source.HttpStatus != 200)
            {
                claims.Add(new("source:" + relative, "unavailable", "http_or_transport_failure:" + source.HttpStatus));
                continue;
            }
            var text = Encoding.UTF8.GetString(payload).TrimStart('\uFEFF');
            if (resource == "/export")
            {
                using var csv = new TextFieldParser(new StringReader(text)) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
                csv.SetDelimiters(",");
                var headers = csv.ReadFields() ?? throw new InvalidDataException("Sector CSV headers missing.");
                var tickerIndex = System.Array.IndexOf(headers, "Ticker");
                var sectorIndex = System.Array.IndexOf(headers, "Sector");
                var industryIndex = System.Array.IndexOf(headers, "Industry");
                if (tickerIndex < 0 || sectorIndex < 0) throw new InvalidDataException("Sector CSV required columns missing.");
                while (!csv.EndOfData)
                {
                    var fields = csv.ReadFields()!;
                    if (fields.Length != headers.Length || !selectedSymbols.Contains(fields[tickerIndex]))
                        throw new InvalidDataException("Sector CSV row shape or requested symbol mismatch.");
                    var value = JsonSerializer.SerializeToElement(new { symbol = fields[tickerIndex], sector = fields[sectorIndex],
                        industry = industryIndex >= 0 ? fields[industryIndex] : null, taxonomy = "Finviz-current-capture",
                        historical_effective_dates_proven = false });
                    Add(facts, HistoricalFactKind.CurrentSector, fields[tickerIndex], fields[tickerIndex], value,
                        HistoricalEvidenceBasis.CurrentSnapshot, captured, null, null, source);
                }
                continue;
            }
            using var document = JsonDocument.Parse(text);
            var data = document.RootElement;
            if (resource == "/v1beta1/news")
            {
                foreach (var article in Array(data, "news"))
                {
                    var suppliedId = OptionalText(article, "id");
                    var validIdentity = !String.IsNullOrWhiteSpace(suppliedId);
                    // Invalid IDs retain a content identity for quarantine; the fallback never becomes a supported provider article ID.
                    var id = validIdentity ? suppliedId!.Trim() : HistoricalEvidenceManifestStore.JsonHash(article);
                    var created = Utc(article, "created_at");
                    var updated = Utc(article, "updated_at");
                    var validSymbols = article.TryGetProperty("symbols", out var symbols) && symbols.ValueKind == JsonValueKind.Array &&
                        symbols.EnumerateArray().All(symbol => symbol.ValueKind == JsonValueKind.String && !String.IsNullOrWhiteSpace(symbol.GetString()));
                    var reason = created is null || updated is null ? "news_timestamp_unverified" :
                        updated < created ? "news_update_precedes_publication" : updated > captured ? "news_timestamp_after_capture" :
                        !validSymbols || !validIdentity ? "news_identity_or_symbols_malformed" : null;
                    // Text remains in its immutable raw payload; no headline/body is silently substituted.
                    var value = JsonSerializer.SerializeToElement(new { article_id = id,
                        headline = OptionalText(article, "headline"),
                        symbols = validSymbols ? symbols.EnumerateArray().Select(symbol => symbol.GetString()).ToArray() : [],
                        input_mode = !String.IsNullOrWhiteSpace(OptionalText(article, "content")) ? "body_supported" :
                            !String.IsNullOrWhiteSpace(OptionalText(article, "summary")) ? "headline_and_summary" : "headline_only",
                        classification_evaluated = false, original_revision_history_proven = false });
                    Add(facts, HistoricalFactKind.NewsVersion, "article:" + id, id, value,
                        HistoricalEvidenceBasis.CapturedProviderVersion, created, created, updated, source, reason,
                        HistoricalEvidenceManifestStore.JsonHash(article));
                }
            }
            else if (resource == "/api/calendar/earnings")
            {
                foreach (var item in Array(data, "items"))
                {
                    var symbol = Text(item, "ticker");
                    if (!selectedSymbols.Contains(symbol)) continue;
                    var dateText = Text(item, "earningsDate");
                    var effective = NewYorkTime(dateText);
                    Add(facts, HistoricalFactKind.EarningsCurrentVintage, symbol, symbol + ":" + dateText, item,
                        HistoricalEvidenceBasis.CapturedProviderVersion, effective, null, null, source,
                        effective is null ? "earnings_exchange_time_unverified" : null);
                }
            }
            else if (resource.StartsWith("/v2/assets/", StringComparison.Ordinal))
            {
                var symbol = Text(data, "symbol");
                if (resource != "/v2/assets/" + symbol || String.IsNullOrWhiteSpace(Text(data, "id")))
                    throw new InvalidDataException("Current asset identity differs from its request.");
                Add(facts, HistoricalFactKind.CurrentAsset, symbol, Text(data, "id"), data,
                    HistoricalEvidenceBasis.CurrentSnapshot, captured, null, null, source);
            }
            else if (resource == "/v1/corporate-actions")
            {
                var actions = data.GetProperty("corporate_actions");
                foreach (var bucket in actions.EnumerateObject())
                    foreach (var action in bucket.Value.EnumerateArray())
                    {
                        var symbol = OptionalText(action, "symbol") ?? OptionalText(action, "new_symbol") ?? OptionalText(action, "old_symbol") ?? "unknown";
                        var value = JsonSerializer.SerializeToElement(new { action_type = bucket.Name, source_action = action,
                            adjustment_factor_reconstructed = false, process_date_is_not_knowledge_or_ex_date = true });
                        Add(facts, HistoricalFactKind.CorporateAction, symbol,
                            OptionalText(action, "id") ?? HistoricalEvidenceManifestStore.JsonHash(action), value,
                            HistoricalEvidenceBasis.CapturedProviderVersion, NewYorkTime(OptionalText(action, "ex_date")),
                            null, null, source);
                    }
            }
            else if (resource == "/v2/calendar")
            {
                if (calendarObservationId is not null) throw new InvalidDataException("Multiple exchange calendar captures require an explicit version selection.");
                calendarObservationId = source.ObservationId;
                foreach (var session in data.EnumerateArray())
                {
                    var date = DateOnly.ParseExact(Text(session, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var dateText = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    sessions.Add(new(date, NewYorkTime(dateText + "T" + Text(session, "open")) ?? throw new InvalidDataException("Calendar open invalid."),
                        NewYorkTime(dateText + "T" + Text(session, "close")) ?? throw new InvalidDataException("Calendar close invalid.")));
                }
            }
            else if (HistoricalEvidenceManifestStore.IsSecSource(source))
                ImportSecSubmissions(facts, data, source);
        }
        claims.AddRange(new[] {
            new HistoricalClaimDisposition("original_news_delivery_and_revision_lineage", "unproved", "Fresh archive captures retain actual receipt; no original delivered-version archive was supplied."),
            new HistoricalClaimDisposition("news_classifier_human_evaluation", "unproved", "Availability import does not label or evaluate a classifier."),
            new HistoricalClaimDisposition("pre_entry_earnings_schedule_and_consensus", "unproved", "Returned current-vintage earnings rows do not establish old schedule/estimate vintages."),
            new HistoricalClaimDisposition("historical_sector_membership", "unproved", "Current Finviz observations are not backdated assignments; SEC SIC is not Finviz sector history."),
            new HistoricalClaimDisposition("historical_share_class_identity", "unproved", "Current asset UUIDs and issuer CIKs are not a dated share-class master."),
            new HistoricalClaimDisposition("decision_time_adjustment_factors", "unproved", "Corporate actions are preserved; no factor of one or complete adjustment reconstruction is inferred."),
            new HistoricalClaimDisposition("official_filing_acceptance", facts.Values.Any(fact => fact.Kind == HistoricalFactKind.OfficialFilingAcceptance) ? "captured" : "not_supplied",
                "Only accepted filing facts are imported from SEC submissions; current root metadata is not historical SIC or symbol membership.") });
        var manifest = new HistoricalEvidenceManifest(HistoricalEvidenceManifest.CurrentSchema, collectionRoot,
            builtAtUtc ?? DateTimeOffset.UtcNow, observations.ToImmutableArray(), facts.Values.OrderBy(fact => fact.FactId, StringComparer.Ordinal).ToImmutableArray(),
            sessions.OrderBy(session => session.ExchangeDate).ToImmutableArray(), calendarObservationId, claims.ToImmutableArray());
        HistoricalEvidenceManifestStore.Validate(manifest);
        return manifest;
    }

    private static bool Wanted(string resource, string provider) => resource is "/v1beta1/news" or "/api/calendar/earnings" or
        "/export" or "/v1/corporate-actions" or "/v2/calendar" || resource.StartsWith("/v2/assets/", StringComparison.Ordinal) ||
        provider == "sec-edgar";

    private static void ImportSecSubmissions(Dictionary<string, HistoricalFactVersion> facts, JsonElement data, HistoricalSourceObservation source)
    {
        var cik = Text(data, "cik").PadLeft(10, '0');
        if (cik.Length != 10 || !cik.All(Char.IsDigit)) throw new InvalidDataException("SEC issuer CIK malformed.");
        var recent = data.GetProperty("filings").GetProperty("recent");
        var accessions = Array(recent, "accessionNumber").ToArray();
        var accepted = Array(recent, "acceptanceDateTime").ToArray();
        var forms = Array(recent, "form").ToArray();
        if (accessions.Length != accepted.Length || forms.Length != accepted.Length) throw new InvalidDataException("SEC filing columns unaligned.");
        for (var index = 0; index < accessions.Length; index++)
        {
            var accession = accessions[index].GetString() ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(accession, @"^\d{10}-\d{2}-\d{6}$")) throw new InvalidDataException("SEC accession malformed.");
            var at = ParseUtc(accepted[index].GetString());
            var value = JsonSerializer.SerializeToElement(new { issuer_cik = cik, accession,
                form = forms[index].GetString(), claim = "filing_accepted", share_class_identity_proven = false,
                historical_sic_or_sector_claimed = false });
            Add(facts, HistoricalFactKind.OfficialFilingAcceptance, "SEC-CIK:" + cik, accession, value,
                HistoricalEvidenceBasis.OfficialAcceptedArchive, at, at, null, source,
                at is null || at > source.CapturedAtUtc ? "official_acceptance_time_unverified" : null);
        }
    }

    private static void Add(Dictionary<string, HistoricalFactVersion> facts, HistoricalFactKind kind, string subject,
        string recordId, JsonElement value, HistoricalEvidenceBasis basis, DateTimeOffset? effective,
        DateTimeOffset? published, DateTimeOffset? updated, HistoricalSourceObservation source,
        string? quality = null, string? contentHash = null)
    {
        contentHash ??= HistoricalEvidenceManifestStore.JsonHash(value);
        var id = HistoricalEvidenceManifestStore.Hash(Encoding.UTF8.GetBytes(kind + "\n" + subject + "\n" + recordId + "\n" + contentHash));
        if (facts.TryGetValue(id, out var existing))
        {
            facts[id] = existing with { ActualReceivedAtUtc = existing.ActualReceivedAtUtc < source.CapturedAtUtc ? existing.ActualReceivedAtUtc : source.CapturedAtUtc,
                SourceObservationIds = existing.SourceObservationIds.Add(source.ObservationId).Distinct().Order(StringComparer.Ordinal).ToImmutableArray() };
        }
        else facts.Add(id, new(id, kind, subject, recordId, contentHash, basis, effective, published, updated,
            source.CapturedAtUtc, [source.ObservationId], JsonSerializer.Serialize(value), quality));
    }

    private static IEnumerable<JsonElement> Array(JsonElement value, string key)
    {
        var items = value.GetProperty(key);
        if (items.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Expected source array: " + key);
        return items.EnumerateArray();
    }
    private static string Text(JsonElement value, string key) => OptionalText(value, key) ?? throw new InvalidDataException("Missing source field: " + key);
    private static string? OptionalText(JsonElement value, string key) => value.TryGetProperty(key, out var field) ?
        field.ValueKind == JsonValueKind.String ? field.GetString() : field.ValueKind == JsonValueKind.Number ? field.GetRawText() : null : null;
    private static DateTimeOffset? Utc(JsonElement value, string key) => ParseUtc(OptionalText(value, key));
    private static DateTimeOffset? ParseUtc(string? text) => text is not null &&
        (text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal)) &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) && time.Offset == TimeSpan.Zero && time != default ? time : null;
    private static DateTimeOffset? NewYorkTime(string? text)
    {
        if (text is null || !DateTime.TryParseExact(text, ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (NewYork.IsInvalidTime(local) || NewYork.IsAmbiguousTime(local)) return null;
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, NewYork), TimeSpan.Zero);
    }
}
