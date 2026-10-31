using TradingFlow.Alpaca;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Services.Desk;

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
public interface IPositionProtectionReader
{
    Task<IReadOnlyDictionary<string, PositionProtection>> GetAsync(IReadOnlyCollection<DeskHolding> holdings, CancellationToken cancellationToken);
}

public sealed class PositionProtectionService : IPositionProtectionReader
{
    private static readonly string[] StopOrderTypes =
        ["stop", "stop_limit", "stop_loss", "trailing_stop"];

    private readonly AlpacaCredentialProvider credentials;
    private readonly IRawArchiveWriter rawArchiveWriter;
    private readonly ILogger<PositionProtectionService> logger;
    private readonly IOrderIntentRepository intents;
    private readonly IHttpClientFactory clients;
    private readonly IConfiguration configuration;

    /// <summary>Reuses the bound broker transport and immutable intent journal; account holdings are supplied by the existing account reader.</summary>
    public PositionProtectionService(
        AlpacaCredentialProvider credentials,
        IRawArchiveWriter rawArchiveWriter,
        ILogger<PositionProtectionService> logger,
        IOrderIntentRepository intents,
        IHttpClientFactory clients,
        IConfiguration configuration)
    {
        this.credentials = credentials;
        this.rawArchiveWriter = rawArchiveWriter;
        this.logger = logger;
        this.intents = intents;
        this.clients = clients;
        this.configuration = configuration;
    }

