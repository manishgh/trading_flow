using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

/// <summary>
/// The read-only evidence-catalog adapter of research-screen runs (design Part 2, item 2) and the coverage rule that checks
/// only member sessions (Part 1, item 7). Every catalog here is a synthetic fixture in a temporary folder.
/// </summary>
public sealed class EvidenceCatalogMarketDataProviderTests
{
    private static readonly DateTimeOffset Open = new(2026, 10, 1, 13, 30, 0, TimeSpan.Zero);

    // The adapter returns exactly the stored bars of the asked stock, timeframe and range, in time order, and archives a
    // receipt naming the dataset and partitions it read.
    [Fact]
    public async Task ReturnsExactStoredBarsInOrderWithAReceipt()
    {
        using var catalog = new SyntheticEvidenceCatalog();
        var (provider, config) = await catalog.ProviderAsync(new string('a', 64));

        var bars = new List<OhlcvBar>();
        await foreach (var bar in provider.GetBarsAsync(["ABC"], ["5m"], Open, Open.AddMinutes(10), default)) bars.Add(bar);
        var capture = await provider.CaptureHistoricalBarsAsync("ABC", "5m", Open, Open.AddMinutes(15), 100, default);

        Assert.Equal([Open, Open.AddMinutes(5)], bars.Select(bar => bar.Timestamp));
        Assert.Equal((100m, 101m, 99m, 100.5m, 1000m, "sip", "raw"), (bars[0].Open, bars[0].High, bars[0].Low, bars[0].Close, bars[0].Volume,
            bars[0].DataFeed, bars[0].AdjustmentPolicy));
        Assert.Equal(3, capture.Bars.Count);
        Assert.All(capture.Bars, bar => Assert.Equal("ABC", bar.Ticker));
        var receipt = Assert.Single(capture.Evidence.Responses);
        using var payload = JsonDocument.Parse(await File.ReadAllBytesAsync(receipt.PayloadPath));
        Assert.Equal(config.FiveMinuteDatasetIds.Single(), payload.RootElement.GetProperty("sources")[0].GetProperty("DatasetId").GetString());
        Assert.Equal(3, payload.RootElement.GetProperty("rows").GetInt32());
        var daily = await provider.CaptureHistoricalBarsAsync("ABC", "1d", Open.AddDays(-4), Open.AddDays(1), 100, default);
        Assert.Equal(3, daily.Bars.Count);
    }

