using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Backtesting;

// Point-in-time universe helpers for BacktestRunner. Split into a partial file so the runner's
// orchestration stays readable; behavior is identical to when these lived inline. The as-of and
// per-day screening logic itself lives in the Engine (HistoricalScreenerUniverseProvider).
public sealed partial class BacktestRunner
{
    private static string ResolveDailyTimeframe(BacktestRunConfig run)
    {
        return run.Intervals.FirstOrDefault(interval =>
            interval.Equals("1d", StringComparison.OrdinalIgnoreCase) ||
            interval.Equals("1day", StringComparison.OrdinalIgnoreCase) ||
            interval.Equals("1D", StringComparison.Ordinal)) ?? "1d";
    }

    // Broad candidate pool from a Finviz Elite screener export. Intended for structural/liquidity
    // screens (price, volume, market cap, sector) so the pool is not hand-picked. Note: Finviz
    // returns a current screen with no as-of, so for historical backtests this carries delisting
    // survivorship at the candidate level; the no-lookahead screen still governs selection.
    private async Task<IReadOnlyList<string>> ResolveFinvizCandidatePoolAsync(
        UniverseConfig universe,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(universe.CandidateScreenerQuery))
        {
            throw new InvalidOperationException(
                "universe.candidate_source=finviz requires universe.candidate_screener_query (a Finviz screener URL or filter string).");
        }

        using var httpClient = new HttpClient();
        using var client = new TradingFlow.Finviz.FinvizClient(
            httpClient,
            TradingFlow.Finviz.FinvizOptions.CreateDefault() with
            {
                AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? String.Empty
            },
            RequireRawArchiveWriter());

        var tickers = await client.GetScreenerTickersAsync(universe.CandidateScreenerQuery, cancellationToken);
        if (tickers.Count == 0)
        {
            throw new InvalidOperationException(
                "Finviz screener returned zero candidates. Check universe.candidate_screener_query and FINVIZ_API_KEY.");
        }

        return tickers;
    }

    /// <summary>
    /// For a saved plan's fixed stock list with daily eligibility rules: decides, for every session in the window, which
    /// stocks qualified using only completed earlier sessions (and that session's open when the rules use it). The stock
    /// list itself is unchanged; trades on a day a stock did not qualify are refused as universe_membership_missing.
    /// Publishes each stock's eligible days with the run results so every refusal can be checked. With a daily ranking the
    /// eligible days are each session's ranked members, and the ranked list with its values is published as well.
    /// </summary>
    private async Task<UniverseMembership> ResolveDailyEligibilityAsync(BacktestRunConfig run, UniverseConfig universe,
        CancellationToken cancellationToken, IProgress<BacktestProgress>? progress)
    {
        var (_, evaluationStart, windowEnd) = ResolveWindow(run.TimeWindow);
        var intraday = ExecutionReplayPolicy.IsIntradayCandle(run.Engine.ExecutionReplayPolicyId);
        universe.DailyEligibility!.RequireKnownBeforeEntry(!intraday);
        // The ranking is known at 09:35 New York; only the intraday replay checks each entry's time against that.
        if (universe.DailyRanking is not null && !intraday)
            throw new InvalidOperationException("universe.daily_ranking is known only at 09:35 New York and needs an intraday candle replay policy.");
        progress?.Report(BacktestProgress.StageOnly("resolving_universe",
            $"Checking each stock's daily eligibility from earlier sessions, {evaluationStart:yyyy-MM-dd} to {windowEnd:yyyy-MM-dd}."));
        var screener = new TradingFlow.Engine.Universe.HistoricalScreenerUniverseProvider(CreateProvider(run));
        var membership = await screener.ResolveMembershipAsync(new UniverseRequest(run.Tickers,
            universe with { Candidates = [] }, evaluationStart, ResolveDailyTimeframe(run), windowEnd), cancellationToken);
        var eligibleDays = run.Tickers.ToDictionary(ticker => ticker, ticker => membership.EligibleDays(ticker)
            .Select(day => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).ToArray(), StringComparer.OrdinalIgnoreCase);
        object payload = universe.DailyRanking is { } ranking
            ? new { runName = run.RunName, rule = universe.DailyEligibility, ranking, issuerIdentityBasis = "symbol",
                eligibleDays, rankedSessions = membership.SessionRankings.Select(PublishedSessionRanking).ToArray() }
            : new { runName = run.RunName, rule = universe.DailyEligibility, eligibleDays };
        await _artifactWriter.WriteTextAsync(Path.Combine(run.ResultsRoot, "universe", $"{run.RunName}-daily-eligibility.json"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                // Price basis is published as prior_close / session_open, the same spelling as the run settings.
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
            }), cancellationToken);
        progress?.Report(BacktestProgress.StageOnly("resolved_universe",
            $"Daily eligibility: {membership.Tickers.Count} of {run.Tickers.Count} stock(s) qualified on at least one day."));
        return membership;
    }

    // One session of the published ranked list: members with rank, measured values and how many baseline sessions were
    // proven zero-volume, the count that met the minimum, and each excluded stock's reason.
    private static object PublishedSessionRanking(TradingFlow.Engine.Universe.SessionRanking session) => new
    {
        session = session.Session.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        qualified = session.QualifiedCount,
        members = session.Members.Select(member => new
        {
            member.Rank, symbol = member.Security.Symbol, issuerId = member.Security.IssuerId,
            member.OpeningVolume, member.BaselineVolume, member.RelativeVolume, member.ProvenZeroBaselineSessions
        }).ToArray(),
        rejections = session.Rejections.Select(rejection => new { symbol = rejection.Symbol, reason = rejection.Reason }).ToArray()
    };

    private async Task PersistUniverseSnapshotAsync(
        BacktestRunConfig run,
        UniverseResolution resolution,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(run.ResultsRoot, "universe");
        var path = Path.Combine(directory, $"{resolution.AsOfDate:yyyyMMdd}-{run.RunName}.json");
        var payload = new
        {
            runName = run.RunName,
            asOfDate = resolution.AsOfDate.ToString("yyyy-MM-dd"),
            source = resolution.Source,
            rule = run.Universe,
            selected = resolution.Selected,
            rejections = resolution.Rejections
        };
        await _artifactWriter.WriteTextAsync(
            path,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
    }
}
