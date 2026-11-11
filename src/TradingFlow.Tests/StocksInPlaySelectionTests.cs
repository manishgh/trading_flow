using System.Runtime.CompilerServices;
using TradingFlow.Application.Jobs;
using TradingFlow.Cli;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Evidence.Normalization;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Tests;

// Daily stocks-in-play selection (design docs/design-review/2026-11-02/orb-stocks-in-play-design.md, steps 2-3): the shared
// relative-volume calculation, the per-session ranking, its use as backtest membership and the opening-bar collection plan.
public sealed class StocksInPlaySelectionTests
{
    private static readonly DailyRankingRules Paper = new();

    private static UniverseSecurityIdentity Id(string symbol, string issuer) => new(symbol, issuer, issuer);

    // Mean is the paper's averaging rule, median the production one; an even count's median is the mean of the middle two.
    [Fact]
    public void RelativeVolume_AveragesByTheChosenRule()
    {
        decimal[] samples = [100m, 200m, 300m, 1_000m];

        Assert.Equal(400m, RelativeVolume.Baseline(samples, VolumeBaselineAverage.Mean));
        Assert.Equal(250m, RelativeVolume.Baseline(samples, VolumeBaselineAverage.Median));
        Assert.Equal(2m, RelativeVolume.Ratio(800m, 400m));
        Assert.Null(RelativeVolume.Ratio(800m, 0m));
        Assert.Throws<ArgumentException>(() => RelativeVolume.Baseline(Array.Empty<decimal>(), VolumeBaselineAverage.Mean));
    }

    // The top members by relative volume are kept in rank order; a stock below 100% is dropped even when slots remain.
    [Fact]
    public void Ranking_KeepsTheHighestRelativeVolumesAboveTheMinimum()
    {
        var rules = Paper with { BaselineSessions = 2, MaxMembersPerSession = 2 };
        var day = new DateOnly(2026, 5, 4);
        var ranking = OpeningVolumeRanking.Rank(day, [
            new(Id("AAA", "issuer-a"), 300m, [100m, 100m]),
            new(Id("BBB", "issuer-b"), 500m, [100m, 100m]),
            new(Id("CCC", "issuer-c"), 200m, [100m, 100m]),
            new(Id("DDD", "issuer-d"), 99m, [100m, 100m])], rules);

        Assert.Equal(["BBB", "AAA"], ranking.Members.Select(member => member.Security.Symbol));
        Assert.Equal([1, 2], ranking.Members.Select(member => member.Rank));
        Assert.Equal(5m, ranking.Members[0].RelativeVolume);
        Assert.Equal(100m, ranking.Members[0].BaselineVolume);
        Assert.Equal(3, ranking.QualifiedCount);
        Assert.Contains(ranking.Rejections, rejection => rejection.Symbol == "CCC" && rejection.Reason == "rank_outside_top_2");
        Assert.Contains(ranking.Rejections, rejection => rejection.Symbol == "DDD" && rejection.Reason.StartsWith("relative_volume 0.9900 < min", StringComparison.Ordinal));
    }

    // Equal relative volumes go to the lower issuer identifier, not the lower ticker, so a ticker change cannot reorder them.
    [Fact]
    public void Ranking_BreaksTiesByIssuerIdentifier()
    {
        var ranking = OpeningVolumeRanking.Rank(new DateOnly(2026, 5, 4), [
            new(Id("AAA", "issuer-9"), 200m, [100m]),
            new(Id("ZZZ", "issuer-1"), 200m, [100m])], Paper with { BaselineSessions = 1, MaxMembersPerSession = 1 });

        Assert.Equal("ZZZ", Assert.Single(ranking.Members).Security.Symbol);
        Assert.Equal("AAA", new DeterministicUniverseRanker().Rank(
            [new(Id("AAA", "issuer-9"), 2m, ["x"]), new(Id("ZZZ", "issuer-1"), 2m, ["x"])], 1).Ranked.Single().Candidate.Security.Symbol);
    }

