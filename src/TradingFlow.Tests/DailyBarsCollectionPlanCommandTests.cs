using TradingFlow.Cli;
using TradingFlow.Domain.Research;
using static TradingFlow.Cli.DailyBarsCollectionPlanCommand;

namespace TradingFlow.Tests;

public sealed class DailyBarsCollectionPlanCommandTests
{
    private static readonly DateOnly Start = new(2016, 1, 1);
    private static readonly DateOnly End = new(2026, 10, 31);

    [Fact]
    public void Request_KeepsListedEquitiesInRepeatableSymbolBatchesWithRawSipDailyBars()
    {
        var securities = new[]
        {
            new ListedSecurity("id-c", "CCC", "NYSE", "us_equity"),
            new ListedSecurity("id-a", "AAA", "NASDAQ", "us_equity"),
            new ListedSecurity("id-b", "BBB", "ARCA", "us_equity"),
            new ListedSecurity("id-otc", "OTCX", "OTC", "us_equity")
        };

        var plan = CreateRequest(securities, Start, End, 2, "security-master", new string('a', 64));

        Assert.Equal(EvidenceDatasetKind.MarketBarsAsTraded, plan.Request.DatasetKind);
        Assert.Equal(new[] { new[] { "AAA", "BBB" }, new[] { "CCC" } }, plan.Request.Requests.Select(request => request.Symbols.ToArray()));
        Assert.All(plan.Request.Requests, request =>
        {
            Assert.Equal("sip", request.DataFeed);
            Assert.Equal("raw", request.Adjustment);
            Assert.Equal("1Day", request.Parameters["timeframe"]);
        });
        Assert.Equal(1, plan.ExcludedOtherVenues);
        Assert.Equal(plan.Request.EffectiveConfigHash, CreateRequest(securities.Reverse().ToArray(), Start, End, 2, "security-master", new string('a', 64)).Request.EffectiveConfigHash);
    }

    [Fact]
    public void Request_LeavesOutASymbolHeldByTwoSecuritiesBecauseItsBarsCannotBeAttributed()
    {
        var securities = new[]
        {
            new ListedSecurity("old-company", "REUS", "NYSE", "us_equity"),
            new ListedSecurity("new-company", "REUS", "NASDAQ", "us_equity"),
            new ListedSecurity("id-a", "AAA", "NASDAQ", "us_equity")
        };

        var plan = CreateRequest(securities, Start, End, 100, "security-master", new string('a', 64));

        Assert.Equal(new[] { "REUS" }, plan.ExcludedReusedSymbols);
        Assert.Equal(new[] { "AAA" }, plan.Request.Requests.Single().Symbols);
    }
}

public sealed class DailyBarsCollectionPlanTickerTests
{
    [Fact]
    public void Request_LeavesOutIdentifierStyleAndRenamedSymbolsThatTheBarsEndpointRejects()
    {
        var securities = new[]
        {
            new ListedSecurity("id-cvr", "003CVR016", "NASDAQ", "us_equity"),
            new ListedSecurity("id-gone", "BRAC_DELISTED", "NASDAQ", "us_equity"),
            new ListedSecurity("id-pref", "ACP.PRA", "NYSE", "us_equity"),
            new ListedSecurity("id-a", "AAA", "NASDAQ", "us_equity")
        };

        var plan = CreateRequest(securities, new DateOnly(2016, 1, 1), new DateOnly(2026, 10, 31), 100, "security-master", new string('a', 64));

        Assert.Equal(new[] { "003CVR016", "BRAC_DELISTED" }, plan.ExcludedNonTickerSymbols);
        Assert.Equal(new[] { "AAA", "ACP.PRA" }, plan.Request.Requests.Single().Symbols);
        Assert.Equal("2", plan.Request.Attributes["excluded_non_ticker_symbol_count"]);
    }
}
