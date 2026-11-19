using System.Runtime.CompilerServices;
using Moq;
using TradingFlow.Data.Evidence;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Regime;

namespace TradingFlow.Tests;

/// <summary>
/// Option A of the 18 Nov 2026 regime design: every entry needs SPY on; a Nasdaq-listed stock also needs QQQ on; a stock
/// whose listing is unknown is refused. Backtest calendar, live/paper service and the frozen listing rule agree.
/// </summary>
public sealed class NasdaqHomeMarketRegimeTests
{
    private static readonly DateTimeOffset Decision = new(2026, 6, 20, 14, 30, 0, TimeSpan.Zero);
    private static readonly RegimeRules Rule = new("SPY", RegimeRules.PriceAboveSma, 3, 0, "QQQ");

    // SPY rises (on); QQQ falls (off). Thirty daily closes up to 19 June.
    private static IReadOnlyList<OhlcvBar> Daily(string symbol, bool rising) => Enumerable.Range(0, 30)
        .Select(i => new DateTimeOffset(2026, 5, 21, 0, 0, 0, TimeSpan.Zero).AddDays(i))
        .Select((day, i) => { var close = rising ? 100m + i : 200m - i; return new OhlcvBar(symbol, day, "1d", close, close, close, close, 1_000m); })
        .ToArray();

    [Fact]
    public void BacktestCalendar_RefusesNasdaqStocksWhenQqqIsOff_AndUnknownListings()
    {
        var spyOn = RegimeCalendarBuilder.Build(Daily("SPY", rising: true), 3);
        var calendar = spyOn.WithNasdaqHomeMarket(RegimeCalendarBuilder.Build(Daily("QQQ", rising: false), 3),
            new Dictionary<string, string> { ["MSFT"] = "NASDAQ", ["JPM"] = "NYSE" });

        Assert.Null(calendar.RejectionFor(Decision, "JPM"));
        Assert.Equal(RegimeCalendar.NasdaqMarketOffReason, calendar.RejectionFor(Decision, "MSFT"));
        Assert.Equal(RegimeCalendar.ListingExchangeUnknownReason, calendar.RejectionFor(Decision, "NEWCO"));
        // Without the home-market rule a stock needs only SPY; SPY off refuses every stock with the market reason.
        Assert.Null(spyOn.RejectionFor(Decision, "MSFT"));
        var spyOff = RegimeCalendarBuilder.Build(Daily("SPY", rising: false), 3).WithNasdaqHomeMarket(
            RegimeCalendarBuilder.Build(Daily("QQQ", rising: true), 3), new Dictionary<string, string> { ["JPM"] = "NYSE" });
        Assert.Equal(RegimeCalendar.MarketOffReason, spyOff.RejectionFor(Decision, "JPM"));
        Assert.Null(RegimeCalendar.Inactive.RejectionFor(Decision, "ANY"));
    }

