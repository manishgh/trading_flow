using Microsoft.Extensions.Hosting;
using TradingFlow.Alpaca;
using TradingFlow.Data.News;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Web.Services.Wishlists;

/// <summary>
/// Durable server-side observer for named wishlist groups. The mobile app only
/// toggles intent; this service owns the 24x7 polling loop so monitoring
/// survives app close and naturally includes pre/post-market Alpaca bars.
/// </summary>
public sealed class WishlistObserverService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LookbackWindow = TimeSpan.FromDays(5);
    private static readonly TimeSpan NewsWindow = TimeSpan.FromHours(4);
    private static readonly TimeZoneInfo ExchangeTimeZone = ResolveExchangeTimeZone();

    private readonly IWishlistRepository wishlists;
    private readonly WishlistMarketMonitor monitor;
    private readonly SqliteNewsFeedRepository newsRepository;
    private readonly AlpacaCredentialProvider alpacaCredentials;
    private readonly IndicatorEngine indicatorEngine = new();
    private readonly ILogger<WishlistObserverService> logger;

    public WishlistObserverService(
        IWishlistRepository wishlists,
        WishlistMarketMonitor monitor,
        SqliteNewsFeedRepository newsRepository,
        AlpacaCredentialProvider alpacaCredentials,
        ILogger<WishlistObserverService> logger)
    {
        this.wishlists = wishlists;
        this.monitor = monitor;
        this.newsRepository = newsRepository;
        this.alpacaCredentials = alpacaCredentials;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ObserveOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Wishlist observer iteration failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    public async Task ObserveOnceAsync(CancellationToken cancellationToken)
    {
        var observed = (await wishlists.ListAsync(cancellationToken))
            .Where(wishlist => wishlist.IsObserved)
            .ToArray();
        if (observed.Length == 0)
        {
            return;
        }

        if (!alpacaCredentials.IsConfigured)
        {
            logger.LogWarning("Wishlist observer skipped because Alpaca credentials are not configured.");
            return;
        }

        var tickers = observed
            .SelectMany(wishlist => wishlist.Items)
            .Where(item => item.Active)
            .Select(item => item.Ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tickers.Length == 0)
        {
            return;
        }

        var end = DateTimeOffset.UtcNow;
        var start = end.Subtract(LookbackWindow);
        var provider = new AlpacaMarketDataProvider(
            new HttpClient(),
            AlpacaOptions.CreateDefault() with
            {
                KeyId = alpacaCredentials.KeyId,
                SecretKey = alpacaCredentials.SecretKey,
                MarketDataFeed = "sip"
            });

        var barsByTicker = new Dictionary<string, List<OhlcvBar>>(StringComparer.OrdinalIgnoreCase);
        await foreach (var bar in provider.GetBarsAsync(tickers, new[] { "1m" }, start, end, cancellationToken))
        {
            if (!barsByTicker.TryGetValue(bar.Ticker, out var bars))
            {
                bars = new List<OhlcvBar>();
                barsByTicker[bar.Ticker] = bars;
            }

            bars.Add(bar);
        }

        foreach (var wishlist in observed)
        {
            var snapshots = new Dictionary<string, WishlistMarketSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var ticker in wishlist.Items.Where(item => item.Active).Select(item => item.Ticker))
            {
                if (!barsByTicker.TryGetValue(ticker, out var bars) || bars.Count < 35)
                {
                    continue;
                }

                var snapshot = await BuildSnapshotAsync(ticker, bars, cancellationToken);
                if (snapshot is not null)
                {
                    snapshots[ticker] = snapshot;
                }
            }

            if (snapshots.Count == 0)
            {
                continue;
            }

            var result = await monitor.EvaluateAndPersistAlertsAsync(wishlist.Id, snapshots, cancellationToken);
            if (result.PersistedSignals.Count > 0)
            {
                logger.LogInformation(
                    "Wishlist observer persisted {SignalCount} alert(s) for {Wishlist}.",
                    result.PersistedSignals.Count,
                    wishlist.Name);
            }
        }
    }

    private async Task<WishlistMarketSnapshot?> BuildSnapshotAsync(
        string ticker,
        IReadOnlyList<OhlcvBar> inputBars,
        CancellationToken cancellationToken)
    {
        var bars = inputBars.OrderBy(bar => bar.Timestamp).ToArray();
        var snapshots = indicatorEngine.Compute(bars);
        if (snapshots.Count < 2)
        {
            return null;
        }

        var current = snapshots[^1];
        var previous = snapshots[^2];
        var catalyst = await GetLatestCatalystAsync(ticker, cancellationToken);
        if (catalyst is not null)
        {
            current = current with { Catalyst = catalyst };
        }

        var recentHigh = bars.Take(Math.Max(0, bars.Length - 1)).TakeLast(30).Select(bar => (decimal?)bar.High).Max();
        var currentSession = ToExchangeDate(current.Timestamp);
        var sessionOpen = bars
            .Where(bar => ToExchangeDate(bar.Timestamp) == currentSession)
            .OrderBy(bar => bar.Timestamp)
            .Select(bar => (decimal?)bar.Open)
            .FirstOrDefault();

        return new WishlistMarketSnapshot(ticker, current, previous, recentHigh, sessionOpen);
    }

    private async Task<CatalystEvent?> GetLatestCatalystAsync(string ticker, CancellationToken cancellationToken)
    {
        var recent = await newsRepository.GetRecentAsync(DateTimeOffset.UtcNow.Subtract(NewsWindow), 10, ticker, cancellationToken);
        var item = recent.FirstOrDefault(news => news.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase))
            ?? recent.FirstOrDefault(news => news.Ticker.Equals("MARKET", StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return null;
        }

        return new CatalystEvent(
            ticker,
            item.Timestamp,
            CatalystType.NewsReport,
            item.Headline,
            item.SentimentScore,
            Provider: item.Provider,
            Summary: item.Summary,
            Source: item.Source,
            Url: item.Url,
            ReceivedAt: item.IngestedAt);
    }

    private static DateOnly ToExchangeDate(DateTimeOffset timestamp)
    {
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, ExchangeTimeZone).DateTime);
    }

    private static TimeZoneInfo ResolveExchangeTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
    }
}
