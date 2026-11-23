using TradingFlow.Application.Preparation;
using TradingFlow.Contracts.Preparation;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Engine.Market;
using TradingFlow.Web.Services.Desk;

namespace TradingFlow.Web.Services;

/// <summary>
/// Settings of the daily options readings (step 4 of docs/design-review/2026-11-23/vix-and-options-plan.md). Off unless the
/// host sets <c>TRADINGFLOW_ENABLE_OPTIONS_DAILY=true</c> with market feeds and Alpaca keys; never in UI fixture hosts.
/// <paramref name="ScreenName"/> names the published research screen whose stocks are read besides the wishlists. From
/// <paramref name="RetryUntilNewYork"/> on, failed stocks are not asked again; a first pass (site down at the run time) still runs.
/// </summary>
public sealed record OptionsDailyCollectionOptions(
    bool Requested, bool MarketFeedsEnabled, bool CredentialAvailable, bool FixtureHost, string? ScreenName, TimeOnly RunAtNewYork,
    TimeOnly RetryUntilNewYork)
{
    public bool Enabled => Requested && MarketFeedsEnabled && CredentialAvailable && !FixtureHost;
}

/// <summary>
/// Decides once per pass whether today's New York trading session is due (at or after <see cref="OptionsDailyCollectionOptions.RunAtNewYork"/>)
/// and, if so, stores the readings of the wishlist and research-screen stocks through <see cref="OptionsDailyCollector"/>,
/// passing each stock's regular-session close (close of the last 5-minute bar before the session close) as the price input.
/// Stocks that failed are asked again on later passes, at least <see cref="RetrySpacing"/> apart, until each has used its
/// attempts or <see cref="OptionsDailyCollectionOptions.RetryUntilNewYork"/> (re-asks only). Market holidays and weekends request nothing.
/// A missed day is not collected later: a reading taken on another day would not describe that session.
/// </summary>
public sealed class OptionsDailyCollectionScheduler(
    OptionsDailyCollectionOptions options,
    IPreparationCalendar calendar,
    IWishlistRepository wishlists,
    ResearchScreenSources screens,
    Lazy<IMarketDataProvider> marketData,
    OptionsDailyCollector collector,
    ProjectPaths paths,
    TimeProvider clock,
    ILogger<OptionsDailyCollectionScheduler> logger)
{
    /// <summary>Minimum time between a pass that left failed stocks and the next attempt for them.</summary>
    public static readonly TimeSpan RetrySpacing = TimeSpan.FromMinutes(30);

    private DateOnly? finishedSession;
    private DateOnly? attemptedSession;
    private DateTimeOffset? retryNotBeforeUtc;

    /// <summary>Runs today's pass when due; returns the pass result, or null when nothing was due.</summary>
    public async Task<OptionsDailyCollectionResult?> RunOnceAsync(CancellationToken token)
    {
        var utcNow = clock.GetUtcNow();
        var now = TimeZoneInfo.ConvertTime(utcNow, PreparationCalendarAdapter.Exchange);
        var today = DateOnly.FromDateTime(now.DateTime);
        var timeOfDay = TimeOnly.FromDateTime(now.DateTime);
        if (finishedSession == today || timeOfDay < options.RunAtNewYork || utcNow < retryNotBeforeUtc)
            return null;
        var session = await LoadSessionAsync(today, token);
        if (!session.TradingDay || (attemptedSession == today && timeOfDay >= options.RetryUntilNewYork))
        {
            finishedSession = today;
            return null;
        }

        attemptedSession = today;
        var (symbols, sources) = await ResolveSymbolsAsync(token);
        await WriteRequestRecordAsync(today, utcNow, symbols, sources, token);
        var closes = await LoadRegularClosesAsync(symbols, session, token);
        var result = await collector.CollectAsync(paths.OptionsDailyRoot, today, symbols, closes, token);
        if (result.Failed == 0)
            finishedSession = today;
        else
            retryNotBeforeUtc = clock.GetUtcNow() + RetrySpacing;
        return result;
    }

    // The exchange calendar decides; an unreadable calendar requests nothing and the next pass asks again.
    private async Task<PreparationSession> LoadSessionAsync(DateOnly date, CancellationToken token)
    {
        var loaded = await calendar.LoadAsync(date, date, token);
        return loaded.Sessions.SingleOrDefault(session => session.Date == date)
            ?? throw new InvalidOperationException($"The exchange calendar has no entry for {date:yyyy-MM-dd}.");
    }

    // Close of each stock's last 5-minute bar of the regular session (bar start = close - 5 minutes), in batched requests
    // through the site's market-data provider. A stock without that bar, or a failed request, leaves the client's own price,
    // which each reading records as provider_latest.
    private async Task<IReadOnlyDictionary<string, decimal>> LoadRegularClosesAsync(
        IReadOnlyList<string> symbols, PreparationSession session, CancellationToken token)
    {
        var lastBarStart = session.CloseUtc.AddMinutes(-5);
        var closes = new Dictionary<string, decimal>(StringComparer.Ordinal);
        try
        {
            await foreach (var bar in marketData.Value.GetBarsAsync(symbols, ["5m"], lastBarStart, session.CloseUtc, token))
            {
                if (bar.Timestamp == lastBarStart && bar.Close > 0m)
                    closes[bar.Ticker.ToUpperInvariant()] = bar.Close;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Regular-session closes for {Session} could not be read; the readings use the provider's own price.", session.Date);
        }
        return closes;
    }

    // Active wishlist stocks (the Desk's own merge) plus every stock of the named research screen, sorted and distinct.
    // An unreadable screen is logged and left out, so the wishlist stocks are still collected.
    private async Task<(IReadOnlyList<string> Symbols, OptionsDailyRequestSources Sources)> ResolveSymbolsAsync(CancellationToken token)
    {
        var lists = await wishlists.ListAsync(token);
        var symbols = DeskSources.Merge(lists).Select(member => member.Symbol).ToHashSet(StringComparer.Ordinal);
        string? screenManifest = null;
        string? screenProblem = null;
        if (!String.IsNullOrWhiteSpace(options.ScreenName))
        {
            try
            {
                var screen = screens.Read(options.ScreenName);
                symbols.UnionWith(screen.PoolSymbols(screen.Manifest.From, screen.Manifest.To));
                screenManifest = screen.ManifestSha256;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                screenProblem = exception.Message;
                logger.LogError(exception, "Options daily readings cannot read research screen {Screen}; only wishlist stocks are collected.", options.ScreenName);
            }
        }
        var sources = new OptionsDailyRequestSources(
            lists.Where(list => list.Items.Any(item => item.Active)).Select(list => list.Name).Order(StringComparer.Ordinal).ToArray(),
            options.ScreenName, screenManifest, screenProblem);
        logger.LogInformation("Options daily readings cover {Count} stocks (wishlists and screen {Screen}).", symbols.Count, options.ScreenName);
        return (symbols.Order(StringComparer.Ordinal).ToArray(), sources);
    }

    // Records, per pass, which stocks were asked for and from which lists, next to the day file, so the cohort of each day
    // can be reconstructed later (the lists change over time and the screen is a fixed past cohort, not current membership).
    private async Task WriteRequestRecordAsync(DateOnly session, DateTimeOffset passStartedUtc, IReadOnlyList<string> symbols,
        OptionsDailyRequestSources sources, CancellationToken token)
    {
        var path = Path.ChangeExtension(OptionsDailyCollector.FilePath(paths.OptionsDailyRoot, session), ".requests.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = System.Text.Json.JsonSerializer.Serialize(new { session, passStartedUtc, sources, symbols },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        await File.AppendAllTextAsync(path, line + Environment.NewLine, token);
    }
}

/// <summary>The lists a pass drew its stocks from: wishlist names, the screen name and manifest fingerprint, or why it was left out.</summary>
public sealed record OptionsDailyRequestSources(
    IReadOnlyList<string> Wishlists, string? ScreenName, string? ScreenManifestSha256, string? ScreenProblem);

/// <summary>Runs the options daily scheduler every five minutes until host shutdown; a failed pass is logged and retried.</summary>
public sealed class OptionsDailyCollectionHostedService(
    OptionsDailyCollectionScheduler scheduler, TimeProvider clock, ILogger<OptionsDailyCollectionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), clock);
        do
        {
            try
            {
                await scheduler.RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "The options daily readings pass could not finish; the next pass retries.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
