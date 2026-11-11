using System.Runtime.CompilerServices;
using Moq;
using TradingFlow.Cli;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Universe;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Tests;

/// <summary>
/// The whole-market research screen as a backtest stock source (design docs/design-review/2026-11-10/orb-stocks-in-play-full-test-design.md,
/// Part 1): the published manifest and its fingerprints, the read-only preview, and membership parity with the in-run ranking.
/// Every screen here is a synthetic fixture in its own temporary folder.
/// </summary>
public sealed class ResearchScreenTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DailyScreenRules Rules = new(5m, DailyScreenPriceBasis.SessionOpen, 14,
        MinAverageShareVolume: 1_000_000m, AtrPeriod: 14, MinAtr: 0.5m, MinHistorySessions: 14);
    private static readonly DailyRankingRules Ranking = new(DailyRankingRules.FirstFiveMinuteRelativeVolume, 14, VolumeBaselineAverage.Mean, 1m, 2);

    // The manifest names every input dataset, rule value and output fingerprint; one changed byte in the ranked list, or a
    // manifest other than the one a plan recorded, is refused.
    [Fact]
    public async Task Manifest_FingerprintsEveryOutputAndAChangedRankedListIsRefused()
    {
        using var root = new TemporaryFolder();
        var folder = await SyntheticScreen.PublishAsync(Path.Combine(root.Path, "screen-a"), [("2026-10-01", "ABC", "sec-abc")]);

        var screen = PublishedResearchScreen.Read(folder);

        Assert.Equal(new ResearchScreenInputs("daily", "actions", "opening", "sessions"), screen.Manifest.Inputs);
        Assert.Equal(ResearchScreenRuleValues.From(Rules), screen.Manifest.ScreenRules);
        Assert.Equal(ResearchRankingRuleValues.From(Ranking), screen.Manifest.RankingRules);
        Assert.Equal(await ResearchScreenManifest.Sha256FileAsync(Path.Combine(folder, "ranked-daily-list.csv"), default), screen.RankedListSha256);
        Assert.Equal(["ranked-daily-counts.csv", "ranked-daily-exclusions.csv", "ranked-daily-list.csv"], screen.Manifest.Outputs.Keys.Order());
        var wrongPlan = Assert.Throws<InvalidDataException>(() => PublishedResearchScreen.Read(folder, new string('0', 64)));
        Assert.Contains("ranked list changed since the plan was saved", wrongPlan.Message, StringComparison.Ordinal);
        await File.AppendAllTextAsync(Path.Combine(folder, "ranked-daily-list.csv"), "2026-10-02,1,XYZ,sec-xyz,alpaca_security_id,1,1,1,0\n");
        Assert.Throws<InvalidDataException>(() => PublishedResearchScreen.Read(folder));
        Assert.Throws<InvalidDataException>(() => PublishedResearchScreen.Read(folder, screen.ManifestSha256));
    }

    // Previewing a research screen only reads its files: it lists every ranked stock with the manifest and ranked-list
    // fingerprints and never touches a wishlist or the Finviz screener. Only folders with a ranked list are offered.
    [Fact]
    public async Task Preview_ReadsTheScreenAndWritesNothingToOperatorLists()
    {
        using var root = new TemporaryFolder();
        var paths = new ProjectPaths(root.Path, root.Path, Path.Combine(root.Path, "cache"));
        var folder = await SyntheticScreen.PublishAsync(Path.Combine(paths.ResearchScreensRoot, "screen-a"),
            [("2026-10-01", "ABC", "sec-abc"), ("2026-10-02", "ABC", "sec-abc"), ("2026-10-02", "XYZ", "sec-xyz")]);
        Directory.CreateDirectory(Path.Combine(paths.ResearchScreensRoot, "not-published"));
        var wishlists = new Mock<IWishlistRepository>(MockBehavior.Strict);
        var screener = new Mock<IScreenerSnapshotSource>(MockBehavior.Strict);
        var screens = new ResearchScreenSources(paths);
        var resolver = new UniverseSourceResolver(wishlists.Object, screener.Object, TimeProvider.System, screens);

        var capture = await resolver.ResolveAsync(new(DiscoverySourceKinds.ResearchScreen, ResearchScreenSources.KeyPrefix + "screen-a"), "day");

        Assert.Equal(["screen-a"], screens.List());
        Assert.Equal(DiscoverySourceKinds.ResearchScreen, capture.SourceKind);
        Assert.Equal(["ABC", "XYZ"], capture.Symbols.Select(item => item.Symbol));
        var screen = PublishedResearchScreen.Read(folder);
        Assert.Equal(("screen-a", screen.ManifestSha256, screen.RankedListSha256), ResearchScreenSources.ParseProviderReference(capture.ProviderReference));
        Assert.Equal(["ABC"], screen.PoolSymbols(new(2026, 10, 1), new(2026, 10, 1)));
        Assert.Throws<InvalidOperationException>(() => screen.PoolSymbols(new(2026, 9, 30), new(2026, 10, 2)));
        Assert.Throws<InvalidOperationException>(() => screen.ToMembership(new(2026, 10, 1), new(2026, 10, 5)));
        wishlists.VerifyNoOtherCalls();
        screener.VerifyNoOtherCalls();
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(new(DiscoverySourceKinds.ResearchScreen, "..\\screen-a"), "day"));
    }

    // The datasets a research-screen run reads: the manifest's daily bars and sessions, plus the five-minute and one-minute
    // datasets collected for this ranked list; a list without five-minute bars or a manifest without sessions is refused.
    [Fact]
    public async Task DatasetReference_UsesOnlyDatasetsCollectedForTheRankedList()
    {
        using var root = new TemporaryFolder();
        var screen = PublishedResearchScreen.Read(await SyntheticScreen.PublishAsync(Path.Combine(root.Path, "screen-a"), [("2026-10-01", "ABC", "sec-abc")]));
        using var catalog = new SyntheticEvidenceCatalog();
        var five = await catalog.BarsAsync("5m", screen.RankedListSha256, [SyntheticEvidenceCatalog.Row("ABC", "5m", new(2026, 10, 1, 13, 30, 0, TimeSpan.Zero), 10m)]);
        var other = await catalog.BarsAsync("5m", new string('e', 64), [SyntheticEvidenceCatalog.Row("ABC", "5m", new(2026, 10, 1, 13, 35, 0, TimeSpan.Zero), 10m)]);
        var minute = await catalog.BarsAsync("1m", screen.RankedListSha256, [SyntheticEvidenceCatalog.Row("ABC", "1m", new(2026, 10, 1, 13, 30, 0, TimeSpan.Zero), 10m)]);

        var reference = ResearchScreenSources.ResolveReference(screen, [five, other, minute]);

        Assert.Equal(new[] { five.DatasetId }, reference.FiveMinuteDatasetIds);
        Assert.Equal(new[] { minute.DatasetId }, reference.OneMinuteDatasetIds);
        Assert.Equal(("daily", "sessions"), (reference.DailyBarsDatasetId, reference.ExchangeSessionsDatasetId));
        Assert.Throws<InvalidOperationException>(() => ResearchScreenSources.ResolveReference(screen, [other, minute]));
        Assert.Throws<InvalidOperationException>(() => ResearchScreenSources.ResolveReference(screen, [five, other]));
    }

    // The five-minute variant of the minute planner asks for the full 09:30-16:00 session of each selected stock-session and
    // of the stock's own earlier traded sessions.
    [Fact]
    public void SelectedSessionPlan_RequestsFullSessionsOfSelectedAndPriorTradedSessions()
    {
        DateOnly[] sessions = [new(2026, 9, 28), new(2026, 9, 29), new(2026, 9, 30), new(2026, 10, 1)];
        var traded = new Dictionary<string, (string, DateOnly[])> { ["ABC"] = ("sec-abc", sessions) };
        var selected = new Dictionary<DateOnly, IReadOnlyCollection<string>> { [sessions[3]] = ["ABC"] };

        var needed = IntradayBarsCollectionPlanCommand.WithBaselineSessions(selected, traded, 2);
        var request = IntradayBarsCollectionPlanCommand.CreateRequest(needed, IntradayBarsCollectionPlanCommand.RegularSessionFiveMinutes, 50,
            new Dictionary<string, string> { ["ranked_list_sha256"] = new string('a', 64) });

        Assert.Equal(sessions[1..], needed.Keys.Order());
        var first = request.Requests.Single(item => item.AsOfDate == sessions[1]);
        Assert.Equal((new DateTimeOffset(2026, 9, 29, 13, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 29, 20, 0, 0, TimeSpan.Zero), "5Min"),
            (first.RequestedStartUtc!.Value, first.RequestedEndUtc!.Value, first.Parameters["timeframe"]));
        Assert.Equal(new string('a', 64), request.Attributes["ranked_list_sha256"]);
    }

    // On a fixture without splits the published ranked list and the in-run ranking choose the same members with the same
    // ranks on every session (both use OpeningVolumeRanking), and no entry before 09:35 New York may use the frozen list.
    [Fact]
    public async Task FrozenList_MatchesTheInRunRankingAndIsUnknownBefore0935()
    {
        using var root = new TemporaryFolder();
        var (daily, opening, sessions) = ParityMarket(["AAA", "BBB", "CCC", "DDD", "EEE"], 230);
        var from = sessions[200];
        var to = sessions[^1];
        var output = new DailyRankingOutput(Ranking, opening.ToDictionary(pair => (pair.Key.Ticker, pair.Key.Day), pair => pair.Value),
            opening.Keys.Select(key => (key.Ticker, key.Day)).ToHashSet());
        foreach (var (ticker, series) in daily)
        foreach (var (date, _) in DailyScreenCommand.Screen(ticker, series, [], Rules, from, to))
            output.Add(date, ticker, ticker, sessions);
        var folder = Path.Combine(root.Path, "parity");
        Directory.CreateDirectory(folder);
        await output.WriteAsync(folder, default);
        await ResearchScreenManifest.PublishAsync(folder, new("daily", "actions", "opening", "sessions"), Rules, Ranking, from, to,
            DailyRankingOutput.IssuerIdentityBasis, null, default);
        var frozen = PublishedResearchScreen.Read(folder).ToMembership(from, to);

        var universe = UniverseConfig.StaticUniverse with { Mode = UniverseConfig.ResolvedSnapshotMode, DailyEligibility = Rules, DailyRanking = Ranking };
        var inRun = await new HistoricalScreenerUniverseProvider(new ParityProvider(daily, opening)).ResolveMembershipAsync(
            new UniverseRequest(daily.Keys.ToArray(), universe, At(from, 9, 30), "1d", At(to, 16, 0)), default);

        static string[] Lists(UniverseMembership membership) => membership.SessionRankings.Select(session => session.Session.ToString("yyyy-MM-dd") + ":" +
            String.Join(';', session.Members.Select(member => $"{member.Rank}={member.Security.Symbol}@{decimal.Round(member.RelativeVolume, 6)}"))).ToArray();
        Assert.Equal(Lists(inRun), Lists(frozen));
        Assert.True(frozen.SessionRankings.Sum(session => session.Members.Count) > 20);
        Assert.Empty(frozen.EligibleDays("EEE"));
        foreach (var ticker in daily.Keys) Assert.Equal(inRun.EligibleDays(ticker), frozen.EligibleDays(ticker));
        var memberDay = frozen.EligibleDays("AAA")[0];
        Assert.False(frozen.IsMemberAt("AAA", At(memberDay, 9, 34)));
        Assert.True(frozen.IsMemberAt("AAA", At(memberDay, 9, 35)));
    }

    // Daily bars (midnight New York), every session's 09:30 opening volume and the session list of a split-free market.
    // EEE trades 500,000 shares a day and never passes the volume rule; the others pass and are ranked by opening volume.
    private static (Dictionary<string, OhlcvBar[]> Daily, Dictionary<(string Ticker, DateOnly Day), decimal> Opening, DateOnly[] Sessions) ParityMarket(
        string[] tickers, int count)
    {
        var sessions = new List<DateOnly>();
        for (var date = new DateOnly(2025, 11, 3); sessions.Count < count; date = date.AddDays(1))
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) sessions.Add(date);
        var daily = new Dictionary<string, OhlcvBar[]>(StringComparer.Ordinal);
        var opening = new Dictionary<(string, DateOnly), decimal>();
        for (var t = 0; t < tickers.Length; t++)
        {
            var volume = tickers[t] == "EEE" ? 500_000m : 2_000_000m + t * 10_000m;
            daily[tickers[t]] = sessions.Select((day, index) => new OhlcvBar(tickers[t], At(day, 0, 0), "1d", 20m + t, 21m + t, 19.5m + t,
                20m + t + (index % 3) * 0.1m, volume)).ToArray();
            for (var index = 0; index < sessions.Count; index++)
                opening[(tickers[t], sessions[index])] = 10_000m + (t * 7919 + index * 104729) % 5000 * 3m;
        }
        return (daily, opening, sessions.ToArray());
    }

    // A New York wall-clock time on a date, as UTC.
    private static DateTimeOffset At(DateOnly day, int hour, int minute) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(hour, minute)), NewYork), TimeSpan.Zero);

    // Serves the parity market to the in-run screen: daily bars and, per session, the 09:30 and 09:35 five-minute bars.
    private sealed class ParityProvider(IReadOnlyDictionary<string, OhlcvBar[]> daily, IReadOnlyDictionary<(string Ticker, DateOnly Day), decimal> opening)
        : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes,
            DateTimeOffset start, DateTimeOffset end, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            foreach (var ticker in tickers)
            {
                if (timeframes.Contains("1d"))
                    foreach (var bar in daily[ticker].Where(bar => bar.Timestamp >= start && bar.Timestamp <= end)) yield return bar;
                if (!timeframes.Contains("5m")) continue;
                foreach (var ((symbol, day), volume) in opening.Where(pair => pair.Key.Ticker == ticker).OrderBy(pair => pair.Key.Day))
                {
                    var open = At(day, 9, 30);
                    if (open < start || open > end) continue;
                    yield return new(symbol, open, "5m", 20m, 20.5m, 19.5m, 20m, volume);
                    yield return new(symbol, open.AddMinutes(5), "5m", 20m, 20.5m, 19.5m, 20m, volume);
                }
            }
        }
    }
}