    // Too few earlier sessions and a zero baseline are named exclusions; only the most recent baseline sessions are averaged.
    [Fact]
    public void Ranking_ExcludesShortHistoryAndZeroBaselineAndUsesOnlyRecentSessions()
    {
        var ranking = OpeningVolumeRanking.Rank(new DateOnly(2026, 5, 4), [
            new(Id("NEW", "n"), 500m, [100m]),
            new(Id("ZERO", "z"), 500m, [0m, 0m]),
            new(Id("OLD", "o"), 500m, [1_000_000m, 100m, 150m])], Paper with { BaselineSessions = 2 });

        Assert.Equal(125m, Assert.Single(ranking.Members).BaselineVolume);
        Assert.Contains(ranking.Rejections, rejection => rejection.Symbol == "NEW" && rejection.Reason == "opening_volume_history 1 < 2 sessions");
        Assert.Contains(ranking.Rejections, rejection => rejection.Symbol == "ZERO" && rejection.Reason == "opening_volume_baseline_zero");
    }

    // The minimum relative volume must be above zero, so a session with a proven-zero first five minutes (relative volume 0,
    // proven in backtests from bars after 09:35) can never qualify; a zero or negative minimum is refused before ranking.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RankingRules_RefuseAMinimumRelativeVolumeOfZeroOrLess(int minimum)
    {
        var rules = Paper with { MinRelativeVolume = minimum };

        Assert.Throws<ArgumentException>(rules.Validate);
        Assert.Throws<ArgumentException>(() => OpeningVolumeRanking.Rank(new DateOnly(2026, 5, 4),
            [new(Id("ZERO", "z"), 0m, Enumerable.Repeat<decimal?>(100m, 14).ToArray())], rules));
    }

    // Backtest membership becomes each session's ranked members. A session's own 09:30 bar ranks that session (it is complete
    // at 09:35), and entries before 09:35 are not members. BBB has no opening bar on 6 May. With a later 10:00 bar that day
    // the missing bar is proven zero volume and counts as zero; without one it is unproven, so BBB is excluded as
    // opening_bar_coverage_missing on 6 May and on every later session whose baseline reads 6 May, instead of a lower baseline.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Membership_WithRankingKeepsEachSessionsTopStockFromNineThirtyFive(bool laterBarProvesNoOpeningTrade)
    {
        var days = Enumerable.Range(0, 5).Select(offset => new DateOnly(2026, 5, 4).AddDays(offset)).ToArray(); // Mon-Fri, EDT
        var bars = new List<OhlcvBar>();
        foreach (var (day, index) in days.Select((day, index) => (day, index)))
        {
            foreach (var ticker in new[] { "AAA", "BBB" })
                bars.Add(new OhlcvBar(ticker, new DateTimeOffset(day.ToDateTime(new TimeOnly(4, 0)), TimeSpan.Zero), "1d", 20m, 21m, 19m, 20m, 2_000_000m));
            // AAA spikes on the last day; BBB is steady but has no opening bar on the third day.
            var opening = new DateTimeOffset(day.ToDateTime(new TimeOnly(13, 30)), TimeSpan.Zero);
            bars.Add(new OhlcvBar("AAA", opening, "5m", 20m, 21m, 19m, 20m, index == 4 ? 900m : 100m));
            if (index != 2) bars.Add(new OhlcvBar("BBB", opening, "5m", 20m, 21m, 19m, 20m, 150m));
            else if (laterBarProvesNoOpeningTrade) bars.Add(new OhlcvBar("BBB", opening.AddMinutes(30), "5m", 20m, 21m, 19m, 20m, 400m));
        }

        var eligibility = new DailyScreenRules(5m, DailyScreenPriceBasis.PriorClose, 2, MinHistorySessions: 2);
        var universe = UniverseConfig.StaticUniverse with
        {
            Mode = UniverseConfig.ResolvedSnapshotMode, DailyEligibility = eligibility,
            DailyRanking = Paper with { BaselineSessions = 2, MaxMembersPerSession = 1 }
        };
        var membership = await new HistoricalScreenerUniverseProvider(new TimeframeProvider(bars)).ResolveMembershipAsync(
            new UniverseRequest(["AAA", "BBB"], universe, new DateTimeOffset(2026, 5, 6, 4, 0, 0, TimeSpan.Zero), "1d",
                new DateTimeOffset(2026, 5, 8, 23, 0, 0, TimeSpan.Zero)), CancellationToken.None);

        if (laterBarProvesNoOpeningTrade)
        {
            // 6 May: BBB 0 / 150 baseline, AAA ranks first. 7 May: BBB baseline (150, proven 0) = 75, ratio 2, one proven zero.
            Assert.Equal(["AAA", "BBB", "AAA"], membership.SessionRankings.Select(session => session.Members.Single().Security.Symbol));
            Assert.Equal(1, membership.SessionRankings[1].Members[0].ProvenZeroBaselineSessions);
            Assert.Equal(0, membership.SessionRankings[^1].Members[0].ProvenZeroBaselineSessions);
        }
        else
        {
            Assert.Equal(["AAA", "AAA", "AAA"], membership.SessionRankings.Select(session => session.Members.Single().Security.Symbol));
            Assert.All(membership.SessionRankings, session => Assert.Contains(session.Rejections,
                rejection => rejection.Symbol == "BBB" && rejection.Reason == OpeningVolumeRanking.CoverageMissing));
        }

        Assert.True(membership.IsMember("AAA", days[4]));
        Assert.Equal(9m, membership.SessionRankings[^1].Members[0].RelativeVolume);
        Assert.False(membership.IsMemberAt("AAA", new DateTimeOffset(2026, 5, 8, 13, 30, 0, TimeSpan.Zero)));
        Assert.True(membership.IsMemberAt("AAA", new DateTimeOffset(2026, 5, 8, 13, 35, 0, TimeSpan.Zero)));
    }

