using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Engine.Universe;

/// <summary>
/// One daily-eligible stock on one session: its first five-minute share volume (09:30-09:35 New York) and the same
/// interval's volume on its earlier sessions, oldest first. A missing opening bar counts as zero only when the caller proved
/// the stock had no trade in that interval; a missing bar that is not proven is null, and the stock is then not ranked that
/// session (<see cref="OpeningVolumeRanking.CoverageMissing"/>). <see cref="ProvenZeroBaselineSessions"/> counts the earlier
/// values that are such proven zeros, so the published list shows how much of each baseline rests on them.
/// </summary>
public sealed record OpeningVolumeObservation(
    UniverseSecurityIdentity Security,
    decimal? OpeningVolume,
    IReadOnlyList<decimal?> PriorOpeningVolumes,
    int ProvenZeroBaselineSessions = 0);

/// <summary>
/// A selected stock with the values that ranked it, published so each session's list can be checked, including how many of
/// its baseline sessions were proven zero-volume (no opening bar, and no trade in that interval).
/// </summary>
public sealed record RankedSessionMember(
    DateOnly Session,
    int Rank,
    UniverseSecurityIdentity Security,
    decimal OpeningVolume,
    decimal BaselineVolume,
    decimal RelativeVolume,
    int ProvenZeroBaselineSessions = 0);

/// <summary>A daily-eligible stock left out of a session's list, with the concrete reason.</summary>
public sealed record SessionRankRejection(DateOnly Session, string Symbol, string Reason);

/// <summary>One session's ranked list: the members in rank order, how many stocks met the minimum, and every exclusion.</summary>
public sealed record SessionRanking(
    DateOnly Session,
    IReadOnlyList<RankedSessionMember> Members,
    int QualifiedCount,
    IReadOnlyList<SessionRankRejection> Rejections);

/// <summary>
/// Ranks one session's daily-eligible stocks by first five-minute relative volume (Zarattini, Barbon and Aziz): the
/// session's opening volume over the average opening volume of the previous sessions, by the shared
/// <see cref="RelativeVolume"/> calculation. Stocks below the minimum are dropped; the highest ratios become members, one
/// listing per issuer, ties to the lower issuer identifier. Uses nothing later than 09:35 New York on the session.
/// </summary>
public static class OpeningVolumeRanking
{
    /// <summary>
    /// Exclusion reason when an opening bar the ranking needs (the session's own or one of its baseline sessions) is missing
    /// and the data does not prove the stock had no trade in that interval.
    /// </summary>
    public const string CoverageMissing = "opening_bar_coverage_missing";

    /// <summary>Builds the session's ranked list; inputs are already restricted to stocks that passed the daily rules.</summary>
    public static SessionRanking Rank(DateOnly session, IEnumerable<OpeningVolumeObservation> observations, DailyRankingRules rules)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();
        var rejections = new List<SessionRankRejection>();
        var measured = new Dictionary<string, (OpeningVolumeObservation Observation, decimal Baseline, decimal Ratio)>(StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            var (baseline, ratio, rejection) = Measure(observation, rules);
            if (rejection is not null) rejections.Add(new(session, observation.Security.Symbol, rejection));
            else measured[observation.Security.SecurityKey] = (observation, baseline, ratio);
        }

        var ranking = new DeterministicUniverseRanker(tiesByIssuer: true).Rank(
            measured.Values.Select(item => new UniverseRankingCandidate(item.Observation.Security, item.Ratio, [rules.Measure])),
            rules.MaxMembersPerSession);
        var members = ranking.Ranked.Select(ranked =>
        {
            var item = measured[ranked.Candidate.Security.SecurityKey];
            return new RankedSessionMember(session, ranked.Rank, ranked.Candidate.Security, item.Observation.OpeningVolume!.Value,
                item.Baseline, item.Ratio, item.Observation.ProvenZeroBaselineSessions);
        }).ToArray();
        rejections.AddRange(ranking.Exclusions.Select(exclusion => new SessionRankRejection(session, exclusion.Candidate.Security.Symbol,
            exclusion.Reason == "outside_member_limit" ? $"rank_outside_top_{rules.MaxMembersPerSession}" : exclusion.Reason)));
        return new SessionRanking(session, members, measured.Count,
            rejections.OrderBy(rejection => rejection.Symbol, StringComparer.Ordinal).ToArray());
    }

    // Relative volume of one stock, or the reason it cannot be ranked: an unproven missing opening bar on the session or in
    // its baseline, too few earlier sessions, a zero baseline, or a ratio below the minimum. Only the most recent configured
    // number of earlier sessions is averaged, and an unproven value there is never replaced by zero.
    private static (decimal Baseline, decimal Ratio, string? Rejection) Measure(OpeningVolumeObservation observation, DailyRankingRules rules)
    {
        if (observation.OpeningVolume is not { } opening) return (0m, 0m, CoverageMissing);
        var prior = observation.PriorOpeningVolumes;
        if (prior.Count < rules.BaselineSessions)
            return (0m, 0m, $"opening_volume_history {prior.Count} < {rules.BaselineSessions} sessions");
        var window = prior.Skip(prior.Count - rules.BaselineSessions).ToArray();
        if (window.Any(volume => volume is null)) return (0m, 0m, CoverageMissing);
        var baseline = RelativeVolume.Baseline(window.Select(volume => volume!.Value).ToArray(), rules.BaselineAverage);
        if (RelativeVolume.Ratio(opening, baseline) is not { } ratio)
            return (baseline, 0m, "opening_volume_baseline_zero");
        return ratio < rules.MinRelativeVolume
            ? (baseline, ratio, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"relative_volume {ratio:F4} < min {rules.MinRelativeVolume:F4}"))
            : (baseline, ratio, null);
    }
}
