using TradingFlow.Alpaca;
using TradingFlow.Domain.Orders;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Web.Services;

/// <summary>
/// Whether a stop is actually working at the broker for one open position.
///
/// The local book's <c>StopLossPrice</c> records what was intended. It does not
/// prove the bracket landed: an entry can fill while its protective leg is
/// rejected, and the position is then uncontrolled risk that the book still
/// describes as protected. This asserts the broker's own working orders instead.
/// </summary>
/// <param name="Ticker">Position symbol.</param>
/// <param name="HasBrokerStop">
/// True only when the broker reports a working stop for this symbol. Null-safe:
/// see <paramref name="IsKnown"/> - an unreadable broker is not "unprotected",
/// it is unknown, and the two must not render the same.
/// </param>
/// <param name="IsKnown">False when the broker could not be read.</param>
/// <param name="Detail">What was observed, for the row's protection cell.</param>
public sealed record PositionProtection(
    string Ticker,
    bool HasBrokerStop,
    bool IsKnown,
    string Detail);

/// <summary>
/// Reads the broker's working orders once and answers the protection question
/// per symbol. One read serves the whole book: asking per position would turn a
/// ten-row page into ten broker calls.
/// </summary>
public sealed class PositionProtectionService
{
    private static readonly string[] StopOrderTypes =
        ["stop", "stop_limit", "stop_loss", "trailing_stop"];

    private readonly AlpacaCredentialProvider credentials;
    private readonly IRawArchiveWriter rawArchiveWriter;
    private readonly ILogger<PositionProtectionService> logger;
    private readonly SemaphoreSlim providerLock = new(1, 1);
    private AlpacaBrokerClient? provider;

    public PositionProtectionService(
        AlpacaCredentialProvider credentials,
        IRawArchiveWriter rawArchiveWriter,
        ILogger<PositionProtectionService> logger)
    {
        this.credentials = credentials;
        this.rawArchiveWriter = rawArchiveWriter;
        this.logger = logger;
    }

    /// <summary>
    /// Protection state for every ticker in <paramref name="tickers"/>, keyed by
    /// ticker. A ticker the broker could not be asked about comes back with
    /// <see cref="PositionProtection.IsKnown"/> false rather than absent.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, PositionProtection>> GetAsync(
        IReadOnlyCollection<string> tickers,
        CancellationToken cancellationToken)
    {
        var normalized = tickers
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
        {
            return new Dictionary<string, PositionProtection>(StringComparer.OrdinalIgnoreCase);
        }

        if (!credentials.IsConfigured)
        {
            return Unknown(normalized, "Alpaca credentials are not configured, so the broker's working orders cannot be read.");
        }

        try
        {
            var client = await GetProviderAsync(cancellationToken);
            var open = await client.GetOpenOrdersAsync(cancellationToken);
            var stopsByTicker = open
                .Where(IsWorkingStop)
                .GroupBy(order => order.Ticker.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

            return normalized.ToDictionary(
                ticker => ticker,
                ticker => stopsByTicker.TryGetValue(ticker, out var stops)
                    ? new PositionProtection(
                        ticker,
                        true,
                        true,
                        stops.Length == 1 && stops[0].StopPrice is { } price
                            ? $"Broker stop working at {price:C2}"
                            : $"{stops.Length} protective order(s) working at the broker")
                    : new PositionProtection(ticker, false, true, "No broker stop on file"),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Unable to read broker working orders for the protection check.");
            return Unknown(normalized, "The broker's working orders could not be read, so protection is unknown.");
        }
    }

    /// <summary>
    /// A working protective leg. A filled or cancelled stop protects nothing, and
    /// a buy-side stop is an entry trigger rather than protection for a long.
    /// </summary>
    private static bool IsWorkingStop(ActiveBrokerOrder order)
    {
        if (order.StopPrice is null &&
            !StopOrderTypes.Contains(order.OrderType, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return order.Status.Equals("new", StringComparison.OrdinalIgnoreCase) ||
            order.Status.Equals("accepted", StringComparison.OrdinalIgnoreCase) ||
            order.Status.Equals("held", StringComparison.OrdinalIgnoreCase) ||
            order.Status.Equals("partially_filled", StringComparison.OrdinalIgnoreCase) ||
            order.Status.Equals("pending_new", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, PositionProtection> Unknown(
        IReadOnlyList<string> tickers,
        string detail) => tickers.ToDictionary(
            ticker => ticker,
            ticker => new PositionProtection(ticker, false, false, detail),
            StringComparer.OrdinalIgnoreCase);

    private async Task<AlpacaBrokerClient> GetProviderAsync(CancellationToken cancellationToken)
    {
        if (provider is not null)
        {
            return provider;
        }

        await providerLock.WaitAsync(cancellationToken);
        try
        {
            provider ??= new AlpacaBrokerClient(
                new HttpClient(),
                new HttpClient(),
                AlpacaOptions.Create(ProductionProfile.Paper) with
                {
                    KeyId = credentials.KeyId,
                    SecretKey = credentials.SecretKey,
                    MarketDataFeed = "sip"
                },
                rawArchiveWriter);
            return provider;
        }
        finally
        {
            providerLock.Release();
        }
    }
}
