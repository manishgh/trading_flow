using System.Threading.Channels;
using System.Text.Json.Serialization;
using TradingFlow.Application.Preparation;
using TradingFlow.Contracts.Preparation;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Web.Services.Wishlists;

public sealed record DailyObservationResult(
    string State,
    string Detail,
    IndicatorSnapshot? Snapshot = null,
    WishlistSwingWatchEvaluation? Evaluation = null,
    [property: JsonIgnore] WishlistMarketSnapshot? Observation = null,
    DateTimeOffset? ObservedAtUtc = null);

public interface IDailyObservationService
{
    Task<DailyObservationResult> GetAsync(string symbol, CancellationToken cancellationToken);
}

public sealed record DailyObservationOptions(
    int ConcurrentLoads = 4,
    int CachedSymbols = 256,
    TimeSpan? LoadTimeout = null,
    TimeSpan? ReadyLifetime = null,
    TimeSpan? RetryLifetime = null);

/// <summary>
/// Current, read-only daily observations. This owns neither subscriptions nor
/// execution state. The queue, cache, and provider concurrency are all bounded;
/// requests never wait for a cold provider read and never cancel a shared load.
/// </summary>
public sealed class DailyObservationService : IDailyObservationService, IDisposable, IAsyncDisposable
{
    public const int RequiredSessions = 260;
    private static readonly TimeZoneInfo Exchange = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private readonly Lazy<IMarketDataProvider> provider;
    private readonly IPreparationCalendar calendar;
    private readonly WishlistSwingWatchEvaluator evaluator;
    private readonly TimeProvider clock;
    private readonly ILogger<DailyObservationService> logger;
    private readonly DailyObservationOptions options;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Channel<Entry> queue;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task[] workers;
    private bool disposed;