/// <summary>Publishes a small synthetic research screen (ranked list, counts, exclusions and manifest) into a folder.</summary>
internal static class SyntheticScreen
{
    internal static readonly DailyScreenRules Rules = new(5m, DailyScreenPriceBasis.SessionOpen, 14,
        MinAverageShareVolume: 1_000_000m, AtrPeriod: 14, MinAtr: 0.5m, MinHistorySessions: 14);
    internal static readonly DailyRankingRules Ranking = new(DailyRankingRules.FirstFiveMinuteRelativeVolume, 14, VolumeBaselineAverage.Mean, 1m, 2);

    /// <summary>Writes the ranked list (rank by order within each session) and the manifest; returns the folder.</summary>
    public static async Task<string> PublishAsync(string folder, IReadOnlyList<(string Session, string Symbol, string SecurityId)> members)
    {
        Directory.CreateDirectory(folder);
        var lines = members.GroupBy(member => member.Session).SelectMany(group => group.Select((member, index) =>
            $"{member.Session},{index + 1},{member.Symbol},{member.SecurityId},alpaca_security_id,200000,100000,2,0"));
        await File.WriteAllLinesAsync(Path.Combine(folder, "ranked-daily-list.csv"),
            new[] { "session_date,rank,symbol,issuer_id,issuer_identity_basis,opening_volume,baseline_volume,relative_volume,proven_zero_baseline_sessions" }.Concat(lines));
        await File.WriteAllLinesAsync(Path.Combine(folder, "ranked-daily-counts.csv"), new[] { "session_date,eligible,qualified,selected" }
            .Concat(members.GroupBy(member => member.Session).Select(group => $"{group.Key},{group.Count()},{group.Count()},{group.Count()}")));
        await File.WriteAllLinesAsync(Path.Combine(folder, "ranked-daily-exclusions.csv"), ["session_date,symbol,reason"]);
        var dates = members.Select(member => DateOnly.Parse(member.Session)).ToArray();
        await ResearchScreenManifest.PublishAsync(folder, new("daily", "actions", "opening", "sessions"), Rules, Ranking, dates.Min(), dates.Max(),
            "alpaca_security_id", null, default);
        return folder;
    }
}

/// <summary>A uniquely named folder under the system temporary directory, deleted with everything in it on disposal.</summary>
internal sealed class TemporaryFolder : IDisposable
{
    /// <summary>Creates the folder, named <paramref name="name"/> when given (a repeated run at the same path) or uniquely.</summary>
    public TemporaryFolder(string? name = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name ?? "tf-research-screen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Deletes the folder after checking it is the one this fixture created directly under the temporary directory.</summary>
    public void Dispose()
    {
        if (System.IO.Path.GetDirectoryName(Path) != System.IO.Path.GetTempPath().TrimEnd(System.IO.Path.DirectorySeparatorChar))
            throw new InvalidOperationException("Fixture cleanup escaped the temporary directory.");
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
