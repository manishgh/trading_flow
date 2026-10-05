using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TradingFlow.Domain.Audit;

public interface IDecisionAuditRepository
{
    Task SaveAuditAsync(DecisionAuditRecord record, CancellationToken cancellationToken);
    Task<IReadOnlyList<DecisionAuditRecord>> GetAuditsByRunNameAsync(string runName, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of decision records for a run, newest first, applying the
    /// filters in the database rather than in the caller.
    /// </summary>
    /// <remarks>
    /// A long backtest can emit hundreds of thousands of decisions. Materialising all
    /// of them to filter and page in memory is what makes the audit screen unusable at
    /// scale, so the query is pushed down instead.
    /// </remarks>
    /// <param name="runName">Run whose decisions are read.</param>
    /// <param name="decision">Optional exact decision filter, e.g. Accepted.</param>
    /// <param name="ticker">Optional ticker substring filter.</param>
    /// <param name="skip">Records to skip.</param>
    /// <param name="take">Maximum records to return.</param>
    Task<DecisionAuditPage> GetAuditPageAsync(
        string runName,
        string? decision,
        string? ticker,
        int skip,
        int take,
        CancellationToken cancellationToken);

    Task<DecisionAuditPage> QueryAsync(string runName, DecisionAuditQuery query, CancellationToken cancellationToken);
    Task<DecisionAuditSummary> SummarizeAsync(string runName, CancellationToken cancellationToken);
}

public sealed record DecisionAuditQuery(string? Decision = null, string? Ticker = null,
    string? Reason = null, string Sort = "time", bool Descending = true,
    int Skip = 0, int Take = 50, long? ThroughId = null);

public sealed record DecisionAuditReasonCount(string Key, string Example, int Count);

/// <summary>Bounded full-run aggregates, independent of the displayed page.</summary>
public sealed record DecisionAuditSummary(int Total, int Accepted, int Rejected,
    int Tickers, int Strategies, int ReasonCount, long LastId,
    IReadOnlyList<DecisionAuditReasonCount> TopReasons);

/// <summary>One page of decision records plus the total the filters matched.</summary>
/// <param name="Records">The records on this page, newest first.</param>
/// <param name="TotalMatching">Total records matching the filters across every page.</param>
public sealed record DecisionAuditPage(
    IReadOnlyList<DecisionAuditRecord> Records,
    int TotalMatching);