    public DailyObservationService(Lazy<IMarketDataProvider> provider, IPreparationCalendar calendar,
        WishlistSwingWatchEvaluator evaluator, TimeProvider clock, ILogger<DailyObservationService> logger,
        DailyObservationOptions? options = null)
    {
        this.provider = provider;
        this.calendar = calendar;
        this.evaluator = evaluator;
        this.clock = clock;
        this.logger = logger;
        this.options = options ?? new();
        if (this.options.ConcurrentLoads is < 1 or > 16 || this.options.CachedSymbols is < 1 or > 4096 ||
            (this.options.LoadTimeout ?? TimeSpan.FromSeconds(60)) <= TimeSpan.Zero ||
            (this.options.ReadyLifetime ?? TimeSpan.FromMinutes(10)) <= TimeSpan.Zero ||
            (this.options.RetryLifetime ?? TimeSpan.FromMinutes(1)) <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(this.options.CachedSymbols)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = this.options.ConcurrentLoads == 1
        });
        workers = Enumerable.Range(0, this.options.ConcurrentLoads).Select(_ => Task.Run(WorkAsync)).ToArray();
    }

    public Task<DailyObservationResult> GetAsync(string symbol, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        symbol = (symbol ?? String.Empty).Trim().ToUpperInvariant();
        if (symbol.Length is < 1 or > 20 || symbol.Any(c => !Char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-'))
            return Task.FromResult(new DailyObservationResult("unavailable", "A valid equity symbol is required."));
        lock (gate)
        {
            if (disposed)
                return Task.FromResult(new DailyObservationResult("unavailable", "Daily observation service is stopped."));
            var now = clock.GetUtcNow();
            if (entries.TryGetValue(symbol, out var existing))
            {
                existing.LastAccess = now;
                if (existing.Loading || existing.ExpiresAt > now)
                    return Task.FromResult(existing.Result);
                entries.Remove(symbol);
            }
            if (entries.Count >= options.CachedSymbols)
            {
                var victim = entries.Values.Where(x => !x.Loading).MinBy(x => x.LastAccess);
                if (victim is null)
                    return Task.FromResult(new DailyObservationResult("loading", "Daily observation capacity is busy; retry shortly."));
                entries.Remove(victim.Symbol);
            }
            var entry = new Entry(symbol, now);
            entries.Add(symbol, entry);
            if (!queue.Writer.TryWrite(entry))
            {
                entries.Remove(symbol);
                return Task.FromResult(new DailyObservationResult("loading", "Daily observation queue is busy; retry shortly."));
            }
            return Task.FromResult(entry.Result);
        }
    }

    private async Task WorkAsync()
    {
        try
        {
            await foreach (var entry in queue.Reader.ReadAllAsync(stopping.Token))
            {
                DailyObservationResult result;
                DateTimeOffset? nextClose = null;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                timeout.CancelAfter(options.LoadTimeout ?? TimeSpan.FromSeconds(60));
                try
                {
                    (result, nextClose) = await LoadAsync(entry.Symbol, timeout.Token);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    result = new("unavailable", "Completed daily history timed out; it will be retried.");
                    logger.LogWarning("Daily observation timed out for {Symbol}.", entry.Symbol);
                }
                catch (Exception exception)
                {
                    result = new("unavailable", "Completed daily history or its exchange calendar is unavailable; it will be retried.");
                    // Provider response bodies can contain sensitive context; only log the failure type.
                    logger.LogWarning("Daily observation failed for {Symbol}: {FailureType}.", entry.Symbol, exception.GetType().Name);
                }
                lock (gate)
                {
                    var now = clock.GetUtcNow();
                    entry.Result = result;
                    entry.Loading = false;
                    entry.ExpiresAt = now + (result.State is "ready" or "insufficient"
                        ? options.ReadyLifetime ?? TimeSpan.FromMinutes(10)
                        : options.RetryLifetime ?? TimeSpan.FromMinutes(1));
                    if (nextClose is { } close && close < entry.ExpiresAt)
                        entry.ExpiresAt = close;
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private async Task<(DailyObservationResult Result, DateTimeOffset? NextClose)> LoadAsync(string symbol, CancellationToken token)
    {
        var cutoff = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(cutoff, Exchange).DateTime);
        var start = today.AddDays(-RequiredSessions * 2 - 40);
        var schedule = await calendar.LoadAsync(start, today, token);
        var sessions = ValidateCalendar(schedule, start, today, clock.GetUtcNow());
        var completed = sessions.Values.Where(x => x.TradingDay && x.CloseUtc <= cutoff)
            .OrderBy(x => x.Date).TakeLast(RequiredSessions).ToArray();
        var nextClose = sessions.Values.Where(x => x.TradingDay && x.CloseUtc > cutoff)
            .Select(x => (DateTimeOffset?)x.CloseUtc).Min();
        if (completed.Length != RequiredSessions)
            return (new("unavailable", "The exchange calendar does not cover the daily warm-up window."), nextClose);

        var expected = completed.ToDictionary(x => x.Date);
        var barsByDate = new Dictionary<DateOnly, OhlcvBar>();
        await foreach (var bar in provider.Value.GetBarsAsync([symbol], ["1d"], Utc(completed[0].Date, TimeOnly.MinValue), cutoff, token))
        {
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(bar.Timestamp, Exchange).DateTime);
            // Daily bars from an open session are never included, even if the provider returns them.
            if (!expected.TryGetValue(date, out var session)) continue;
            if (!String.Equals(bar.Ticker, symbol, StringComparison.OrdinalIgnoreCase) || bar.Timeframe != "1d" ||
                bar.DataFeed != "sip" || bar.AdjustmentPolicy != "all" ||
                bar.CoverageVerifiedThroughUtc is not { } coverage || coverage < session.CloseUtc ||
                bar.KnownAtUtc > clock.GetUtcNow() || bar.Timestamp > cutoff ||
                bar.Open <= 0 || bar.Close <= 0 || bar.Low <= 0 || bar.High < bar.Low ||
                bar.High < Math.Max(bar.Open, bar.Close) || bar.Low > Math.Min(bar.Open, bar.Close) || bar.Volume < 0)
                return (new("unavailable", "Daily history has unverified, future, or invalid market evidence."), nextClose);
            if (!barsByDate.TryAdd(date, bar))
                return (new("unavailable", "Daily history contains conflicting rows for the same exchange session."), nextClose);
        }
        token.ThrowIfCancellationRequested();
        var observedAt = clock.GetUtcNow();
        if (!barsByDate.ContainsKey(completed[^1].Date))
            return (new("unavailable", "The latest completed exchange session is missing from daily history.", ObservedAtUtc: observedAt), nextClose);
        var bars = barsByDate.OrderBy(x => x.Key).Select(x => x.Value).ToArray();
        var first = barsByDate.Keys.Min();
        if (completed.Any(x => x.Date >= first && !barsByDate.ContainsKey(x.Date)))
            return (new("unavailable", "Daily history has a gap between completed exchange sessions.", ObservedAtUtc: observedAt), nextClose);
        var profile = MarketEvidenceProfile.ProductionDefault.WithSessionSchedules(sessions.ToDictionary(x => x.Key,
            x => new MarketSessionSchedule(x.Key, x.Value.TradingDay,
                TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.Value.OpenUtc, Exchange).DateTime),
                TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.Value.CloseUtc, Exchange).DateTime))));
        var snapshots = new IndicatorEngine(profile).Compute(bars);
        var current = snapshots[^1];
        if (bars.Length < RequiredSessions)
            return (new("insufficient", $"{bars.Length}/{RequiredSessions} real completed daily sessions; observation is warming. No pre-listing history is synthesized.",
                current, ObservedAtUtc: observedAt), nextClose);
        var observation = new WishlistMarketSnapshot(symbol, current, snapshots[^2], bars.Take(bars.Length - 1).TakeLast(20).Max(x => x.High));
        return (new("ready", $"Completed SIP/all daily evidence through {completed[^1].Date:yyyy-MM-dd}; observational swing watch only.",
            current, evaluator.Evaluate(observation), observation, observedAt), nextClose);
    }

    private static Dictionary<DateOnly, PreparationSession> ValidateCalendar(PreparationCalendar calendar,
        DateOnly start, DateOnly end, DateTimeOffset now)
    {
        if (calendar.Provider != "alpaca-calendar" || calendar.RetrievedAtUtc == default || calendar.RetrievedAtUtc > now ||
            calendar.Sessions.Length != end.DayNumber - start.DayNumber + 1)
            throw new InvalidOperationException("Authoritative calendar coverage is unavailable.");
        var sessions = calendar.Sessions.ToDictionary(x => x.Date);
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            if (!sessions.TryGetValue(date, out var session) || session.TradingDay &&
                (session.OpenUtc >= session.CloseUtc ||
                 DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(session.OpenUtc, Exchange).DateTime) != date ||
                 DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(session.CloseUtc, Exchange).DateTime) != date))
                throw new InvalidOperationException("Authoritative calendar coverage is incomplete or invalid.");
        }
        return sessions;
    }

    private static DateTimeOffset Utc(DateOnly date, TimeOnly time) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), Exchange));

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            queue.Writer.TryComplete();
            stopping.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Task.WhenAll(workers);
    }

    private sealed class Entry(string symbol, DateTimeOffset now)
    {
        public string Symbol { get; } = symbol;
        public bool Loading { get; set; } = true;
        public DateTimeOffset LastAccess { get; set; } = now;
        public DateTimeOffset ExpiresAt { get; set; }
        public DailyObservationResult Result { get; set; } = new("loading", "Loading completed daily SIP history.");
    }
}
