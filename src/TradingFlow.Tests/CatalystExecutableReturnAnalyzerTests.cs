using TradingFlow.Domain.Research;
using TradingFlow.Research.Catalysts;

namespace TradingFlow.Tests;

public sealed class CatalystExecutableReturnAnalyzerTests
{
    private static readonly DateTimeOffset EntryAt =
        new(2026, 7, 6, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ExitAt = EntryAt.AddHours(1);
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(2);

    [Fact]
    public void Analyze_LongUsesAskAtEntryAndBidAtExit()
    {
        var result = Analyze(
            ExecutablePositionDirection.Long,
            [
                Quote(EntryAt.AddMilliseconds(200), 100.00m, 100.10m),
                Quote(ExitAt.AddMilliseconds(400), 102.00m, 102.10m)
            ]);

        Assert.Equal(CatalystExecutableReturnStatus.NetExecutableEstimate, result.Status);
        Assert.Equal(100.10m, result.EntryPrice);
        Assert.Equal(102.00m, result.ExitPrice);
        Assert.Equal(1.898102m, result.GrossReturnPercent);
        Assert.Equal(result.GrossReturnPercent, result.NetReturnPercent);
        Assert.Equal(CatalystExecutionFillStatus.Full, result.FillStatus);
        Assert.Equal(100, result.EntryFilledQuantity);
        Assert.Equal(100, result.ExitFilledQuantity);
        Assert.True(result.PromotionEligible);
        Assert.Equal(
            CatalystExecutionEvidenceStatus.NotRequired,
            result.QueuePositionEvidenceStatus);
    }

    [Fact]
    public void Analyze_ShortUsesBidAtEntryAndAskAtExit()
    {
        var result = Analyze(
            ExecutablePositionDirection.Short,
            [
                Quote(EntryAt.AddMilliseconds(200), 100.00m, 100.10m),
                Quote(ExitAt.AddMilliseconds(400), 97.90m, 98.00m)
            ]);

        Assert.Equal(CatalystExecutableReturnStatus.NetExecutableEstimate, result.Status);
        Assert.Equal(100.00m, result.EntryPrice);
        Assert.Equal(98.00m, result.ExitPrice);
        Assert.Equal(2.000000m, result.GrossReturnPercent);
    }

    [Fact]
    public void Analyze_AppliesAdverseImpactAndFeesToNetReturn()
    {
        var result = Analyze(
            ExecutablePositionDirection.Long,
            [
                Quote(EntryAt.AddMilliseconds(200), 100.00m, 100.10m),
                Quote(ExitAt.AddMilliseconds(400), 102.00m, 102.10m)
            ],
            quantity: 100,
            costModel: new CatalystExecutionCostModel(
                perShareFee: 0.005m,
                minimumFeePerOrder: 1m,
                entryMarketImpactBasisPoints: 5m,
                exitMarketImpactBasisPoints: 5m));

        Assert.Equal(CatalystExecutableReturnStatus.NetExecutableEstimate, result.Status);
        Assert.Equal(2m, result.EstimatedFees);
        Assert.True(result.EffectiveEntryPrice > result.EntryPrice);
        Assert.True(result.EffectiveExitPrice < result.ExitPrice);
        Assert.True(result.EstimatedMarketImpactCost > 0m);
        Assert.True(result.NetReturnPercent < result.GrossReturnPercent);
    }

    [Fact]
    public void Analyze_RejectsIneligibleQuoteCondition()
    {
        var result = Analyze(
            ExecutablePositionDirection.Long,
            [
                Quote(
                    EntryAt.AddMilliseconds(200),
                    100m,
                    100.10m,
                    conditions: ["H"]),
                Quote(ExitAt.AddMilliseconds(400), 102m, 102.10m)
            ],
            policy: new SipQuoteEligibilityPolicy(["R"], ["Q", "P"], true));

        Assert.Equal(CatalystExecutableReturnStatus.Rejected, result.Status);
        Assert.Equal(CatalystExecutableReturnFailure.QuoteConditionIneligible, result.Failure);
        Assert.Equal("entry", result.FailureStage);
    }

    [Fact]
    public void Analyze_UsesEvidenceBackedPartialFillWithoutAssumingFullFill()
    {
        var result = Analyze(
            ExecutablePositionDirection.Long,
            [
                Quote(
                    EntryAt.AddMilliseconds(200),
                    100m,
                    100.10m,
                    bidSize: 500,
                    askSize: 80),
                Quote(ExitAt.AddMilliseconds(400), 102m, 102.10m)
            ],
            quantity: 100,
            calibration: Calibrated(maximumParticipation: 0.5m));

        Assert.Equal(CatalystExecutableReturnStatus.NetExecutableEstimate, result.Status);
        Assert.Equal(CatalystExecutionFillStatus.PartialEntry, result.FillStatus);
        Assert.Equal(40, result.EntryFilledQuantity);
        Assert.Equal(40, result.ExitFilledQuantity);
        Assert.Equal(0, result.OpenQuantityAfterExit);
        Assert.True(result.PromotionEligible);
    }

    [Fact]
    public void Analyze_RecordsNoFillWhenDisplayedLiquidityIsZero()
    {
        var result = Analyze(
            ExecutablePositionDirection.Long,
            [
                Quote(
                    EntryAt.AddMilliseconds(200),
                    100m,
                    100.10m,
                    askSize: 0)
            ]);

        Assert.Equal(CatalystExecutableReturnStatus.NoFill, result.Status);
        Assert.Equal(CatalystExecutableReturnFailure.NoDisplayedLiquidity, result.Failure);
        Assert.Equal(CatalystExecutionFillStatus.NoFill, result.FillStatus);
        Assert.Equal(0, result.EntryFilledQuantity);
        Assert.False(result.PromotionEligible);
        Assert.Contains(
            "no_evidence_backed_displayed_liquidity_available",
            result.PromotionBlockers);
    }

    [Fact]
    public void Analyze_MissingDisplayedSizeProducesDiagnosticAndFailsPromotionClosed()
    {
        var result = Analyze(
            ExecutablePositionDirection.Long,
            [
                Quote(
                    EntryAt.AddMilliseconds(200),
                    100m,
                    100.10m,
                    askSize: null)
            ]);

        Assert.Equal(CatalystExecutableReturnStatus.Rejected, result.Status);
        Assert.Equal(
            CatalystExecutableReturnFailure.QuoteDisplayedSizeUnavailable,
            result.Failure);
        Assert.Equal(
            CatalystExecutionFillStatus.EvidenceInsufficient,
            result.FillStatus);
        Assert.Equal(100.10m, result.EntryPrice);
        Assert.NotNull(result.EntryQuoteTimestampUtc);
        Assert.False(result.PromotionEligible);
        Assert.Contains(
            "executable_side_displayed_size_unavailable",
            result.PromotionBlockers);
    }

    [Fact]
    public void Analyze_RejectsSpreadBeyondCalibratedMaximum()
    {
        var result = Analyze(
            ExecutablePositionDirection.Long,
            [
                Quote(EntryAt.AddMilliseconds(200), 100m, 101m)
            ],
            calibration: Calibrated(maximumSpreadBasisPoints: 50m));

        Assert.Equal(CatalystExecutableReturnStatus.Rejected, result.Status);
        Assert.Equal(
            CatalystExecutableReturnFailure.SpreadExceedsMaximum,
            result.Failure);
        Assert.True(result.EntrySpreadBasisPoints > 50m);
        Assert.False(result.PromotionEligible);
        Assert.Contains(
            "nbbo_spread_exceeds_calibrated_limit",
            result.PromotionBlockers);
    }

    [Fact]
    public void Analyze_UsesDirectionAndStageSpecificDisplayedSize()
    {
        var result = Analyze(
            ExecutablePositionDirection.Short,
            [
                Quote(
                    EntryAt.AddMilliseconds(200),
                    100m,
                    100.10m,
                    bidSize: 100,
                    askSize: 1),
                Quote(
                    ExitAt.AddMilliseconds(400),
                    98m,
                    98.10m,
                    bidSize: 1,
                    askSize: 100)
            ],
            quantity: 100);

        Assert.Equal(CatalystExecutableReturnStatus.NetExecutableEstimate, result.Status);
    }

    [Fact]
    public void Analyze_RejectsIneligibleExecutableExchange()
    {
        var result = Analyze(
            ExecutablePositionDirection.Long,
            [
                Quote(
                    EntryAt.AddMilliseconds(200),
                    100m,
                    100.10m,
                    askExchange: "Z"),
                Quote(ExitAt.AddMilliseconds(400), 102m, 102.10m)
            ]);

        Assert.Equal(CatalystExecutableReturnStatus.Rejected, result.Status);
        Assert.Equal(CatalystExecutableReturnFailure.QuoteExchangeIneligible, result.Failure);
    }

    [Fact]
    public void Analyze_DoesNotSkipInvalidFirstEligibleQuote()
    {
        var result = Analyze(
            ExecutablePositionDirection.Long,
            [
                Quote(EntryAt.AddMilliseconds(100), 100m, 100.10m, feed: "iex"),
                Quote(EntryAt.AddMilliseconds(200), 100m, 100.10m),
                Quote(ExitAt.AddMilliseconds(100), 101m, 101.10m)
            ]);

        Assert.Equal(CatalystExecutableReturnStatus.Rejected, result.Status);
        Assert.Equal(CatalystExecutableReturnFailure.QuoteWrongFeed, result.Failure);
        Assert.Equal("entry", result.FailureStage);
        Assert.Equal(100.10m, result.EntryPrice);
        Assert.False(result.PromotionEligible);
    }

    [Fact]
    public void Analyze_InvalidExitCensorsOtherwiseValidatedEntry()
    {
        var result = Analyze(
            ExecutablePositionDirection.Long,
            [
                Quote(EntryAt.AddMilliseconds(100), 100m, 100.10m),
                Quote(ExitAt.AddMilliseconds(100), 101m, null)
            ]);

        Assert.Equal(CatalystExecutableReturnStatus.Censored, result.Status);
        Assert.Equal(CatalystExecutableReturnFailure.QuoteOneSided, result.Failure);
        Assert.Equal("exit", result.FailureStage);
        Assert.Equal(100.10m, result.EntryPrice);
        Assert.Null(result.NetReturnPercent);
    }

    [Fact]
    public void Analyze_PartialExitNeverInfersTheRemainingFill()
    {
        var result = Analyze(
            ExecutablePositionDirection.Long,
            [
                Quote(EntryAt.AddMilliseconds(100), 100m, 100.10m),
                Quote(
                    ExitAt.AddMilliseconds(100),
                    101m,
                    101.10m,
                    bidSize: 25)
            ],
            quantity: 100);

        Assert.Equal(CatalystExecutableReturnStatus.Censored, result.Status);
        Assert.Equal(
            CatalystExecutableReturnFailure.PartialExitNotFullyClosed,
            result.Failure);
        Assert.Equal(CatalystExecutionFillStatus.PartialExit, result.FillStatus);
        Assert.Equal(100, result.EntryFilledQuantity);
        Assert.Equal(25, result.ExitFilledQuantity);
        Assert.Equal(75, result.OpenQuantityAfterExit);
        Assert.False(result.PromotionEligible);
    }

    [Fact]
    public void Analyze_OrderLatencyUsesFirstPointInTimeQuoteObservedAfterArrival()
    {
        var result = Analyze(
            ExecutablePositionDirection.Long,
            [
                Quote(EntryAt.AddMilliseconds(100), 99.90m, 100.00m),
                Quote(EntryAt.AddMilliseconds(600), 100.10m, 100.20m),
                Quote(ExitAt.AddMilliseconds(600), 102.00m, 102.10m)
            ],
            calibration: Calibrated(orderLatency: TimeSpan.FromMilliseconds(500)));

        Assert.Equal(100.20m, result.EntryPrice);
        Assert.Equal(102.00m, result.ExitPrice);
        Assert.Equal(TimeSpan.FromMilliseconds(500), result.OrderLatency);
    }

    [Fact]
    public void Analyze_AuctionRequestFailsClosedWithoutAuctionEvidence()
    {
        var result = Analyze(
            ExecutablePositionDirection.Long,
            [
                Quote(EntryAt.AddMilliseconds(100), 100m, 100.10m)
            ],
            calibration: Calibrated(
                mechanism: CatalystExecutionMechanism.OpeningAuction));

        Assert.Equal(CatalystExecutableReturnStatus.Rejected, result.Status);
        Assert.Equal(
            CatalystExecutableReturnFailure.AuctionEvidenceUnavailable,
            result.Failure);
        Assert.Equal(
            CatalystExecutionEvidenceStatus.Missing,
            result.AuctionEvidenceStatus);
        Assert.Equal(
            CatalystExecutionEvidenceStatus.ProhibitedInference,
            result.QueuePositionEvidenceStatus);
        Assert.False(result.PromotionEligible);
    }

    [Fact]
    public void Analyze_CostMultiplierScalesSpreadImpactAndFeesDeterministically()
    {
        var quotes = new[]
        {
            Quote(EntryAt.AddMilliseconds(200), 100.00m, 100.10m),
            Quote(ExitAt.AddMilliseconds(400), 102.00m, 102.10m)
        };
        var costs = new CatalystExecutionCostModel(0.005m, 1m, 5m, 5m);

        var oneX = Analyze(
            ExecutablePositionDirection.Long,
            quotes,
            costModel: costs,
            calibration: Calibrated(costMultiplier: 1m));
        var threeX = Analyze(
            ExecutablePositionDirection.Long,
            quotes,
            costModel: costs,
            calibration: Calibrated(costMultiplier: 3m));

        Assert.Equal(oneX.EstimatedFees * 3m, threeX.EstimatedFees);
        Assert.Equal(oneX.EstimatedSpreadCost * 3m, threeX.EstimatedSpreadCost);
        Assert.Equal(
            oneX.EstimatedMarketImpactCost * 3m,
            threeX.EstimatedMarketImpactCost);
        Assert.True(threeX.NetReturnPercent < oneX.NetReturnPercent);
        Assert.Equal(3m, threeX.CostMultiplier);
    }

    [Theory]
    [InlineData("stale", CatalystExecutableReturnFailure.QuoteStale)]
    [InlineData("crossed", CatalystExecutableReturnFailure.QuoteCrossed)]
    [InlineData("one-sided", CatalystExecutableReturnFailure.QuoteOneSided)]
    [InlineData("wrong-feed", CatalystExecutableReturnFailure.QuoteWrongFeed)]
    [InlineData("wrong-session", CatalystExecutableReturnFailure.QuoteWrongSession)]
    [InlineData("unavailable", CatalystExecutableReturnFailure.QuoteUnavailable)]
    public void Analyze_RejectsNonExecutableEntry(
        string scenario,
        CatalystExecutableReturnFailure expected)
    {
        var quotes = scenario switch
        {
            "stale" => new[] { Quote(EntryAt.AddSeconds(3), 100m, 100.10m) },
            "crossed" => new[] { Quote(EntryAt.AddMilliseconds(100), 100.20m, 100.10m) },
            "one-sided" => new[] { Quote(EntryAt.AddMilliseconds(100), null, 100.10m) },
            "wrong-feed" => new[] { Quote(EntryAt.AddMilliseconds(100), 100m, 100.10m, feed: "iex") },
            "wrong-session" => new[] { Quote(new DateTimeOffset(2026, 7, 6, 20, 0, 0, TimeSpan.Zero), 100m, 100.10m) },
            "unavailable" => Array.Empty<SipQuoteEvidenceRow>(),
            _ => throw new InvalidOperationException()
        };

        var result = Analyze(ExecutablePositionDirection.Long, quotes);

        Assert.Equal(CatalystExecutableReturnStatus.Rejected, result.Status);
        Assert.Equal(expected, result.Failure);
        Assert.Equal("entry", result.FailureStage);
        Assert.Null(result.GrossReturnPercent);
        Assert.False(result.PromotionEligible);
        Assert.NotEmpty(result.PromotionBlockers);
    }

    private static CatalystExecutableReturnResult Analyze(
        ExecutablePositionDirection direction,
        IReadOnlyList<SipQuoteEvidenceRow> quotes,
        long quantity = 100,
        SipQuoteEligibilityPolicy? policy = null,
        CatalystExecutionCostModel? costModel = null,
        CatalystExecutionCalibrationPolicy? calibration = null) =>
        new CatalystExecutableReturnAnalyzer().Analyze(
            new CatalystExecutableReturnRequest(
                "security-msft",
                "MSFT",
                direction,
                EntryAt,
                ExitAt,
                UsEquityTradingSession.Regular,
                new DateTimeOffset(2026, 7, 6, 13, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 6, 20, 0, 0, TimeSpan.Zero),
                MaximumDelay,
                quantity,
                policy ?? new SipQuoteEligibilityPolicy(
                    eligibleConditions: [],
                    eligibleExchanges: ["Q", "P"],
                    allowUnconditionedQuotes: true),
                costModel ?? new CatalystExecutionCostModel(0m, 0m, 0m, 0m),
                calibration ?? Calibrated()),
            quotes);

    private static CatalystExecutionCalibrationPolicy Calibrated(
        TimeSpan? orderLatency = null,
        decimal maximumParticipation = 1m,
        decimal maximumSpreadBasisPoints = 100m,
        decimal costMultiplier = 1m,
        CatalystExecutionMechanism mechanism =
            CatalystExecutionMechanism.ContinuousMarketableNbbo) =>
        new(
            orderLatency ?? TimeSpan.Zero,
            maximumParticipation,
            maximumSpreadBasisPoints,
            costMultiplier,
            mechanism,
            CatalystExecutionEvidenceStatus.EvidenceBacked,
            CatalystExecutionEvidenceStatus.EvidenceBacked,
            CatalystExecutionEvidenceStatus.EvidenceBacked);

    private static SipQuoteEvidenceRow Quote(
        DateTimeOffset timestamp,
        decimal? bid,
        decimal? ask,
        string feed = "sip",
        long? bidSize = 1_000,
        long? askSize = 1_000,
        string bidExchange = "Q",
        string askExchange = "P",
        IReadOnlyList<string>? conditions = null) =>
        new(
            1,
            "run-1",
            new string('b', 64),
            "code-1",
            "security-msft",
            "MSFT",
            timestamp,
            bid is null ? null : EvidenceFixedDecimal.ToPriceUnits(bid.Value),
            ask is null ? null : EvidenceFixedDecimal.ToPriceUnits(ask.Value),
            bid is null ? null : bidSize,
            ask is null ? null : askSize,
            bidExchange,
            askExchange,
            feed,
            "USD",
            conditions ?? [],
            timestamp,
            timestamp.AddMilliseconds(10),
            [new EvidenceRowSourceAddress("observation-1", new string('a', 64))]);
}
