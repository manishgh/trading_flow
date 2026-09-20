using System.Text.Json;
using TradingFlow.Contracts.Evidence;
using static TradingFlow.Data.Evidence.Collection.SharedNews.CollectionWire;
using static TradingFlow.Data.Evidence.Collection.SharedNews.LocalPublicationFiles;

namespace TradingFlow.Data.Evidence.Collection.SharedNews;

internal sealed record CollectedPublication(string WindowId, int AttemptNumber, byte[] Intent, byte[] Result, ValidatedNewsReceipt Receipt)
{
    internal string Key => Hash(Intent);
}

internal sealed record VerifiedPublicationPlan(byte[] Bytes, string Revision, IReadOnlyList<CollectedPublication> Publications,
    IReadOnlyList<SharedNewsWindowReport> Windows);

internal static class SharedNewsPublicationVerifier
{
    internal static VerifiedPublicationPlan Verify(string root, string pin, SharedNewsConsumptionLimits limits, CancellationToken cancellationToken)
    {
        byte[] ReadRecord(string path, int maximum) => Read(path, maximum, cancellationToken);
        List<string> Scan(string path, int maximum) => Entries(path, maximum, cancellationToken);
        foreach (var entry in Scan(root, 1024))
            Require(Path.GetFileName(entry) is "owner" or "runs" or "collection-owner.lock" || Pending(entry), "Unexpected shared collection root artifact.");
        var ownerPath = Path.Combine(root, "owner");
        Files(ownerPath, "owner.json");
        var owner = Parse(ReadRecord(Path.Combine(ownerPath, "owner.json"), MaximumRecordBytes));
        Fields(owner, "schema_version", "owner", "coordination", "storage_root");
        Equal(owner, "schema_version", "alpaca.news_collection_owner.v1");
        Equal(owner, "owner", "market_predictor");
        Equal(owner, "coordination", "single_local_root");
        Equal(owner, "storage_root", root);

        var run = Path.Combine(root, "runs", pin);
        foreach (var entry in Scan(run, 32))
            Require(Path.GetFileName(entry) is "plan" or "attempts" || Pending(entry), "Unexpected collection run artifact.");
        var planPath = Path.Combine(run, "plan");
        Files(planPath, "plan.json");
        var bytes = ReadRecord(Path.Combine(planPath, "plan.json"), MaximumPlanBytes);
        Require(Hash(bytes) == pin, "Collection plan does not match its independently configured pin.");
        var plan = Parse(bytes);
        Fields(plan, "schema_version", "owner", "coordination", "attempt_semantics", "producer_revision", "windows", "max_pages_per_window", "max_attempts_per_page");
        Equal(plan, "schema_version", "alpaca.news_collection_plan.v1");
        Equal(plan, "owner", "market_predictor");
        Equal(plan, "coordination", "single_local_root");
        Equal(plan, "attempt_semantics", "logical_transport_invocation");
        var revision = Text(plan, "producer_revision");
        Require(Matches(revision, "[0-9a-f]{40}"), "Invalid collection producer revision.");
        var maxPages = Number(plan, "max_pages_per_window", 1, 10_000);
        var maxAttempts = Number(plan, "max_attempts_per_page", 1, 10);
        var windows = plan.GetProperty("windows");
        Require(windows.ValueKind == JsonValueKind.Array && windows.GetArrayLength() is >= 1 and <= 1000, "Invalid collection windows.");
        Require((long)windows.GetArrayLength() * maxPages * maxAttempts <= 100_000, "Collection plan exceeds its total ledger budget.");
        var parsed = new List<(string Id, NewsPageRequest Request)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queries = new HashSet<(string, DateTimeOffset, DateTimeOffset)>();
        foreach (var window in windows.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Fields(window, "window_id", "request");
            var id = Text(window, "window_id");
            Require(Matches(id, "[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}") &&
                !Matches(id.ToLowerInvariant(), "con|prn|aux|nul|com[1-9]|lpt[1-9]"), "Invalid collection window name.");
            var request = Request(window.GetProperty("request"));
            Require(request.PageToken is null && names.Add(id) && queries.Add((request.Symbol, request.StartUtc, request.EndUtc)), "Collection windows must have unique identities and initial null tokens.");
            parsed.Add((id, request));
        }
        var attemptsRoot = Path.Combine(run, "attempts");
        foreach (var entry in Scan(attemptsRoot, parsed.Count))
            Require(parsed.Any(w => w.Id == Path.GetFileName(entry)) && Directory.Exists(entry), "Unplanned collection window.");

        var publications = new List<CollectedPublication>();
        var reports = new List<SharedNewsWindowReport>();
        long retainedBytes = bytes.Length;
        var attemptsRead = 0;
        foreach (var window in parsed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = window.Request;
            var pageCount = 0;
            var pageAttempts = 0;
            var ambiguous = 0;
            string? terminal = null;
            var seenTokens = new HashSet<string>(StringComparer.Ordinal);
            var maximum = maxPages * maxAttempts;
            var entries = Scan(Path.Combine(attemptsRoot, window.Id), Math.Min(100_000, maximum * 2 + 16));
            var attempts = new List<string>();
            foreach (var entry in entries)
            {
                if (Pending(entry)) continue;
                Require(Directory.Exists(entry) && Matches(Path.GetFileName(entry), "[0-9]{6}"), "Unexpected collection attempt entry.");
                attempts.Add(entry);
            }
            Require(attempts.Count <= maximum, "Collection attempts exceed the pinned budget.");
            attempts.Sort(StringComparer.Ordinal);
            for (var index = 0; index < attempts.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Require(++attemptsRead <= limits.MaximumAttempts, "Consumer attempt verification budget exceeded.");
                var attempt = attempts[index];
                Require(Path.GetFileName(attempt) == (index + 1).ToString("D6", System.Globalization.CultureInfo.InvariantCulture) &&
                    terminal is null && pageCount < maxPages && pageAttempts < maxAttempts, "Discontinuous attempts or attempt after a terminal boundary.");
                var intentBytes = ReadRecord(Path.Combine(attempt, "intent.json"), MaximumRecordBytes);
                var intent = Parse(intentBytes);
                Fields(intent, "schema_version", "plan_sha256", "window_id", "attempt_number", "page_number", "request");
                Equal(intent, "schema_version", "alpaca.news_collection_intent.v1");
                Equal(intent, "plan_sha256", pin);
                Equal(intent, "window_id", window.Id);
                Require(Number(intent, "attempt_number", 1, 100_000) == index + 1 && Number(intent, "page_number", 1, 10_000) == pageCount + 1 &&
                    Request(intent.GetProperty("request")) == request, "Collection intent differs from reconstructed plan/page identity.");
                pageAttempts++;
                foreach (var entry in Scan(attempt, 32))
                    Require(Path.GetFileName(entry) is "intent.json" or "receipt" or "result" || Pending(entry), "Unexpected collection attempt artifact.");
                var resultPath = Path.Combine(attempt, "result");
                Plain(resultPath);
                if (!Directory.Exists(resultPath))
                {
                    Require(!File.Exists(resultPath), "Collection result must be a directory.");
                    // Even a complete receipt is ambiguous until the producer commits its result.
                    ambiguous++;
                    continue;
                }
                Files(resultPath, "result.json");
                var resultBytes = ReadRecord(Path.Combine(resultPath, "result.json"), MaximumRecordBytes);
                var result = Parse(resultBytes);
                Fields(result, "schema_version", "intent_sha256", "status", "receipt_sha256");
                Equal(result, "schema_version", "alpaca.news_collection_result.v1");
                Equal(result, "intent_sha256", Hash(intentBytes));
                var receiptPath = Path.Combine(attempt, "receipt");
                var receiptPin = result.GetProperty("receipt_sha256").GetString();
                if (Text(result, "status") == "transport_failure")
                {
                    Require(receiptPin is null && !Directory.Exists(receiptPath) && !File.Exists(receiptPath), "Failed attempt cannot have a receipt.");
                    continue;
                }
                Equal(result, "status", "received");
                Require(receiptPin is not null && Digest(receiptPin), "Invalid result receipt pin.");
                Require(publications.Count < limits.MaximumReceipts, "Consumer receipt verification budget exceeded.");
                Files(receiptPath, "manifest.json", "payload.json");
                var receipt = NewsReceiptImporter.Read(Path.Combine(receiptPath, "manifest.json"), Path.Combine(receiptPath, "payload.json"), receiptPin!);
                cancellationToken.ThrowIfCancellationRequested();
                Require(receipt.Manifest.Request == request && receipt.Manifest.Producer == "market_predictor" && receipt.Manifest.ProducerRevision == revision,
                    "Receipt request, producer or revision differs from pinned plan.");
                retainedBytes += intentBytes.Length + resultBytes.Length + receipt.ManifestBytes.Length + receipt.PayloadBytes.Length;
                Require(retainedBytes <= limits.MaximumRetainedBytes, "Consumer retained-byte budget exceeded.");
                publications.Add(new CollectedPublication(window.Id, index + 1, intentBytes, resultBytes, receipt));
                pageCount++;
                pageAttempts = 0;
                using var body = JsonDocument.Parse(receipt.PayloadBytes);
                var next = body.RootElement.TryGetProperty("next_page_token", out var token) ? token.GetString() : null;
                if (next is null) terminal = "complete";
                else if (!seenTokens.Add(next)) terminal = "pagination_cycle";
                else request = request with { PageToken = next };
            }
            var status = terminal ?? (pageCount >= maxPages ? "page_budget" : pageAttempts >= maxAttempts ? "attempt_budget" : "pending");
            reports.Add(new SharedNewsWindowReport(window.Id, status, attempts.Count, ambiguous, pageCount));
        }
        return new VerifiedPublicationPlan(bytes, revision, publications, reports);
    }
}
