using System.Threading;
using System.Threading.Tasks;

namespace TradingFlow.Domain.Orders;

public interface IOrderStateRepository
{
    Task SaveOrderAsync(PersistedOrder order, CancellationToken cancellationToken);
    Task UpdateOrderStatusAsync(string orderId, string status, CancellationToken cancellationToken);
    Task<IReadOnlyList<PersistedOrder>> GetActiveOrdersByTickerAsync(string ticker, CancellationToken cancellationToken);
    Task<PersistedOrder?> GetOrderAsync(string orderId, CancellationToken cancellationToken);
}
