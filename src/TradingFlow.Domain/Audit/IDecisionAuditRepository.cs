using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TradingFlow.Domain.Audit;

public interface IDecisionAuditRepository
{
    Task SaveAuditAsync(DecisionAuditRecord record, CancellationToken cancellationToken);
    Task<IReadOnlyList<DecisionAuditRecord>> GetAuditsByRunNameAsync(string runName, CancellationToken cancellationToken);
}
