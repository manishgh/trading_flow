using System.Globalization;
using static TradingFlow.Data.Evidence.Collection.SharedNews.CollectionWire;
using static TradingFlow.Data.Evidence.Collection.SharedNews.LocalPublicationFiles;

namespace TradingFlow.Data.Evidence.Collection.SharedNews;

public sealed record SharedNewsConsumptionLimits(int MaximumAttempts = 10_000, int MaximumReceipts = 1_000,
    long MaximumRetainedBytes = 67_108_864);

public sealed record SharedNewsWindowReport(string WindowId, string Status, int Attempts, int AmbiguousAttempts, int ReceivedPages);
public sealed record SharedNewsImportReport(string PlanSha256, int Imported, int AlreadyImported,
    IReadOnlyList<SharedNewsWindowReport> Windows);

/// <summary>
/// Explicit offline raw import from an operator-controlled local collector root. The caller must
/// independently trust the root's writers/ACLs and configure the plan pin; hashing a mutable root
/// does not authenticate it. No provider, normalized evidence, coverage admission or desk feed is used.
/// A committed inbox bundle is its durable acknowledgement; pending staging is never acknowledged.
/// </summary>
public static class SharedNewsPublicationConsumer
{
    public const string ConsumerRevision = "shared_news_consumer.v1";

    public static SharedNewsImportReport Import(string trustedCollectorRoot, string expectedPlanSha256, string inboxRoot,
        SharedNewsConsumptionLimits? limits = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Require(expectedPlanSha256 is not null && Digest(expectedPlanSha256), "An independently configured lowercase SHA256 plan pin is required.");
        limits ??= new SharedNewsConsumptionLimits();
        Require(limits.MaximumAttempts is >= 1 and <= 100_000 && limits.MaximumReceipts is >= 1 and <= 10_000 &&
            limits.MaximumRetainedBytes is >= MaximumPlanBytes and <= 268_435_456, "Invalid bounded consumer limits.");
        var source = Root(trustedCollectorRoot);
        var inbox = Root(inboxRoot);
        Require(!Within(source, inbox) && !Within(inbox, source), "Collector and inbox roots must be disjoint.");
        // FileMode.Open and the producer's same nonqueueing byte-range lock: never initialize its root.
        using var sourceLock = Lock(Path.Combine(source, "collection-owner.lock"), create: false);
        var plan = SharedNewsPublicationVerifier.Verify(source, expectedPlanSha256!, limits, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Publications.Count == 0 && !Directory.Exists(inbox))
            return new SharedNewsImportReport(expectedPlanSha256!, 0, 0, plan.Windows);

        DirectoryPath(inbox);
        using var inboxLock = Lock(Path.Combine(inbox, "consumer.lock"), create: true);
        var ownerBytes = Encode(new
        {
            schema_version = "trading_flow.shared_news_inbox_owner.v1", consumer = "trading_flow",
            collector_root = source, inbox_root = inbox
        });
        foreach (var entry in Entries(inbox, 1024, cancellationToken))
            Require(Path.GetFileName(entry) is "owner" or "consumer.lock" or "runs" || Pending(entry), "Unexpected inbox artifact.");
        var ownerPath = Path.Combine(inbox, "owner");
        if (Directory.Exists(ownerPath)) Exact(ownerPath, "owner.json", ownerBytes, MaximumRecordBytes);
        else
        {
            Require(!Directory.Exists(Path.Combine(inbox, "runs")), "Inbox runs cannot exist without committed ownership.");
            Publish(ownerPath, new Dictionary<string, byte[]> { ["owner.json"] = ownerBytes }, cancellationToken);
        }

        var run = Path.Combine(inbox, "runs", expectedPlanSha256!);
        foreach (var entry in Entries(run, 32, cancellationToken))
            Require(Path.GetFileName(entry) is "plan" or "receipts" || Pending(entry), "Unexpected inbox run artifact.");
        var planPath = Path.Combine(run, "plan");
        if (Directory.Exists(planPath)) Exact(planPath, "plan.json", plan.Bytes, MaximumPlanBytes);
        else Require(!Directory.Exists(Path.Combine(run, "receipts")), "Inbox receipts cannot exist without their retained plan.");
        var receiptsPath = Path.Combine(run, "receipts");
        var expected = plan.Publications.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Entries(receiptsPath, limits.MaximumReceipts * 2 + 16, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Pending(entry)) continue;
            var key = Path.GetFileName(entry);
            Require(Directory.Exists(entry) && expected.ContainsKey(key), "Inbox contains an unverified or no-longer-published attempt.");
            VerifyBundle(entry, source, expectedPlanSha256!, plan.Revision, expected[key], cancellationToken);
            existing.Add(key);
        }