    // Twenty-one stocks qualify; the 20th and 21st have the same relative volume. The last slot goes to the lower issuer
    // identifier (issuer-10, symbol ZZZ) and the other is excluded as outside the top 20, whatever their symbols.
    [Fact]
    public void Ranking_TieAtTheTopTwentyBoundaryGoesToTheLowerIssuer()
    {
        var observations = Enumerable.Range(0, 19)
            .Select(index => new OpeningVolumeObservation(Id($"S{index:00}", $"issuer-a{index:00}"), 1_000m - index * 10m, [100m]))
            .Append(new(Id("AAA", "issuer-99"), 500m, [100m]))
            .Append(new(Id("ZZZ", "issuer-10"), 500m, [100m]));

        var ranking = OpeningVolumeRanking.Rank(new DateOnly(2026, 5, 4), observations, Paper with { BaselineSessions = 1 });

        Assert.Equal(20, ranking.Members.Count);
        Assert.Equal(21, ranking.QualifiedCount);
        Assert.Equal(("ZZZ", 20), (ranking.Members[^1].Security.Symbol, ranking.Members[^1].Rank));
        Assert.Contains(ranking.Rejections, rejection => rejection.Symbol == "AAA" && rejection.Reason == "rank_outside_top_20");
    }

    // An unproven session value is never averaged as zero: a missing opening value on the session itself or anywhere in the
    // averaged baseline excludes the stock as opening_bar_coverage_missing; an unproven value older than the baseline window
    // is not read. Proven zeros are counted on the member.
    [Fact]
    public void Ranking_ExcludesUnprovenOpeningValuesAndCountsProvenZeros()
    {
        var ranking = OpeningVolumeRanking.Rank(new DateOnly(2026, 5, 4), [
            new(Id("OPEN", "o"), null, [100m, 100m]),
            new(Id("BASE", "b"), 500m, [100m, null]),
            new(Id("OLD", "d"), 500m, [null, 100m, 100m]),
            new(Id("ZERO", "z"), 500m, [0m, 100m], ProvenZeroBaselineSessions: 1)], Paper with { BaselineSessions = 2 });

        Assert.Equal(["ZERO", "OLD"], ranking.Members.Select(member => member.Security.Symbol));
        Assert.Equal(1, ranking.Members[0].ProvenZeroBaselineSessions);
        Assert.Equal(["BASE", "OPEN"], ranking.Rejections.Where(rejection => rejection.Reason == OpeningVolumeRanking.CoverageMissing)
            .Select(rejection => rejection.Symbol));
    }

