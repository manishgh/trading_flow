using System.Security.Cryptography;
using System.Text;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Research;
using TradingFlow.Research;
using TradingFlow.Research.Catalysts;
using TradingFlow.Research.Momentum;

namespace TradingFlow.Tests;

public sealed class EvidenceCatalogResearchRunnerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Momentum_UsesCommittedAdjustedBarsAndPointInTimeMembership()
    {
        var adjustedManifest = Manifest(
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            "sip",
            "adjusted-bars");
        var asTradedManifest = Manifest(
            EvidenceDatasetKind.MarketBarsAsTraded,
            "sip",
            "raw-bars");
        var universeManifest = Manifest(
            EvidenceDatasetKind.UniverseMembership,
            "finviz",
            "universe");
        var bars = BuildBars();
        var adjustedRows = BuildMarketRows(bars, "all", TimeSpan.FromDays(1));
        var asTradedRows = BuildMarketRows(bars, "raw", TimeSpan.FromDays(1));
        var membershipRows = BuildMembershipRows(
            bars["SPY"].Select(bar => DateOnly.FromDateTime(bar.Timestamp.UtcDateTime)),
            ["A", "B", "C", "D", "E"]);
        var runner = new EvidenceCatalogResearchRunner(
            new CatalogStub([adjustedManifest, asTradedManifest, universeManifest]),
            new PartitionReaderStub(adjustedRows, asTradedRows, membershipRows));

        var result = await runner.RunMomentumAsync(new CatalogMomentumStudyRequest(
            adjustedManifest.DatasetId,
            asTradedManifest.DatasetId,
            universeManifest.DatasetId,
            Definition()));

        Assert.True(result.Report.PointInTimeUniverseEvidence);
        Assert.True(result.Report.AdjustedPricesConfirmed);
        Assert.NotEmpty(result.Report.RankObservations);
        Assert.DoesNotContain(
            result.Report.RankObservations,
            observation => observation.Ticker == "EXCLUDED");
    }

    [Fact]
    public async Task Catalyst_RejectsNonSipBarsBeforeReadingPartitions()
    {
        var barsManifest = Manifest(
            EvidenceDatasetKind.MarketBarsAsTraded,
            "iex",
            "bars");
        var newsManifest = Manifest(
            EvidenceDatasetKind.NewsArticles,
            "alpaca",
            "news");
        var reader = new PartitionReaderStub([], [], []);
        var runner = new EvidenceCatalogResearchRunner(
            new CatalogStub([barsManifest, newsManifest]),
            reader);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            runner.RunCatalystAsync(new CatalogCatalystStudyRequest(
                barsManifest.DatasetId,
                newsManifest.DatasetId,
                "sentiment-dataset-not-read",
                "universe-not-read",
                null,
                Now.AddDays(-1),
                Now,
                "5m")));

        Assert.Contains("requires SIP bar evidence", exception.Message);
        Assert.Equal(0, reader.ReadCount);
    }

    [Fact]
    public async Task Catalyst_RequiresCommittedSentimentAssessmentDataset()
    {
        var barsManifest = Manifest(
            EvidenceDatasetKind.MarketBarsAsTraded,
            "sip",
            "bars-without-sentiment");
        var newsManifest = Manifest(
            EvidenceDatasetKind.NewsRevisions,
            "alpaca",
            "news-without-sentiment");
        var reader = new PartitionReaderStub([], [], []);
        var runner = new EvidenceCatalogResearchRunner(
            new CatalogStub([barsManifest, newsManifest]),
            reader);

        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            runner.RunCatalystAsync(new CatalogCatalystStudyRequest(
                barsManifest.DatasetId,
                newsManifest.DatasetId,
                "missing-sentiment-dataset",
                "universe-not-read",
                null,
                Now.AddDays(-1),
                Now,
                "5m")));

        Assert.Contains("missing-sentiment-dataset", exception.Message);
        Assert.Equal(0, reader.ReadCount);
    }

    [Fact]
    public async Task Momentum_RejectsUniverseSnapshotsObservedAfterCompletedFormationBars()
    {
        var adjustedManifest = Manifest(
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            "sip",
            "adjusted-bars-late");
        var asTradedManifest = Manifest(
            EvidenceDatasetKind.MarketBarsAsTraded,
            "sip",
            "raw-bars-late");
        var universeManifest = Manifest(
            EvidenceDatasetKind.UniverseMembership,
            "finviz",
            "universe-late");
        var bars = BuildBars();
        var runner = new EvidenceCatalogResearchRunner(
            new CatalogStub([adjustedManifest, asTradedManifest, universeManifest]),
            new PartitionReaderStub(
                BuildMarketRows(bars, "all"),
                BuildMarketRows(bars, "raw"),
                BuildMembershipRows(
                    bars["SPY"].Select(bar =>
                        DateOnly.FromDateTime(bar.Timestamp.UtcDateTime)),
                    ["A", "B", "C", "D", "E"],
                    observedAfterClose: true)));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            runner.RunMomentumAsync(new CatalogMomentumStudyRequest(
                adjustedManifest.DatasetId,
                asTradedManifest.DatasetId,
                universeManifest.DatasetId,
                Definition())));

        Assert.Contains("no active members", exception.Message);
    }

    [Fact]
    public async Task Catalyst_UsesCommittedSipQuotesForNetExecutableReturns()
    {
        var start = new DateTimeOffset(2026, 7, 6, 13, 30, 0, TimeSpan.Zero);
        var barsManifest = Manifest(
            EvidenceDatasetKind.MarketBarsAsTraded,
            "sip",
            "catalyst-bars");
        var newsManifest = Manifest(
            EvidenceDatasetKind.NewsRevisions,
            "alpaca",
            "catalyst-news");
        var quotesManifest = Manifest(
            EvidenceDatasetKind.SipQuotes,
            "sip",
            "catalyst-quotes");
        var sentimentManifest = Manifest(
            EvidenceDatasetKind.SentimentAssessments,
            "finbert",
            "catalyst-sentiment");
        var universeManifest = Manifest(
            EvidenceDatasetKind.UniverseMembership,
            "finviz",
            "catalyst-universe");
        var bars = Enumerable.Range(0, 40)
            .Select(index =>
            {
                var open = 100m + index * 0.05m;
                return new MarketBarEvidenceRow(
                    1,
                    "run-catalyst",
                    Hash("config-catalyst"),
                    "code-v1",
                    "sip",
                    "security-POET",
                    "POET",
                    start.AddMinutes(index * 5),
                    start.AddMinutes((index + 1) * 5),
                    "5m",
                    EvidenceFixedDecimal.ToPriceUnits(open),
                    EvidenceFixedDecimal.ToPriceUnits(open + 0.20m),
                    EvidenceFixedDecimal.ToPriceUnits(open - 0.10m),
                    EvidenceFixedDecimal.ToPriceUnits(open + 0.10m),
                    null,
                    10_000 + index * 100,
                    100,
                    "raw",
                    "USD",
                    start.AddMinutes((index + 1) * 5),
                    start.AddMinutes((index + 1) * 5).AddSeconds(1),
                    [Source("catalyst-bars")]);
            })
            .ToArray();
        var publishedAt = start.AddMinutes(17);
        var receivedAt = publishedAt.AddSeconds(1);
        var news = new NewsRevisionEvidenceRow(
            1,
            "run-catalyst",
            Hash("config-catalyst"),
            "code-v1",
            "alpaca",
            NewsAvailabilityEvidence.ObservedReceiptTime,
            "alpaca",
            "article-1",
            "article-1-r1",
            "POET wins a material customer contract",
            "Contract value expands the company's backlog.",
            "https://example.test/article-1",
            ["POET"],
            ["contracts"],
            publishedAt,
            publishedAt,
            publishedAt,
            receivedAt,
            [Source("catalyst-news")]);
        var entryAt = start.AddMinutes(20);
        var exitAt = start.AddMinutes(35);
        var quotes = new[]
        {
            Quote("POET", entryAt.AddMilliseconds(100), 101.00m, 101.05m),
            Quote("POET", exitAt.AddMilliseconds(100), 102.00m, 102.05m)
        };
        var sentiment = Sentiment(news, 0.72m, receivedAt);
        var reader = new PartitionReaderStub(
            [],
            bars,
            BuildMembershipRows(
                [DateOnly.FromDateTime(start.UtcDateTime)],
                ["POET"]),
            [news],
            quotes,
            [sentiment]);
        var runner = new EvidenceCatalogResearchRunner(
            new CatalogStub([
                barsManifest,
                newsManifest,
                quotesManifest,
                sentimentManifest,
                universeManifest
            ]),
            reader,
            new FixedRegularSessionResolver(
                start.Date,
                start,
                start.AddHours(6.5)));

        var result = await runner.RunCatalystAsync(new CatalogCatalystStudyRequest(
            barsManifest.DatasetId,
            newsManifest.DatasetId,
            sentimentManifest.DatasetId,
            universeManifest.DatasetId,
            quotesManifest.DatasetId,
            start,
            start.AddHours(3),
            "5m",
            new CatalystEventStudyOptions
            {
                Horizons = [TimeSpan.FromMinutes(15)],
                StudyPartitions =
                [
                    new CatalystStudyPartition("holdout", start, start.AddHours(3))
                ]
            },
            new CatalogCatalystExecutionOptions(
                ExecutablePositionDirection.Long,
                100,
                TimeSpan.FromSeconds(1),
                new SipQuoteEligibilityPolicy([], ["Q", "P"], true),
                new CatalystExecutionCostModel(0.005m, 1m, 2m, 2m),
                new CatalystExecutionCalibrationPolicy(
                    TimeSpan.Zero,
                    1m,
                    100m,
                    1m,
                    CatalystExecutionMechanism.ContinuousMarketableNbbo,
                    CatalystExecutionEvidenceStatus.EvidenceBacked,
                    CatalystExecutionEvidenceStatus.EvidenceBacked,
                    CatalystExecutionEvidenceStatus.EvidenceBacked))));

        var executable = Assert.Single(result.ExecutableObservations);
        Assert.Equal("15m", executable.Horizon);
        Assert.Equal(
            CatalystExecutableReturnStatus.NetExecutableEstimate,
            executable.Result.Status);
        Assert.Equal(101.05m, executable.Result.EntryPrice);
        Assert.Equal(102.00m, executable.Result.ExitPrice);
        Assert.Equal(0.72m, Assert.Single(result.Report.Observations).SentimentScore);
        Assert.Equal(sentimentManifest.DatasetId, result.SentimentAssessmentsDatasetId);
        Assert.False(result.ExecutableEvidenceReady);
        Assert.Contains(
            "independent_catalyst_events_below_minimum:1/300",
            result.ReadinessFailures);
        Assert.DoesNotContain(
            "executable_quote_return_model_not_yet_applied",
            result.ReadinessFailures);
    }

    [Fact]
    public async Task Catalyst_RejectsMissingSentimentAssessment()
    {
        var fixture = CatalystFixture([]);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Runner.RunCatalystAsync(fixture.Request));

        Assert.Contains("assessment is missing", exception.Message);
    }

    [Fact]
    public async Task Catalyst_RejectsAmbiguousSentimentAssessments()
    {
        var news = CatalystNews();
        var first = Sentiment(news, 0.5m, news.AvailabilityTimestampUtc);
        var second = Sentiment(
            news,
            0.6m,
            news.AvailabilityTimestampUtc,
            assessmentId: "assessment-2");
        var fixture = CatalystFixture([first, second], news);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Runner.RunCatalystAsync(fixture.Request));

        Assert.Contains("assessment is ambiguous", exception.Message);
    }

    [Fact]
    public async Task Catalyst_RejectsStaleInputAndLineage()
    {
        var news = CatalystNews();
        var staleContent = SentimentAssessmentEvidenceRow
            .CreateNewsRevisionInputContent(news)
            .Replace("raises guidance", "repeats guidance", StringComparison.Ordinal);
        var stale = Sentiment(
            news,
            0.5m,
            news.AvailabilityTimestampUtc,
            inputContent: staleContent);
        var staleFixture = CatalystFixture([stale], news);

        var staleException = await Assert.ThrowsAsync<InvalidDataException>(() =>
            staleFixture.Runner.RunCatalystAsync(staleFixture.Request));

        Assert.Contains("input hash does not match", staleException.Message);

        var wrongLineage = Sentiment(
            news,
            0.5m,
            news.AvailabilityTimestampUtc,
            sources: [Source("different-news-source")]);
        var lineageFixture = CatalystFixture([wrongLineage], news);

        var lineageException = await Assert.ThrowsAsync<InvalidDataException>(() =>
            lineageFixture.Runner.RunCatalystAsync(lineageFixture.Request));

        Assert.Contains("lineage does not exactly match", lineageException.Message);
    }

    [Fact]
    public async Task Catalyst_DelaysDecisionUntilAssessmentIsObserved()
    {
        var news = CatalystNews();
        var assessmentObservedAt = news.AvailabilityTimestampUtc.AddMilliseconds(1);
        var future = Sentiment(
            news,
            0.5m,
            assessmentObservedAt);
        var fixture = CatalystFixture([future], news);

        var result = await fixture.Runner.RunCatalystAsync(fixture.Request with
        {
            Options = new CatalystEventStudyOptions
            {
                StudyPartitions =
                [
                    new CatalystStudyPartition(
                        "holdout",
                        fixture.Request.StartUtc,
                        fixture.Request.EndUtc)
                ]
            }
        });

        var observation = Assert.Single(result.Report.Observations);
        Assert.Equal(assessmentObservedAt, observation.AvailableTimestampUtc);
        Assert.Equal(
            CatalystAvailabilityEvidence.NewsAndAssessmentObservedTime,
            observation.AvailabilityEvidence);
    }

    [Fact]
    public async Task Catalyst_ValidModelVintageDoesNotAddVintageReadinessFailure()
    {
        var news = CatalystNews();
        var assessment = Sentiment(
            news,
            0.5m,
            news.AvailabilityTimestampUtc.AddSeconds(1));
        var fixture = CatalystFixture([assessment], news);

        var result = await fixture.Runner.RunCatalystAsync(fixture.Request with
        {
            Options = new CatalystEventStudyOptions
            {
                StudyPartitions =
                [
                    new CatalystStudyPartition(
                        "holdout",
                        fixture.Request.StartUtc,
                        fixture.Request.EndUtc)
                ]
            }
        });

        Assert.DoesNotContain(
            result.ReadinessFailures,
            failure => failure.StartsWith(
                "sentiment_model_",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Catalyst_FailsReadinessWhenModelCutoffFollowsFirstNewsAvailability()
    {
        var news = CatalystNews();
        var assessment = Sentiment(
            news,
            0.5m,
            news.AvailabilityTimestampUtc.AddSeconds(2),
            modelTrainingDataCutoffUtc:
                news.AvailabilityTimestampUtc.AddSeconds(1));
        var fixture = CatalystFixture([assessment], news);

        var result = await fixture.Runner.RunCatalystAsync(fixture.Request with
        {
            Options = new CatalystEventStudyOptions
            {
                StudyPartitions =
                [
                    new CatalystStudyPartition(
                        "development",
                        fixture.Request.StartUtc,
                        fixture.Request.EndUtc)
                ]
            }
        });

        Assert.False(result.ExecutableEvidenceReady);
        Assert.Contains(
            result.ReadinessFailures,
            failure => failure.StartsWith(
                "sentiment_model_training_cutoff_after_first_news_availability:",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Catalyst_FailsReadinessWhenModelCutoffFollowsHoldoutBoundary()
    {
        var news = CatalystNews();
        var fixture = CatalystFixture(
            [
                Sentiment(
                    news,
                    0.5m,
                    news.AvailabilityTimestampUtc.AddSeconds(1),
                    modelTrainingDataCutoffUtc:
                        news.AvailabilityTimestampUtc.AddMinutes(-5))
            ],
            news);
        var holdoutStart = news.AvailabilityTimestampUtc.AddMinutes(-10);

        var result = await fixture.Runner.RunCatalystAsync(fixture.Request with
        {
            Options = new CatalystEventStudyOptions
            {
                StudyPartitions =
                [
                    new CatalystStudyPartition(
                        "development",
                        fixture.Request.StartUtc,
                        holdoutStart),
                    new CatalystStudyPartition(
                        "holdout",
                        holdoutStart,
                        fixture.Request.EndUtc)
                ]
            }
        });

        Assert.False(result.ExecutableEvidenceReady);
        Assert.Contains(
            result.ReadinessFailures,
            failure => failure.StartsWith(
                "sentiment_model_training_cutoff_after_holdout_boundary:",
                StringComparison.Ordinal));
    }

    private static CatalystTestFixture CatalystFixture(
        IReadOnlyList<SentimentAssessmentEvidenceRow> assessments,
        NewsRevisionEvidenceRow? news = null)
    {
        news ??= CatalystNews();
        var start = news.PublishedAtUtc.AddMinutes(-15);
        var end = start.AddHours(1);
        var barsManifest = Manifest(
            EvidenceDatasetKind.MarketBarsAsTraded,
            "sip",
            $"join-bars-{Guid.NewGuid():N}");
        var newsManifest = Manifest(
            EvidenceDatasetKind.NewsRevisions,
            "alpaca",
            $"join-news-{Guid.NewGuid():N}");
        var sentimentManifest = Manifest(
            EvidenceDatasetKind.SentimentAssessments,
            "finbert",
            $"join-sentiment-{Guid.NewGuid():N}");
        var universeManifest = Manifest(
            EvidenceDatasetKind.UniverseMembership,
            "finviz",
            $"join-universe-{Guid.NewGuid():N}");
        var bar = new MarketBarEvidenceRow(
            1,
            "run-catalyst",
            Hash("config-catalyst"),
            "code-v1",
            "sip",
            "security-POET",
            "POET",
            start,
            start.AddMinutes(5),
            "5m",
            EvidenceFixedDecimal.ToPriceUnits(100m),
            EvidenceFixedDecimal.ToPriceUnits(101m),
            EvidenceFixedDecimal.ToPriceUnits(99m),
            EvidenceFixedDecimal.ToPriceUnits(100.5m),
            null,
            10_000,
            100,
            "raw",
            "USD",
            start.AddMinutes(5),
            start.AddMinutes(5).AddSeconds(1),
            [Source("join-bars")]);
        var runner = new EvidenceCatalogResearchRunner(
            new CatalogStub([
                barsManifest,
                newsManifest,
                sentimentManifest,
                universeManifest
            ]),
            new PartitionReaderStub(
                [],
                [bar],
                BuildMembershipRows(
                    [DateOnly.FromDateTime(start.UtcDateTime)],
                    ["POET"]),
                [news],
                [],
                assessments),
            new FixedRegularSessionResolver(start.Date, start, end));
        var request = new CatalogCatalystStudyRequest(
            barsManifest.DatasetId,
            newsManifest.DatasetId,
            sentimentManifest.DatasetId,
            universeManifest.DatasetId,
            null,
            start,
            end,
            "5m");
        return new CatalystTestFixture(runner, request);
    }

    private static NewsRevisionEvidenceRow CatalystNews()
    {
        var publishedAt = new DateTimeOffset(2026, 7, 6, 13, 45, 0, TimeSpan.Zero);
        return new NewsRevisionEvidenceRow(
            1,
            "run-catalyst",
            Hash("config-catalyst"),
            "code-v1",
            "alpaca",
            NewsAvailabilityEvidence.ObservedReceiptTime,
            "alpaca",
            "article-join",
            "article-join-r1",
            "POET raises guidance",
            "Management increased its full-year outlook.",
            "https://example.test/article-join",
            ["POET"],
            ["guidance"],
            publishedAt,
            publishedAt,
            publishedAt,
            publishedAt.AddSeconds(1),
            [Source("catalyst-news")]);
    }

    private static SentimentAssessmentEvidenceRow Sentiment(
        NewsRevisionEvidenceRow news,
        decimal score,
        DateTimeOffset observedAtUtc,
        string assessmentId = "assessment-1",
        string? inputContent = null,
        IReadOnlyList<EvidenceRowSourceAddress>? sources = null,
        DateTimeOffset? modelTrainingDataCutoffUtc = null)
    {
        inputContent ??=
            SentimentAssessmentEvidenceRow.CreateNewsRevisionInputContent(news);
        return new SentimentAssessmentEvidenceRow(
            SentimentAssessmentEvidenceRow.ContractSchemaVersion,
            "run-sentiment",
            Hash("config-sentiment"),
            "code-v1",
            "finbert",
            assessmentId,
            news.Provider,
            news.ProviderArticleId,
            news.RevisionId,
            inputContent,
            SentimentAssessmentEvidenceRow.ComputeInputContentSha256(inputContent),
            "local-finbert",
            "ProsusAI/finbert",
            "2026-07-model-pin",
            "hf:ProsusAI/finbert@2026-07-model-pin",
            Hash("finbert-model-artifact"),
            modelTrainingDataCutoffUtc ??
                news.AvailabilityTimestampUtc.AddDays(-30),
            "headline-summary-stage-v1",
            score,
            observedAtUtc,
            observedAtUtc,
            sources ?? news.Sources);
    }

    private static CrossSectionalMomentumStudyDefinition Definition() =>
        new(
            "catalog momentum",
            "SPY",
            "point-in-time test universe",
            false,
            false,
            new CrossSectionalMomentumResearchOptions(
                MomentumLookbackBars: 6,
                SkipRecentBars: 2,
                FastTrendSmaBars: 2,
                SlowTrendSmaBars: 4,
                AverageDollarVolumeBars: 2,
                MinimumAverageDollarVolume: 0m,
                FormationSchedule: MomentumFormationSchedule.EveryNBars,
                DecisionCadenceBars: 2,
                ForwardHorizons: [1, 2],
                QuantileCount: 2,
                PrimarySelectionFraction: 0.25m,
                MinimumCandidatesPerDate: 4,
                DevelopmentFraction: 0.50m,
                ValidationFraction: 0.25m,
                HoldoutFraction: 0.25m,
                MinimumFormationDates: 1,
                MinimumHoldoutFormationDates: 1,
                MinimumDistinctSelectedTickers: 1,
                Cells: [MomentumResearchCell.MomentumOnly],
                ExchangeTimezone: "America/New_York"));

    private static Dictionary<string, IReadOnlyList<OhlcvBar>> BuildBars()
    {
        var output = new Dictionary<string, IReadOnlyList<OhlcvBar>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = Bars("SPY", 30, index => 100m + index),
            ["EXCLUDED"] = Bars("EXCLUDED", 30, index => 20m + (index * 4m)),
            ["A"] = Bars("A", 30, index => 30m + index),
            ["B"] = Bars("B", 30, index => 40m + index),
            ["C"] = Bars("C", 30, index => 50m + index),
            ["D"] = Bars("D", 30, index => 60m + index),
            ["E"] = Bars("E", 30, index => 70m + index)
        };
        return output;
    }

    private static IReadOnlyList<OhlcvBar> Bars(
        string ticker,
        int count,
        Func<int, decimal> close) =>
        Enumerable.Range(0, count)
            .Select(index =>
            {
                var value = close(index);
                return new OhlcvBar(
                    ticker,
                    new DateTimeOffset(2026, 1, 2, 5, 0, 0, TimeSpan.Zero).AddDays(index),
                    "1d",
                    value,
                    value + 1m,
                    value - 1m,
                    value,
                    1_000_000m);
            })
            .ToArray();

    private static IReadOnlyList<MarketBarEvidenceRow> BuildMarketRows(
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> bars,
        string adjustment,
        TimeSpan? barDuration = null) =>
        bars
            .SelectMany(entry => entry.Value.Select(bar =>
            {
                var end = bar.Timestamp.Add(barDuration ?? TimeSpan.FromHours(16));
                return new MarketBarEvidenceRow(
                    1,
                    "run-test",
                    Hash("config"),
                    "code-v1",
                    "sip",
                    $"security-{entry.Key}",
                    entry.Key,
                    bar.Timestamp,
                    end,
                    "1d",
                    EvidenceFixedDecimal.ToPriceUnits(bar.Open),
                    EvidenceFixedDecimal.ToPriceUnits(bar.High),
                    EvidenceFixedDecimal.ToPriceUnits(bar.Low),
                    EvidenceFixedDecimal.ToPriceUnits(bar.Close),
                    null,
                    Decimal.ToInt64(bar.Volume),
                    null,
                    adjustment,
                    "USD",
                    end,
                    Now,
                    [new EvidenceRowSourceAddress(
                        $"observation-{adjustment}-{entry.Key}",
                        Hash($"source-{adjustment}-{entry.Key}"))]);
            }))
            .ToArray();

    private static IReadOnlyList<UniverseMembershipEvidenceRow> BuildMembershipRows(
        IEnumerable<DateOnly> dates,
        IReadOnlyList<string> symbols,
        bool observedAfterClose = false)
    {
        var rows = new List<UniverseMembershipEvidenceRow>();
        foreach (var date in dates.Distinct())
        {
            var observedAt = new DateTimeOffset(
                date.ToDateTime(observedAfterClose
                    ? new TimeOnly(23, 0)
                    : new TimeOnly(12, 0)),
                TimeSpan.Zero);
            var snapshotHash = Hash($"snapshot-{date:O}");
            for (var index = 0; index < symbols.Count; index++)
            {
                var symbol = symbols[index];
                rows.Add(new UniverseMembershipEvidenceRow(
                    1,
                    "run-test",
                    Hash("config"),
                    "code-v1",
                    "finviz",
                    "finviz",
                    "test-universe",
                    $"snapshot-{date:O}",
                    "test",
                    snapshotHash,
                    date,
                    $"security-{symbol}",
                    $"issuer-{symbol}",
                    symbol,
                    observedAt,
                    true,
                    index + 1,
                    "eligible",
                    observedAt,
                    observedAt,
                    [new EvidenceRowSourceAddress(
                        $"observation-universe-{date:O}",
                        snapshotHash)]));
            }
        }

        return rows;
    }

    private static EvidenceDatasetManifest Manifest(
        EvidenceDatasetKind kind,
        string feed,
        string seed)
    {
        var sourceArtifact = Artifact($"raw-{seed}", "raw/test", "application/json");
        var partitionArtifact = Artifact(
            $"partition-{seed}",
            "normalized/test",
            "application/vnd.apache.parquet");
        var source = new EvidenceSourceReference(
            $"observation-{seed}",
            sourceArtifact,
            Now.AddMinutes(-2));
        return new EvidenceDatasetManifest(
            kind,
            1,
            Now,
            $"job-{seed}",
            Hash($"plan-{seed}"),
            Hash($"config-{seed}"),
            "code-v1",
            "normalizer-v1",
            feed,
            [
                new EvidenceDatasetPartitionManifest(
                    $"partition-{seed}",
                    kind,
                    1,
                    new EvidencePartitionProvenance(
                        kind == EvidenceDatasetKind.NewsArticles ? "alpaca" : "test",
                        "/test",
                        feed,
                        kind == EvidenceDatasetKind.MarketBarsResearchAdjusted
                            ? "all"
                            : kind == EvidenceDatasetKind.MarketBarsAsTraded
                                ? "raw"
                                : "none",
                        "USD",
                        kind is EvidenceDatasetKind.MarketBarsAsTraded or
                            EvidenceDatasetKind.MarketBarsResearchAdjusted or
                            EvidenceDatasetKind.SipQuotes or
                            EvidenceDatasetKind.UniverseMembership
                            ? new DateOnly(2026, 7, 25)
                            : null,
                        kind is EvidenceDatasetKind.MarketBarsAsTraded or
                            EvidenceDatasetKind.MarketBarsResearchAdjusted or
                            EvidenceDatasetKind.SipQuotes or
                            EvidenceDatasetKind.UniverseMembership
                            ? ["security-a"]
                            : [],
                        kind == EvidenceDatasetKind.UniverseMembership
                            ? ["issuer-a"]
                            : [],
                        ["A"],
                        kind is EvidenceDatasetKind.UniverseMembership or
                            EvidenceDatasetKind.MarketBarsResearchAdjusted or
                            EvidenceDatasetKind.MarketBarsAsTraded
                            ? "1d"
                            : "5m",
                        Now.AddDays(-1),
                        Now),
                    Now.AddDays(-1),
                    Now,
                    1,
                    partitionArtifact,
                    [source],
                    "normalizer-v1",
                    "code-v1",
                    new EvidenceQualityReport())
            ],
            new EvidenceQualityReport());
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

    private static EvidenceRowSourceAddress Source(string seed) =>
        new($"observation-{seed}", Hash($"source-{seed}"));

    private static SipQuoteEvidenceRow Quote(
        string symbol,
        DateTimeOffset timestamp,
        decimal bid,
        decimal ask) =>
        new(
            1,
            "run-catalyst",
            Hash("config-catalyst"),
            "code-v1",
            $"security-{symbol}",
            symbol,
            timestamp,
            EvidenceFixedDecimal.ToPriceUnits(bid),
            EvidenceFixedDecimal.ToPriceUnits(ask),
            1_000,
            1_000,
            "P",
            "Q",
            "sip",
            "USD",
            [],
            timestamp,
            timestamp.AddMilliseconds(10),
            [Source($"quote-{symbol}-{timestamp:O}")]);

    private sealed class PartitionReaderStub(
        IReadOnlyList<MarketBarEvidenceRow> adjustedBars,
        IReadOnlyList<MarketBarEvidenceRow> asTradedBars,
        IReadOnlyList<UniverseMembershipEvidenceRow> universeRows,
        IReadOnlyList<NewsRevisionEvidenceRow>? newsRows = null,
        IReadOnlyList<SipQuoteEvidenceRow>? quoteRows = null,
        IReadOnlyList<SentimentAssessmentEvidenceRow>? sentimentRows = null) :
        IEvidencePartitionDataReader
    {
        public int ReadCount { get; private set; }

        public Task<IReadOnlyList<MarketBarEvidenceRow>> ReadMarketBarsAsync(
                EvidenceDatasetManifest manifest,
                CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(
                manifest.Kind == EvidenceDatasetKind.MarketBarsResearchAdjusted
                    ? adjustedBars
                    : asTradedBars);
        }

        public Task<IReadOnlyList<NewsRevisionEvidenceRow>> ReadNewsRevisionsAsync(
                EvidenceDatasetManifest manifest,
                CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult<IReadOnlyList<NewsRevisionEvidenceRow>>(
                newsRows ?? []);
        }

        public Task<IReadOnlyList<UniverseMembershipEvidenceRow>> ReadUniverseMembershipAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(universeRows);
        }

        public Task<IReadOnlyList<SentimentAssessmentEvidenceRow>> ReadSentimentAssessmentsAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult<IReadOnlyList<SentimentAssessmentEvidenceRow>>(
                sentimentRows ?? []);
        }

        public Task<IReadOnlyList<SecurityMasterSnapshotEvidenceRow>>
            ReadSecurityMasterSnapshotsAsync(
                EvidenceDatasetManifest manifest,
                CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SecurityMasterSnapshotEvidenceRow>>([]);

        public Task<IReadOnlyList<SymbolIntervalEvidenceRow>> ReadSymbolIntervalsAsync(
                EvidenceDatasetManifest manifest,
                CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SymbolIntervalEvidenceRow>>([]);

        public Task<IReadOnlyList<CorporateActionEvidenceRow>> ReadCorporateActionsAsync(
                EvidenceDatasetManifest manifest,
                CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CorporateActionEvidenceRow>>([]);

        public Task<IReadOnlyList<SipQuoteEvidenceRow>> ReadSipQuotesAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult<IReadOnlyList<SipQuoteEvidenceRow>>(
                quoteRows ?? []);
        }

        public Task<IReadOnlyList<ExchangeSessionEvidenceRow>> ReadExchangeSessionsAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExchangeSessionEvidenceRow>>([]);
    }

    private sealed class FixedRegularSessionResolver(
        DateTime sessionDate,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc) : IExchangeSessionResolver
    {
        public ExchangeSessionResolution Resolve(DateTimeOffset timestampUtc) =>
            new(
                DateOnly.FromDateTime(sessionDate),
                timestampUtc >= startUtc && timestampUtc < endUtc
                    ? EquityTradingSession.Regular
                    : EquityTradingSession.Closed,
                startUtc,
                endUtc);
    }

    private sealed class CatalogStub(
        IEnumerable<EvidenceDatasetManifest> manifests) : IEvidenceCatalog
    {
        private readonly IReadOnlyDictionary<string, EvidenceDatasetManifest> byId =
            manifests.ToDictionary(manifest => manifest.DatasetId, StringComparer.Ordinal);

        public Task<EvidenceDatasetManifest?> GetDatasetAsync(
            string datasetId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(byId.GetValueOrDefault(datasetId));

        public Task<EvidenceCatalogCommitResult> RegisterCollectionPlanAsync(
            EvidenceCollectionPlan plan,
            CancellationToken cancellationToken = default) => Unused<EvidenceCatalogCommitResult>();
        public Task<EvidenceCollectionPlan?> GetCollectionPlanAsync(
            string jobId,
            CancellationToken cancellationToken = default) => Unused<EvidenceCollectionPlan?>();
        public Task SaveCollectionCheckpointAsync(
            EvidenceCollectionCheckpoint checkpoint,
            CancellationToken cancellationToken = default) => Unused();
        public Task<EvidenceCollectionCheckpoint?> GetCollectionCheckpointAsync(
            string jobId,
            CancellationToken cancellationToken = default) => Unused<EvidenceCollectionCheckpoint?>();
        public Task SaveRequestCursorCheckpointAsync(
            EvidenceRequestCursorCheckpoint checkpoint,
            CancellationToken cancellationToken = default) => Unused();
        public Task<EvidenceRequestCursorCheckpoint?> GetRequestCursorCheckpointAsync(
            string jobId,
            string requestId,
            CancellationToken cancellationToken = default) => Unused<EvidenceRequestCursorCheckpoint?>();
        public Task<EvidenceCatalogCommitResult> RegisterSourceObservationAsync(
            EvidenceSourceObservation observation,
            CancellationToken cancellationToken = default) => Unused<EvidenceCatalogCommitResult>();
        public Task<EvidenceSourceObservation?> GetSourceObservationAsync(
            string observationId,
            CancellationToken cancellationToken = default) => Unused<EvidenceSourceObservation?>();
        public Task<IReadOnlyList<EvidenceSourceObservation>> FindSourceObservationsAsync(
            string jobId,
            string requestId,
            CancellationToken cancellationToken = default) =>
            Unused<IReadOnlyList<EvidenceSourceObservation>>();
        public Task<EvidenceCatalogCommitResult> CommitDatasetAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) => Unused<EvidenceCatalogCommitResult>();
        public Task<IReadOnlyList<EvidenceDatasetManifest>> FindDatasetsAsync(
            EvidenceDatasetQuery query,
            CancellationToken cancellationToken = default) => Unused<IReadOnlyList<EvidenceDatasetManifest>>();
        public Task<EvidenceCatalogCommitResult> RegisterResearchRunAsync(
            EvidenceResearchRunManifest manifest,
            CancellationToken cancellationToken = default) => Unused<EvidenceCatalogCommitResult>();
        public Task<EvidenceResearchRunManifest?> GetResearchRunAsync(
            string researchRunId,
            CancellationToken cancellationToken = default) => Unused<EvidenceResearchRunManifest?>();

        public Task<EvidenceHoldoutConsumption?> GetHoldoutConsumptionAsync(
            string holdoutId,
            CancellationToken cancellationToken = default) =>
            Unused<EvidenceHoldoutConsumption?>();

        public Task<EvidenceCatalogCommitResult> ReserveHoldoutAsync(
            EvidenceHoldoutConsumption consumption,
            CancellationToken cancellationToken = default) =>
            Unused<EvidenceCatalogCommitResult>();
        public Task<EvidenceCatalogCommitResult> RegisterQuarantineAsync(
            EvidenceQuarantineRecord quarantine,
            CancellationToken cancellationToken = default) => Unused<EvidenceCatalogCommitResult>();
        public Task<EvidenceQuarantineEntry?> GetQuarantineAsync(
            string quarantineId,
            CancellationToken cancellationToken = default) => Unused<EvidenceQuarantineEntry?>();
        public Task<IReadOnlyList<EvidenceQuarantineEntry>> FindQuarantinesAsync(
            EvidenceQuarantineStatus status,
            CancellationToken cancellationToken = default) => Unused<IReadOnlyList<EvidenceQuarantineEntry>>();
        public Task ResolveQuarantineAsync(
            EvidenceQuarantineResolution resolution,
            CancellationToken cancellationToken = default) => Unused();
        public Task<IReadOnlyList<EvidenceArtifactReference>> ResolveReferenceClosureAsync(
            EvidencePinSubject subject,
            CancellationToken cancellationToken = default) => Unused<IReadOnlyList<EvidenceArtifactReference>>();
        public Task<EvidenceRetentionPin> PinAsync(
            EvidencePinSubject subject,
            string reason,
            CancellationToken cancellationToken = default) => Unused<EvidenceRetentionPin>();

        private static Task Unused() =>
            Task.FromException(new NotSupportedException("Unexpected catalog call."));

        private static Task<T> Unused<T>() =>
            Task.FromException<T>(new NotSupportedException("Unexpected catalog call."));
    }

    private sealed record CatalystTestFixture(
        EvidenceCatalogResearchRunner Runner,
        CatalogCatalystStudyRequest Request);
}
