using Moq;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Strategies;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Desk;

namespace TradingFlow.Tests;

public sealed class CurrentStrategyEvidenceTests
{
    /// <summary>A fresh-news veto alone, without any positive-news entry flag, makes a strategy require news.</summary>
    [Fact]
    public void FreshNewsVeto_IsRecognizedAsARequiredNewsCapability()
    {
        var root = TestRepository.FindRoot();
        var source = new SimpleYamlReader().ReadStrategy(Path.Combine(root, "configs", "backtest", "strategies",
            "swing-connors-rsi2-sma5-risk-managed.bt-v7.yaml"));
        Assert.False(StrategyCapabilityInspector.UsesNews(source));
        var strategy = source with { EntryRules = source.EntryRules with { VetoFreshNewsHours = 48m } };
        Assert.True(StrategyCapabilityInspector.UsesNews(strategy));
    }

    /// <summary>A complete fixed-clock query covers the full veto horizon; actual shared-kernel rejection changes when real news is present.</summary>
    [Fact]
    public async Task CompleteVetoWindow_CollectsFullHorizonAndFeedsTheActualKernel()
    {
        var original = StrategyDecisionKernelTests.CreateParityScenario("long_regular_catalyst");
        var strategy = original.Strategy with { EntryRules = original.Strategy.EntryRules with {
            RequirePositiveNews = false, VetoFreshNewsHours = 48m, MaxNewsAgeHours = 1m } };
        var request = StrategyDecisionKernelTests.RebindStrategy(original, strategy);
        // This fixture's classifier finishes after receipt but before the completed setup candle.
        // The inherited parity fixture finishes at 14:03, which cannot change a bar completed at 14:00.
        var knownBeforeClose = request.BarsByTimeframe["1d"][^1].Timestamp.AddDays(1).AddMinutes(-1);
        var article = request.Catalysts.Single();
        Assert.True(article.FirstReceivedAtUtc <= knownBeforeClose);
        request = request with { Catalysts = [article with { DecisionKnownAtUtc = knownBeforeClose,
            Event = article.Event with { DecisionAvailableAt = knownBeforeClose } }] };
        DateTimeOffset requestedStart = default;
        var events = request.Catalysts.Select(item => item.Event).ToArray();
        var provider = new Mock<ICatalystEvidenceProvider>();
        provider.Setup(item => item.GetCatalystEvidenceAsync("RGTI", It.IsAny<DateTimeOffset>(), request.AsOfUtc, It.IsAny<CancellationToken>()))
            .Returns((string _, DateTimeOffset start, DateTimeOffset end, CancellationToken _) => {
                requestedStart = start;
                return Task.FromResult(new CatalystQueryResult(events, true, "Complete fixed-clock fixture", 1, start, end));
            });
        var collected = await StockStrategyEvidenceLoader.LoadNewsAsync(strategy, ["RGTI"], provider.Object, request.AsOfUtc, default);
        var coverage = collected["RGTI"];
        Assert.Equal(request.AsOfUtc.AddHours(-48), requestedStart);
        Assert.Null(StockStrategyEvidenceLoader.MissingNewsCoverage(strategy, coverage, events, request.AsOfUtc));
        var rejected = new StrategyDecisionKernel().Evaluate(request);
        Assert.NotNull(rejected.Signal?.Catalyst);
        Assert.Contains("fresh_news_veto", rejected.NoEntryReason);
        Assert.Contains(rejected.Rules, rule => !rule.Passed && rule.Code.Contains("fresh_news_veto", StringComparison.Ordinal));
        Assert.NotEqual(StrategyCandidateState.Triggered, rejected.State);
        var emptyCoverage = coverage with { Events = [] };
        Assert.Null(StockStrategyEvidenceLoader.MissingNewsCoverage(strategy, emptyCoverage, [], request.AsOfUtc));
        Assert.Equal(StrategyCandidateState.Triggered, new StrategyDecisionKernel().Evaluate(request with { Catalysts = [] }).State);
    }