    // The ranking is known only at 09:35, so a plan whose entries fill at a daily open is refused.
    [Fact]
    public void PlanRanking_IsRefusedForDailyOpenReplays()
    {
        var requested = new BacktestEligibilityRules(5m, "session_open", 14, 14, 1_000_000m, AtrPeriod: 14, MinAtr: 0.5m,
            Ranking: new BacktestRankingRules(DailyRankingRules.FirstFiveMinuteRelativeVolume, 14, "mean", 1m, 20));

        Assert.Equal(Paper, BacktestEligibility.ToRanking(requested, ExecutionReplayPolicy.ForEntryMode(TradingFlow.Domain.Strategies.IntradayEntryOrderMode.StopMarket)));
        Assert.Throws<InvalidOperationException>(() => BacktestEligibility.ToRanking(requested with { PriceBasis = "prior_close" }, null));
        Assert.Null(BacktestEligibility.ToRanking(requested with { Ranking = null }, null));
    }

    // The opening-bar plan asks for 09:30-09:35 New York (13:30Z in summer, 14:30Z in winter) on each eligible session and on
    // the stock's own earlier traded sessions the baseline reads, one request per session and batch.
    [Fact]
    public void OpeningBarsPlan_CoversEligibleAndBaselineSessions()
    {
        var sessions = new[] { new DateOnly(2026, 10, 29), new DateOnly(2026, 10, 30), new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 3) };
        var traded = new Dictionary<string, (string, DateOnly[])> { ["AAA"] = ("sec-a", sessions), ["BBB"] = ("sec-b", sessions) };
        var eligible = new Dictionary<DateOnly, IReadOnlyCollection<string>> { [sessions[3]] = ["AAA"], [sessions[2]] = ["BBB"] };

        var needed = IntradayBarsCollectionPlanCommand.WithBaselineSessions(eligible, traded, 2);
        var request = IntradayBarsCollectionPlanCommand.CreateRequest(needed, IntradayBarsCollectionPlanCommand.OpeningFiveMinutes, 1,
            new Dictionary<string, string>());

