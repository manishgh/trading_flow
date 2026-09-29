namespace TradingFlow.Web.Services;

/// <summary>Preparation state of one desk symbol in the warmup service.</summary>
public enum DeskPreparationState
{
    /// <summary>Not on the warmup watchlist.</summary>
    NotPrepared,

    /// <summary>On the watchlist, not yet warmed.</summary>
    Queued,

    /// <summary>Warmed within the freshness window.</summary>
    Ready,

    /// <summary>Warmed, but longer ago than the freshness window.</summary>
    Stale,

    /// <summary>The last warmup attempt failed.</summary>
    Failed,

    /// <summary>The warmup service could not be read, so the state is unknown.</summary>
    Unknown
}

/// <summary>What the desk shows for one symbol's preparation.</summary>
public sealed record DeskPreparationStatus(
    DeskPreparationState State,
    DateTimeOffset? LastWarmedAtUtc,
    string? Detail)
{
    public string Label => State switch
    {
        DeskPreparationState.Ready => "Ready",
        DeskPreparationState.Stale => "Stale",
        DeskPreparationState.Queued => "Queued",
        DeskPreparationState.Failed => "Failed",
        DeskPreparationState.NotPrepared => "Not prepared",
        _ => "Unknown"
    };

    public string CssClass => State switch
    {
        DeskPreparationState.Ready => "tag prep-tag is-ready",
        DeskPreparationState.Failed => "tag prep-tag is-failed",
        DeskPreparationState.Stale or DeskPreparationState.NotPrepared => "tag prep-tag is-attention",
        _ => "tag prep-tag"
    };

    /// <summary>Whether a prepare request should include this symbol.</summary>
    public bool NeedsPreparation => State is DeskPreparationState.NotPrepared
        or DeskPreparationState.Stale
        or DeskPreparationState.Failed;
}

/// <summary>Preparation state for the whole desk universe.</summary>
public sealed record DeskPreparationSnapshot(
    bool Available,
    string? Error,
    IReadOnlyDictionary<string, DeskPreparationStatus> BySymbol,
    WarmupRunRecordDto? LatestRun)
{
    public static readonly DeskPreparationStatus UnknownStatus = new(DeskPreparationState.Unknown, null, null);

    public DeskPreparationStatus For(string ticker) =>
        BySymbol.TryGetValue(ticker, out var status) ? status : UnknownStatus;

    public int Count(DeskPreparationState state) => BySymbol.Values.Count(status => status.State == state);

    /// <summary>Symbols a prepare request should send, in ticker order.</summary>
    public IReadOnlyList<string> NeedingPreparation => BySymbol
        .Where(pair => pair.Value.NeedsPreparation)
        .Select(pair => pair.Key)
        .OrderBy(ticker => ticker, StringComparer.Ordinal)
        .ToArray();
}

/// <summary>
/// Brings the warmup service's view of the desk's symbols onto the desk, and
/// queues preparation for the ones that need it.
/// </summary>
/// <remarks>
/// Preparation is the warmup service's job - candles, indicators and news
/// catalysts cached before a session needs them. The desk only reads that state
/// and asks for more; it never warms anything itself. The warmup service is a
/// separate process, so every read is time-boxed and a failure renders as
/// unknown rather than blocking the desk.
/// </remarks>
public sealed class DeskPreparationService
{
    /// <summary>A warm older than this is shown as stale.</summary>
    public static readonly TimeSpan FreshnessWindow = TimeSpan.FromHours(24);

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private readonly WarmupServiceClient warmup;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<DeskPreparationService> logger;

    public DeskPreparationService(
        WarmupServiceClient warmup,
        TimeProvider timeProvider,
        ILogger<DeskPreparationService> logger)
    {
        this.warmup = warmup;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<DeskPreparationSnapshot> GetAsync(
        IReadOnlyCollection<string> tickers,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        try
        {
            var watchlistTask = warmup.GetWatchlistAsync(timeout.Token);
            var runsTask = warmup.GetRunsAsync(timeout.Token);
            await Task.WhenAll(watchlistTask, runsTask);
            return Build(
                tickers,
                await watchlistTask ?? [],
                await runsTask ?? [],
                timeProvider.GetUtcNow());
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Any failure of the separate warmup process - unreachable, timed out,
            // or an unreadable answer - renders as unknown. The desk never fails
            // because preparation state could not be read.
            logger.LogInformation(exception, "Warmup service unavailable for desk preparation state.");
            return Unavailable(tickers, "Preparation service is not reachable.");
        }
    }

    /// <summary>
    /// Adds <paramref name="tickers"/> to the warmup watchlist and asks for an
    /// immediate run. Returns the service's own acknowledgement text.
    /// </summary>
    public async Task<string> PrepareAsync(
        IReadOnlyCollection<string> tickers,
        string universeLabel,
        CancellationToken cancellationToken)
    {
        if (tickers.Count == 0)
        {
            return "Every symbol in this view is already prepared.";
        }

        var accepted = await warmup.AddWatchlistAsync(
            new WarmupWatchRequestDto(
                tickers.ToArray(),
                $"desk preparation: {universeLabel}",
                "desk",
                60,
                14,
                ["1m", "5m", "15m", "1h", "1d"],
                true,
                true),
            cancellationToken);
        return accepted is null
            ? $"Preparation requested for {tickers.Count} symbol(s)."
            : $"Preparation accepted for {accepted.Accepted} symbol(s); run queued: {(accepted.RunQueued ? "yes" : "no")}.";
    }

    /// <summary>
    /// Classifies each desk symbol against the warmup watchlist. Pure, so the
    /// state rules are testable without the warmup service.
    /// </summary>
    public static DeskPreparationSnapshot Build(
        IEnumerable<string> tickers,
        IReadOnlyList<WarmupTickerIntentDto> watchlist,
        IReadOnlyList<WarmupRunRecordDto> runs,
        DateTimeOffset now)
    {
        var intents = watchlist
            .Where(intent => intent.Active)
            .GroupBy(intent => intent.Ticker.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(intent => intent.LastWarmedAtUtc ?? intent.RequestedAtUtc).First(),
                StringComparer.OrdinalIgnoreCase);

        var bySymbol = tickers
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                ticker => ticker,
                ticker => Classify(intents.GetValueOrDefault(ticker), now),
                StringComparer.OrdinalIgnoreCase);

        return new DeskPreparationSnapshot(
            true,
            null,
            bySymbol,
            runs.OrderByDescending(run => run.StartedAtUtc).FirstOrDefault());
    }

    public static DeskPreparationStatus Classify(WarmupTickerIntentDto? intent, DateTimeOffset now)
    {
        if (intent is null)
        {
            return new DeskPreparationStatus(DeskPreparationState.NotPrepared, null, null);
        }

        if (String.Equals(intent.LastStatus, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return new DeskPreparationStatus(DeskPreparationState.Failed, intent.LastWarmedAtUtc, intent.LastError);
        }

        if (intent.LastWarmedAtUtc is not { } warmedAt)
        {
            return new DeskPreparationStatus(DeskPreparationState.Queued, null, intent.Reason);
        }

        return now - warmedAt <= FreshnessWindow
            ? new DeskPreparationStatus(DeskPreparationState.Ready, warmedAt, intent.Reason)
            : new DeskPreparationStatus(DeskPreparationState.Stale, warmedAt, intent.Reason);
    }

    private static DeskPreparationSnapshot Unavailable(IEnumerable<string> tickers, string error) => new(
        false,
        error,
        tickers
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(ticker => ticker, _ => DeskPreparationSnapshot.UnknownStatus, StringComparer.OrdinalIgnoreCase),
        null);
}