    /// <summary>An advancing source-refresh clock must expose the exact unqueried interval, not pass an empty-news veto.</summary>
    [Fact]
    public void EarlierCompleteNewsWindow_DoesNotProveCoverageAtTheFinalDecisionTime()
    {
        var request = StrategyDecisionKernelTests.CreateParityScenario("long_regular_catalyst");
        var strategy = request.Strategy with { EntryRules = request.Strategy.EntryRules with { VetoFreshNewsHours = 48m } };
        var events = request.Catalysts.Select(item => item.Event).ToArray();
        var query = new CatalystQueryResult(events, true, "Complete earlier query", 1, request.AsOfUtc.AddHours(-72), request.AsOfUtc);
        var cutoff = request.AsOfUtc.AddSeconds(2);
        var missing = StockStrategyEvidenceLoader.MissingNewsCoverage(strategy, query, events, cutoff);
        Assert.Contains($"{request.AsOfUtc:O} through {cutoff:O}", missing);
        Assert.Contains("Latest known returned article", missing);
        Assert.Contains(events[0].Timestamp.ToString("O"), missing);
        Assert.DoesNotContain("unavailable", missing, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Both regime and relative-strength readers reject ample old benchmark history and require each latest completed session.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentBenchmark_RequiresFreshAuthoritativeSessionsForRegimeAndRelativeStrength(bool relativeStrength)
    {
        var request = StrategyDecisionKernelTests.CreateRequest();
        var strategy = request.Strategy with {
            Regime = relativeStrength ? null : new("SPY", RegimeRules.PriceAboveSma, 50),
            EntryRules = request.Strategy.EntryRules with { TechnicalFactor = relativeStrength
                ? new(TechnicalFactorRules.RelativeRoc, 20, MinValue: 0) : null } };
        var calendar = Calendar(request.AsOfUtc);
        var count = relativeStrength ? 21 : 50;
        var fresh = Benchmark("SPY", count, calendar, request.AsOfUtc);
        var stale = fresh.Select(bar => bar with { Timestamp = bar.Timestamp.AddDays(-90) }).ToArray();
        var market = new TickerMarketState("RGTI", request.BarsByTimeframe.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<OhlcvBar>)pair.Value), request.SnapshotsByTimeframe.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<IndicatorSnapshot>)pair.Value));
        var stock = new StockStrategyEvidence(market, [], request.Candidate.Regime,
            relativeStrength ? stale : null, null, null, null, null, relativeStrength ? null : stale);
        var missing = StockStrategyEvidenceLoader.MissingAtDecision(strategy, stock, calendar, request.AsOfUtc);
        Assert.Contains("SPY", missing);
        Assert.Contains("stale", missing);
        var current = stock with { RegimeBars = relativeStrength ? null : fresh, ReferenceBars = relativeStrength ? fresh : null };
        Assert.Null(StockStrategyEvidenceLoader.MissingAtDecision(strategy, current, calendar, request.AsOfUtc));
        Assert.Contains("missing", StockStrategyEvidenceLoader.MissingBenchmark("SPY", fresh.Where((_, index) => index != 3).ToArray(),
            count, calendar, request.AsOfUtc, "America/New_York"));
        Assert.Contains("stale", StockStrategyEvidenceLoader.MissingBenchmark("SPY", fresh, count,
            calendar, request.AsOfUtc.AddDays(1), "America/New_York"));
    }

    /// <summary>Builds a complete controlled exchange calendar for fixture-only evidence.</summary>
    private static IReadOnlyDictionary<DateOnly, MarketSessionSchedule> Calendar(DateTimeOffset now)
    {
        var end = DateOnly.FromDateTime(now.UtcDateTime).AddDays(2);
        return Enumerable.Range(0, 250).Select(index => end.AddDays(index - 249)).ToDictionary(date => date,
            date => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? MarketSessionSchedule.Closed(date) : MarketSessionSchedule.RegularDay(date));
    }

    /// <summary>Provides real dated fixture rows for every expected session; tests may explicitly remove or age a row.</summary>
    private static IReadOnlyList<OhlcvBar> Benchmark(string symbol, int count,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> calendar, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        return TechnicalFactorCalculator.ExpectedCompletedSessions(calendar, now, count).Select((date, index) => {
            var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            var stamp = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
            return new OhlcvBar(symbol, stamp, "1d", 100 + index, 101 + index, 99 + index, 100 + index,
                1000000, "sip", "all", stamp.AddDays(1), now);
        }).ToArray();
    }
}
