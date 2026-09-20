using System.Security.Cryptography;
using System.Text;
using Moq;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Research;
using TradingFlow.Research.Momentum;

namespace TradingFlow.Tests;

// Retains the swing evidence boundary tests from the former mixed research fixture.
public sealed class SwingEvidenceCatalogResearchRunnerTests
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
            CreateCatalog([adjustedManifest, asTradedManifest, universeManifest]),
            CreateReader(adjustedRows, asTradedRows, membershipRows));

        var result = await runner.RunMomentumAsync(new CatalogMomentumStudyRequest(
            adjustedManifest.DatasetId,
            asTradedManifest.DatasetId,
            universeManifest.DatasetId,
            Definition()));

        Assert.True(result.Report.PointInTimeUniverseEvidence);
        Assert.True(result.Report.AdjustedPricesConfirmed);
        Assert.NotEmpty(result.Report.RankObservations);
        Assert.NotEmpty(result.Audit.Formations);
        Assert.NotEmpty(result.Audit.Robustness);
        Assert.DoesNotContain(
            result.Report.RankObservations,
            observation => observation.Ticker == "EXCLUDED");
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
            CreateCatalog([adjustedManifest, asTradedManifest, universeManifest]),
            CreateReader(
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
            ["SPY"] = Bars("SPY", 180, index => 100m + index),
            ["EXCLUDED"] = Bars("EXCLUDED", 180, index => 20m + (index * 4m)),
            ["A"] = Bars("A", 180, index => 30m + index),
            ["B"] = Bars("B", 180, index => 40m + index),
            ["C"] = Bars("C", 180, index => 50m + index),
            ["D"] = Bars("D", 180, index => 60m + index),
            ["E"] = Bars("E", 180, index => 70m + index)
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
                    new DateTimeOffset(2025, 11, 3, 5, 0, 0, TimeSpan.Zero)
                        .AddDays(index + (index / 5 * 2)),
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
                        "test",
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
                            EvidenceDatasetKind.UniverseMembership
                            ? new DateOnly(2026, 7, 25)
                            : null,
                        kind is EvidenceDatasetKind.MarketBarsAsTraded or
                            EvidenceDatasetKind.MarketBarsResearchAdjusted or
                            EvidenceDatasetKind.UniverseMembership
                            ? ["security-a"]
                            : [],
                        kind == EvidenceDatasetKind.UniverseMembership
                            ? ["issuer-a"]
                            : [],
                        ["A"],
                        "1d",
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

    private static IEvidenceCatalog CreateCatalog(
        IEnumerable<EvidenceDatasetManifest> manifests)
    {
        var byId = manifests.ToDictionary(manifest => manifest.DatasetId, StringComparer.Ordinal);
        var catalog = new Mock<IEvidenceCatalog>(MockBehavior.Strict);
        catalog.Setup(value => value.GetDatasetAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => byId.GetValueOrDefault(id));
        return catalog.Object;
    }

    private static IEvidencePartitionDataReader CreateReader(
        IReadOnlyList<MarketBarEvidenceRow> adjustedBars,
        IReadOnlyList<MarketBarEvidenceRow> asTradedBars,
        IReadOnlyList<UniverseMembershipEvidenceRow> memberships)
    {
        var reader = new Mock<IEvidencePartitionDataReader>(MockBehavior.Strict);
        reader.Setup(value => value.ReadMarketBarsAsync(
                It.IsAny<EvidenceDatasetManifest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((EvidenceDatasetManifest manifest, CancellationToken _) =>
                manifest.Kind == EvidenceDatasetKind.MarketBarsResearchAdjusted
                    ? adjustedBars
                    : asTradedBars);
        reader.Setup(value => value.ReadUniverseMembershipAsync(
                It.Is<EvidenceDatasetManifest>(manifest =>
                    manifest.Kind == EvidenceDatasetKind.UniverseMembership),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(memberships);
        return reader.Object;
    }
}
