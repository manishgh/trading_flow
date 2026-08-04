using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Audit;

namespace TradingFlow.Data.Audit;

public sealed class SqliteDecisionAuditRepository : IDecisionAuditRepository
{
    private readonly IDbContextFactory<TradingFlowDbContext> _contextFactory;

    public SqliteDecisionAuditRepository(IDbContextFactory<TradingFlowDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task SaveAuditAsync(DecisionAuditRecord record, CancellationToken cancellationToken)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.DecisionAudits.Add(record);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DecisionAuditRecord>> GetAuditsByRunNameAsync(string runName, CancellationToken cancellationToken)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var records = await context.DecisionAudits
            .Where(x => x.RunName == runName)
            .ToListAsync(cancellationToken);
        return records.OrderByDescending(x => x.Timestamp).ToList();
    }

    public async Task<DecisionAuditPage> GetAuditPageAsync(
        string runName,
        string? decision,
        string? ticker,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.DecisionAudits.Where(x => x.RunName == runName);

        if (!String.IsNullOrWhiteSpace(decision))
        {
            var normalized = decision.Trim();
            query = query.Where(x => x.Decision == normalized);
        }

        if (!String.IsNullOrWhiteSpace(ticker))
        {
            var normalized = ticker.Trim();
            query = query.Where(x => x.Ticker.Contains(normalized));
        }

        // Counted before paging so the operator sees how many the filters matched, not
        // just how many fit on the page.
        var total = await query.CountAsync(cancellationToken);
        var records = await query
            .OrderByDescending(x => x.Timestamp)
            .Skip(Math.Max(skip, 0))
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken);
        return new DecisionAuditPage(records, total);
    }
}