        Assert.Equal(["BBB"], needed[sessions[0]].Select(item => item.Symbol));
        Assert.Equal(["AAA", "BBB"], needed[sessions[2]].Select(item => item.Symbol).Order());
        var summer = request.Requests.Single(item => item.RequestId == "opening-bars-20261029-000");
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 13, 30, 0, TimeSpan.Zero), summer.RequestedStartUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 13, 35, 0, TimeSpan.Zero), summer.RequestedEndUtc);
        var winter = request.Requests.First(item => item.RequestId.StartsWith("opening-bars-20261103", StringComparison.Ordinal));
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 14, 30, 0, TimeSpan.Zero), winter.RequestedStartUtc);
        Assert.Equal("5Min", winter.Parameters["timeframe"]);
        Assert.Equal(new DateOnly(2026, 11, 3), winter.AsOfDate);
        Assert.Equal(6, request.Requests.Count); // 1 + 2 + 2 + 1 stock-sessions, one stock per batch.
    }

    // The research screen publishes the ranked list with security identifiers standing in for issuers, per-session counts
    // and exclusion reasons; the one-minute plan reads that list back for the selected stocks only.
    [Fact]
    public async Task ResearchRanking_PublishesTheListTheMinutePlanReads()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tradingflow-tests", "stocks-in-play-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            DateOnly[] sessions = [new(2026, 5, 4), new(2026, 5, 5), new(2026, 5, 6)];
            var volumes = new Dictionary<(string, DateOnly), decimal>
            {
                [("sec-a", sessions[0])] = 100m, [("sec-a", sessions[1])] = 100m, [("sec-a", sessions[2])] = 300m,
                [("sec-b", sessions[0])] = 100m, [("sec-b", sessions[1])] = 100m, [("sec-b", sessions[2])] = 50m
            };
            var output = new DailyRankingOutput(Paper with { BaselineSessions = 2 }, volumes, new HashSet<(string, DateOnly)>());
            output.Add(sessions[2], "sec-a", "AAA", sessions);
            output.Add(sessions[2], "sec-b", "BBB", sessions);

            var ranked = await output.WriteAsync(folder, CancellationToken.None);
            var plan = Path.Combine(folder, "minute-plan.json");
            await IntradayBarsCollectionPlanCommand.RunMinuteBarsAsync(
                ["--ranked", Path.Combine(folder, "ranked-daily-list.csv"), "--batch", "50", "--out", plan], TextWriter.Null, CancellationToken.None);

            Assert.Equal("AAA", Assert.Single(Assert.Single(ranked).Members).Security.Symbol);
            Assert.Equal("2026-05-06,1,AAA,sec-a,alpaca_security_id,300,100,3,0", File.ReadAllLines(Path.Combine(folder, "ranked-daily-list.csv"))[1]);
            Assert.Equal("2026-05-06,2,1,1", File.ReadAllLines(Path.Combine(folder, "ranked-daily-counts.csv"))[1]);
            var json = await File.ReadAllTextAsync(plan);
            Assert.Contains("\"1Min\"", json, StringComparison.Ordinal);
            Assert.Contains("minute-bars-20260506-000", json, StringComparison.Ordinal);
            Assert.DoesNotContain("BBB", json, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // BBB has no opening bar on 5 May. The research ranking counts it as zero volume only when the opening-bar collection
    // completed the request for BBB's 09:30-09:35 interval that day; if that request is not recorded as completed, the missing
    // bar is unproven and BBB is excluded as opening_bar_coverage_missing instead of getting a lower baseline.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResearchRanking_CountsAMissingOpeningBarAsZeroOnlyWhenItsCollectionCompleted(bool requestCompleted)
    {
        DateOnly[] sessions = [new(2026, 5, 4), new(2026, 5, 5), new(2026, 5, 6)];
        IReadOnlyCollection<EvidenceSecurityIdentity> securities = [new("sec-a", "AAA"), new("sec-b", "BBB")];
        var request = IntradayBarsCollectionPlanCommand.CreateRequest(sessions.ToDictionary(day => day, _ => securities),
            IntradayBarsCollectionPlanCommand.OpeningFiveMinutes, 1, new Dictionary<string, string>());
        var completed = request.Requests.Select(item => item.RequestId)
            .Where(id => requestCompleted || id != "opening-bars-20260505-001").ToArray();
        var collected = DailyRankingOutput.CollectedSessions(request.Requests, completed);
        var volumes = new Dictionary<(string, DateOnly), decimal>
        {
            [("sec-a", sessions[0])] = 100m, [("sec-a", sessions[1])] = 100m, [("sec-a", sessions[2])] = 100m,
            [("sec-b", sessions[0])] = 100m, [("sec-b", sessions[2])] = 200m
        };
        var output = new DailyRankingOutput(Paper with { BaselineSessions = 2, MaxMembersPerSession = 1 }, volumes, collected);
        output.Add(sessions[2], "sec-a", "AAA", sessions);
        output.Add(sessions[2], "sec-b", "BBB", sessions);
        var folder = Path.Combine(Path.GetTempPath(), "tradingflow-tests", "stocks-in-play-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var session = Assert.Single(await output.WriteAsync(folder, CancellationToken.None));

            Assert.Equal(requestCompleted, collected.Contains(("BBB", sessions[1])));
            Assert.Equal(6 - (requestCompleted ? 0 : 1), collected.Count);
            if (requestCompleted)
            {
                // BBB baseline (100, proven 0) = 50, ratio 4, against AAA's 1.
                var member = Assert.Single(session.Members);
                Assert.Equal(("BBB", 4m, 1), (member.Security.Symbol, member.RelativeVolume, member.ProvenZeroBaselineSessions));
                Assert.EndsWith(",1", File.ReadAllLines(Path.Combine(folder, "ranked-daily-list.csv"))[1], StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal("AAA", Assert.Single(session.Members).Security.Symbol);
                Assert.Contains(session.Rejections, rejection => rejection.Symbol == "BBB" && rejection.Reason == OpeningVolumeRanking.CoverageMissing);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // Serves only the requested timeframe, like the real providers, and every bar regardless of dates so the screen's own
    // date guards are what keep later bars out.
    private sealed class TimeframeProvider(IReadOnlyList<OhlcvBar> bars) : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes,
            DateTimeOffset start, DateTimeOffset end, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var bar in bars.Where(bar => tickers.Contains(bar.Ticker, StringComparer.OrdinalIgnoreCase) && timeframes.Contains(bar.Timeframe)))
                yield return bar;
            await Task.CompletedTask;
        }
    }
}
