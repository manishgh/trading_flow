using TradingFlow.Domain.Strategies;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Finviz;
using TradingFlow.Web.Models;
using TradingFlow.Web.Pages;
using TradingFlow.Web.Pages.Shared;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Tests;

/// <summary>
/// The desk monitors a universe - every wishlist, one wishlist, or a screener's
/// hits - with held positions, preparation state and live streams keyed to the
/// exact symbols on screen. These cover the pure rules behind each of those.
/// </summary>
public sealed class DeskUniverseTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ForAllWishlists_DeduplicatesSymbolsAndRecordsEveryList()
    {
        var swing = List("Swing", ("mu", null, true), ("NVDA", "NVIDIA", true));
        var tech = List("Tech", ("NVDA", null, true), ("AMD", null, true), ("INTC", null, false));

        var universe = DeskUniverse.ForAllWishlists([tech, swing]);

        Assert.Equal(DeskUniverseKind.AllWishlists, universe.Kind);
        Assert.Null(universe.WishlistId);
        Assert.Equal(["AMD", "MU", "NVDA"], universe.Members.Select(member => member.Ticker));
        var nvda = universe.Members.Single(member => member.Ticker == "NVDA");
        Assert.Equal(["Swing", "Tech"], nvda.Lists);
        Assert.Equal("NVIDIA", nvda.Item.DisplayName);
        Assert.DoesNotContain(universe.Members, member => member.Ticker == "INTC");
    }

    [Fact]
    public void ForWishlist_KeepsOnlyActiveItemsAndTheListId()
    {
        var swing = List("Swing", ("MU", null, true), ("INTC", null, false));

        var universe = DeskUniverse.ForWishlist(swing);

        Assert.Equal(DeskUniverseKind.Wishlist, universe.Kind);
        Assert.Equal(swing.Id, universe.WishlistId);
        var member = Assert.Single(universe.Members);
        Assert.Equal("MU", member.Ticker);
        Assert.Equal(["Swing"], member.Lists);
        Assert.False(universe.IncludesHeldPositions);
    }

    [Fact]
    public void ForScreener_AnnotatesKnownSymbolsAndLeavesNewOnesOnNoList()
    {
        var swing = List("Swing", ("MU", "Micron", true));

        var universe = DeskUniverse.ForScreener("Finviz · Channel Up", ["mu", "PLTR", "PLTR", " "], [swing]);

        Assert.Equal(DeskUniverseKind.Screener, universe.Kind);
        Assert.Equal(["MU", "PLTR"], universe.Members.Select(member => member.Ticker));
        Assert.Equal(["Swing"], universe.Members[0].Lists);
        Assert.Empty(universe.Members[1].Lists);
        Assert.Equal("PLTR", universe.Members[1].Item.Ticker);
        Assert.False(universe.Members[1].IsPositionOnly);
    }

    [Fact]
    public void WithHeldPositions_AddsUnlistedHoldingsOnlyToTheAllWishlistsView()
    {
        var swing = List("Swing", ("MU", null, true));

        var all = DeskUniverse.ForAllWishlists([swing]).WithHeldPositions(["mu", "TSLA"]);
        var single = DeskUniverse.ForWishlist(swing).WithHeldPositions(["TSLA"]);
        var screener = DeskUniverse.ForScreener("s", ["MU"], [swing]).WithHeldPositions(["TSLA"]);

        Assert.Equal(["MU", "TSLA"], all.Members.Select(member => member.Ticker));
        var held = all.Members.Single(member => member.Ticker == "TSLA");
        Assert.True(held.IsPositionOnly);
        Assert.Equal([DeskUniverse.HeldPositionLabel], held.Lists);
        Assert.Contains("TSLA", all.Tickers);
        Assert.DoesNotContain("TSLA", single.Tickers);
        Assert.DoesNotContain("TSLA", screener.Tickers);
    }

    [Fact]
    public void ForSymbols_AnnotatesListsAndNeverAddsHoldings()
    {
        var swing = List("Swing", ("MU", "Micron", true));

        var universe = DeskUniverse.ForSymbols("Orders", ["mu", "msft"], [swing]).WithHeldPositions(["TSLA"]);

        Assert.Equal(DeskUniverseKind.Symbols, universe.Kind);
        Assert.False(universe.IncludesHeldPositions);
        Assert.Equal(["MU", "MSFT"], universe.Members.Select(member => member.Ticker));
        Assert.Equal(["Swing"], universe.Members[0].Lists);
        Assert.Empty(universe.Members[1].Lists);
    }

    [Fact]
    public void TicketDefaults_FollowStrategyExitsOrFallBackAndHonourAReplacedLimit()
    {
        var fallback = DeskTicketDefaults.For(ask: 100m, mid: 99.5m, exits: null);
        Assert.Equal(new DeskTicketDefaults(100m, 96.50m, 107.00m), fallback);

        var fromMid = DeskTicketDefaults.For(ask: null, mid: 50m, exits: null);
        Assert.Equal(50m, fromMid.Limit);

        var replaced = DeskTicketDefaults.For(ask: 100m, mid: 99.5m, exits: null, limitOverride: 80m);
        Assert.Equal(new DeskTicketDefaults(80m, 77.20m, 85.60m), replaced);

        var exits = new ExitRules(2m, 3m, 48m, false, 0m, 0m, false, false, false, 0);
        var strategy = DeskTicketDefaults.For(ask: 100m, mid: 99.5m, exits);
        Assert.Equal(new DeskTicketDefaults(100m, 98.00m, 106.00m), strategy);

        var noQuote = DeskTicketDefaults.For(ask: null, mid: null, exits: null, limitOverride: 0m);
        Assert.Equal(new DeskTicketDefaults(0m, 0m, 0m), noQuote);
    }

    [Theory]
    [InlineData(" msft ", "MSFT")]
    [InlineData("BRK.B", "BRK.B")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("MSFT,AAPL", null)]
    [InlineData("<script>", null)]
    public void OrdersTicketSymbol_AcceptsExactlyOneValidSymbol(string? input, string? expected)
    {
        Assert.Equal(expected, OrdersModel.NormalizeSymbol(input));
    }

    [Fact]
    public void PortfolioSummary_TotalsCostValueAndPercentOverEveryPosition()
    {
        var summary = DeskPortfolioSummary.From(
        [
            Trade("MU", quantity: 10m, entry: 100m, current: 110m, pl: 100m),
            Trade("TSLA", quantity: 2m, entry: 250m, current: 200m, pl: -100m)
        ]);

        Assert.Equal(2, summary.Positions);
        Assert.Equal(1_500m, summary.CostBasis);
        Assert.Equal(1_500m, summary.MarketValue);
        Assert.Equal(0m, summary.UnrealizedPl);
        Assert.Equal(0m, summary.UnrealizedPlPct);
        Assert.Null(DeskPortfolioSummary.Empty.UnrealizedPlPct);
    }

    [Fact]
    public void SignalCatalog_ResolvesOnlyCataloguedCodes()
    {
        Assert.Equal(
            FinvizSignalCatalog.All.Count,
            FinvizSignalCatalog.All.Select(signal => signal.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.All(FinvizSignalCatalog.All, signal =>
            Assert.Matches("^(ta|n|it)_[a-z_]+$", signal.Code));

        Assert.True(FinvizSignalCatalog.TryResolveInput(" signal:TA_P_CHANNELUP ", out var channelUp));
        Assert.Equal("Channel Up", channelUp.Label);
        Assert.Equal("s=ta_p_channelup", channelUp.Query);
        Assert.Equal("signal:ta_p_channelup", FinvizSignalCatalog.ToInput(channelUp));

        Assert.False(FinvizSignalCatalog.TryResolveInput("signal:ta_made_up", out _));
        Assert.False(FinvizSignalCatalog.TryResolveInput("s=ta_p_channelup", out _));
        Assert.False(FinvizSignalCatalog.TryResolveInput(null, out _));
    }

    [Fact]
    public void Preparation_ClassifiesEveryState()
    {
        Assert.Equal(DeskPreparationState.NotPrepared, DeskPreparationService.Classify(null, Now).State);
        Assert.Equal(DeskPreparationState.Queued, DeskPreparationService.Classify(Intent("MU", null, "pending"), Now).State);
        Assert.Equal(DeskPreparationState.Ready, DeskPreparationService.Classify(Intent("MU", Now.AddHours(-2), "warmed"), Now).State);
        Assert.Equal(DeskPreparationState.Stale, DeskPreparationService.Classify(Intent("MU", Now.AddHours(-30), "warmed"), Now).State);
        var failed = DeskPreparationService.Classify(Intent("MU", Now.AddHours(-1), "failed", "no bars"), Now);
        Assert.Equal(DeskPreparationState.Failed, failed.State);
        Assert.Equal("no bars", failed.Detail);
    }

    [Fact]
    public void Preparation_ListsOnlySymbolsThatNeedWorkAndIgnoresInactiveIntents()
    {
        var snapshot = DeskPreparationService.Build(
            ["MU", "NVDA", "TSLA", "AMD", "PLTR"],
            [
                Intent("MU", Now.AddHours(-1), "warmed"),
                Intent("NVDA", Now.AddDays(-3), "warmed"),
                Intent("TSLA", null, "pending"),
                Intent("AMD", Now.AddHours(-1), "failed"),
                Intent("PLTR", Now.AddHours(-1), "warmed") with { Active = false }
            ],
            [Run("older", Now.AddDays(-1)), Run("newest", Now.AddHours(-1))],
            Now);

        Assert.True(snapshot.Available);
        Assert.Equal(["AMD", "NVDA", "PLTR"], snapshot.NeedingPreparation);
        Assert.Equal(1, snapshot.Count(DeskPreparationState.Ready));
        Assert.Equal(1, snapshot.Count(DeskPreparationState.Queued));
        Assert.Equal("newest", snapshot.LatestRun?.RunId);
        Assert.Equal(DeskPreparationState.Unknown, snapshot.For("ZZZ").State);
    }

    [Fact]
    public void StreamTickers_KeepsValidSymbolsOnceAndBoundsTheList()
    {
        var parsed = DeskStreamTickers.Parse(" mu,BRK.B, nvda ,MU,,1BAD,<script>,TOOLONGSYMBOL1");
        Assert.Equal(["BRK.B", "MU", "NVDA"], parsed.OrderBy(ticker => ticker, StringComparer.Ordinal));

        var many = String.Join(",", Enumerable.Range(0, 400).Select(index => $"A{index}"));
        Assert.Equal(DeskStreamTickers.MaximumTickers, DeskStreamTickers.Parse(many).Count);
        Assert.Empty(DeskStreamTickers.Parse(null));
    }

    private static Wishlist List(string name, params (string Ticker, string? DisplayName, bool Active)[] items)
    {
        var id = Guid.NewGuid();
        return new Wishlist
        {
            Id = id,
            Name = name,
            Items = items.Select(item => new WishlistItem
            {
                Id = Guid.NewGuid(),
                WishlistId = id,
                Ticker = item.Ticker,
                DisplayName = item.DisplayName,
                Active = item.Active
            }).ToList()
        };
    }

    private static MobileRunningTrade Trade(string ticker, decimal quantity, decimal entry, decimal current, decimal pl) =>
        new("paper", ticker, quantity, entry, current, pl, pl / (quantity * entry) * 100m,
            "open", "ref", null, null, "sell");

    private static WarmupTickerIntentDto Intent(string ticker, DateTimeOffset? warmedAt, string status, string? error = null) =>
        new(ticker, "desk", "desk", Now.AddDays(-5), 60, 14, ["1d"], true, true, warmedAt, status, error);

    private static WarmupRunRecordDto Run(string id, DateTimeOffset startedAt) =>
        new(id, "desk", startedAt, startedAt.AddMinutes(5), "completed", 1, 1, 0, []);
}
