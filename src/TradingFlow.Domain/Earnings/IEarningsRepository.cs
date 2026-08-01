namespace TradingFlow.Domain.Earnings;

public interface IEarningsRepository
{
    Task ReplaceProviderWindowAsync(
        string provider,
        DateOnly fromExchangeDate,
        DateOnly toExchangeDate,
        IReadOnlyCollection<EarningsCalendarEvent> events,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EarningsCalendarEvent>> GetCalendarAsync(
        DateOnly fromExchangeDate,
        DateOnly toExchangeDate,
        IReadOnlyCollection<string>? tickers,
        CancellationToken cancellationToken);

    Task UpsertAnalysisAsync(EarningsAnalysisSnapshot snapshot, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, EarningsAnalysisSnapshot>> GetLatestAnalysesAsync(
        IReadOnlyCollection<string> eventIds,
        CancellationToken cancellationToken);
}
