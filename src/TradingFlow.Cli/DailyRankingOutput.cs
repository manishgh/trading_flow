using System.Globalization;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Cli;

/// <summary>
/// Optional ranking step of <see cref="DailyScreenCommand"/>: ranks each session's daily-eligible stocks by first five-minute
/// relative volume with the shared <see cref="OpeningVolumeRanking"/> and publishes the ranked list with its values.
/// Identity is the provider security identifier carried on every bar row; Alpaca publishes no issuer identifier, so the
/// security identifier stands in for the issuer and the output says so. A missing opening bar counts as zero volume only
/// for a stock-session in <c>collectedSessions</c>: the opening-bar collection asked Alpaca for exactly that
/// interval and recorded the request as completed, and Alpaca omits an interval without trades. Any other missing bar is
/// unproven and excludes the stock for that session as opening_bar_coverage_missing.
/// </summary>
public sealed class DailyRankingOutput(DailyRankingRules rules, IReadOnlyDictionary<(string SecurityId, DateOnly Day), decimal> openingVolumes,
    IReadOnlySet<(string Symbol, DateOnly Day)> collectedSessions)
{
    public const string IssuerIdentityBasis = "alpaca_security_id";
    private const string OpeningTimeframe = "5Min";
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeOnly OpeningStart = new(9, 30);
    private static readonly TimeOnly OpeningEnd = new(9, 30 + DailyRankingRules.OpeningMinutes);
    private readonly SortedDictionary<DateOnly, List<OpeningVolumeObservation>> observations = [];

    /// <summary>The ranking rules this output applies, recorded in the screen manifest.</summary>
    public DailyRankingRules Rules => rules;

    /// <summary>
    /// Reads a published five-minute bar dataset and keeps only each session's 09:30-09:35 New York bar volume, keyed by
    /// security and New York date. The opening-bar collection stores one partition per session and batch, so one stock
    /// appears in many partitions; the whole-dataset read checks that every bar is unique across all of them.
    /// </summary>
    public static async Task<Dictionary<(string SecurityId, DateOnly Day), decimal>> ReadOpeningVolumesAsync(
        ParquetEvidencePartitionDataReader reader, EvidenceDatasetManifest bars, CancellationToken cancellationToken)
    {
        var volumes = new Dictionary<(string, DateOnly), decimal>();
        foreach (var row in await reader.ReadMarketBarsAsync(bars, cancellationToken))
        {
            var local = TimeZoneInfo.ConvertTime(row.BarStartUtc, NewYork);
            if (row.Timeframe != "5m" || local.TimeOfDay != OpeningStart.ToTimeSpan()) continue;
            volumes[(row.SecurityId, DateOnly.FromDateTime(local.DateTime))] = row.Volume;
        }

        return volumes;
    }

    /// <summary>
    /// Reads, from the catalog, the opening-bar dataset's frozen collection plan and its completion checkpoint, and returns
    /// the stock-sessions whose opening interval was requested and completed (see <see cref="CollectedSessions"/>). A dataset
    /// without a checkpoint proves nothing, so every missing bar stays unproven.
    /// </summary>
    public static async Task<HashSet<(string Symbol, DateOnly Day)>> ReadCollectedSessionsAsync(SqliteEvidenceCatalog catalog,
        EvidenceDatasetManifest bars, CancellationToken cancellationToken)
    {
        var plan = await catalog.GetCollectionPlanAsync(bars.CollectionJobId, cancellationToken)
            ?? throw new InvalidDataException($"Collection plan '{bars.CollectionJobId}' of the opening-bar dataset was not found.");
        var checkpoint = await catalog.GetCollectionCheckpointAsync(bars.CollectionJobId, cancellationToken);
        return CollectedSessions(plan.Requests, checkpoint?.CompletedRequestIds ?? []);
    }

    /// <summary>
    /// Stock-sessions (upper-case symbol, New York date) covered by a completed five-minute bar request whose session date is
    /// that day and whose UTC range contains the whole 09:30-09:35 New York interval. Requests not completed, of another
    /// timeframe, or without a session date prove nothing.
    /// </summary>
    public static HashSet<(string Symbol, DateOnly Day)> CollectedSessions(IEnumerable<EvidenceCollectionRequest> requests,
        IEnumerable<string> completedRequestIds)
    {
        var completed = completedRequestIds.ToHashSet(StringComparer.Ordinal);
        var sessions = new HashSet<(string, DateOnly)>();
        foreach (var request in requests)
        {
            if (!completed.Contains(request.RequestId) || request.AsOfDate is not { } day ||
                !request.Parameters.TryGetValue("timeframe", out var timeframe) || timeframe != OpeningTimeframe ||
                request.RequestedStartUtc > At(day, OpeningStart) || request.RequestedEndUtc < At(day, OpeningEnd)) continue;
            foreach (var symbol in request.Symbols) sessions.Add((symbol.ToUpperInvariant(), day));
        }

        return sessions;
    }

    /// <summary>
    /// Records one daily-eligible stock on one session with the opening volumes of its earlier trading sessions (from its
    /// own daily bars, oldest first), each proven or null, and how many of those earlier values are proven zeros.
    /// </summary>
    public void Add(DateOnly session, string securityId, string symbol, DateOnly[] tradedSessions)
    {
        var index = Array.BinarySearch(tradedSessions, session);
        if (index < 0) throw new InvalidDataException($"{symbol} was screened on {session:yyyy-MM-dd} without a daily bar that day.");
        var baseline = tradedSessions.Skip(Math.Max(0, index - rules.BaselineSessions)).Take(Math.Min(index, rules.BaselineSessions)).ToArray();
        if (!observations.TryGetValue(session, out var list)) observations[session] = list = [];
        list.Add(new(new UniverseSecurityIdentity(symbol, securityId, securityId), Volume(securityId, symbol, session),
            baseline.Select(day => Volume(securityId, symbol, day)).ToArray(), baseline.Count(day => IsProvenZero(securityId, symbol, day))));
    }

    /// <summary>The opening volume of the stock on the day: the bar's volume, zero when proven without a trade, else null.</summary>
    private decimal? Volume(string securityId, string symbol, DateOnly day) =>
        openingVolumes.TryGetValue((securityId, day), out var volume) ? volume : IsProvenZero(securityId, symbol, day) ? 0m : null;

    /// <summary>True when the opening bar is missing and the collection completed a request for exactly that interval.</summary>
    private bool IsProvenZero(string securityId, string symbol, DateOnly day) =>
        !openingVolumes.ContainsKey((securityId, day)) && collectedSessions.Contains((symbol.ToUpperInvariant(), day));

    /// <summary>Ranks every recorded session and writes the ranked list, per-session counts and every exclusion reason.</summary>
    public async Task<IReadOnlyList<SessionRanking>> WriteAsync(string folder, CancellationToken cancellationToken)
    {
        var rankings = observations.Select(pair => OpeningVolumeRanking.Rank(pair.Key, pair.Value, rules)).ToArray();
        await File.WriteAllLinesAsync(Path.Combine(folder, "ranked-daily-list.csv"),
            new[] { "session_date,rank,symbol,issuer_id,issuer_identity_basis,opening_volume,baseline_volume,relative_volume,proven_zero_baseline_sessions" }
                .Concat(rankings.SelectMany(session => session.Members).Select(member => String.Join(',', Day(member.Session),
                    member.Rank.ToString(CultureInfo.InvariantCulture), member.Security.Symbol, member.Security.IssuerId, IssuerIdentityBasis,
                    Text(member.OpeningVolume), Text(member.BaselineVolume), Text(member.RelativeVolume),
                    member.ProvenZeroBaselineSessions.ToString(CultureInfo.InvariantCulture)))), cancellationToken);
        await File.WriteAllLinesAsync(Path.Combine(folder, "ranked-daily-counts.csv"),
            new[] { "session_date,eligible,qualified,selected" }.Concat(rankings.Select(session => String.Join(',', Day(session.Session),
                observations[session.Session].Count.ToString(CultureInfo.InvariantCulture),
                session.QualifiedCount.ToString(CultureInfo.InvariantCulture), session.Members.Count.ToString(CultureInfo.InvariantCulture)))),
            cancellationToken);
        await File.WriteAllLinesAsync(Path.Combine(folder, "ranked-daily-exclusions.csv"),
            new[] { "session_date,symbol,reason" }.Concat(rankings.SelectMany(session => session.Rejections)
                .Select(rejection => String.Join(',', Day(rejection.Session), rejection.Symbol, rejection.Reason.Replace(',', ';')))),
            cancellationToken);
        return rankings;
    }

    /// <summary>A New York wall-clock time on a session as UTC, honouring daylight saving time.</summary>
    private static DateTimeOffset At(DateOnly day, TimeOnly time)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, NewYork.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>The published spelling of a session date.</summary>
    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The published spelling of a measured value, rounded to six decimals.</summary>
    private static string Text(decimal value) => decimal.Round(value, 6).ToString(CultureInfo.InvariantCulture);
}
