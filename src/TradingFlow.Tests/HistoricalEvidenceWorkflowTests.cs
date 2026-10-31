using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingFlow.Data.Evidence.Historical;

namespace TradingFlow.Tests;

public sealed class HistoricalEvidenceWorkflowTests
{
    private static readonly DateTimeOffset Capture = DateTimeOffset.Parse("2026-10-19T12:00:00Z", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset Decision = DateTimeOffset.Parse("2026-04-07T13:30:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void CurrentArchiveDoesNotBecomeOriginalHistoricalDelivery()
    {
        using var fixture = new CollectionFixture();
        fixture.Standard();
        var manifest = fixture.Build();
        var news = Assert.Single(manifest.Facts.Where(fact => fact.Kind == HistoricalFactKind.NewsVersion));
        Assert.Equal(Capture, news.ActualReceivedAtUtc);
        var strict = HistoricalEvidenceResolver.Resolve(manifest, news, Decision);
        Assert.False(strict.Available);
        Assert.Equal("original_delivery_and_revision_lineage_not_proven", strict.Reason);
        Assert.Null(strict.AvailableAtUtc);
        Assert.False(HistoricalEvidenceResolver.Resolve(manifest, news, Decision, HistoricalAvailabilityMode.ObservedCapture).Available);
        var afterCapture = HistoricalEvidenceResolver.Resolve(manifest, news, Capture, HistoricalAvailabilityMode.ObservedCapture);
        Assert.True(afterCapture.Available);
        Assert.Equal("observed_capture", afterCapture.Status);
        Assert.Equal(Capture, afterCapture.ActualReceivedAtUtc);
    }

    [Fact]
    public void ConservativeNewsUsesCapturedHolidayCalendarAndKeepsActualReceipt()
    {
        using var fixture = new CollectionFixture();
        fixture.Standard();
        var manifest = fixture.Build();
        var news = manifest.Facts.Single(fact => fact.Kind == HistoricalFactKind.NewsVersion);
        var result = HistoricalEvidenceResolver.Resolve(manifest, news, Decision, HistoricalAvailabilityMode.ConservativeProviderUpdateDiagnostic);
        Assert.True(result.Available);
        Assert.Equal(DateTimeOffset.Parse("2026-04-06T13:30:00Z", CultureInfo.InvariantCulture), result.AvailableAtUtc);
        Assert.Equal("reconstructed_diagnostic", result.Status);
        Assert.Equal(Capture, result.ActualReceivedAtUtc);
        Assert.Equal(64, result.ReconstructionPolicySha256!.Length);
        Assert.NotEqual(HistoricalEvidenceResolver.PolicySha256(HistoricalReconstructionPolicy.ConservativeV1),
            HistoricalEvidenceResolver.PolicySha256(HistoricalReconstructionPolicy.ConservativeV1 with { ProviderUpdateLag = TimeSpan.FromHours(48) }));
    }

    [Fact]
    public void CalendarIsNotExtendedByAssumingTheNextWeekday()
    {
        using var fixture = new CollectionFixture();
        fixture.Standard();
        fixture.Capture("news/late/page-0001", "alpaca-data", "/v1beta1/news", new { news = new[] {
            Article(2, "2026-10-06T20:00:00Z", "2026-10-06T20:00:00Z") }, next_page_token = (string?)null });
        var manifest = fixture.Build();
        var late = manifest.Facts.Single(fact => fact.SourceRecordId == "2");
        Assert.Equal("next_session_outside_captured_calendar", HistoricalEvidenceResolver.Resolve(manifest, late,
            Capture, HistoricalAvailabilityMode.ConservativeProviderUpdateDiagnostic).Reason);
    }

    [Theory]
    [InlineData(HistoricalFactKind.CurrentAsset)]
    [InlineData(HistoricalFactKind.CurrentSector)]
    public void CurrentIdentityAndSectorNeverPassAsHistoricalMembership(HistoricalFactKind kind)
    {
        using var fixture = new CollectionFixture();
        fixture.Standard();
        var manifest = fixture.Build();
        var fact = manifest.Facts.Single(fact => fact.Kind == kind);
        foreach (var mode in Enum.GetValues<HistoricalAvailabilityMode>())
        {
            var result = HistoricalEvidenceResolver.Resolve(manifest, fact, Capture.AddYears(1), mode);
            Assert.False(result.Available);
            Assert.Equal("current_only", result.Status);
        }
    }

    [Fact]
    public void HistoricalEarningsResultsCannotBeUsedAsOldSchedulesOrConsensus()
    {
        using var fixture = new CollectionFixture();
        fixture.Standard();
        var manifest = fixture.Build();
        var fact = manifest.Facts.Single(fact => fact.Kind == HistoricalFactKind.EarningsCurrentVintage);
        Assert.Equal(DateTimeOffset.Parse("2026-04-06T20:30:00Z", CultureInfo.InvariantCulture), fact.EffectiveAtUtc);
        Assert.False(HistoricalEvidenceResolver.Resolve(manifest, fact, Decision, HistoricalAvailabilityMode.ObservedCapture).Available);
        Assert.Equal("provider_update_reconstruction_is_news_only", HistoricalEvidenceResolver.Resolve(manifest, fact,
            Decision, HistoricalAvailabilityMode.ConservativeProviderUpdateDiagnostic).Reason);
        Assert.Contains(manifest.Claims, claim => claim.Claim == "pre_entry_earnings_schedule_and_consensus" && claim.Status == "unproved");
        Assert.DoesNotContain(manifest.Facts, item => item.Subject == "OTHER");
    }

    [Fact]
    public void CorporateActionWithoutFactorOrExDateIsRetainedWithoutInventingThem()
    {
        using var fixture = new CollectionFixture();
        fixture.Standard();
        var manifest = fixture.Build();
        var action = manifest.Facts.Single(fact => fact.Kind == HistoricalFactKind.CorporateAction);
        Assert.Null(action.EffectiveAtUtc);
        using var value = JsonDocument.Parse(action.ValueJson);
        Assert.False(value.RootElement.GetProperty("adjustment_factor_reconstructed").GetBoolean());
        Assert.Contains(manifest.Claims, claim => claim.Claim == "decision_time_adjustment_factors" && claim.Status == "unproved");
    }

    [Fact]
    public void InvertedNewsTimesRemainQuarantinedInEveryMode()
    {
        using var fixture = new CollectionFixture();
        fixture.Standard();
        fixture.Capture("news/inverted/page-0001", "alpaca-data", "/v1beta1/news", new { news = new[] {
            Article(3, "2026-04-02T10:00:00Z", "2026-04-02T09:59:59Z") }, next_page_token = (string?)null });
        var manifest = fixture.Build();
        var inverted = manifest.Facts.Single(fact => fact.SourceRecordId == "3");
        foreach (var mode in Enum.GetValues<HistoricalAvailabilityMode>())
            Assert.Equal("news_update_precedes_publication", HistoricalEvidenceResolver.Resolve(manifest, inverted, Decision, mode).Reason);
    }

    [Fact]
    public void DuplicateCapturedVersionKeepsBothPhysicalSourcesAndEarliestActualReceipt()
    {
        using var fixture = new CollectionFixture();
        fixture.Standard();
        fixture.Capture("marketwide-news/2026-04/page-0001", "alpaca-data", "/v1beta1/news",
            new { news = new[] { Article() }, next_page_token = (string?)null }, Capture.AddHours(1));
        var manifest = fixture.Build();
        var news = manifest.Facts.Single(fact => fact.Kind == HistoricalFactKind.NewsVersion);
        Assert.Equal(2, news.SourceObservationIds.Length);
        Assert.Equal(Capture, news.ActualReceivedAtUtc);
        Assert.Equal(manifest.Facts.Length, HistoricalAvailabilityAudit.Build(manifest, [Decision], HistoricalAvailabilityMode.StrictOriginalDelivery).Sum(row => row.Count));
    }

    [Fact]
    public void SecAcceptanceIsSeparateFromOriginalDeliveryAndDoesNotImportCurrentSicAsOldSector()
    {
        using var fixture = new CollectionFixture();
        fixture.Standard();
        fixture.Capture("sec/test", "sec-edgar", "https://data.sec.gov/submissions/CIK0000000001.json", new {
            cik = 1, sic = "9999", tickers = new[] { "CURRENT" }, filings = new { recent = new {
                accessionNumber = new[] { "0000000001-26-000001" }, acceptanceDateTime = new[] { "2026-04-02T10:00:00Z" }, form = new[] { "8-K" } } } });
        var manifest = fixture.Build();
        var filing = manifest.Facts.Single(fact => fact.Kind == HistoricalFactKind.OfficialFilingAcceptance);
        Assert.Equal("SEC-CIK:0000000001", filing.Subject);
        Assert.DoesNotContain("9999", filing.ValueJson);
        Assert.DoesNotContain("CURRENT", filing.ValueJson);
        Assert.False(HistoricalEvidenceResolver.Resolve(manifest, filing, Decision).Available);
        var official = HistoricalEvidenceResolver.Resolve(manifest, filing, Decision, HistoricalAvailabilityMode.OfficialArchivePublicationDiagnostic);
        Assert.True(official.Available);
        Assert.Equal("reconstructed_diagnostic", official.Status);
    }

    [Fact]
    public void ManifestPublicationIsExclusiveAndDetectsChangedSourceBytes()
    {
        using var fixture = new CollectionFixture();
        fixture.Standard();
        var manifest = fixture.Build();
        var output = Path.Combine(fixture.Root, "published");
        HistoricalEvidenceManifestStore.WriteExclusive(output, manifest);
        Assert.Equal(manifest.Facts.Length, HistoricalEvidenceManifestStore.LoadVerified(output).Facts.Length);
        Assert.Throws<IOException>(() => HistoricalEvidenceManifestStore.WriteExclusive(output, manifest));
        var payload = Path.Combine(fixture.Root, manifest.Observations[0].PayloadRelativePath);
        File.AppendAllText(payload, " ");
        Assert.Throws<InvalidDataException>(() => HistoricalEvidenceManifestStore.LoadVerified(output));
    }

    [Fact]
    public void EscapedPayloadPathIsRejectedBeforeReadingOutsideCollection()
    {
        using var fixture = new CollectionFixture();
        fixture.Capture("news/escape", "alpaca-data", "/v1beta1/news", new { news = new[] { Article() }, next_page_token = (string?)null });
        var receipt = Path.Combine(fixture.Root, "receipts", "news", "escape.json");
        var value = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(receipt))!;
        value["Payload"] = JsonSerializer.SerializeToElement("../outside-secret.json");
        File.WriteAllText(receipt, JsonSerializer.Serialize(value));
        Assert.Throws<InvalidDataException>(() => fixture.Build());
    }

    [Fact]
    public void ClaimedOriginalReceiptBooleanAndFreeTextCannotCertifyOldDelivery()
    {
        using var fixture = new CollectionFixture();
        fixture.Standard();
        var manifest = fixture.Build();
        var receipt = Path.Combine(fixture.Root, "receipts", "news", "stocks-1", "page-0001.json");
        var value = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(receipt))!;
        value["HistoricalFirstReceiptClaim"] = JsonSerializer.SerializeToElement(true);
        value["OriginalDeliveryEvidence"] = JsonSerializer.SerializeToElement("claimed original; no independently bound proof");
        File.WriteAllText(receipt, JsonSerializer.Serialize(value));
        Assert.Throws<InvalidDataException>(() => fixture.Build());
        var forged = manifest with { Observations = manifest.Observations.SetItem(0,
            manifest.Observations[0] with { OriginalDeliveryObserved = true, OriginalDeliveryEvidence = "unverified free text" }) };
        Assert.Throws<InvalidDataException>(() => HistoricalEvidenceManifestStore.Validate(forged));
    }

