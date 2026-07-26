using System.Security.Cryptography;
using System.Text;
using Moq;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Research;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Research.Catalysts;
using TradingFlow.Research.Workflows;

namespace TradingFlow.Tests;

public sealed class CatalogProviderUpdatedDailyNewsWorkflowTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 7, 0, 0, 0, TimeSpan.Zero);
    private static readonly EvidenceRowSourceAddress Source =
        new("observation", Hash("source"));
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-daily-news-workflow-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Diagnostic_IsCatalogOnlyImmutableAndPermanentlyNonPromotable()
    {
        var newsManifest = Manifest(
            EvidenceDatasetKind.NewsRevisions,
            "alpaca_news",
            "none",
            "news",
            ["AAPL"],
            new Dictionary<string, string>
            {
                ["historical_availability_proven"] = "false",
                ["provider_updated_timestamp_only"] = "true"
            });
        var barsManifest = Manifest(
            EvidenceDatasetKind.MarketBarsAsTraded,
            "sip",
            "raw",
            "1d",
            ["AAPL", "SPY"]);
        var sessionsManifest = Manifest(
            EvidenceDatasetKind.ExchangeSessions,
            "alpaca_trading",
            "none",
            "session",
            ["XNYS"]);
        var manifests = new[] { newsManifest, barsManifest, sessionsManifest };
        EvidenceResearchRunManifest? registered = null;
        EvidenceHoldoutConsumption? reserved = null;

        var catalog = new Mock<IEvidenceCatalog>(MockBehavior.Strict);
        catalog
            .Setup(value => value.GetDatasetAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
                manifests.SingleOrDefault(manifest =>
                    manifest.DatasetId.Equals(id, StringComparison.Ordinal)));
        catalog
            .Setup(value => value.ReserveHoldoutAsync(
                It.IsAny<EvidenceHoldoutConsumption>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                EvidenceHoldoutConsumption consumption,
                CancellationToken _) =>
            {
                if (reserved is null)
                {
                    reserved = consumption;
                    return new EvidenceCatalogCommitResult(
                        consumption.HoldoutId,
                        false);
                }

                Assert.Equal(reserved, consumption);
                return new EvidenceCatalogCommitResult(
                    consumption.HoldoutId,
                    true);
            });
        catalog
            .Setup(value => value.RegisterResearchRunAsync(
                It.IsAny<EvidenceResearchRunManifest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                EvidenceResearchRunManifest manifest,
                CancellationToken _) =>
            {
                if (registered is null)
                {
                    registered = manifest;
                    return new EvidenceCatalogCommitResult(
                        manifest.ResearchRunId,
                        false);
                }

                Assert.Equal(
                    EvidenceCanonicalJson.SerializeToUtf8Bytes(registered),
                    EvidenceCanonicalJson.SerializeToUtf8Bytes(manifest));
                return new EvidenceCatalogCommitResult(
                    manifest.ResearchRunId,
                    true);
            });
        catalog
            .Setup(value => value.GetResearchRunAsync(
                "daily-news-run",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => registered);

        var reader = new Mock<IEvidencePartitionDataReader>(MockBehavior.Strict);
        reader
            .Setup(value => value.ReadNewsRevisionsAsync(
                newsManifest,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([News()]);
        reader
            .Setup(value => value.ReadMarketBarsAsync(
                barsManifest,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Bars());
        reader
            .Setup(value => value.ReadExchangeSessionsAsync(
                sessionsManifest,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Sessions());

        var workflow = Workflow(catalog.Object, reader.Object);
        var request = Request(newsManifest, barsManifest, sessionsManifest);
        var first =
            await workflow.RunProviderUpdatedDailyNewsDiagnosticAsync(request);
        var replay =
            await workflow.RunProviderUpdatedDailyNewsDiagnosticAsync(request);

        Assert.False(first.AlreadyRegistered);
        Assert.True(replay.AlreadyRegistered);
        Assert.NotNull(reserved);
        Assert.Equal(
            EvidenceCanonicalJson.SerializeToUtf8Bytes(first.Manifest),
            EvidenceCanonicalJson.SerializeToUtf8Bytes(replay.Manifest));
        Assert.False(first.Manifest.EvidenceReady);
        Assert.False(first.Report.PromotionEligible);
        Assert.Contains(
            "historical_news_first_seen_unproven",
            first.Manifest.ReadinessFailures);
        Assert.Contains(
            CatalogResearchWorkflow.StaticUniversePromotionBlocker,
            first.Manifest.ReadinessFailures);
        Assert.Single(first.Report.StoryClusters);
        Assert.Single(first.Report.EventObservations);
        Assert.Single(first.Report.HorizonReturns);
        Assert.Equal(7, first.Manifest.Outputs.Count);
        catalog.VerifyAll();
        reader.VerifyAll();
    }

    [Fact]
    public async Task Diagnostic_RejectsCostAssumptionsThatDifferFromStudyOptions()
    {
        var request = Request(
            Manifest(
                EvidenceDatasetKind.NewsRevisions,
                "alpaca_news",
                "none",
                "news",
                ["AAPL"],
                new Dictionary<string, string>
                {
                    ["historical_availability_proven"] = "false",
                    ["provider_updated_timestamp_only"] = "true"
                }),
            Manifest(
                EvidenceDatasetKind.MarketBarsAsTraded,
                "sip",
                "raw",
                "1d",
                ["AAPL", "SPY"]),
            Manifest(
                EvidenceDatasetKind.ExchangeSessions,
                "alpaca_trading",
                "none",
                "session",
                ["XNYS"])) with
        {
            Options = new ProviderUpdatedDailyNewsStudyOptions("SPY", [0], 10m)
        };
        var catalog = new Mock<IEvidenceCatalog>(MockBehavior.Strict);
        var reader = new Mock<IEvidencePartitionDataReader>(MockBehavior.Strict);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            Workflow(catalog.Object, reader.Object)
                .RunProviderUpdatedDailyNewsDiagnosticAsync(request));

        Assert.Contains(
            "exactly match",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        catalog.VerifyNoOtherCalls();
        reader.VerifyNoOtherCalls();
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private CatalogResearchWorkflow Workflow(
        IEvidenceCatalog catalog,
        IEvidencePartitionDataReader reader) =>
        new(
            catalog,
            reader,
            new EvidenceResearchRunArtifactPackager(
                new FileSystemImmutableArtifactStore(
                    new ImmutableArtifactStoreOptions(
                        Path.Combine(root, "objects")))));

    private static CatalogProviderUpdatedDailyNewsDiagnosticRequest Request(
        EvidenceDatasetManifest news,
        EvidenceDatasetManifest bars,
        EvidenceDatasetManifest sessions) =>
        new(
            "daily-news-run",
            "daily.provider-updated-news.diagnostic.v1",
            Now,
            "git:test",
            news.DatasetId,
            bars.DatasetId,
            sessions.DatasetId,
            ["AAPL"],
            "Current membership projected backward; diagnostic only.",
            new ProviderUpdatedDailyNewsStudyOptions("SPY", [0], 14.2m),
            new EvidenceStudyPartitions(
                new EvidenceStudyWindow(
                    "development",
                    Utc(2026, 7, 1),
                    Utc(2026, 7, 3)),
                new EvidenceStudyWindow(
                    "validation",
                    Utc(2026, 7, 3),
                    Utc(2026, 7, 4)),
                new EvidenceStudyWindow(
                    "holdout",
                    Utc(2026, 7, 4),
                    Utc(2026, 7, 8))),
            new CatalogResearchAssumptionSet(
                new CatalogCostAssumptions(0m, 0.2m),
                new CatalogSpreadAssumptions("frozen-test-spread", 4m),
                new CatalogSlippageAssumptions("frozen-test-slippage", 5m),
                new CatalogBorrowAssumptions(false, 0m),
                new CatalogBenchmarkAssumptions(
                    "SPY",
                    "next-eligible-open to horizon close")));

    private static NewsRevisionEvidenceRow News() =>
        new(
            1,
            "news-run",
            Hash("news-config"),
            "tests",
            "alpaca_news",
            NewsAvailabilityEvidence.ProviderUpdatedTimestampOnly,
            "alpaca",
            "article-1",
            "revision-1",
            "AAPL raises guidance",
            "Summary",
            "https://example.test/article-1",
            ["AAPL"],
            ["earnings"],
            new DateTimeOffset(2026, 7, 1, 11, 55, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 1, 11, 55, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero),
            Now,
            [Source]);

    private static IReadOnlyList<MarketBarEvidenceRow> Bars() =>
        [
            Bar("AAPL", new DateOnly(2026, 7, 1), 100m, 110m),
            Bar("AAPL", new DateOnly(2026, 7, 2), 110m, 111m),
            Bar("SPY", new DateOnly(2026, 7, 1), 200m, 202m),
            Bar("SPY", new DateOnly(2026, 7, 2), 202m, 203m)
        ];

    private static MarketBarEvidenceRow Bar(
        string symbol,
        DateOnly date,
        decimal open,
        decimal close) =>
        new(
            1,
            "bars-run",
            Hash("bars-config"),
            "tests",
            "sip",
            $"security-{symbol.ToLowerInvariant()}",
            symbol,
            new DateTimeOffset(
                date.Year,
                date.Month,
                date.Day,
                4,
                0,
                0,
                TimeSpan.Zero),
            new DateTimeOffset(
                date.AddDays(1).Year,
                date.AddDays(1).Month,
                date.AddDays(1).Day,
                4,
                0,
                0,
                TimeSpan.Zero),
            "1d",
            EvidenceFixedDecimal.ToPriceUnits(open),
            EvidenceFixedDecimal.ToPriceUnits(Math.Max(open, close)),
            EvidenceFixedDecimal.ToPriceUnits(Math.Min(open, close)),
            EvidenceFixedDecimal.ToPriceUnits(close),
            null,
            1_000_000,
            10_000,
            "raw",
            "USD",
            new DateTimeOffset(
                date.Year,
                date.Month,
                date.Day,
                4,
                0,
                0,
                TimeSpan.Zero),
            Now,
            [Source]);

    private static IReadOnlyList<ExchangeSessionEvidenceRow> Sessions() =>
        new[]
        {
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 2)
        }.Select(date =>
        {
            var next = date.AddDays(1);
            return new ExchangeSessionEvidenceRow(
                1,
                "calendar-run",
                Hash("calendar-config"),
                "tests",
                "alpaca_trading",
                "alpaca",
                "XNYS",
                date,
                new DateTimeOffset(
                    date.Year,
                    date.Month,
                    date.Day,
                    8,
                    0,
                    0,
                    TimeSpan.Zero),
                new DateTimeOffset(
                    date.Year,
                    date.Month,
                    date.Day,
                    13,
                    30,
                    0,
                    TimeSpan.Zero),
                new DateTimeOffset(
                    date.Year,
                    date.Month,
                    date.Day,
                    20,
                    0,
                    0,
                    TimeSpan.Zero),
                new DateTimeOffset(
                    next.Year,
                    next.Month,
                    next.Day,
                    0,
                    0,
                    0,
                    TimeSpan.Zero),
                false,
                "alpaca_official_market_calendar",
                Now,
                [Source]);
        }).ToArray();

    private static EvidenceDatasetManifest Manifest(
        EvidenceDatasetKind kind,
        string dataFeed,
        string adjustment,
        string timeframe,
        IReadOnlyList<string> symbols,
        IReadOnlyDictionary<string, string>? attributes = null)
    {
        var seed = $"{kind}-{dataFeed}-{adjustment}-{timeframe}";
        var requiresIdentity =
            kind is EvidenceDatasetKind.MarketBarsAsTraded or
                EvidenceDatasetKind.MarketBarsResearchAdjusted;
        var provenance = new EvidencePartitionProvenance(
            "alpaca",
            kind == EvidenceDatasetKind.NewsRevisions
                ? "/v1beta1/news"
                : kind == EvidenceDatasetKind.ExchangeSessions
                    ? "/v2/calendar"
                    : "/v2/stocks/bars",
            dataFeed,
            adjustment,
            "USD",
            requiresIdentity ? new DateOnly(2026, 7, 2) : null,
            requiresIdentity
                ? symbols.Select(symbol => $"security-{symbol.ToLowerInvariant()}").ToArray()
                : [],
            [],
            symbols,
            timeframe,
            Utc(2026, 7, 1),
            Utc(2026, 7, 3));
        var sourceArtifact = Artifact($"source-{seed}", "raw/test", "application/json");
        var partition = new EvidenceDatasetPartitionManifest(
            $"partition-{seed}",
            kind,
            1,
            provenance,
            Utc(2026, 7, 1),
            Utc(2026, 7, 2),
            2,
            Artifact(
                $"partition-{seed}",
                "normalized/test",
                "application/vnd.apache.parquet"),
            [
                new EvidenceSourceReference(
                    $"observation-{seed}",
                    sourceArtifact,
                    Now.AddMinutes(-1))
            ],
            "normalizer-v1",
            "code-v1",
            new EvidenceQualityReport());
        return new EvidenceDatasetManifest(
            kind,
            1,
            Now,
            $"job-{seed}",
            Hash($"plan-{seed}"),
            Hash($"config-{seed}"),
            "code-v1",
            "normalizer-v1",
            dataFeed,
            [partition],
            new EvidenceQualityReport(),
            attributes);
    }

    private static EvidenceArtifactReference Artifact(
        string value,
        string objectNamespace,
        string mediaType) =>
        new(
            new EvidenceContentAddress(
                Hash(value),
                Encoding.UTF8.GetByteCount(value),
                mediaType),
            new EvidenceObjectNamespace(objectNamespace));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static DateTimeOffset Utc(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.Zero);
}