        // Source preflight and all existing acknowledgements pass before any new raw import.
        cancellationToken.ThrowIfCancellationRequested();
        DirectoryPath(run);
        if (!Directory.Exists(planPath)) Publish(planPath, new Dictionary<string, byte[]> { ["plan.json"] = plan.Bytes }, cancellationToken);
        DirectoryPath(receiptsPath);
        var imported = 0;
        foreach (var publication in plan.Publications)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (existing.Contains(publication.Key)) continue;
            var files = RawFiles(publication);
            files["acknowledgement.json"] = Acknowledgement(source, expectedPlanSha256!, plan.Revision, publication,
                DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
            Publish(Path.Combine(receiptsPath, publication.Key), files, cancellationToken);
            imported++;
        }
        return new SharedNewsImportReport(expectedPlanSha256!, imported, existing.Count, plan.Windows);
    }

    private static bool Within(string candidate, string root) => candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void Exact(string directory, string name, byte[] expected, int maximum)
    {
        Files(directory, name);
        Require(Read(Path.Combine(directory, name), maximum).AsSpan().SequenceEqual(expected), "Existing inbox metadata differs from trusted publication.");
    }

    private static Dictionary<string, byte[]> RawFiles(CollectedPublication publication) => new()
    {
        ["intent.json"] = publication.Intent, ["result.json"] = publication.Result,
        ["manifest.json"] = publication.Receipt.ManifestBytes.ToArray(), ["payload.json"] = publication.Receipt.PayloadBytes.ToArray()
    };

    private static byte[] Acknowledgement(string source, string pin, string revision, CollectedPublication publication, string importedAt) => Encode(new
    {
        schema_version = "trading_flow.shared_news_import.v1", consumer = "trading_flow", consumer_revision = ConsumerRevision,
        collector_root = source, plan_sha256 = pin, producer_revision = revision,
        window_id = publication.WindowId, attempt_number = publication.AttemptNumber,
        intent_sha256 = publication.Key, result_sha256 = Hash(publication.Result),
        receipt_sha256 = publication.Receipt.ReceiptId, payload_sha256 = publication.Receipt.Manifest.PayloadSha256,
        imported_at_utc = importedAt
    });

    private static void VerifyBundle(string path, string source, string pin, string revision, CollectedPublication publication, CancellationToken cancellationToken)
    {
        Files(path, "intent.json", "result.json", "manifest.json", "payload.json", "acknowledgement.json");
        foreach (var (name, expected) in RawFiles(publication))
            Require(Read(Path.Combine(path, name), expected.Length, cancellationToken).AsSpan().SequenceEqual(expected), "Existing inbox raw bytes differ from trusted publication.");
        var acknowledgement = Read(Path.Combine(path, "acknowledgement.json"), MaximumRecordBytes, cancellationToken);
        var record = Parse(acknowledgement);
        var importedAt = Text(record, "imported_at_utc");
        _ = Clock(importedAt);
        Require(acknowledgement.AsSpan().SequenceEqual(Acknowledgement(source, pin, revision, publication, importedAt)),
            "Existing acknowledgement differs from its publication provenance.");
    }
}