    [Fact]
    public async Task LivePaperService_UsesTheBrokerListingWithTheSameReasons()
    {
        var provider = new DailyProvider(new Dictionary<string, IReadOnlyList<OhlcvBar>>
        {
            ["SPY"] = Daily("SPY", rising: true), ["QQQ"] = Daily("QQQ", rising: false)
        });
        var assets = new Mock<IAssetTradingEligibilityProvider>();
        assets.Setup(x => x.GetEligibilityAsync("MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssetTradingEligibility("MSFT", true, true, false, Decision, Exchange: "NASDAQ"));
        assets.Setup(x => x.GetEligibilityAsync("JPM", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssetTradingEligibility("JPM", true, true, false, Decision, Exchange: "NYSE"));
        var gate = new RegimeGateService();

        Assert.Equal(RegimeCalendar.NasdaqMarketOffReason, (await gate.EntryDecisionAsync(Rule, provider, ["1d"], Decision, "MSFT", assets.Object, default)).Rejection);
        Assert.Null((await gate.EntryDecisionAsync(Rule, provider, ["1d"], Decision, "JPM", assets.Object, default)).Rejection);
        Assert.Equal(RegimeCalendar.ListingExchangeUnknownReason, (await gate.EntryDecisionAsync(Rule, provider, ["1d"], Decision, "NEWCO", assets.Object, default)).Rejection);
        Assert.Equal(RegimeCalendar.ListingExchangeUnknownReason, (await gate.EntryDecisionAsync(Rule, provider, ["1d"], Decision, "JPM", null, default)).Rejection);
        // Without the home-market rule no asset lookup happens.
        Assert.Null((await gate.EntryDecisionAsync(Rule with { NasdaqListingBenchmark = null }, provider, ["1d"], Decision, "MSFT", null, default)).Rejection);
    }

    // A failed broker lookup refuses the entry with its own audited reason instead of failing the ticker's evaluation, and a
    // mixed-case ticker finds its listing.
    [Fact]
    public async Task FailedLookupsFailClosedWithAReason_AndTickerCaseDoesNotMatter()
    {
        var provider = new DailyProvider(new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["SPY"] = Daily("SPY", rising: true) });
        var assets = new Mock<IAssetTradingEligibilityProvider>();
        assets.Setup(x => x.GetEligibilityAsync("MSFT", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Alpaca asset eligibility failed with HTTP 429."));
        Assert.Equal(RegimeCalendar.ListingExchangeLookupFailedReason,
            (await new RegimeGateService().EntryDecisionAsync(Rule, provider, ["1d"], Decision, "MSFT", assets.Object, default)).Rejection);

        var calendar = RegimeCalendarBuilder.Build(Daily("SPY", rising: true), 3).WithNasdaqHomeMarket(
            RegimeCalendarBuilder.Build(Daily("QQQ", rising: true), 3), new Dictionary<string, string> { ["BRK.B"] = "NYSE" });
        Assert.Null(calendar.RejectionFor(Decision, " brk.b "));
    }

    // Unset new fields leave serialized strategy rules exactly as before, and a plan's screen reference notices a different
    // security master.
    [Fact]
    public void UnsetFieldsKeepSerializedRules_AndScreenReferencesCompareTheSecurityMaster()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new RegimeRules("SPY", RegimeRules.PriceAboveSma, 50));
        Assert.DoesNotContain("SmaRisingLookbackBars", json);
        Assert.DoesNotContain("NasdaqListingBenchmark", json);
        Assert.DoesNotContain("RequiredCloses", json);
        Assert.DoesNotContain("EvidenceRule", json);
        Assert.Equal("SPY:50", new RegimeRules("SPY", RegimeRules.PriceAboveSma, 50).EvidenceRule);
        Assert.Equal("SPY:50:rising20:nasdaqQQQ", new RegimeRules("SPY", RegimeRules.PriceAboveSma, 50, 20, "QQQ").EvidenceRule);

        var reference = new TradingFlow.Contracts.V1.ResearchScreenReference("screen", "m", "r", "daily", "sessions", [], [], "actions");
        Assert.True(reference.SameInputs(reference with { }));
        Assert.False(reference.SameInputs(reference with { SecurityMasterDatasetId = "security-master" }));
    }

    [Fact]
    public async Task FrozenListings_KeepUsStocksAndDropAmbiguousSymbols_AndRequireAConfiguredSnapshot()
    {
        var listings = EvidenceCatalogMarketDataProvider.ListingExchanges(
        [
            Row("sec-1", "msft", "NASDAQ"), Row("sec-2", "JPM", "NYSE"),
            Row("sec-3", "DUAL", "NYSE"), Row("sec-4", "DUAL", "NASDAQ"),
            Row("sec-5", "BTCUSD", "CRYPTO", "crypto")
        ]);
        Assert.Equal(new Dictionary<string, string> { ["MSFT"] = "NASDAQ", ["JPM"] = "NYSE" }, listings);

        var config = new EvidenceCatalogProviderConfig("catalog.db", "objects", new string('a', 64), "daily", "sessions", [], []);
        var provider = new EvidenceCatalogMarketDataProvider(config, (_, _) => Task.FromResult<EvidenceDatasetManifest?>(null),
            Mock.Of<TradingFlow.Engine.Research.IEvidencePartitionDataReader>(), Mock.Of<TradingFlow.Engine.Storage.IRawArchiveWriter>());
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.LoadListingExchangesAsync(default));
        Assert.Contains("security_master_dataset is not set", refusal.Message);
    }

    // A security-master snapshot row with only the fields the listing rule reads set meaningfully.
    private static SecurityMasterSnapshotEvidenceRow Row(string securityId, string symbol, string exchange, string assetClass = "us_equity") => new(
        1, "run", new string('c', 64), "test", "alpaca-trading", "alpaca", securityId, null, symbol, symbol, exchange, assetClass, "USD", "active",
        true, true, true, true, true, null, null, null, null, [], Decision, false,
        [new EvidenceRowSourceAddress("observation", new string('d', 64))]);

    // Daily bars by symbol for the regime service, filtered to the requested window.
    private sealed class DailyProvider(IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> bySymbol) : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes,
            DateTimeOffset start, DateTimeOffset end, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (timeframes.First() == "1d")
                foreach (var bar in tickers.SelectMany(ticker => bySymbol.GetValueOrDefault(ticker) ?? []))
                    if (bar.Timestamp >= start && bar.Timestamp <= end) yield return bar;
            await Task.CompletedTask;
        }
    }
}
