using TradingFlow.Alpaca;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Web.Services;

public sealed record OperationalStatusSnapshot(
    string Environment,
    string MarketSession,
    string MarketSessionDetail,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset NewYorkTime,
    string QuoteStatus,
    string QuoteDetail,
    string QuoteFeed,
    string ModelStatus,
    string BrokerStatus,
    string BrokerDetail,
    string AdmissionStatus,
    string AdmissionDetail);

/// <summary>
/// Produces the operator-facing status summary from authoritative server state.
/// Alpaca calendar reads are cached by the provider, and failures are represented
/// as unknown state rather than guessed from local wall-clock hours.
/// </summary>
public sealed class OperationalStatusService : IDisposable
{
    private static readonly TimeSpan FreshQuoteAge = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StaleQuoteAge = TimeSpan.FromMinutes(2);

    private readonly AlpacaCredentialProvider credentials;
    private readonly IRawArchiveWriter rawArchiveWriter;
    private readonly IOrderSynchronizationCoordinator synchronization;
    private readonly IAccountReconciliationService reconciliation;
    private readonly IEntryAdmissionControl admission;
    private readonly MarketPredictorHttpClient marketPredictor;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<OperationalStatusService> logger;
    private readonly SemaphoreSlim providerLock = new(1, 1);
    private AlpacaBrokerClient? provider;

    public OperationalStatusService(
        AlpacaCredentialProvider credentials,
        IRawArchiveWriter rawArchiveWriter,
        IOrderSynchronizationCoordinator synchronization,
        IAccountReconciliationService reconciliation,
        IEntryAdmissionControl admission,
        MarketPredictorHttpClient marketPredictor,
        TimeProvider timeProvider,
        ILogger<OperationalStatusService> logger)
    {
        this.credentials = credentials;
        this.rawArchiveWriter = rawArchiveWriter;
        this.synchronization = synchronization;
        this.reconciliation = reconciliation;
        this.admission = admission;
        this.marketPredictor = marketPredictor;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<OperationalStatusSnapshot> GetAsync(
        string quoteFeed,
        IEnumerable<DateTimeOffset?> quoteTimestamps,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var newYorkTime = TimeZoneInfo.ConvertTime(now, ResolveNewYorkTimeZone());
        var session = await GetSessionAsync(now, cancellationToken);
        var newestQuote = quoteTimestamps
            .Where(timestamp => timestamp.HasValue)
            .Select(timestamp => timestamp!.Value.ToUniversalTime())
            .DefaultIfEmpty()
            .Max();
        var quote = DescribeQuote(now, newestQuote == default ? null : newestQuote);
        var sync = synchronization.GetHealth();
        var reconcile = reconciliation.GetHealth();
        var admissionSnapshot = admission.GetSnapshot();
        var model = await marketPredictor.GetHealthAsync(cancellationToken);

        var brokerReady = sync.StreamConnected && reconcile.InitialReconciliationCompleted &&
            String.Equals(reconcile.Status, "clean", StringComparison.OrdinalIgnoreCase);
        var brokerDetail = brokerReady
            ? "Order stream connected and account reconciliation is clean."
            : $"Stream {(sync.StreamConnected ? "connected" : "disconnected")}; reconciliation {reconcile.Status}.";
        var admissionDetail = admissionSnapshot.EntriesAllowed
            ? "New entries are allowed by the server risk gate."
            : String.Join("; ", admissionSnapshot.Blocks.Select(block => $"{block.Code}: {block.Detail}"));

        return new OperationalStatusSnapshot(
            "PAPER",
            session?.Session.ToString().ToLowerInvariant() ?? "unknown",
            session is null
                ? "Alpaca calendar unavailable; execution remains fail-closed."
                : $"Alpaca trade date {session.TradeDate:yyyy-MM-dd}.",
            now,
            newYorkTime,
            quote.Status,
            quote.Detail,
            String.IsNullOrWhiteSpace(quoteFeed) ? "unknown" : quoteFeed.Trim().ToUpperInvariant(),
            model.Status,
            brokerReady ? "ready" : "attention",
            brokerDetail,
            admissionSnapshot.EntriesAllowed ? "allowed" : "blocked",
            admissionDetail);
    }

    public void Dispose()
    {
        provider?.Dispose();
        providerLock.Dispose();
    }

    private async Task<TradingSessionSnapshot?> GetSessionAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!credentials.IsConfigured)
        {
            return null;
        }

        try
        {
            var client = await GetProviderAsync(cancellationToken);
            return await client.GetSessionAsync(now, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Unable to read the Alpaca trading calendar for the operational status strip.");
            return null;
        }
    }

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

    private static (string Status, string Detail) DescribeQuote(
        DateTimeOffset now,
        DateTimeOffset? newestQuote)
    {
        if (newestQuote is null)
        {
            return ("disconnected", "No quote has been received for the selected wishlist.");
        }

        var age = now - newestQuote.Value;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age <= FreshQuoteAge)
        {
            return ("connected", $"Newest quote is {FormatAge(age)} old.");
        }

        return age <= StaleQuoteAge
            ? ("delayed", $"Newest quote is {FormatAge(age)} old.")
            : ("stale", $"Newest quote is {FormatAge(age)} old; trading actions require a fresh server validation.");
    }

    private static string FormatAge(TimeSpan age) => age.TotalSeconds < 60
        ? $"{Math.Max(0, (int)Math.Floor(age.TotalSeconds))}s"
        : $"{Math.Max(1, (int)Math.Floor(age.TotalMinutes))}m";

    private static TimeZoneInfo ResolveNewYorkTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }
}