    // Hourly bars frozen in providers.evidence_catalog.hourly_datasets are served like five-minute ones, in time order, and an
    // hourly dataset collected for another list is refused; without hourly datasets an hourly request finds no bars.
    [Fact]
    public async Task ServesFrozenHourlyBarsOfTheScreensListOnly()
    {
        using var catalog = new SyntheticEvidenceCatalog();
        var list = new string('a', 64);
        var (_, config) = await catalog.ProviderAsync(list);
        var fourteen = new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
        var hourly = await catalog.BarsAsync("1h", list, [SyntheticEvidenceCatalog.Row("ABC", "1h", fourteen.AddHours(1), 101m),
            SyntheticEvidenceCatalog.Row("ABC", "1h", fourteen, 100m)]);
        var other = await catalog.BarsAsync("1h", new string('b', 64), [SyntheticEvidenceCatalog.Row("ABC", "1h", fourteen, 100m)]);
        var provider = catalog.Provider(config with { HourlyDatasetIds = [hourly.DatasetId] });

        var bars = new List<OhlcvBar>();
        await foreach (var bar in provider.GetBarsAsync(["ABC"], ["1h"], fourteen, fourteen.AddHours(2), default)) bars.Add(bar);
        var none = new List<OhlcvBar>();
        await foreach (var bar in catalog.Provider(config).GetBarsAsync(["ABC"], ["1h"], fourteen, fourteen.AddHours(2), default)) none.Add(bar);

        Assert.Equal([(fourteen, "1h", 100m), (fourteen.AddHours(1), "1h", 101m)], bars.Select(bar => (bar.Timestamp, bar.Timeframe, bar.Open)));
        Assert.Empty(none);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in catalog.Provider(config with { HourlyDatasetIds = [other.DatasetId] })
                               .GetBarsAsync(["ABC"], ["1h"], fourteen, fourteen.AddHours(2), default)) { }
        });
    }

    // A dataset whose manifest no longer produces its frozen ID, or a five-minute dataset not collected for the run's ranked
    // list, is refused; the calendar is refused past its recorded coverage instead of reading those days as closed.
    [Fact]
    public async Task RefusesAChangedDatasetAndACalendarThatDoesNotReachTheWindowEnd()
    {
        using var catalog = new SyntheticEvidenceCatalog();
        var (provider, config) = await catalog.ProviderAsync(new string('a', 64));
        var swapped = new EvidenceCatalogMarketDataProvider(config,
            (id, _) => Task.FromResult<EvidenceDatasetManifest?>(catalog.Datasets[config.DailyDatasetId]), catalog.Reader,
            new FileSystemRawArchiveWriter(new RawArchiveOptions(catalog.RawRoot)));
        var otherList = new EvidenceCatalogMarketDataProvider(config with { RankedListSha256 = new string('b', 64) },
            catalog.GetAsync, catalog.Reader, new FileSystemRawArchiveWriter(new RawArchiveOptions(catalog.RawRoot)));

        await Assert.ThrowsAsync<InvalidDataException>(() => swapped.CaptureHistoricalBarsAsync("ABC", "5m", Open, Open.AddHours(1), 100, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => otherList.CaptureHistoricalBarsAsync("ABC", "5m", Open, Open.AddHours(1), 100, default));
        var sessions = await provider.LoadMarketSessionSchedulesAsync(new(2026, 9, 28), new(2026, 10, 4), default);
        Assert.True(sessions[new(2026, 10, 1)].IsTradingDay);
        Assert.Equal(new TimeOnly(9, 30), sessions[new(2026, 10, 1)].RegularOpen);
        Assert.False(sessions[new(2026, 10, 3)].IsTradingDay);
        var beyond = await Assert.ThrowsAsync<InvalidDataException>(() => provider.LoadMarketSessionSchedulesAsync(new(2026, 10, 1), new(2026, 10, 20), default));
        Assert.Contains("covers", beyond.Message, StringComparison.Ordinal);
    }

    // The provider factory builds the adapter for market_data.provider evidence_catalog without creating any HTTP client,
    // and the adapter holds no network client at all.
    [Fact]
    public void ProviderFactory_CreatesTheAdapterWithoutAnyNetworkClient()
    {
        var created = 0;
        var providers = new ConfiguredDataProviders(() => throw new InvalidOperationException("no credentials"),
            () => throw new InvalidOperationException("no Finviz"), () => null, () => { created++; return new HttpClient(); });
        var config = new EvidenceCatalogProviderConfig(Path.Combine(Path.GetTempPath(), "absent-" + Guid.NewGuid().ToString("N"), "catalog.db"),
            Path.Combine(Path.GetTempPath(), "absent-objects"), new string('a', 64), "daily", "sessions", ["five"], ["one"]);
        var run = new TradingFlow.Engine.Configuration.SimpleYamlReader().ReadBacktestRun(Path.Combine(TestRepository.FindRoot(), "configs", "backtest",
            "orb-published-recipe-whole-market-profile.yaml")) with
        { Provider = EvidenceCatalogMarketDataProvider.ProviderName, Providers = new(new("sip"), config) };

        var provider = providers.CreateMarket(run, new Moq.Mock<IRawArchiveWriter>(Moq.MockBehavior.Strict).Object);

        Assert.IsType<EvidenceCatalogMarketDataProvider>(provider);
        Assert.Equal(0, created);
        Assert.DoesNotContain(typeof(EvidenceCatalogMarketDataProvider).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            field => typeof(HttpClient).IsAssignableFrom(field.FieldType));
        Assert.Equal(4_500_000, run.Engine.MaxRetainedReplayBars);
    }

    // With member sessions only those sessions (and each one's previous session) must be complete: a non-member session with
    // no bars at all fails nothing, while a missing bar on a member session still fails from that bar.
    [Theory]
    [InlineData(null, "2026-07-06T13:30:00Z")]
    [InlineData("2026-07-02", null)]
    [InlineData("2026-07-02,2026-07-06", "2026-07-06T13:30:00Z")]
    [InlineData("2026-07-02;gap", "2026-07-02T14:00:00Z")]
    public void Coverage_ChecksOnlyMemberSessions(string? members, string? expected)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        DateOnly[] trading = [new(2026, 7, 1), new(2026, 7, 2), new(2026, 7, 6)];
        var schedules = Enumerable.Range(0, 6).Select(day => new DateOnly(2026, 7, 1).AddDays(day))
            .ToDictionary(day => day, day => trading.Contains(day) ? MarketSessionSchedule.RegularDay(day) : MarketSessionSchedule.Closed(day));
        var sessions = trading[..2].ToDictionary(day => day, day => Enumerable.Range(0, 78)
            .Select(slot => new OhlcvBar("ABC", new DateTimeOffset(day.ToDateTime(new TimeOnly(13, 30)), TimeSpan.Zero).AddMinutes(slot * 5), "5m",
                10m, 11m, 9m, 10m, 100m, "sip", "raw")).ToArray());
        if (members?.EndsWith(";gap", StringComparison.Ordinal) == true)
            sessions[trading[1]] = sessions[trading[1]].Where(bar => bar.Timestamp != new DateTimeOffset(2026, 7, 2, 14, 0, 0, TimeSpan.Zero)).ToArray();
        var memberSet = members?.Split(';')[0].Split(',').Select(DateOnly.Parse).ToHashSet();
        var prior = new Dictionary<DateOnly, DateOnly> { [trading[1]] = trading[0], [trading[2]] = trading[1] };

        var boundary = BacktestRunner.FindIntradaySourceGap(sessions, schedules, prior, zone,
            new DateTimeOffset(2026, 7, 2, 13, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 7, 6, 20, 0, 0, TimeSpan.Zero), null, memberSet);

        Assert.Equal(expected is null ? null : DateTimeOffset.Parse(expected), boundary);
        Assert.Equal(trading.ToHashSet(), BacktestRunner.IntradayRequiredSessions(new HashSet<DateOnly> { trading[2] }, schedules, prior, 2).ToHashSet());
    }

    // The real opening-bar collection stores one partition per session and batch, so one stock sits in several partitions:
    // the ranking reads every partition's 09:30 bar, and the adapter merges them in time order.
    [Fact]
    public async Task OpeningVolumes_ReadAStockStoredInTwoPartitions()
    {
        using var catalog = new SyntheticEvidenceCatalog();
        var first = new DateTimeOffset(2026, 9, 30, 13, 30, 0, TimeSpan.Zero);
        var dataset = await catalog.BarsAsync("5m", new string('a', 64), [SyntheticEvidenceCatalog.Row("ABC", "5m", first, 10m, volume: 500),
            SyntheticEvidenceCatalog.Row("ABC", "5m", first.AddDays(1), 11m, volume: 700)], partitionPerSession: true);

        var volumes = await TradingFlow.Cli.DailyRankingOutput.ReadOpeningVolumesAsync(catalog.Reader, dataset, default);

        Assert.Equal(2, dataset.Partitions.Count);
        Assert.Equal((500m, 700m), (volumes[("sec-abc", new DateOnly(2026, 9, 30))], volumes[("sec-abc", new DateOnly(2026, 10, 1))]));
    }

    // Orders triggering at the same time are admitted by published rank: rank 1 scores highest; an unranked stock scores zero.
    [Fact]
    public void RankSelectionScore_PutsRankOneFirst()
    {
        var day = new DateOnly(2026, 10, 1);
        var ranks = new Dictionary<(string, DateOnly), int> { [("AAA", day)] = 1, [("BBB", day)] = 2 };

        Assert.Equal((1m, 0.5m, 0m), (BacktestRunner.RankSelectionScore(ranks, "aaa", day), BacktestRunner.RankSelectionScore(ranks, "BBB", day),
            BacktestRunner.RankSelectionScore(ranks, "CCC", day)));
    }
}