    /// <summary>
    /// Protection state for the actual signed quantity in <paramref name="holdings"/>, keyed by
    /// symbol. An unreadable or incomplete order snapshot comes back with
    /// <see cref="PositionProtection.IsKnown"/> false rather than absent.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, PositionProtection>> GetAsync(
        IReadOnlyCollection<DeskHolding> holdings,
        CancellationToken cancellationToken)
    {
        var positions = holdings.Where(holding => holding.Quantity != 0)
            .Select(holding => holding with { Symbol = holding.Symbol.Trim().ToUpperInvariant() }).ToArray();
        var normalized = positions.Select(holding => holding.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
            using var client = new AlpacaBrokerClient(clients.CreateClient(), clients.CreateClient(),
                AlpacaOptions.Create(ProductionProfile.Paper) with {
                    KeyId = credentials.KeyId, SecretKey = credentials.SecretKey, MarketDataFeed = "sip"
                }, rawArchiveWriter);
            var open = await client.GetOpenOrdersAsync(cancellationToken);
            var result = new Dictionary<string, PositionProtection>(StringComparer.OrdinalIgnoreCase);
            foreach (var symbol in normalized)
            {
                var matching = positions.Where(holding => holding.Symbol == symbol).ToArray();
                result[symbol] = matching.Length != 1
                    ? new(symbol, false, false, "Duplicate broker holdings prevent a reliable protection quantity.")
                    : await AssessAsync(matching[0], open.Where(order => order.Ticker.Equals(symbol, StringComparison.OrdinalIgnoreCase)).ToArray(), cancellationToken);
            }
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Unable to read broker working orders for the protection check.");
            return Unknown(normalized, "The broker's working orders could not be read, so protection is unknown.");
        }
    }

    /// <summary>
    /// Requires closing-role evidence and the held direction, then compares executable remaining stop quantity with the full holding.
    /// </summary>
    private async Task<PositionProtection> AssessAsync(DeskHolding holding, IReadOnlyList<ActiveBrokerOrder> orders, CancellationToken token)
    {
        var closingSide = holding.Quantity > 0 ? "sell" : "buy";
        var required = Math.Abs(holding.Quantity);
        decimal covered = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var order in orders.Where(IsStopCandidate))
        {
            if (!ids.Add(order.OrderId))
                return new(holding.Symbol, false, false, "Duplicate broker stop records prevent a reliable protection quantity.");
            var role = await ResolveRoleAsync(order, holding, token);
            if (role == StopRole.Opening) continue;
            if (role == StopRole.Unknown || !order.Side.Equals(closingSide, StringComparison.OrdinalIgnoreCase))
                return new(holding.Symbol, false, false, "A broker stop's closing role or direction is unverified; protection is unknown.");
            if (!IsExecutableStatus(order.Status) || order.Qty is not { } total || total <= 0 || order.FilledQuantity < 0 || order.FilledQuantity > total)
                return new(holding.Symbol, false, false, "A closing stop is pending, inactive or has an invalid remaining quantity; protection is unknown.");
            covered += total - order.FilledQuantity;
        }
        if (covered > required)
            return new(holding.Symbol, false, false, $"Closing stops total {covered:0.####} shares for a {required:0.####}-share holding; excess coverage needs review.");
        if (covered == required)
            return new(holding.Symbol, true, true, $"Working broker closing stop covers all {required:0.####} shares.");
        return new(holding.Symbol, false, true, covered > 0
            ? $"Partial broker stop coverage: {covered:0.####} of {required:0.####} shares."
            : "No verified working closing stop covers this holding.");
    }

    /// <summary>Uses explicit broker intent or exact owned intent/parent-leg ownership; side alone never proves an order closes a position.</summary>
    private async Task<StopRole> ResolveRoleAsync(ActiveBrokerOrder order, DeskHolding holding, CancellationToken token)
    {
        var expected = holding.Quantity > 0 ? "sell_to_close" : "buy_to_close";
        if (order.PositionIntent is "buy_to_open" or "sell_to_open") return StopRole.Opening;
        if (order.PositionIntent is not null) return order.PositionIntent == expected ? StopRole.Closing : StopRole.Unknown;
        var owner = await intents.GetByClientOrderIdAsync(order.ClientOrderId, token);
        if (Owns(owner, order.Ticker) && owner!.Side.Equals(order.Side, StringComparison.OrdinalIgnoreCase))
            return owner!.Kind is OrderIntentKind.ProtectiveStop or OrderIntentKind.PositionExit ? StopRole.Closing : StopRole.Opening;
        if (!String.IsNullOrWhiteSpace(order.ParentClientOrderId))
        {
            var parent = await intents.GetByClientOrderIdAsync(order.ParentClientOrderId, token);
            if (Owns(parent, order.Ticker) && (parent!.Kind is OrderIntentKind.StrategyEntry or OrderIntentKind.OperatorEntry) &&
                !parent!.Side.Equals(order.Side, StringComparison.OrdinalIgnoreCase)) return StopRole.Closing;
        }
        return StopRole.Unknown;
    }

    /// <summary>Requires the local immutable role to belong to this exact bound account and symbol.</summary>
    private bool Owns(OrderIntentRecord? intent, string symbol) => intent is not null &&
        !String.IsNullOrWhiteSpace(configuration["Alpaca:ExpectedAccountId"]) &&
        intent.AccountId.Equals(configuration["Alpaca:ExpectedAccountId"], StringComparison.OrdinalIgnoreCase) &&
        intent.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase);

    /// <summary>Considers nonterminal broker stop records, retaining pending records as explicit unknown protection.</summary>
    private static bool IsStopCandidate(ActiveBrokerOrder order) =>
        (order.StopPrice is not null || StopOrderTypes.Contains(order.OrderType, StringComparer.OrdinalIgnoreCase)) &&
        order.Status.ToLowerInvariant() is not ("filled" or "canceled" or "cancelled" or "expired" or "rejected");

    /// <summary>Only accepted working orders count; pending and held orders are not asserted to be executable.</summary>
    private static bool IsExecutableStatus(string status) => status.ToLowerInvariant() is "new" or "accepted" or "partially_filled";

    private enum StopRole { Unknown, Opening, Closing }

    /// <summary>Preserves one explicit unknown result per requested symbol when the complete broker snapshot cannot be obtained.</summary>
    private static Dictionary<string, PositionProtection> Unknown(
        IReadOnlyList<string> tickers,
        string detail) => tickers.ToDictionary(
            ticker => ticker,
            ticker => new PositionProtection(ticker, false, false, detail),
            StringComparer.OrdinalIgnoreCase);

}
