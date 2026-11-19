using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Universe;
using SharedDailyRankingOutput = TradingFlow.Data.Evidence.Screens.DailyRankingOutput;

namespace TradingFlow.Cli;

/// <summary>
/// Thin CLI facade over <see cref="SharedDailyRankingOutput"/> preserving existing CLI and test references without
/// duplicating opening-volume ranking logic.
/// </summary>
public sealed class DailyRankingOutput(
    DailyRankingRules rules,
    IReadOnlyDictionary<(string SecurityId, DateOnly Day), decimal> openingVolumes,
    IReadOnlySet<(string Symbol, DateOnly Day)> collectedSessions)
{
    public const string IssuerIdentityBasis = SharedDailyRankingOutput.IssuerIdentityBasis;
    private readonly SharedDailyRankingOutput inner = new(rules, openingVolumes, collectedSessions);

    /// <summary>The ranking rules this output applies, recorded in the screen manifest.</summary>
    public DailyRankingRules Rules => inner.Rules;

    /// <summary>Reads 09:30-09:35 New York opening bar volumes from a published five-minute bar dataset.</summary>
    public static Task<Dictionary<(string SecurityId, DateOnly Day), decimal>> ReadOpeningVolumesAsync(
        ParquetEvidencePartitionDataReader reader,
        EvidenceDatasetManifest bars,
        CancellationToken cancellationToken) =>
        SharedDailyRankingOutput.ReadOpeningVolumesAsync(reader, bars, cancellationToken);

    /// <summary>Reads completed opening-bar stock-sessions from the dataset's collection plan and checkpoint.</summary>
    public static Task<HashSet<(string Symbol, DateOnly Day)>> ReadCollectedSessionsAsync(
        SqliteEvidenceCatalog catalog,
        EvidenceDatasetManifest bars,
        CancellationToken cancellationToken) =>
        SharedDailyRankingOutput.ReadCollectedSessionsAsync(catalog, bars, cancellationToken);

    /// <summary>Extracts completed 09:30-09:35 New York stock-sessions from collection requests and checkpoint IDs.</summary>
    public static HashSet<(string Symbol, DateOnly Day)> CollectedSessions(
        IEnumerable<EvidenceCollectionRequest> requests,
        IEnumerable<string> completedRequestIds) =>
        SharedDailyRankingOutput.CollectedSessions(requests, completedRequestIds);

    /// <summary>Records one daily-eligible stock on one session for opening-volume ranking.</summary>
    public void Add(DateOnly session, string securityId, string symbol, DateOnly[] tradedSessions) =>
        inner.Add(session, securityId, symbol, tradedSessions);

    /// <summary>Ranks every recorded session and writes ranked-daily-list.csv, counts, and exclusions.</summary>
    public Task<IReadOnlyList<SessionRanking>> WriteAsync(string folder, CancellationToken cancellationToken) =>
        inner.WriteAsync(folder, cancellationToken);
}
