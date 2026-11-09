using TradingFlow.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

/// <summary>
/// Confirming five-minute intervals without a candle from consolidated-tape trade records (9 November 2026): only
/// intervals whose every trade sets no price are quiet; anything price-setting, unknown or unavailable stays a gap.
/// </summary>
public sealed class IntradayQuietIntervalsTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateOnly Day = new(2026, 10, 19);
    private static readonly DateTimeOffset Open = new(2026, 10, 19, 13, 30, 0, TimeSpan.Zero);

    // Builds one trade print at a time inside an interval with the given tape and condition codes.
    private static SipTradePrint Trade(DateTimeOffset at, string tape, params string[] codes) => new(at, tape, codes);

    // The BKSY 19 October interval (odd lots, sweep odd lots, an average-price trade) sets no price on the CTA tape; a
    // regular sale, a UTP-only code on a CTA tape, or an unknown tape does.
    [Fact]
    public void ConditionTable_FollowsTheTapeSpecifications()
    {
        Assert.True(SipNonPriceSettingConditions.IsNonPriceSetting(Trade(Open, "A", " ", "I")));
        Assert.True(SipNonPriceSettingConditions.IsNonPriceSetting(Trade(Open, "A", " ", "F", "I")));
        Assert.True(SipNonPriceSettingConditions.IsNonPriceSetting(Trade(Open, "A", " ", "4", "I")));
        Assert.True(SipNonPriceSettingConditions.IsNonPriceSetting(Trade(Open, "A", " ", "B")));
        Assert.True(SipNonPriceSettingConditions.IsNonPriceSetting(Trade(Open, "C", "@", "W")));
        Assert.False(SipNonPriceSettingConditions.IsNonPriceSetting(Trade(Open, "A", " ")));
        Assert.False(SipNonPriceSettingConditions.IsNonPriceSetting(Trade(Open, "A", " ", "F")));
        Assert.False(SipNonPriceSettingConditions.IsNonPriceSetting(Trade(Open, "A", " ", "W")));
        Assert.False(SipNonPriceSettingConditions.IsNonPriceSetting(Trade(Open, "C", "@", "B")));
        Assert.False(SipNonPriceSettingConditions.IsNonPriceSetting(Trade(Open, "A", " ", "Q")));
        Assert.False(SipNonPriceSettingConditions.IsNonPriceSetting(Trade(Open, "", "I")));
    }

    // Only intervals without a candle in sessions that have candles are gaps.
    [Fact]
    public void ListGaps_FindsMissingRegularSessionIntervals()
    {
        var bars = Enumerable.Range(0, 78).Where(slot => slot is not (8 or 9 or 40))
            .Select(slot => Bar(Open.AddMinutes(slot * 5))).ToArray();
        var sessions = new Dictionary<DateOnly, MarketSessionSchedule> { [Day] = MarketSessionSchedule.RegularDay(Day) };

        var gaps = IntradayQuietIntervals.ListGaps(new Dictionary<DateOnly, OhlcvBar[]> { [Day] = bars }, sessions, NewYork);

        Assert.Equal([Open.AddMinutes(40), Open.AddMinutes(45), Open.AddMinutes(200)], gaps);
    }

    // One request per run of back-to-back gaps; an interval is quiet only when it holds trades and none sets a price. A
    // regular sale, no trade records at all (indistinguishable from an outage) or an unavailable request keeps it a gap.
    [Fact]
    public async Task Resolve_ConfirmsOnlyIntervalsWithoutPriceSettingTrades()
    {
        var first = Open.AddMinutes(40);
        var second = Open.AddMinutes(45);
        var third = Open.AddMinutes(200);
        var fourth = Open.AddMinutes(300);
        var source = new FakeTradeSource
        {
            [(first, second.AddMinutes(5))] = new([Trade(first.AddSeconds(30), "A", " ", "I"),
                Trade(second.AddSeconds(10), "A", " ")], "run-1"),
            [(third, third.AddMinutes(5))] = new([], "run-2"),
            [(fourth, fourth.AddMinutes(5))] = IntervalTradePrints.Unavailable("trade_records_failed:HttpRequestException:401")
        };

        var result = await IntradayQuietIntervals.ResolveAsync("BKSY", [first, second, third, fourth], NewYork, source, 100, default);

        Assert.Equal(3, source.Requests.Count);
        Assert.Equal(new HashSet<DateTimeOffset> { first }, result.QuietIntervals);
        Assert.Equal([second, third, fourth], result.UnresolvedGaps);
        Assert.Equal("price_setting_trade_in_interval", result.UnresolvedReasons[second]);
        Assert.Equal("no_trade_records_in_interval", result.UnresolvedReasons[third]);
        Assert.Equal("trade_records_failed:HttpRequestException:401", result.UnresolvedReasons[fourth]);
        Assert.Equal(["run-1", "run-2"], result.EvidenceIds);
        Assert.Equal(Day, source.Requests[0].Session);
    }

    // A decision sees only quiet intervals from the window start up to (not including) its decision time.
    [Fact]
    public void Window_HidesIntervalsAfterTheDecision()
    {
        var quiet = new HashSet<DateTimeOffset> { Open.AddDays(-1), Open.AddMinutes(10), Open.AddMinutes(30) };

        Assert.Equal(new HashSet<DateTimeOffset> { Open.AddMinutes(10) }, IntradayQuietIntervals.Window(quiet, Open, Open.AddMinutes(30)));
        Assert.Null(IntradayQuietIntervals.Window(quiet, Open.AddMinutes(40), Open.AddMinutes(60)));
        Assert.Null(IntradayQuietIntervals.Window(null, Open, Open.AddMinutes(30)));
    }

    // Without a trade source, or beyond the per-stock request cap, nothing is assumed quiet.
    [Fact]
    public async Task Resolve_AssumesNothingWithoutEvidence()
    {
        var gaps = new[] { Open.AddMinutes(40), Open.AddMinutes(100) };
        var none = await IntradayQuietIntervals.ResolveAsync("BKSY", gaps, NewYork, null, 100, default);
        var oddLot = (DateTimeOffset at) => new IntervalTradePrints([Trade(at.AddSeconds(5), "C", "@", "I")], "run");
        var source = new FakeTradeSource { [(gaps[0], gaps[0].AddMinutes(5))] = oddLot(gaps[0]),
            [(gaps[1], gaps[1].AddMinutes(5))] = oddLot(gaps[1]) };
        var capped = await IntradayQuietIntervals.ResolveAsync("BKSY", gaps, NewYork, source, 1, default);

        Assert.Empty(none.QuietIntervals);
        Assert.Equal(gaps, none.UnresolvedGaps);
        Assert.Equal("no_trade_record_source_for_this_provider_or_feed", none.UnresolvedReasons[gaps[0]]);
        Assert.Equal([gaps[0]], capped.QuietIntervals);
        Assert.Equal([gaps[1]], capped.UnresolvedGaps);
        Assert.Equal("beyond_per_stock_trade_request_cap", capped.UnresolvedReasons[gaps[1]]);
    }

    // The shared coverage rule accepts a confirmed quiet interval in place of a candle, and nothing else.
    [Fact]
    public void Coverage_AcceptsOnlyConfirmedQuietIntervals()
    {
        var bars = new[] { Bar(Open), Bar(Open.AddMinutes(10)) };
        var close = Open.AddMinutes(15);

        Assert.False(IntradayFiveMinuteBars.HasCompleteCoverage(bars, Open, close));
        Assert.True(IntradayFiveMinuteBars.HasCompleteCoverage(bars, Open, close, new HashSet<DateTimeOffset> { Open.AddMinutes(5) }));
        Assert.False(IntradayFiveMinuteBars.HasCompleteCoverage(bars, Open, close, new HashSet<DateTimeOffset> { Open.AddMinutes(20) }));
    }

    // The Alpaca source archives a real-shaped SIP trades page, reads tape and codes from it, and re-reads the archive on a
    // repeat request without another HTTP call; a 401 becomes a recorded reason.
    [Fact]
    public async Task AlpacaSource_ArchivesAndReadsTradePages()
    {
        var root = Path.Combine(Path.GetTempPath(), "tf-quiet-trades-" + Guid.NewGuid().ToString("N"));
        try
        {
            var start = Open.AddMinutes(100);
            const string page = """{"trades":{"BKSY":[{"t":"2026-10-19T15:10:03.123456789Z","x":"N","p":10.5,"s":5,"c":[" ","I"],"i":1,"z":"A"},{"t":"2026-10-19T15:11:00Z","x":"D","p":10.4,"s":100,"c":[" ","B"],"i":2,"z":"A"}]},"next_page_token":null}""";
            var handler = new FixedHandler(System.Net.HttpStatusCode.OK, page);
            var source = new AlpacaSipIntervalTradePrintSource(new HttpClient(handler), root);

            var prints = await source.GetTradesAsync("BKSY", start, start.AddMinutes(5), Day, default);
            var again = await source.GetTradesAsync("BKSY", start, start.AddMinutes(5), Day, default);
            var refused = await new AlpacaSipIntervalTradePrintSource(new HttpClient(new FixedHandler(System.Net.HttpStatusCode.Unauthorized, "")),
                Path.Combine(root, "other")).GetTradesAsync("BKSY", start, start.AddMinutes(5), Day, default);

            Assert.Null(prints!.UnavailableReason);
            Assert.Equal(2, prints.Trades.Count);
            Assert.All(prints.Trades, trade => Assert.True(SipNonPriceSettingConditions.IsNonPriceSetting(trade)));
            Assert.Equal(new DateTimeOffset(2026, 10, 19, 15, 10, 3, TimeSpan.Zero).AddTicks(1_234_567), prints.Trades[0].TimestampUtc);
            Assert.Equal(1, handler.Calls);
            Assert.Equal(prints.EvidenceId, again!.EvidenceId);
            Assert.StartsWith("trade_records_failed:HttpRequestException", refused!.UnavailableReason);
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(root), ignoreCase: true);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // Answers every HTTP request with one fixed status and body, counting the calls.
    private sealed class FixedHandler(System.Net.HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static OhlcvBar Bar(DateTimeOffset at) => new("BKSY", at, "5m", 10m, 10.1m, 9.9m, 10m, 1_000m, "sip", "all", at.AddMinutes(5));

    // Returns a prepared answer per exact [start, end) request and records each request.
    private sealed class FakeTradeSource : Dictionary<(DateTimeOffset Start, DateTimeOffset End), IntervalTradePrints?>, IIntervalTradePrintSource
    {
        public List<(DateTimeOffset Start, DateTimeOffset End, DateOnly Session)> Requests { get; } = [];

        public Task<IntervalTradePrints?> GetTradesAsync(string ticker, DateTimeOffset startUtc, DateTimeOffset endUtc,
            DateOnly sessionDate, CancellationToken cancellationToken)
        {
            Requests.Add((startUtc, endUtc, sessionDate));
            return Task.FromResult(TryGetValue((startUtc, endUtc), out var prints) ? prints : null);
        }
    }
}
