using System.Security.Cryptography;
using Moq;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;
using TradingFlow.Research.Catalysts;
using Xunit;

namespace TradingFlow.Tests;

public sealed class ExchangeSessionEvidenceTests
{
    private const string ConfigHash =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string SourceHash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData(2026, 1, 12, 14)]
    [InlineData(2026, 7, 13, 13)]
    public void Resolver_UsesExplicitEstAndEdtBounds(
        int year,
        int month,
        int day,
        int expectedRegularOpenUtcHour)
    {
        var date = new DateOnly(year, month, day);
        var row = Session(date, earlyClose: false);
        var resolver = Resolver(row);

        var resolution = resolver.Resolve(row.RegularOpenUtc.AddMinutes(1));

        Assert.Equal(EquityTradingSession.Regular, resolution.Session);
        Assert.Equal(expectedRegularOpenUtcHour, resolution.SessionStartUtc.Hour);
        Assert.Equal(date, resolution.TradeDate);
    }

    [Fact]
    public void Resolver_PreservesDstTransitionAndRejectsAmbiguousLocalTimestamp()
    {
        var friday = Session(new DateOnly(2026, 3, 6), earlyClose: false);
        var monday = Session(new DateOnly(2026, 3, 9), earlyClose: false);
        var resolver = Resolver(friday, monday);

        Assert.Equal(14, resolver.Resolve(friday.RegularOpenUtc).SessionStartUtc.Hour);
        Assert.Equal(13, resolver.Resolve(monday.RegularOpenUtc).SessionStartUtc.Hour);
        Assert.Throws<InvalidDataException>(() =>
            resolver.Resolve(Utc(2026, 11, 1, 5, 30)));
    }

    [Fact]
    public void Resolver_UsesExplicitEarlyClose()
    {
        var row = Session(new DateOnly(2026, 11, 27), earlyClose: true);
        var resolver = Resolver(row);

        var resolution = resolver.Resolve(row.RegularCloseUtc.AddMinutes(1));

        Assert.Equal(EquityTradingSession.AfterHours, resolution.Session);
        Assert.Equal(row.RegularCloseUtc, resolution.SessionStartUtc);
        Assert.True(row.IsEarlyClose);
    }

    [Fact]
    public void Resolver_RefusesDateOutsideCommittedCoverage()
    {
        var row = Session(new DateOnly(2026, 7, 13), earlyClose: false);
        var resolver = Resolver(row);

        var exception = Assert.Throws<InvalidDataException>(() =>
            resolver.Resolve(Utc(2026, 7, 14, 15)));

        Assert.Contains("outside committed exchange-calendar coverage", exception.Message);
    }

    [Fact]
    public void HistoricalResolver_RejectsMissingDateInsteadOfInferringHoliday()
    {
        var monday = new DateOnly(2026, 7, 13);
        var open = LocalToUtc(monday, new TimeOnly(9, 30), NewYorkTimeZone());
        var resolver = new HistoricalExchangeSessionResolver(
            [
                new TradingSessionSnapshot(
                    monday,
                    EquityTradingSession.Regular,
                    Utc(2026, 1, 1),
                    open,
                    open.AddHours(6.5))
            ],
            NewYorkTimeZone(),
            new TimeOnly(4, 0),
            new TimeOnly(20, 0));

        var exception = Assert.Throws<InvalidDataException>(() =>
            resolver.Resolve(Utc(2026, 7, 14, 15)));

        Assert.Contains("absent from exchange-calendar evidence", exception.Message);
    }

    [Fact]
    public void HistoricalResolver_ResolvesOnlyExplicitClosedDateAsClosed()
    {
        var holiday = new DateOnly(2026, 7, 3);
        var resolver = new HistoricalExchangeSessionResolver(
            [
                new TradingSessionSnapshot(
                    holiday,
                    EquityTradingSession.Closed,
                    Utc(2026, 1, 1),
                    null,
                    null)
            ],
            NewYorkTimeZone(),
            new TimeOnly(4, 0),
            new TimeOnly(20, 0));

        var resolution = resolver.Resolve(Utc(2026, 7, 3, 15));

        Assert.Equal(EquityTradingSession.Closed, resolution.Session);
        Assert.Equal(holiday, resolution.TradeDate);
    }

    [Fact]
    public void Resolver_InfersWeekendAndHolidayClosedInsideCompleteCoverage()
    {
        var thursday = Session(new DateOnly(2026, 7, 2), earlyClose: false);
        var monday = Session(new DateOnly(2026, 7, 6), earlyClose: false);
        var resolver = new CatalogBackedExchangeSessionResolver(
            "calendar-dataset",
            [thursday, monday],
            new ExchangeCalendarEvidenceCoverage(
                new DateOnly(2026, 7, 2),
                new DateOnly(2026, 7, 7),
                "alpaca_official_market_calendar",
                sourceComplete: true),
            NewYorkTimeZone());

        var holiday = resolver.Resolve(Utc(2026, 7, 3, 15));
        var weekend = resolver.Resolve(Utc(2026, 7, 4, 15));

        Assert.Equal(EquityTradingSession.Closed, holiday.Session);
        Assert.Equal(new DateOnly(2026, 7, 3), holiday.TradeDate);
        Assert.Equal(EquityTradingSession.Closed, weekend.Session);
        Assert.Equal(new DateOnly(2026, 7, 4), weekend.TradeDate);
        Assert.Equal(TimeSpan.FromHours(24), weekend.SessionEndUtc - weekend.SessionStartUtc);
    }

    [Fact]
    public void Resolver_RefusesIncompleteSource()
    {
        var row = Session(new DateOnly(2026, 7, 13), earlyClose: false);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new CatalogBackedExchangeSessionResolver(
                "calendar-dataset",
                [row],
                new ExchangeCalendarEvidenceCoverage(
                    row.TradeDate,
                    row.TradeDate.AddDays(1),
                    "alpaca_official_market_calendar",
                    sourceComplete: false),
                NewYorkTimeZone()));

        Assert.Contains("source is incomplete", exception.Message);
    }

    [Fact]
    public async Task Codec_IsDeterministicAndRejectsWrongKind()
    {
        var codec = new EvidenceParquetCodec();
        var earlier = Session(new DateOnly(2026, 1, 12), earlyClose: false);
        var later = Session(new DateOnly(2026, 7, 13), earlyClose: false);

        var first = await codec.WriteExchangeSessionsAsync(
            [later, earlier],
            "calendar-normalizer-v1");
        var replay = await codec.WriteExchangeSessionsAsync(
            [earlier, later],
            "calendar-normalizer-v1");
        var decoded = await codec.ReadExchangeSessionsAsync(first);

        Assert.Equal(first, replay);
        Assert.Equal(EvidenceDatasetKind.ExchangeSessions, decoded.DatasetKind);
        Assert.Equal(
            [earlier.TradeDate, later.TradeDate],
            decoded.Rows.Select(row => row.TradeDate));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            codec.ReadSipQuotesAsync(first));
    }

    [Fact]
    public async Task CatalogFactory_UsesOnlyCommittedVerifiedCalendarEvidence()
    {
        var fixture = await PublishedFixtureAsync(
            Session(new DateOnly(2026, 7, 13), earlyClose: false));
        var catalog = Catalog(fixture.Manifest);
        var factory = new CatalogExchangeSessionResolverFactory(
            catalog.Object,
            fixture.Reader);

        var resolver = await factory.CreateAsync(
            fixture.Manifest.DatasetId,
            Utc(2026, 7, 13, 23));
        var resolution = resolver.Resolve(Utc(2026, 7, 13, 15));

        Assert.Equal(EquityTradingSession.Regular, resolution.Session);
        Assert.Equal(fixture.Manifest.DatasetId,
            Assert.IsType<CatalogBackedExchangeSessionResolver>(resolver).DatasetId);
        catalog.Verify(
            value => value.GetDatasetAsync(
                fixture.Manifest.DatasetId,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CatalogFactory_RefusesUncommittedWrongKindAndUnavailableEvidence()
    {
        var fixture = await PublishedFixtureAsync(
            Session(new DateOnly(2026, 7, 13), earlyClose: false));
        var missingCatalog = Catalog(null);
        var missingFactory = new CatalogExchangeSessionResolverFactory(
            missingCatalog.Object,
            fixture.Reader);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            missingFactory.CreateAsync("missing", Utc(2026, 7, 13, 23)));

        var wrongKind = WrongKindManifest();
        var wrongCatalog = Catalog(wrongKind);
        var wrongFactory = new CatalogExchangeSessionResolverFactory(
            wrongCatalog.Object,
            fixture.Reader);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            wrongFactory.CreateAsync(wrongKind.DatasetId, Utc(2026, 7, 13, 23)));

        var futureCatalog = Catalog(fixture.Manifest);
        var futureFactory = new CatalogExchangeSessionResolverFactory(
            futureCatalog.Object,
            fixture.Reader);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            futureFactory.CreateAsync(
                fixture.Manifest.DatasetId,
                Utc(2025, 12, 31, 23)));
    }

    [Fact]
    public async Task CatalogFactory_RefusesTamperedPartitionBeforeReading()
    {
        var fixture = await PublishedFixtureAsync(
            Session(new DateOnly(2026, 7, 13), earlyClose: false));
        fixture.Store.Tamper(fixture.Manifest.Partitions.Single().Artifact);
        var factory = new CatalogExchangeSessionResolverFactory(
            Catalog(fixture.Manifest).Object,
            fixture.Reader);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            factory.CreateAsync(
                fixture.Manifest.DatasetId,
                Utc(2026, 7, 13, 23)));
    }

    [Fact]
    public async Task CatalogFactory_RefusesIncompleteCalendarSource()
    {
        var fixture = await PublishedFixtureAsync(
            Session(new DateOnly(2026, 7, 13), earlyClose: false));
        var original = fixture.Manifest.Partitions.Single();
        var incomplete = CopyCalendarPartition(
            original,
            "incomplete-calendar",
            new DateOnly(2026, 7, 13),
            new DateOnly(2026, 7, 14),
            sourceComplete: false);
        var manifest = CopyManifest(fixture.Manifest, [incomplete]);
        var factory = new CatalogExchangeSessionResolverFactory(
            Catalog(manifest).Object,
            fixture.Reader);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            factory.CreateAsync(manifest.DatasetId, Utc(2026, 7, 13, 23)));

        Assert.Contains("does not prove complete", exception.Message);
    }

    [Fact]
    public async Task CatalogFactory_RefusesGappedCalendarCoverage()
    {
        var fixture = await PublishedFixtureAsync(
            Session(new DateOnly(2026, 7, 13), earlyClose: false));
        var original = fixture.Manifest.Partitions.Single();
        var first = CopyCalendarPartition(
            original,
            "calendar-first",
            new DateOnly(2026, 7, 10),
            new DateOnly(2026, 7, 12),
            sourceComplete: true);
        var second = CopyCalendarPartition(
            original,
            "calendar-second",
            new DateOnly(2026, 7, 13),
            new DateOnly(2026, 7, 15),
            sourceComplete: true);
        var manifest = CopyManifest(fixture.Manifest, [first, second]);
        var factory = new CatalogExchangeSessionResolverFactory(
            Catalog(manifest).Object,
            fixture.Reader);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            factory.CreateAsync(manifest.DatasetId, Utc(2026, 7, 13, 23)));

        Assert.Contains("gapped", exception.Message);
    }

    private static CatalogBackedExchangeSessionResolver Resolver(
        params ExchangeSessionEvidenceRow[] rows)
    {
        var start = rows.Min(row => row.TradeDate);
        var endExclusive = rows.Max(row => row.TradeDate).AddDays(1);
        return new(
            "calendar-dataset",
            rows,
            new ExchangeCalendarEvidenceCoverage(
                start,
                endExclusive,
                "alpaca_official_market_calendar",
                sourceComplete: true),
            NewYorkTimeZone());
    }

    private static async Task<PublishedFixture> PublishedFixtureAsync(
        params ExchangeSessionEvidenceRow[] rows)
    {
        var codec = new EvidenceParquetCodec();
        var store = new InMemoryArtifactStore();
        var sourceArtifact = new EvidenceArtifactReference(
            new EvidenceContentAddress(SourceHash, 1, "application/json"),
            new EvidenceObjectNamespace("raw/calendar"));
        var source = new EvidenceSourceReference(
            "calendar-observation",
            sourceArtifact,
            Utc(2026, 1, 1));
        var coverageStart = rows.Min(row => row.TradeDate);
        var coverageEndExclusive = rows.Max(row => row.TradeDate).AddDays(1);
        var provenance = new EvidencePartitionProvenance(
            "alpaca",
            "/v2/calendar",
            "alpaca-trading",
            "raw",
            "USD",
            coverageEndExclusive.AddDays(-1),
            [],
            [],
            ["US_EQUITIES"],
            "session",
            Utc(coverageStart.Year, coverageStart.Month, coverageStart.Day),
            Utc(
                coverageEndExclusive.Year,
                coverageEndExclusive.Month,
                coverageEndExclusive.Day));
        var request = new EvidenceParquetPartitionRequest(
            "calendar-partition",
            EvidenceDatasetKind.ExchangeSessions,
            1,
            provenance,
            [source],
            "calendar-run",
            ConfigHash,
            "calendar-normalizer-v1",
            "calendar-code-v1",
            new EvidenceQualityReport(),
            new EvidenceObjectNamespace("normalized/calendar"),
            Dimensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["calendar_coverage_start_date"] = coverageStart.ToString("yyyy-MM-dd"),
                ["calendar_coverage_end_date_exclusive"] =
                    coverageEndExclusive.ToString("yyyy-MM-dd"),
                ["calendar_source"] = "alpaca_official_market_calendar",
                ["calendar_source_complete"] = "true",
                ["authoritative_page_count"] = "1"
            });
        var publisher = new EvidenceParquetPartitionPublisher(codec, store);
        var partition = await publisher.PublishExchangeSessionsAsync(request, rows);
        var manifest = EvidenceParquetPartitionPublisher.BuildDatasetManifest(
            EvidenceDatasetKind.ExchangeSessions,
            1,
            Utc(2026, 7, 13, 23),
            "calendar-run",
            SourceHash,
            ConfigHash,
            "calendar-code-v1",
            "calendar-normalizer-v1",
            "alpaca-trading",
            [partition],
            new EvidenceQualityReport());
        return new(
            manifest,
            store,
            new ParquetEvidencePartitionDataReader(codec, store));
    }

    private static ExchangeSessionEvidenceRow Session(
        DateOnly date,
        bool earlyClose)
    {
        var zone = NewYorkTimeZone();
        return new(
            1,
            "calendar-run",
            ConfigHash,
            "calendar-code-v1",
            "alpaca-trading",
            "alpaca",
            "XNYS",
            date,
            LocalToUtc(date, new TimeOnly(4, 0), zone),
            LocalToUtc(date, new TimeOnly(9, 30), zone),
            LocalToUtc(date, earlyClose ? new TimeOnly(13, 0) : new TimeOnly(16, 0), zone),
            LocalToUtc(date, new TimeOnly(20, 0), zone),
            earlyClose,
            "alpaca_official_market_calendar",
            Utc(2026, 1, 1),
            [new EvidenceRowSourceAddress("calendar-observation", SourceHash)]);
    }

    private static EvidenceDatasetManifest WrongKindManifest()
    {
        var artifact = new EvidenceArtifactReference(
            new EvidenceContentAddress(SourceHash, 1, "application/octet-stream"),
            new EvidenceObjectNamespace("normalized/wrong"));
        var source = new EvidenceSourceReference(
            "wrong-observation",
            artifact,
            Utc(2026, 1, 1));
        var provenance = new EvidencePartitionProvenance(
            "test",
            "/wrong",
            "test",
            "none",
            "USD",
            null,
            [],
            [],
            ["TEST"],
            "news",
            Utc(2026, 1, 1),
            Utc(2026, 1, 2));
        var partition = new EvidenceDatasetPartitionManifest(
            "wrong-partition",
            EvidenceDatasetKind.NewsRevisions,
            1,
            provenance,
            Utc(2026, 1, 1),
            Utc(2026, 1, 1),
            1,
            artifact,
            [source],
            "wrong-normalizer",
            "wrong-code",
            new EvidenceQualityReport(),
            dimensions: new Dictionary<string, string>
            {
                ["run_id"] = "wrong-run",
                ["config_hash"] = ConfigHash,
                ["data_feed"] = "test"
            });
        return new EvidenceDatasetManifest(
            EvidenceDatasetKind.NewsRevisions,
            1,
            Utc(2026, 1, 2),
            "wrong-run",
            SourceHash,
            ConfigHash,
            "wrong-code",
            "wrong-normalizer",
            "test",
            [partition],
            new EvidenceQualityReport());
    }

    private static EvidenceDatasetPartitionManifest CopyCalendarPartition(
        EvidenceDatasetPartitionManifest source,
        string partitionId,
        DateOnly coverageStart,
        DateOnly coverageEndExclusive,
        bool sourceComplete)
    {
        var provenance = new EvidencePartitionProvenance(
            source.Provenance.Provider,
            source.Provenance.Endpoint,
            source.Provenance.DataFeed,
            source.Provenance.Adjustment,
            source.Provenance.Currency,
            source.Provenance.AsOfDate,
            source.Provenance.SecurityIds,
            source.Provenance.IssuerIds,
            source.Provenance.Symbols,
            source.Provenance.Timeframe,
            Utc(coverageStart.Year, coverageStart.Month, coverageStart.Day),
            Utc(
                coverageEndExclusive.Year,
                coverageEndExclusive.Month,
                coverageEndExclusive.Day));
        var dimensions = source.Dimensions.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        dimensions["calendar_coverage_start_date"] =
            coverageStart.ToString("yyyy-MM-dd");
        dimensions["calendar_coverage_end_date_exclusive"] =
            coverageEndExclusive.ToString("yyyy-MM-dd");
        dimensions["calendar_source"] = "alpaca_official_market_calendar";
        dimensions["calendar_source_complete"] = sourceComplete ? "true" : "false";
        return new(
            partitionId,
            source.DatasetKind,
            source.SchemaVersion,
            provenance,
            source.MinimumSourceTimestampUtc,
            source.MaximumSourceTimestampUtc,
            source.RowCount,
            source.Artifact,
            source.SourceObservations,
            source.NormalizerVersion,
            source.CodeVersion,
            source.Quality,
            source.QuarantineLedger,
            dimensions);
    }

    private static EvidenceDatasetManifest CopyManifest(
        EvidenceDatasetManifest source,
        IReadOnlyList<EvidenceDatasetPartitionManifest> partitions) =>
        new(
            source.Kind,
            source.SchemaVersion,
            source.CreatedAtUtc,
            source.CollectionJobId,
            source.CollectionPlanHash,
            source.ConfigHash,
            source.CodeVersion,
            source.NormalizerVersion,
            source.DataFeed,
            partitions,
            source.Quality,
            source.Attributes);

    private static Mock<IEvidenceCatalog> Catalog(EvidenceDatasetManifest? manifest)
    {
        var mock = new Mock<IEvidenceCatalog>(MockBehavior.Strict);
        mock.Setup(value => value.GetDatasetAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
                manifest is not null && manifest.DatasetId.Equals(id, StringComparison.Ordinal)
                    ? manifest
                    : null);
        return mock;
    }

    private static DateTimeOffset LocalToUtc(
        DateOnly date,
        TimeOnly time,
        TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static TimeZoneInfo NewYorkTimeZone()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
        }

        throw new InvalidOperationException("New York timezone is unavailable.");
    }

    private static DateTimeOffset Utc(
        int year,
        int month,
        int day,
        int hour = 0,
        int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private sealed record PublishedFixture(
        EvidenceDatasetManifest Manifest,
        InMemoryArtifactStore Store,
        ParquetEvidencePartitionDataReader Reader);

    private sealed class InMemoryArtifactStore : IImmutableArtifactStore
    {
        private readonly Dictionary<string, byte[]> content = new(StringComparer.Ordinal);

        public void Tamper(EvidenceArtifactReference artifact)
        {
            var bytes = content[artifact.Content.Sha256].ToArray();
            bytes[0] ^= 0xff;
            content[artifact.Content.Sha256] = bytes;
        }

        public Task<ImmutableArtifactReceipt> PutIfAbsentAsync(
            ImmutableArtifactWriteRequest request,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existed = content.ContainsKey(request.Artifact.Content.Sha256);
            content.TryAdd(request.Artifact.Content.Sha256, bytes.ToArray());
            return Task.FromResult(new ImmutableArtifactReceipt(
                request.Artifact,
                request.Artifact.Content.Sha256,
                existed));
        }

        public Task<Stream> OpenReadAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(
                new MemoryStream(content[artifact.Content.Sha256], writable: false));
        }

        public Task<ImmutableArtifactVerification> VerifyAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!content.TryGetValue(artifact.Content.Sha256, out var bytes))
            {
                return Task.FromResult(new ImmutableArtifactVerification(
                    false,
                    artifact,
                    null,
                    null,
                    "missing"));
            }

            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var valid = bytes.LongLength == artifact.Content.ByteLength &&
                        hash.Equals(artifact.Content.Sha256, StringComparison.Ordinal);
            return Task.FromResult(new ImmutableArtifactVerification(
                valid,
                artifact,
                bytes.LongLength,
                hash,
                valid ? null : "content_hash_mismatch"));
        }
    }
}