    [Fact]
    public void PublicationHashAndActualReceiptCannotBeSilentlyChanged()
    {
        using var fixture = new CollectionFixture();
        fixture.Standard();
        var manifest = fixture.Build();
        var newsIndex = manifest.Facts.Select((fact, index) => (fact, index)).Single(item => item.fact.Kind == HistoricalFactKind.NewsVersion).index;
        var changed = manifest with { Facts = manifest.Facts.SetItem(newsIndex, manifest.Facts[newsIndex] with { ActualReceivedAtUtc = Decision }) };
        Assert.Throws<InvalidDataException>(() => HistoricalEvidenceManifestStore.Validate(changed));
        var output = Path.Combine(fixture.Root, "published");
        HistoricalEvidenceManifestStore.WriteExclusive(output, manifest);
        File.AppendAllText(Path.Combine(output, "historical-manifest.json"), " ");
        Assert.Throws<InvalidDataException>(() => HistoricalEvidenceManifestStore.LoadVerified(output, false));
    }

    private static object Article(int id = 1, string created = "2026-04-02T10:00:00Z", string updated = "2026-04-02T10:00:00Z") =>
        new { id, created_at = created, updated_at = updated, symbols = new[] { "TEST" }, headline = "Exact headline", content = "", summary = "" };

    private sealed class CollectionFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tf-historical-tests-" + Guid.NewGuid().ToString("N"));
        public void Capture(string name, string provider, string resource, object payload, DateTimeOffset? capture = null, object? parameters = null)
        {
            var payloadRelative = Path.Combine("datasets", name + ".txt");
            var receiptRelative = Path.Combine("receipts", name + ".json");
            var bytes = payload is string text ? Encoding.UTF8.GetBytes(text) : JsonSerializer.SerializeToUtf8Bytes(payload);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Root, payloadRelative))!);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Root, receiptRelative))!);
            File.WriteAllBytes(Path.Combine(Root, payloadRelative), bytes);
            File.WriteAllText(Path.Combine(Root, receiptRelative), JsonSerializer.Serialize(new {
                Provider = provider, Resource = resource, Parameters = parameters ?? new { }, HttpStatus = 200,
                CapturedUtc = capture ?? HistoricalEvidenceWorkflowTests.Capture, Payload = payloadRelative,
                PayloadSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), HistoricalFirstReceiptClaim = false }));
        }
        public void Standard()
        {
            Capture("finviz-current-sectors/batch-01", "finviz", "/export", "\"Ticker\",\"Sector\",\"Industry\"\n\"TEST\",\"Technology\",\"Hardware\"\n", parameters: new { t = "TEST" });
            Capture("assets/TEST", "alpaca-trading-readonly", "/v2/assets/TEST", new { id = "current-asset-uuid", symbol = "TEST", @class = "us_equity" });
            Capture("news/stocks-1/page-0001", "alpaca-data", "/v1beta1/news", new { news = new[] { Article() }, next_page_token = (string?)null });
            Capture("earnings/day/page-001", "finviz", "/api/calendar/earnings", new { items = new[] {
                new { ticker = "TEST", earningsDate = "2026-04-06T16:30:00", epsEstimate = 1, epsActual = 2 },
                new { ticker = "OTHER", earningsDate = "2026-04-06T16:30:00", epsEstimate = 1, epsActual = 2 } } });
            Capture("corporate-actions/batch-01/page-0001", "alpaca-data", "/v1/corporate-actions", new { corporate_actions = new {
                name_changes = new[] { new { id = "action-1", old_symbol = "TEST", new_symbol = "TEST", process_date = "2026-04-06" } } }, next_page_token = (string?)null });
            Capture("calendar/daily-session-window", "alpaca-trading-readonly", "/v2/calendar", new[] {
                new { date = "2026-04-02", open = "09:30", close = "16:00" },
                new { date = "2026-04-06", open = "09:30", close = "16:00" },
                new { date = "2026-04-07", open = "09:30", close = "16:00" } });
        }
        public HistoricalEvidenceManifest Build() => HistoricalEvidenceManifestBuilder.BuildFromCollectionRoot(Root, HistoricalEvidenceWorkflowTests.Capture.AddDays(1));
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("tf-historical-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside owned temporary test root.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
