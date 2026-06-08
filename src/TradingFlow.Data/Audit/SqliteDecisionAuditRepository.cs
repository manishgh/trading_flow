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
}
