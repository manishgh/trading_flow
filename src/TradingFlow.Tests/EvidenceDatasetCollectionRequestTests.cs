using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingFlow.Data.Evidence.Normalization;
using TradingFlow.Domain.Research;
using TradingFlow.Research.Orchestration;

namespace TradingFlow.Tests;

public sealed class EvidenceDatasetCollectionRequestTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 1, 13, 30, 0, TimeSpan.Zero);

    [Fact]
    public void FrozenRequest_RoundTripsAndBuildsMatchingJobs()
    {
        var request = BarsRequest();
        var serializerOptions = new JsonSerializerOptions();
        serializerOptions.Converters.Add(new JsonStringEnumConverter());

        var json = JsonSerializer.Serialize(request, serializerOptions);
        var replay = JsonSerializer.Deserialize<EvidenceDatasetCollectionRequest>(
            json,
            serializerOptions)!;
        var firstPlan = request.CreatePlan();
        var replayPlan = replay.CreatePlan();

        Assert.Equal(firstPlan.JobId, replayPlan.JobId);
        Assert.Equal(firstPlan.LogicalPlanHash, replayPlan.LogicalPlanHash);
        Assert.Equal(
            firstPlan.JobId,
            replay.CreateNormalizationJob(replayPlan).JobId);
    }

    [Fact]
    public void AbsoluteProviderEndpoint_IsRejectedBeforeCredentialsCanBeAttached()
    {
        var request = BarsRequest().Requests[0];

        var error = Assert.Throws<ArgumentException>(() =>
            Create(
                EvidenceDatasetKind.MarketBarsAsTraded,
                [
                    new EvidenceCollectionRequest(
                        request.RequestId,
                        request.Provider,
                        "https://attacker.example/v2/stocks/bars",
                        request.Symbols,
                        request.RequestedStartUtc,
                        request.RequestedEndUtc,
                        request.DataFeed,
                        request.Adjustment,
                        request.Currency,
                        request.AsOfDate,
                        request.Parameters)
                ]));

        Assert.Contains("absolute URLs are forbidden", error.Message);
    }

    [Fact]
    public void NewsDataset_RequiresExplicitAvailabilityEvidence()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            Create(
                EvidenceDatasetKind.NewsRevisions,
                [
                    new EvidenceCollectionRequest(
                        "news",
                        "alpaca",
                        "/v1beta1/news",
                        ["AAPL"],
                        Start,
                        Start.AddDays(1),
                        "alpaca_news",
                        "raw",
                        "USD",
                        new DateOnly(2026, 7, 2))
                ]));

        Assert.Contains("availability policy", error.Message);
    }

    [Fact]
    public void NewsDataset_RejectsUnscopedProviderRequest()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            Create(
                EvidenceDatasetKind.NewsRevisions,
                [
                    new EvidenceCollectionRequest(
                        "news",
                        "alpaca",
                        "/v1beta1/news",
                        [],
                        Start,
                        Start.AddDays(1),
                        "alpaca_news",
                        "raw",
                        "USD",
                        new DateOnly(2026, 7, 2))
                ],
                NewsAvailabilityEvidence.ProviderTimestampOnly));

        Assert.Contains("explicitly name", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DatasetKindAndEndpointMismatch_IsRejected()
    {
        var bars = BarsRequest().Requests[0];

        var error = Assert.Throws<ArgumentException>(() =>
            Create(
                EvidenceDatasetKind.SipQuotes,
                [bars]));

        Assert.Contains("/v2/stocks/quotes", error.Message);
    }

    [Fact]
    public void AdjustedBars_RequireAllAdjustment()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            Create(
                EvidenceDatasetKind.MarketBarsResearchAdjusted,
                [
                    new EvidenceCollectionRequest(
                        "bars",
                        "alpaca",
                        "/v2/stocks/bars",
                        ["AAPL"],
                        Start,
                        Start.AddDays(1),
                        "sip",
                        "split",
                        "USD",
                        new DateOnly(2026, 7, 2),
                        new Dictionary<string, string>
                        {
                            ["timeframe"] = "1Day"
                        })
                ]));

        Assert.Contains("adjustment 'all'", error.Message);
    }

    [Fact]
    public void ExchangeCalendarRequest_AllowsNoSecurityIdentityAndRequiresOfficialContract()
    {
        var request = new EvidenceDatasetCollectionRequest(
            Start.AddDays(-1),
            "calendar-request-test",
            Hash("calendar-request"),
            "request-tests",
            "collector-v1",
            "partition-v1",
            EvidenceDatasetKind.ExchangeSessions,
            1,
            "calendar-normalizer-v1",
            "normalized/calendar",
            [
                new EvidenceCollectionRequest(
                    "calendar",
                    "alpaca",
                    "/v2/calendar",
                    [],
                    new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 7, 8, 0, 0, 0, TimeSpan.Zero),
                    "alpaca-trading",
                    "raw",
                    "USD",
                    new DateOnly(2026, 7, 8))
            ],
            []);

        var job = request.CreateNormalizationJob(request.CreatePlan());

        Assert.Empty(job.Securities);
        Assert.Equal(EvidenceDatasetKind.ExchangeSessions, job.DatasetKind);
    }

    [Fact]
    public void NormalizationContractChangesCollectionIdentity()
    {
        var baseline = BarsRequest();
        var variants = new[]
        {
            Copy(baseline, schemaVersion: 2),
            Copy(baseline, normalizerVersion: "normalizer-v2"),
            Copy(
                baseline,
                securities: [new EvidenceSecurityIdentity("security-aapl-v2", "AAPL")]),
            Copy(baseline, outputNamespace: "normalized/request-contract-v2"),
            Copy(
                baseline,
                attributes: new Dictionary<string, string>
                {
                    ["quality-policy"] = "strict"
                })
        };

        foreach (var variant in variants)
        {
            Assert.NotEqual(
                baseline.NormalizationContractHash,
                variant.NormalizationContractHash);
            Assert.NotEqual(
                baseline.CreatePlan().LogicalPlanHash,
                variant.CreatePlan().LogicalPlanHash);
            Assert.NotEqual(
                baseline.CreatePlan().JobId,
                variant.CreatePlan().JobId);
        }
    }

    [Fact]
    public void NewsAvailabilityPolicyChangesCollectionIdentity()
    {
        var providerTimestamp = NewsRequest(
            NewsAvailabilityEvidence.ProviderTimestampOnly);
        var observedReceipt = NewsRequest(
            NewsAvailabilityEvidence.ObservedReceiptTime);

        Assert.NotEqual(
            providerTimestamp.NormalizationContractHash,
            observedReceipt.NormalizationContractHash);
        Assert.NotEqual(
            providerTimestamp.CreatePlan().LogicalPlanHash,
            observedReceipt.CreatePlan().LogicalPlanHash);
        Assert.NotEqual(
            providerTimestamp.CreatePlan().JobId,
            observedReceipt.CreatePlan().JobId);
    }

    private static EvidenceDatasetCollectionRequest BarsRequest() =>
        Create(
            EvidenceDatasetKind.MarketBarsAsTraded,
            [
                new EvidenceCollectionRequest(
                    "bars",
                    "alpaca",
                    "/v2/stocks/bars",
                    ["AAPL"],
                    Start,
                    Start.AddDays(1),
                    "sip",
                    "raw",
                    "USD",
                    new DateOnly(2026, 7, 2),
                    new Dictionary<string, string>
                    {
                        ["timeframe"] = "1Min"
                    })
            ]);

    private static EvidenceDatasetCollectionRequest NewsRequest(
        NewsAvailabilityEvidence availability) =>
        Create(
            EvidenceDatasetKind.NewsRevisions,
            [
                new EvidenceCollectionRequest(
                    "news",
                    "alpaca",
                    "/v1beta1/news",
                    ["AAPL"],
                    Start,
                    Start.AddDays(1),
                    "alpaca_news",
                    "raw",
                    "USD",
                    new DateOnly(2026, 7, 2))
            ],
            availability);

    private static EvidenceDatasetCollectionRequest Create(
        EvidenceDatasetKind kind,
        IReadOnlyList<EvidenceCollectionRequest> requests,
        NewsAvailabilityEvidence? newsAvailability = null) =>
        new(
            Start.AddDays(-1),
            "request-contract-test",
            Hash("request-contract"),
            "request-tests",
            "collector-v1",
            "partition-v1",
            kind,
            1,
            "normalizer-v1",
            "normalized/request-contract",
            requests,
            [new EvidenceSecurityIdentity("security-aapl", "AAPL")],
            newsAvailability);

    private static EvidenceDatasetCollectionRequest Copy(
        EvidenceDatasetCollectionRequest source,
        int? schemaVersion = null,
        string? normalizerVersion = null,
        IReadOnlyCollection<EvidenceSecurityIdentity>? securities = null,
        string? outputNamespace = null,
        IReadOnlyDictionary<string, string>? attributes = null) =>
        new(
            source.CreatedAtUtc,
            source.RunId,
            source.ConfigHash,
            source.CodeVersion,
            source.CollectionPolicyVersion,
            source.PartitionPolicyVersion,
            source.DatasetKind,
            schemaVersion ?? source.SchemaVersion,
            normalizerVersion ?? source.NormalizerVersion,
            outputNamespace ?? source.OutputNamespace,
            source.Requests,
            securities ?? source.Securities,
            source.NewsAvailabilityEvidence,
            attributes ?? source.Attributes);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
