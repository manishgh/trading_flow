using System.Globalization;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Data.Evidence.Screens;

/// <summary>
/// Optional ranking step of <see cref="ResearchDailyScreenPublisher"/>: ranks each session's daily-eligible stocks by
/// first five-minute relative volume with the shared <see cref="OpeningVolumeRanking"/> and publishes the ranked list
/// with its values. Identity is the provider security identifier carried on every bar row; Alpaca publishes no issuer
/// identifier, so the security identifier stands in for the issuer and the output records that basis. A missing opening
/// bar counts as zero volume only when a completed opening-bar collection proves that exact interval was requested.
/// </summary>
public sealed class DailyRankingOutput(
    DailyRankingRules rules,
    IReadOnlyDictionary<(string SecurityId, DateOnly Day), decimal> openingVolumes,
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
    /// Reads a published five-minute bar dataset and retains only each session's 09:30-09:35 New York bar volume,
    /// keyed by security identifier and New York trading date.
    /// </summary>
    public static async Task<Dictionary<(string SecurityId, DateOnly Day), decimal>> ReadOpeningVolumesAsync(
        ParquetEvidencePartitionDataReader reader,
        EvidenceDatasetManifest bars,
        CancellationToken cancellationToken)
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
    /// Reads the opening-bar dataset's frozen collection plan and completion checkpoint from the catalog and returns
    /// the stock-sessions whose opening 5-minute interval was requested and completed.
    /// </summary>
    public static async Task<HashSet<(string Symbol, DateOnly Day)>> ReadCollectedSessionsAsync(
        IEvidenceCatalog catalog,
        EvidenceDatasetManifest bars,
        CancellationToken cancellationToken)
    {
        var plan = await catalog.GetCollectionPlanAsync(bars.CollectionJobId, cancellationToken)
            ?? throw new InvalidDataException($"Collection plan '{bars.CollectionJobId}' of the opening-bar dataset was not found.");
        var checkpoint = await catalog.GetCollectionCheckpointAsync(bars.CollectionJobId, cancellationToken);
        return CollectedSessions(plan.Requests, checkpoint?.CompletedRequestIds ?? []);
    }

    /// <summary>
    /// Extracts upper-case (symbol, New York session) pairs covered by completed five-minute bar requests whose UTC
    /// range spans the entire 09:30-09:35 New York opening interval.
    /// </summary>
    public static HashSet<(string Symbol, DateOnly Day)> CollectedSessions(
        IEnumerable<EvidenceCollectionRequest> requests,
        IEnumerable<string> completedRequestIds)
    {
        var completed = completedRequestIds.ToHashSet(StringComparer.Ordinal);
        var sessions = new HashSet<(string, DateOnly)>();
        foreach (var request in requests)
        {
            if (!completed.Contains(request.RequestId) || request.AsOfDate is not { } day ||
                !request.Parameters.TryGetValue("timeframe", out var timeframe) || timeframe != OpeningTimeframe ||
                request.RequestedStartUtc > At(day, OpeningStart) || request.RequestedEndUtc < At(day, OpeningEnd))
            {
                continue;
            }

            foreach (var symbol in request.Symbols)
            {
                sessions.Add((symbol.ToUpperInvariant(), day));
            }
        }

        return sessions;
    }

    /// <summary>
    /// Records one daily-eligible stock on one session together with its preceding baseline opening volumes (oldest
    /// first) and the count of proven-zero baseline sessions.
    /// </summary>
    public void Add(DateOnly session, string securityId, string symbol, DateOnly[] tradedSessions)
    {
        var index = Array.BinarySearch(tradedSessions, session);
        if (index < 0)
        {
            throw new InvalidDataException($"{symbol} was screened on {session:yyyy-MM-dd} without a daily bar that day.");
        }

        var baseline = tradedSessions.Skip(Math.Max(0, index - rules.BaselineSessions)).Take(Math.Min(index, rules.BaselineSessions)).ToArray();
        if (!observations.TryGetValue(session, out var list))
        {
            observations[session] = list = [];
        }

        list.Add(new(
            new UniverseSecurityIdentity(symbol, securityId, securityId),
            Volume(securityId, symbol, session),
            baseline.Select(day => Volume(securityId, symbol, day)).ToArray(),
            baseline.Count(day => IsProvenZero(securityId, symbol, day))));
    }

    /// <summary>Ranks every recorded session and writes ranked-daily-list.csv, counts, and exclusions into <paramref name="folder"/>.</summary>
    public async Task<IReadOnlyList<SessionRanking>> WriteAsync(string folder, CancellationToken cancellationToken)
    {
        var rankings = observations.Select(pair => OpeningVolumeRanking.Rank(pair.Key, pair.Value, rules)).ToArray();
        await File.WriteAllLinesAsync(
            Path.Combine(folder, "ranked-daily-list.csv"),
            new[] { "session_date,rank,symbol,issuer_id,issuer_identity_basis,opening_volume,baseline_volume,relative_volume,proven_zero_baseline_sessions" }
                .Concat(rankings.SelectMany(session => session.Members).Select(member => String.Join(',',
                    Day(member.Session),
                    member.Rank.ToString(CultureInfo.InvariantCulture),
                    member.Security.Symbol,
                    member.Security.IssuerId,
                    IssuerIdentityBasis,
                    Text(member.OpeningVolume),
                    Text(member.BaselineVolume),
                    Text(member.RelativeVolume),
                    member.ProvenZeroBaselineSessions.ToString(CultureInfo.InvariantCulture)))),
            cancellationToken);
        await File.WriteAllLinesAsync(
            Path.Combine(folder, "ranked-daily-counts.csv"),
            new[] { "session_date,eligible,qualified,selected" }
                .Concat(rankings.Select(session => String.Join(',',
                    Day(session.Session),
                    observations[session.Session].Count.ToString(CultureInfo.InvariantCulture),
                    session.QualifiedCount.ToString(CultureInfo.InvariantCulture),
                    session.Members.Count.ToString(CultureInfo.InvariantCulture)))),
            cancellationToken);
        await File.WriteAllLinesAsync(
            Path.Combine(folder, "ranked-daily-exclusions.csv"),
            new[] { "session_date,symbol,reason" }
                .Concat(rankings.SelectMany(session => session.Rejections).Select(rejection => String.Join(',',
                    Day(rejection.Session),
                    rejection.Symbol,
                    rejection.Reason.Replace(',', ';')))),
            cancellationToken);
        return rankings;
    }

    // Returns the observed opening volume, 0 when a completed request proved no trades, or null when unproven.
    private decimal? Volume(string securityId, string symbol, DateOnly day) =>
        openingVolumes.TryGetValue((securityId, day), out var volume) ? volume : IsProvenZero(securityId, symbol, day) ? 0m : null;

    // True only when no bar exists and the completed opening-bar collection requested that exact stock-session.
    private bool IsProvenZero(string securityId, string symbol, DateOnly day) =>
        !openingVolumes.ContainsKey((securityId, day)) && collectedSessions.Contains((symbol.ToUpperInvariant(), day));

    // Converts a New York session wall-clock time to UTC using that date's exact DST offset.
    private static DateTimeOffset At(DateOnly day, TimeOnly time)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, NewYork.GetUtcOffset(local)).ToUniversalTime();
    }

    // Formats a trading date as yyyy-MM-dd in invariant culture.
    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Formats measured decimal metrics rounded to 6 decimal places in invariant culture.
    private static string Text(decimal value) => decimal.Round(value, 6).ToString(CultureInfo.InvariantCulture);
}
