using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingFlow.Data.Evidence.Collection;
using TradingFlow.Data.Evidence.Collection.SharedNews;

namespace TradingFlow.Tests;

public sealed class SharedNewsPublicationConsumerTests
{
    [Fact]
    public void PythonPublicationImportsExactBytesAndRestartIsVerifiedNoOp()
    {
        using var fixture = new PublicationFixture();
        var first = fixture.Import();
        Assert.Equal(1, first.Imported);
        Assert.Equal("complete", Assert.Single(first.Windows).Status);
        Assert.Equal(fixture.PlanPin, first.PlanSha256);
        var bundle = fixture.Bundle();
        Assert.Equal(fixture.Bytes("intent_utf8"), File.ReadAllBytes(Path.Combine(bundle, "intent.json")));
        Assert.Equal(fixture.Bytes("result_utf8"), File.ReadAllBytes(Path.Combine(bundle, "result.json")));
        Assert.Equal(fixture.Bytes("manifest_utf8"), File.ReadAllBytes(Path.Combine(bundle, "manifest.json")));
        Assert.Equal(fixture.Bytes("payload_utf8"), File.ReadAllBytes(Path.Combine(bundle, "payload.json")));
        Assert.Equal(fixture.Bytes("plan_utf8"), File.ReadAllBytes(Path.Combine(fixture.Inbox, "runs", fixture.PlanPin, "plan", "plan.json")));
        var acknowledgement = File.ReadAllBytes(Path.Combine(bundle, "acknowledgement.json"));
        var received = NewsReceiptImporter.Read(Path.Combine(bundle, "manifest.json"), Path.Combine(bundle, "payload.json"),
            PublicationFixture.Hash(fixture.Bytes("manifest_utf8")));
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero), received.Manifest.ReceivedAtUtc);
        Assert.False(received.ObservableAt(new DateTimeOffset(2019, 7, 10, 0, 0, 0, TimeSpan.Zero)));
        var again = fixture.Import();
        Assert.Equal(0, again.Imported);
        Assert.Equal(1, again.AlreadyImported);
        Assert.Equal(acknowledgement, File.ReadAllBytes(Path.Combine(bundle, "acknowledgement.json")));
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("owner")]
    [InlineData("intent")]
    [InlineData("result")]
    [InlineData("manifest")]
    [InlineData("payload")]
    public void TamperingFailsBeforeInboxCreation(string artifact)
    {
        using var fixture = new PublicationFixture();
        var path = artifact switch
        {
            "plan" => Path.Combine(fixture.Run, "plan", "plan.json"),
            "owner" => Path.Combine(fixture.Source, "owner", "owner.json"),
            "intent" => Path.Combine(fixture.Attempt(), "intent.json"),
            "result" => Path.Combine(fixture.Attempt(), "result", "result.json"),
            _ => Path.Combine(fixture.Attempt(), "receipt", artifact + ".json")
        };
        File.AppendAllText(path, " ");
        Assert.ThrowsAny<Exception>(() => fixture.Import());
        Assert.False(Directory.Exists(fixture.Inbox));
    }

    [Theory]
    [InlineData("producer", "trading_flow")]
    [InlineData("producer_revision", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public void InternallyValidReceiptMustMatchPinnedProducer(string field, string value)
    {
        using var fixture = new PublicationFixture();
        var manifest = fixture.Object("manifest_utf8");
        manifest[field] = value;
        fixture.WriteAttempt(1, manifest: manifest);
        Assert.Throws<InvalidDataException>(() => fixture.Import());
        Assert.False(Directory.Exists(fixture.Inbox));
    }

    [Fact]
    public void WrongOwnerRootAndWrongIndependentPinFailClosed()
    {
        using var fixture = new PublicationFixture();
        Assert.ThrowsAny<Exception>(() => SharedNewsPublicationConsumer.Import(fixture.Source, new string('0', 64), fixture.Inbox));
        var owner = JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.Source, "owner", "owner.json")))!.AsObject();
        owner["storage_root"] = fixture.Inbox;
        PublicationFixture.Write(Path.Combine(fixture.Source, "owner", "owner.json"), PublicationFixture.Canonical(owner));
        Assert.Throws<InvalidDataException>(() => fixture.Import());
        Assert.False(Directory.Exists(fixture.Inbox));
    }

    [Fact]
    public void ReceiptWithoutResultIsAmbiguousAndRetryKeepsAttemptIdentity()
    {
        using var fixture = new PublicationFixture();
        Directory.Delete(Path.Combine(fixture.Attempt(), "result"), true);
        var pending = fixture.Import();
        Assert.Equal(0, pending.Imported);
        Assert.Equal(1, Assert.Single(pending.Windows).AmbiguousAttempts);
        Assert.False(Directory.Exists(fixture.Inbox));
        fixture.WriteAttempt(2);
        var resumed = fixture.Import();
        Assert.Equal(1, resumed.Imported);
        Assert.Equal(1, Assert.Single(resumed.Windows).AmbiguousAttempts);
        Assert.True(File.Exists(Path.Combine(fixture.Attempt(), "receipt", "manifest.json")));
        Assert.True(Directory.Exists(fixture.Bundle(2)));
        Assert.False(Directory.Exists(fixture.Bundle(1)));
    }

    [Fact]
    public void AllWindowsAreValidatedBeforeImportingFirstGoodWindow()
    {
        using var fixture = new PublicationFixture();
        fixture.ChangePlan(plan =>
        {
            var second = plan["windows"]![0]!.DeepClone();
            second["window_id"] = "OTHER";
            second["request"]!["symbol"] = "AAPL";
            plan["windows"]!.AsArray().Add(second);
        });
        fixture.WriteAttempt(1);
        PublicationFixture.Write(Path.Combine(fixture.Run, "attempts", "OTHER", "000001", "intent.json"), "{}"u8.ToArray());
        Assert.ThrowsAny<Exception>(() => fixture.Import());
        Assert.False(Directory.Exists(fixture.Inbox));
    }

    [Fact]
    public void MissingLockIsNotCreatedAndHeldProducerLockFailsWithoutWriting()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new PublicationFixture();
        var path = Path.Combine(fixture.Source, "collection-owner.lock");
        using (var producer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            producer.Lock(0, 1);
            Assert.Throws<IOException>(() => fixture.Import());
        }
        File.Delete(path);
        Assert.Throws<FileNotFoundException>(() => fixture.Import());
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(fixture.Inbox));
    }

    [Fact]
    public void CancellationAndReceiptBudgetDoNotPublishPartialPreflight()
    {
        using var fixture = new PublicationFixture();
        Assert.Throws<OperationCanceledException>(() => SharedNewsPublicationConsumer.Import(fixture.Source, fixture.PlanPin, fixture.Inbox,
            cancellationToken: new CancellationToken(true)));
        var body = fixture.Object("payload_utf8");
        body["next_page_token"] = "next";
        fixture.WriteAttempt(1, payload: body);
        fixture.WriteAttempt(2, page: 2, token: "next");
        Assert.Throws<InvalidDataException>(() => SharedNewsPublicationConsumer.Import(fixture.Source, fixture.PlanPin, fixture.Inbox,
            new SharedNewsConsumptionLimits(MaximumReceipts: 1)));
        Assert.False(Directory.Exists(fixture.Inbox));
    }

    [Fact]
    public void PageProgressionPreservesSamePayloadUnderDistinctAttemptsAndReportsCycle()
    {
        using var fixture = new PublicationFixture();
        var body = fixture.Object("payload_utf8");
        body["next_page_token"] = "next";
        fixture.WriteAttempt(1, payload: body);
        fixture.WriteAttempt(2, page: 2, token: "next", payload: body);
        var report = fixture.Import();
        Assert.Equal(2, report.Imported);
        Assert.Equal("pagination_cycle", Assert.Single(report.Windows).Status);
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Bundle(1), "payload.json")), File.ReadAllBytes(Path.Combine(fixture.Bundle(2), "payload.json")));
        Assert.NotEqual(fixture.Bundle(1), fixture.Bundle(2));
        fixture.WriteAttempt(3, page: 3, token: "next");
        Assert.Throws<InvalidDataException>(() => fixture.Import());
    }

    [Theory]
    [InlineData("intent.json")]
    [InlineData("result.json")]
    [InlineData("manifest.json")]
    [InlineData("payload.json")]
    [InlineData("acknowledgement.json")]
    public void ExistingBundleMustBeCompleteAndByteIdentical(string name)
    {
        using var fixture = new PublicationFixture();
        fixture.Import();
        var path = Path.Combine(fixture.Bundle(), name);
        var original = File.ReadAllBytes(path);
        File.AppendAllText(path, " ");
        Assert.ThrowsAny<Exception>(() => fixture.Import());
        File.WriteAllBytes(path, original);
        File.Delete(path);
        Assert.ThrowsAny<Exception>(() => fixture.Import());
    }

    [Fact]
    public void PendingInboxIsIgnoredAndNeverAcknowledged()
    {
        using var fixture = new PublicationFixture();
        fixture.Import();
        var bundle = fixture.Bundle();
        var pending = Path.Combine(Path.GetDirectoryName(bundle)!, ".pending-" + Guid.NewGuid().ToString("N"));
        Directory.Move(bundle, pending);
        var report = fixture.Import();
        Assert.Equal(1, report.Imported);
        Assert.Equal(0, report.AlreadyImported);
        Assert.True(Directory.Exists(pending));
        Assert.Equal(1, fixture.Import().AlreadyImported);
    }

    [Fact]
    public void SourceRollbackCannotSilentlyForgetAnAcknowledgedAttempt()
    {
        using var fixture = new PublicationFixture();
        fixture.Import();
        Directory.Delete(fixture.Attempt(), true);
        Assert.Throws<InvalidDataException>(() => fixture.Import());
    }

    [Fact]
    public void UnknownFieldsDuplicatesAndNoncanonicalNumbersAreRejected()
    {
        using var fixture = new PublicationFixture();
        var intentPath = Path.Combine(fixture.Attempt(), "intent.json");
        var original = File.ReadAllText(intentPath);
        foreach (var bad in new[]
        {
            original.Replace("\"attempt_number\":1", "\"attempt_number\":1,\"attempt_number\":1", StringComparison.Ordinal),
            original.Replace("\"attempt_number\":1", "\"attempt_number\":1.0", StringComparison.Ordinal),
            original.Insert(1, "\"extra\":true,")
        })
        {
            File.WriteAllText(intentPath, bad);
            Assert.ThrowsAny<Exception>(() => fixture.Import());
        }
        Assert.False(Directory.Exists(fixture.Inbox));
    }

    [Fact]
    public void TransportFailuresConsumeAttemptsWithoutImportAndCapsRemainExplicit()
    {
        using var fixture = new PublicationFixture();
        Directory.Delete(fixture.Attempt(), true);
        for (var i = 1; i <= 3; i++) fixture.WriteAttempt(i, failure: true);
        var report = fixture.Import();
        Assert.Equal(0, report.Imported);
        Assert.Equal("attempt_budget", Assert.Single(report.Windows).Status);
        fixture.WriteAttempt(4, failure: true);
        Assert.Throws<InvalidDataException>(() => fixture.Import());
    }

    [Fact]
    public void UnexpectedWindowDiscontinuousAttemptAndPostTerminalAttemptFail()
    {
        using var fixture = new PublicationFixture();
        fixture.WriteAttempt(2);
        Assert.Throws<InvalidDataException>(() => fixture.Import());
        Directory.Delete(fixture.Attempt(1), true);
        Assert.Throws<InvalidDataException>(() => fixture.Import());
        Directory.CreateDirectory(Path.Combine(fixture.Run, "attempts", "UNKNOWN"));
        Assert.Throws<InvalidDataException>(() => fixture.Import());
        Assert.False(Directory.Exists(fixture.Inbox));
    }

    [Fact]
    public void OversizedMetadataAndNestedRootsAreRejected()
    {
        using var fixture = new PublicationFixture();
        Assert.Throws<InvalidDataException>(() => SharedNewsPublicationConsumer.Import(fixture.Source, fixture.PlanPin, Path.Combine(fixture.Source, "inbox")));
        File.WriteAllBytes(Path.Combine(fixture.Attempt(), "intent.json"), new byte[16_385]);
        Assert.Throws<InvalidDataException>(() => fixture.Import());
        Assert.False(Directory.Exists(fixture.Inbox));
    }

    [Fact]
    public void RepinnedReceiptWithWrongRequestCannotSatisfyTheIntent()
    {
        using var fixture = new PublicationFixture();
        var manifest = fixture.Object("manifest_utf8");
        manifest["request"]!["symbol"] = "AAPL";
        fixture.WriteAttempt(1, manifest: manifest);
        Assert.Throws<InvalidDataException>(() => fixture.Import());
        Assert.False(Directory.Exists(fixture.Inbox));
    }

    [Fact]
    public void PageCapIsNotReportedAsCompletedCoverage()
    {
        using var fixture = new PublicationFixture();
        fixture.ChangePlan(plan => plan["max_pages_per_window"] = 1);
        var body = fixture.Object("payload_utf8");
        body["next_page_token"] = "remaining";
        fixture.WriteAttempt(1, payload: body);
        var report = fixture.Import();
        Assert.Equal(1, report.Imported);
        Assert.Equal("page_budget", Assert.Single(report.Windows).Status);
        Assert.Equal(1, fixture.Import().AlreadyImported);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("../outside")]
    [InlineData("invalid:stream")]
    public void UnsafeWindowIdentityIsRejectedBeforeDiscovery(string window)
    {
        using var fixture = new PublicationFixture();
        fixture.ChangePlan(plan => plan["windows"]![0]!["window_id"] = window);
        Assert.Throws<InvalidDataException>(() => fixture.Import());
        Assert.False(Directory.Exists(fixture.Inbox));
    }

    [Fact]
    public void ReservedNamePrefixesAreNotMistakenForReservedWindows()
    {
        using var fixture = new PublicationFixture();
        fixture.ChangePlan(plan => plan["windows"]![0]!["window_id"] = "CONSUMER");
        var report = fixture.Import();
        Assert.Equal("pending", Assert.Single(report.Windows).Status);
        Assert.False(Directory.Exists(fixture.Inbox));
    }

    [Fact]
    public void TerminalFailuresCannotCarryAReceiptAndAttemptReadBudgetAppliesToExistingImports()
    {
        using var fixture = new PublicationFixture();
        fixture.WriteAttempt(1, failure: true);
        Assert.Throws<InvalidDataException>(() => fixture.Import());
        Directory.Delete(fixture.Attempt(), true);
        fixture.WriteAttempt(1, failure: true);
        fixture.WriteAttempt(2);
        Assert.Equal(1, fixture.Import().Imported);
        Assert.Throws<InvalidDataException>(() => SharedNewsPublicationConsumer.Import(fixture.Source, fixture.PlanPin, fixture.Inbox,
            new SharedNewsConsumptionLimits(MaximumAttempts: 1)));
    }

    [Fact]
    public void ReparsePointAncestorIsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new PublicationFixture();
        var link = Path.Combine(fixture.Home, "linked-source");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, fixture.Source },
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        Assert.True(process.WaitForExit(10_000));
        Assert.Equal(0, process.ExitCode);
        try { Assert.Throws<InvalidDataException>(() => SharedNewsPublicationConsumer.Import(link, fixture.PlanPin, fixture.Inbox)); }
        finally { Directory.Delete(link); }
    }

    internal sealed class PublicationFixture : IDisposable
    {
        private readonly JsonObject fixture;
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "tf-shared-news-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Home, "collector");
        public string Inbox => Path.Combine(Home, "inbox");
        public string PlanPin { get; private set; }
        public string Run => Path.Combine(Source, "runs", PlanPin);
        public string Attempt(int number = 1) => Path.Combine(Run, "attempts", "MSFT", number.ToString("D6", CultureInfo.InvariantCulture));
        public string Bundle(int number = 1) => Path.Combine(Inbox, "runs", PlanPin, "receipts", Hash(File.ReadAllBytes(Path.Combine(Attempt(number), "intent.json"))));
        public byte[] Bytes(string name) => Encoding.UTF8.GetBytes(fixture[name]!.GetValue<string>());
        public JsonObject Object(string name) => JsonNode.Parse(Bytes(name))!.AsObject();
        public SharedNewsImportReport Import() => SharedNewsPublicationConsumer.Import(Source, PlanPin, Inbox);

        public PublicationFixture()
        {
            fixture = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "news_collection_exchange.json")))!.AsObject();
            PlanPin = fixture["plan_sha256"]!.GetValue<string>();
            Assert.Equal(PlanPin, Hash(Bytes("plan_utf8")));
            Write(Path.Combine(Source, "owner", "owner.json"), Canonical(new JsonObject
            {
                ["schema_version"] = "alpaca.news_collection_owner.v1", ["owner"] = "market_predictor",
                ["coordination"] = "single_local_root", ["storage_root"] = Source
            }));
            Write(Path.Combine(Source, "collection-owner.lock"), [0]);
            Write(Path.Combine(Run, "plan", "plan.json"), Bytes("plan_utf8"));
            Write(Path.Combine(Attempt(), "intent.json"), Bytes("intent_utf8"));
            Write(Path.Combine(Attempt(), "result", "result.json"), Bytes("result_utf8"));
            Write(Path.Combine(Attempt(), "receipt", "manifest.json"), Bytes("manifest_utf8"));
            Write(Path.Combine(Attempt(), "receipt", "payload.json"), Bytes("payload_utf8"));
        }

        public void ChangePlan(Action<JsonObject> change)
        {
            var plan = Object("plan_utf8");
            change(plan);
            var bytes = Canonical(plan);
            PlanPin = Hash(bytes);
            Write(Path.Combine(Run, "plan", "plan.json"), bytes);
        }

        public void WriteAttempt(int number, int page = 1, string? token = null, JsonObject? payload = null, JsonObject? manifest = null, bool failure = false)
        {
            var intent = Object("intent_utf8");
            intent["plan_sha256"] = PlanPin;
            intent["attempt_number"] = number;
            intent["page_number"] = page;
            intent["request"]!["page_token"] = token;
            var intentBytes = Canonical(intent);
            Write(Path.Combine(Attempt(number), "intent.json"), intentBytes);
            var result = Object("result_utf8");
            result["intent_sha256"] = Hash(intentBytes);
            result["status"] = failure ? "transport_failure" : "received";
            result["receipt_sha256"] = null;
            if (!failure)
            {
                var bodyBytes = payload is null ? Bytes("payload_utf8") : Canonical(payload);
                manifest ??= Object("manifest_utf8");
                manifest["request"]!["page_token"] = token;
                manifest["payload_sha256"] = Hash(bodyBytes);
                manifest["payload_bytes"] = bodyBytes.Length;
                var manifestBytes = Canonical(manifest);
                result["receipt_sha256"] = Hash(manifestBytes);
                Write(Path.Combine(Attempt(number), "receipt", "manifest.json"), manifestBytes);
                Write(Path.Combine(Attempt(number), "receipt", "payload.json"), bodyBytes);
            }
            Write(Path.Combine(Attempt(number), "result", "result.json"), Canonical(result));
        }

        public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        public static void Write(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        public static byte[] Canonical(JsonNode node)
        {
            JsonNode? Sort(JsonNode? value) => value switch
            {
                JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Sort(p.Value)))),
                JsonArray array => new JsonArray(array.Select(Sort).ToArray()),
                _ => value?.DeepClone()
            };
            var json = Sort(node)!.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var ascii = new StringBuilder();
            foreach (var c in json) ascii.Append(c >= 127 ? "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture) : c.ToString());
            return Encoding.UTF8.GetBytes(ascii.ToString());
        }
        public void Dispose() => Directory.Delete(Home, recursive: true);
    }
}
