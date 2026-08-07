namespace TradingFlow.Domain.Earnings;

public interface IEarningsRepository
{
    Task ReplaceProviderWindowAsync(
        string provider,
        DateOnly fromExchangeDate,
        DateOnly toExchangeDate,
        IReadOnlyCollection<EarningsCalendarEvent> events,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies results parsed from a structured news headline to the calendar event the article
    /// belongs to. Providers do not publish after-close actuals on the evening of the release, so
    /// this is the only same-session result path. Only values the calendar is still missing are
    /// written, and the provider estimate is always left in place so the surprise stays consistent.
    /// Returns true when the event was found and at least one value was filled in.
    /// </summary>
    Task<bool> TryApplyNewsResultAsync(
        string ticker,
        DateTimeOffset observedAtUtc,
        EarningsNewsResult result,
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
