namespace TradingFlow.Domain.Persistence;

/// <summary>
/// Appends immutable order intents to the operational journal before broker submission.
/// </summary>
public interface IOrderIntentRepository
{
    Task AppendAsync(OrderIntentRecord intent, CancellationToken cancellationToken = default);
}