/// <summary>
/// A synthetic immutable evidence catalog in a temporary folder: as-traded bar datasets (one partition per stock) and an XNYS
/// exchange-session dataset, published through the ordinary Parquet publisher and read through the ordinary verified reader.
/// </summary>
internal sealed class SyntheticEvidenceCatalog : IDisposable
{
    private const string RunId = "synthetic-run";
    private const string ConfigHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string CodeVersion = "synthetic-code";
    private const string Normalizer = "synthetic-normalizer-v1";
    private static readonly EvidenceSourceReference Source = new("synthetic-observation",
        new EvidenceArtifactReference(new EvidenceContentAddress(new string('d', 64), 1, "application/json"), new EvidenceObjectNamespace("raw/synthetic")),
        new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private readonly TemporaryFolder folder = new();
    private readonly FileSystemImmutableArtifactStore store;
    private readonly EvidenceParquetCodec codec = new();

    public SyntheticEvidenceCatalog()
    {
        store = new(new ImmutableArtifactStoreOptions(Path.Combine(folder.Path, "objects")));
        Reader = new ParquetEvidencePartitionDataReader(codec, store);
    }

    public ParquetEvidencePartitionDataReader Reader { get; }
    public string RawRoot => Path.Combine(folder.Path, "raw");
    public Dictionary<string, EvidenceDatasetManifest> Datasets { get; } = new(StringComparer.Ordinal);

    /// <summary>The dataset lookup the adapter uses: the committed manifest for an ID, or null.</summary>
    public Task<EvidenceDatasetManifest?> GetAsync(string id, CancellationToken token) => Task.FromResult(Datasets.GetValueOrDefault(id));

    /// <summary>One as-traded SIP row; high and low default to one dollar around the price.</summary>
    public static MarketBarEvidenceRow Row(string symbol, string timeframe, DateTimeOffset start, decimal price, decimal? high = null,
        decimal? low = null, decimal? close = null, long volume = 1000)
    {
        var length = timeframe switch { "1d" => TimeSpan.FromDays(1), "1h" => TimeSpan.FromHours(1), "1m" => TimeSpan.FromMinutes(1), _ => TimeSpan.FromMinutes(5) };
        return new(1, RunId, ConfigHash, CodeVersion, "sip", "sec-" + symbol.ToLowerInvariant(), symbol, start, start + length, timeframe,
            EvidenceFixedDecimal.ToPriceUnits(price), EvidenceFixedDecimal.ToPriceUnits(high ?? price + 1m), EvidenceFixedDecimal.ToPriceUnits(low ?? price - 1m),
            EvidenceFixedDecimal.ToPriceUnits(close ?? price + 0.5m), null, volume, null, "raw", "USD", start + length, start + length,
            [new EvidenceRowSourceAddress(Source.ObservationId, Source.Artifact.Content.Sha256)]);
    }

    /// <summary>
    /// Publishes an as-traded bar dataset with one partition per stock, or per stock and UTC date when
    /// <paramref name="partitionPerSession"/> (as the real collections store one partition per session and batch), tagged with
    /// a ranked list when given.
    /// </summary>
    public async Task<EvidenceDatasetManifest> BarsAsync(string timeframe, string? rankedListSha256, IReadOnlyList<MarketBarEvidenceRow> rows,
        bool partitionPerSession = false)
    {
        var publisher = new EvidenceParquetPartitionPublisher(codec, store);
        var partitions = new List<EvidenceDatasetPartitionManifest>();
        foreach (var group in rows.GroupBy(row => (row.Symbol, Day: partitionPerSession ? DateOnly.FromDateTime(row.BarStartUtc.UtcDateTime) : default)))
        {
            var provenance = new EvidencePartitionProvenance("alpaca", "/v2/stocks/bars", "sip", "raw", "USD",
                DateOnly.FromDateTime(group.Min(row => row.BarStartUtc).UtcDateTime), [group.First().SecurityId], [], [group.Key.Symbol], timeframe,
                group.Min(row => row.BarStartUtc), group.Max(row => row.BarStartUtc).AddDays(1));
            partitions.Add(await publisher.PublishMarketBarsAsTradedAsync(new EvidenceParquetPartitionRequest($"{timeframe}-{group.Key.Symbol}-{Guid.NewGuid():N}",
                EvidenceDatasetKind.MarketBarsAsTraded, 1, provenance, [Source], RunId, ConfigHash, Normalizer, CodeVersion,
                new EvidenceQualityReport(), new EvidenceObjectNamespace("normalized/synthetic")), group.ToArray()));
        }
        var attributes = rankedListSha256 is null ? null : new Dictionary<string, string> { ["ranked_list_sha256"] = rankedListSha256 };
        return Add(EvidenceParquetPartitionPublisher.BuildDatasetManifest(EvidenceDatasetKind.MarketBarsAsTraded, 1, DateTimeOffset.UtcNow,
            "job-" + Guid.NewGuid().ToString("N"), new string('f', 64), ConfigHash, CodeVersion, Normalizer, "sip", partitions,
            new EvidenceQualityReport(), attributes));
    }

    /// <summary>Publishes an XNYS calendar covering [from, toExclusive) whose trading days are the weekdays not in <paramref name="closed"/>.</summary>
    public async Task<EvidenceDatasetManifest> SessionsAsync(DateOnly from, DateOnly toExclusive, IReadOnlySet<DateOnly>? closed = null)
    {
        var rows = new List<ExchangeSessionEvidenceRow>();
        for (var day = from; day < toExclusive; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || closed?.Contains(day) == true) continue;
            rows.Add(new(1, RunId, ConfigHash, CodeVersion, "alpaca-trading", "alpaca", "XNYS", day, At(day, 4, 0), At(day, 9, 30), At(day, 16, 0),
                At(day, 20, 0), false, "alpaca_official_market_calendar", At(day, 0, 0), [new EvidenceRowSourceAddress(Source.ObservationId, Source.Artifact.Content.Sha256)]));
        }
        var provenance = new EvidencePartitionProvenance("alpaca", "/v2/calendar", "alpaca-trading", "raw", "USD", from, [], [], ["US_EQUITIES"], "session",
            new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), new DateTimeOffset(toExclusive.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        var partition = await new EvidenceParquetPartitionPublisher(codec, store).PublishExchangeSessionsAsync(new EvidenceParquetPartitionRequest(
            "sessions-" + Guid.NewGuid().ToString("N"), EvidenceDatasetKind.ExchangeSessions, 1, provenance, [Source], RunId, ConfigHash, Normalizer,
            CodeVersion, new EvidenceQualityReport(), new EvidenceObjectNamespace("normalized/synthetic-sessions")), rows);
        return Add(EvidenceParquetPartitionPublisher.BuildDatasetManifest(EvidenceDatasetKind.ExchangeSessions, 1, DateTimeOffset.UtcNow,
            "job-" + Guid.NewGuid().ToString("N"), new string('f', 64), ConfigHash, CodeVersion, Normalizer, "alpaca-trading", [partition], new EvidenceQualityReport()));
    }

    /// <summary>
    /// Publishes a corporate-actions dataset of forward splits (symbol, ex-date, new shares per old share), with the Alpaca
    /// old_rate/new_rate fields the shared split reader parses.
    /// </summary>
    public async Task<EvidenceDatasetManifest> SplitsAsync(IReadOnlyList<(string Symbol, DateOnly ExDate, decimal Ratio)> splits)
    {
        var received = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        var rows = splits.Select((split, index) => new CorporateActionEvidenceRow(1, RunId, ConfigHash, CodeVersion, "alpaca-market-data", "alpaca",
            CorporateActionEvidenceType.ForwardSplit, $"split-{index}-{split.Symbol}", null, null, split.Symbol, null, split.ExDate, split.ExDate,
            split.ExDate, null, null, new Dictionary<string, string> { ["symbol"] = split.Symbol, ["old_rate"] = "1",
                ["new_rate"] = split.Ratio.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            received, [new EvidenceRowSourceAddress(Source.ObservationId, Source.Artifact.Content.Sha256)])).ToArray();
        var first = splits.Min(split => split.ExDate);
        var provenance = new EvidencePartitionProvenance("alpaca", "/v1/corporate-actions", "alpaca-market-data", "raw", "USD", first, [], [],
            splits.Select(split => split.Symbol).Distinct().ToArray(), "event", new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            new DateTimeOffset(splits.Max(split => split.ExDate).AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        var partition = await new EvidenceParquetPartitionPublisher(codec, store).PublishCorporateActionsAsync(new EvidenceParquetPartitionRequest(
            "actions-" + Guid.NewGuid().ToString("N"), EvidenceDatasetKind.CorporateActions, 1, provenance, [Source], RunId, ConfigHash, Normalizer,
            CodeVersion, new EvidenceQualityReport(), new EvidenceObjectNamespace("normalized/synthetic-actions")), rows);
        return Add(EvidenceParquetPartitionPublisher.BuildDatasetManifest(EvidenceDatasetKind.CorporateActions, 1, DateTimeOffset.UtcNow,
            "job-" + Guid.NewGuid().ToString("N"), new string('f', 64), ConfigHash, CodeVersion, Normalizer, "alpaca-market-data", [partition],
            new EvidenceQualityReport()));
    }

    /// <summary>The catalog file path a provider configuration names (the synthetic catalog serves datasets from memory).</summary>
    public string CatalogPath => Path.Combine(folder.Path, "catalog.db");

    /// <summary>The object store root a provider configuration names.</summary>
    public string ObjectRoot => Path.Combine(folder.Path, "objects");

    /// <summary>
    /// An adapter over a small market: ABC daily bars for three sessions, three ABC and one XYZ five-minute bars from 1 October
    /// 2026 13:30 UTC, two ABC one-minute bars, and an XNYS calendar for 21 September to 10 October 2026.
    /// </summary>
    /// <summary>A provider over this catalog with the given configuration.</summary>
    public EvidenceCatalogMarketDataProvider Provider(EvidenceCatalogProviderConfig config) =>
        new(config, GetAsync, Reader, new FileSystemRawArchiveWriter(new RawArchiveOptions(RawRoot)));

    public async Task<(EvidenceCatalogMarketDataProvider Provider, EvidenceCatalogProviderConfig Config)> ProviderAsync(string rankedListSha256)
    {
        var open = new DateTimeOffset(2026, 10, 1, 13, 30, 0, TimeSpan.Zero);
        var daily = await BarsAsync("1d", null, [.. Enumerable.Range(0, 3).Select(day => Row("ABC", "1d", new DateTimeOffset(2026, 9, 28 + day, 4, 0, 0, TimeSpan.Zero), 100m))]);
        var five = await BarsAsync("5m", rankedListSha256, [Row("ABC", "5m", open.AddMinutes(10), 102m), Row("ABC", "5m", open, 100m),
            Row("ABC", "5m", open.AddMinutes(5), 101m), Row("XYZ", "5m", open, 50m)]);
        var minute = await BarsAsync("1m", rankedListSha256, [Row("ABC", "1m", open, 100m), Row("ABC", "1m", open.AddMinutes(1), 100.5m)]);
        var sessions = await SessionsAsync(new DateOnly(2026, 9, 21), new DateOnly(2026, 10, 11));
        var config = new EvidenceCatalogProviderConfig(Path.Combine(folder.Path, "catalog.db"), Path.Combine(folder.Path, "objects"), rankedListSha256,
            daily.DatasetId, sessions.DatasetId, [five.DatasetId], [minute.DatasetId]);
        return (new EvidenceCatalogMarketDataProvider(config, GetAsync, Reader, new FileSystemRawArchiveWriter(new RawArchiveOptions(RawRoot))), config);
    }

    /// <summary>Records a dataset so the lookup returns it by its ID.</summary>
    private EvidenceDatasetManifest Add(EvidenceDatasetManifest dataset)
    {
        Datasets[dataset.DatasetId] = dataset;
        return dataset;
    }

    /// <summary>A New York wall-clock time on a date, as UTC.</summary>
    private static DateTimeOffset At(DateOnly day, int hour, int minute) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(hour, minute)), NewYork), TimeSpan.Zero);

    /// <summary>Deletes the temporary catalog folder.</summary>
    public void Dispose() => folder.Dispose();
}
