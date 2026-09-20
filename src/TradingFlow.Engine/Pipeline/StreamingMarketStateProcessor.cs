using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;

namespace TradingFlow.Engine.Pipeline;

/// <summary>
/// Ordered, bounded, per-symbol projector for completed market bars. Live and
/// replay events share this path; only accepted live provider bars are persisted.
/// </summary>
public sealed class StreamingMarketStateProcessor
    : IMarketStateSnapshotProvider
{
    private const string PersistedSource = "stream";
    private const int SourceBarsPerDay = 24 * 60;
    private static readonly TimeZoneInfo ExchangeTimeZone = ResolveExchangeTimeZone();
    private readonly ConcurrentDictionary<string, SymbolPipeline> pipelines =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ICandleStore candleStore;
    private readonly IFencedCandleStore fencedCandleStore;
    private readonly CandleStoreContext storeContext;
    private readonly StreamingMarketStateOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<StreamingMarketStateProcessor> logger;
    private Indicators.IIndicatorCalculator indicatorCalculator;
    private BarResampler barResampler;
    private long fencingFloor;
    private long activeOwnershipFence;
    private long snapshotReadyFence;

    public int RecoveryLookbackDays => options.RecoveryLookbackDays;

    public StreamingMarketStateProcessor(
        ICandleStore candleStore,
        CandleStoreContext storeContext,
        StreamingMarketStateOptions options,
        TimeProvider timeProvider,
        ILogger<StreamingMarketStateProcessor> logger)
        : this(
            candleStore,
            storeContext,
            options,
            timeProvider,
            logger,
            new IndicatorEngine())
    {
    }

    public StreamingMarketStateProcessor(
        ICandleStore candleStore,
        CandleStoreContext storeContext,
        StreamingMarketStateOptions options,
        TimeProvider timeProvider,
        ILogger<StreamingMarketStateProcessor> logger,
        Indicators.IIndicatorCalculator indicatorCalculator)
    {
        this.candleStore = candleStore ?? throw new ArgumentNullException(nameof(candleStore));
        fencedCandleStore = candleStore as IFencedCandleStore
            ?? throw new ArgumentException(
                "Live market-state processing requires an IFencedCandleStore.",
                nameof(candleStore));
        this.storeContext = storeContext ?? throw new ArgumentNullException(nameof(storeContext));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.indicatorCalculator = indicatorCalculator ?? throw new ArgumentNullException(nameof(indicatorCalculator));
        barResampler = new BarResampler();
        options.Validate();
    }

    /// <summary>
    /// Atomically installs an authoritative exchange calendar for both indicator
    /// evidence and timeframe aggregation. The stream owner calls this before it
    /// marks snapshots ready and refreshes it while the lease remains active.
    /// </summary>
    public void UpdateMarketSessionSchedules(
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        if (schedules.Count == 0)
        {
            throw new ArgumentException("At least one authoritative market-session schedule is required.", nameof(schedules));
        }

        var profile = MarketEvidenceProfile.ProductionDefault.WithSessionSchedules(schedules);
        Volatile.Write(ref indicatorCalculator, new IndicatorEngine(profile));
        Volatile.Write(ref barResampler, new BarResampler(profile));
    }

    /// <summary>
    /// Advances the process-wide floor obtained from the durable stream lease.
    /// The owning service must await this before opening its provider socket.
    /// </summary>
    public long AdvanceFencingFloor(long durableFencingToken)
    {
        if (durableFencingToken <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durableFencingToken),
                "A durable fencing token must be positive.");
        }

        return AdvanceMaximum(ref fencingFloor, durableFencingToken);
    }

    /// <summary>
    /// Advances the global floor and brings existing pipeline snapshots up to
    /// that floor before returning.
    /// </summary>
    public async Task AdvanceFencingFloorAsync(
        long durableFencingToken,
        CancellationToken cancellationToken = default)
    {
        await fencedCandleStore.AdvanceFencingTokenAsync(
            storeContext,
            PersistedSource,
            durableFencingToken,
            cancellationToken);
        var floor = AdvanceFencingFloor(durableFencingToken);
        var existing = pipelines.Values.ToArray();
        await Task.WhenAll(existing.Select(async pipeline =>
        {
            try
            {
                await EnqueueAsync(
                    pipeline,
                    () =>
                    {
                        pipeline.FencingToken = Math.Max(pipeline.FencingToken, floor);
                        return Task.FromResult(true);
                    },
                    waitForCapacity: true,
                    cancellationToken);
            }
            catch (SymbolPipelineUnavailableException)
            {
                // An evicted pipeline cannot publish data.
            }
        }));
    }

    /// <summary>
    /// Enables live event ingestion for the current fenced owner. Strategy
    /// snapshots remain unavailable until initial subscription recovery finishes.
    /// </summary>
    public void ActivateOwnership(long fencingToken)
    {
        if (fencingToken <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fencingToken),
                "An active market-state fencing token must be positive.");
        }

        if (fencingToken < Volatile.Read(ref fencingFloor))
        {
            throw new StaleCandleStoreWriteException(
                $"Cannot activate stale market-state fence {fencingToken}.");
        }

        Volatile.Write(ref snapshotReadyFence, 0);
        Volatile.Write(ref activeOwnershipFence, fencingToken);
    }

    public void MarkSnapshotsReady(long fencingToken)
    {
        if (Volatile.Read(ref activeOwnershipFence) != fencingToken)
        {
            throw new InvalidOperationException(
                $"Cannot publish market-state snapshots for inactive fence {fencingToken}.");
        }

        Volatile.Write(ref snapshotReadyFence, fencingToken);
    }

    public void InvalidateOwnership(long fencingToken)
    {
        _ = Interlocked.CompareExchange(ref snapshotReadyFence, 0, fencingToken);
        _ = Interlocked.CompareExchange(ref activeOwnershipFence, 0, fencingToken);
    }

    public async Task EnsureSymbolReadyAsync(string symbol, CancellationToken cancellationToken)
    {
        var pipeline = GetPipeline(symbol);
        if (pipeline.IsReady && !pipeline.NeedsRepair)
        {
            return;
        }

        try
        {
            await EnqueueAsync(
                pipeline,
                async () =>
                {
                    if (!pipeline.IsReady || pipeline.NeedsRepair)
                    {
                        var end = timeProvider.GetUtcNow();
                        var bars = await candleStore.ReadBarsAsync(
                            new CandleStoreReadRequest(
                                storeContext.Scope,
                                storeContext.RunName,
                                storeContext.ProviderName,
                                pipeline.Symbol,
                                "1m",
                                end.AddDays(-options.RecoveryLookbackDays),
                                end,
                                PersistedSource),
                            cancellationToken);
                        foreach (var bar in bars
                                     .Select(Normalize)
                                     .Where(bar => IsValidSourceBar(bar) &&
                                                   CanonicalMarketBarValidator.IsCompletedAsOf(bar, end))
                                     .OrderBy(value => value.Timestamp))
                        {
                            pipeline.Bars[bar.Timestamp] = [bar];
                        }

                        pipeline.DataFeed = ResolveSingleFeed(
                            pipeline.Bars.Values.Select(versions => versions[^1]));

                        DetectUnconfirmedGaps(pipeline);
                        TrimRetention(pipeline);
                        pipeline.FencingToken = Math.Max(
                            pipeline.FencingToken,
                            Volatile.Read(ref fencingFloor));
                        pipeline.IsReady = true;
                        pipeline.NeedsRepair = pipeline.PendingMissingMinutes.Count > 0;
                    }

                    return true;
                },
                waitForCapacity: true,
                cancellationToken);
        }
        catch
        {
            pipeline.NeedsRepair = true;
            throw;
        }
    }

    public async Task BackfillSymbolAsync(
        string symbol,
        IMarketDataProvider provider,
        CancellationToken cancellationToken) =>
        await BackfillSymbolsAsync([symbol], provider, cancellationToken);

    public async Task BackfillSymbolsAsync(
        IReadOnlyCollection<string> symbols,
        IMarketDataProvider provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var normalizedSymbols = symbols
            .Select(NormalizeSymbol)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedSymbols.Length == 0)
        {
            return;
        }

        await Task.WhenAll(normalizedSymbols.Select(symbol =>
            EnsureSymbolReadyAsync(symbol, cancellationToken)));
        var end = timeProvider.GetUtcNow();
        var providerConfirmsSparseNoTradeIntervals =
            provider is IMarketDataCompletenessProvider
            {
                OmittedSubDailyIntervalsMeanNoQualifyingTrades: true
            };
        var incomingBySymbol = normalizedSymbols.ToDictionary(
            symbol => symbol,
            _ => new List<OhlcvBar>(),
            StringComparer.OrdinalIgnoreCase);
        try
        {
            await foreach (var bar in provider.GetBarsAsync(
                               normalizedSymbols,
                               ["1m"],
                               end.AddDays(-options.RecoveryLookbackDays),
                               end,
                               cancellationToken))
            {
                var normalizedBar = Normalize(bar);
                if (incomingBySymbol.TryGetValue(normalizedBar.Ticker, out var target) &&
                    IsValidSourceBar(normalizedBar) &&
                    CanonicalMarketBarValidator.IsCompletedAsOf(normalizedBar, end))
                {
                    target.Add(normalizedBar);
                }
            }
        }
        catch
        {
            foreach (var symbol in normalizedSymbols)
            {
                if (pipelines.TryGetValue(symbol, out var pipeline))
                {
                    pipeline.NeedsRepair = true;
                }
            }

            throw;
        }

        await Task.WhenAll(incomingBySymbol.Select(pair =>
            MergeBackfillAsync(
                pair.Key,
                pair.Value,
                end,
                providerConfirmsSparseNoTradeIntervals,
                cancellationToken)));
    }

    private async Task MergeBackfillAsync(
        string symbol,
        IReadOnlyCollection<OhlcvBar> incoming,
        DateTimeOffset backfillEndUtc,
        bool providerConfirmsSparseNoTradeIntervals,
        CancellationToken cancellationToken)
    {
        var pipeline = GetPipeline(symbol);
        try
        {
            await EnqueueAsync(
                pipeline,
                async () =>
                {
                    // Buffered live bars win over overlapping REST history at the
                    // subscribe/backfill boundary.
                    var incomingFeed = ResolveSingleFeed(incoming);
                    var effectiveIncomingFeed = incomingFeed is null or "unspecified"
                        ? pipeline.DataFeed ?? "unspecified"
                        : incomingFeed;
                    if (pipeline.DataFeed is not null && incomingFeed is not null &&
                        !pipeline.DataFeed.Equals("unspecified", StringComparison.Ordinal) &&
                        !incomingFeed.Equals("unspecified", StringComparison.Ordinal) &&
                        !pipeline.DataFeed.Equals(incomingFeed, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Market-data feed mismatch for {symbol}: cached={pipeline.DataFeed}, backfill={incomingFeed}.");
                    }

                    var normalizedIncoming = incoming
                        .Select(bar => NormalizeFeed(bar.DataFeed) == "unspecified" &&
                            !effectiveIncomingFeed.Equals("unspecified", StringComparison.Ordinal)
                                ? bar with { DataFeed = effectiveIncomingFeed }
                                : bar)
                        .Select(bar => providerConfirmsSparseNoTradeIntervals &&
                            bar.CoverageVerifiedThroughUtc is null
                                ? bar with { CoverageVerifiedThroughUtc = backfillEndUtc.ToUniversalTime() }
                                : bar)
                        .ToArray();
                    var missing = normalizedIncoming
                        .Where(bar => !pipeline.Bars.TryGetValue(bar.Timestamp, out var versions) ||
                            versions[^1].DataFeed.Equals("unspecified", StringComparison.OrdinalIgnoreCase) ||
                            versions[^1].KnownAtUtc is null)
                        .OrderBy(bar => bar.Timestamp)
                        .ToArray();
                    var incomingTimestamps = normalizedIncoming
                        .Select(bar => bar.Timestamp.ToUniversalTime())
                        .ToHashSet();
                    pipeline.PendingMissingMinutes.ExceptWith(incomingTimestamps);
                    pipeline.ConfirmedNoTradeMinutes.ExceptWith(incomingTimestamps);
                    if (missing.Length > 0)
                    {
                        await candleStore.UpsertBarsAsync(
                            new CandleStoreWriteRequest(
                                storeContext,
                                PersistedSource,
                                missing,
                                pipeline.FencingToken > 0 ? pipeline.FencingToken : null),
                            cancellationToken);
                        foreach (var bar in missing)
                        {
                            pipeline.Bars[bar.Timestamp] = [bar];
                        }
                    }

                    pipeline.DataFeed = effectiveIncomingFeed;

                    DetectUnconfirmedGaps(pipeline);

                    if (providerConfirmsSparseNoTradeIntervals)
                    {
                        var confirmedNoTrade = pipeline.PendingMissingMinutes
                            .Where(timestamp => timestamp < backfillEndUtc.ToUniversalTime())
                            .Where(timestamp => !incomingTimestamps.Contains(timestamp))
                            .ToArray();
                        pipeline.PendingMissingMinutes.ExceptWith(confirmedNoTrade);
                        pipeline.ConfirmedNoTradeMinutes.UnionWith(confirmedNoTrade);
                    }

                    TrimRetention(pipeline);
                    pipeline.FencingToken = Math.Max(
                        pipeline.FencingToken,
                        Volatile.Read(ref fencingFloor));
                    pipeline.IsReady = true;
                    pipeline.NeedsRepair = pipeline.PendingMissingMinutes.Count > 0;
                    return true;
                },
                waitForCapacity: true,
                cancellationToken);
        }
        catch
        {
            pipeline.NeedsRepair = true;
            throw;
        }
    }

    public async Task<TickerMarketState?> GetTickerStateAsync(
        string symbol,
        IReadOnlyCollection<string> requiredTimeframes,
        int minimumBarsPerTimeframe,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        if (!TryCaptureSnapshotFence(out var snapshotFence))
        {
            return null;
        }

        var pipeline = GetPipeline(symbol);
        var state = await EnqueueAsync(
            pipeline,
            () => Task.FromResult(
                !pipeline.IsReady || pipeline.NeedsRepair
                    ? null
                    : BuildCompletedState(pipeline, asOfUtc)),
            waitForCapacity: true,
            cancellationToken);
        if (state is null || !SnapshotFenceIsCurrent(snapshotFence))
        {
            return null;
        }

        var latestSourceBar = state.BarsByTimeframe["1m"].LastOrDefault();
        if (IsExtendedSessionActive(asOfUtc) &&
            (latestSourceBar is null ||
             asOfUtc.ToUniversalTime() - latestSourceBar.Timestamp.AddMinutes(1) >
             TimeSpan.FromMinutes(options.ActiveSessionStalenessMinutes)))
        {
            return null;
        }

        var bars = requiredTimeframes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                timeframe => timeframe,
                timeframe => state.BarsByTimeframe.GetValueOrDefault(timeframe) ?? [],
                StringComparer.OrdinalIgnoreCase);
        if (bars.Any(pair => pair.Value.Count < minimumBarsPerTimeframe))
        {
            return null;
        }

        var currentResampler = Volatile.Read(ref barResampler);
        if (IsExtendedSessionActive(asOfUtc) && bars.Any(pair =>
                !HasLatestCompletedTimeframe(pair.Value, pair.Key, asOfUtc, currentResampler)))
        {
            return null;
        }

        var snapshots = bars.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<IndicatorSnapshot>)Volatile.Read(ref indicatorCalculator).Compute(pair.Value),
            StringComparer.OrdinalIgnoreCase);
        if (!SnapshotFenceIsCurrent(snapshotFence))
        {
            return null;
        }

        return new TickerMarketState(NormalizeSymbol(symbol), bars, snapshots);
    }

    private bool TryCaptureSnapshotFence(out long fencingToken)
    {
        var active = Volatile.Read(ref activeOwnershipFence);
        fencingToken = active;
        return active > 0 && Volatile.Read(ref snapshotReadyFence) == active;
    }

    private bool SnapshotFenceIsCurrent(long fencingToken) =>
        fencingToken > 0 &&
        Volatile.Read(ref activeOwnershipFence) == fencingToken &&
        Volatile.Read(ref snapshotReadyFence) == fencingToken;

    public async Task<MarketBarApplyResult> ProcessAsync(
        MarketBarEvent marketEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marketEvent);
        var symbol = NormalizeSymbol(marketEvent.Bar.Ticker);
        var pipeline = GetPipeline(symbol);
        try
        {
            return await EnqueueAsync(
                pipeline,
                () => ApplyAsync(pipeline, marketEvent, cancellationToken),
                waitForCapacity: false,
                cancellationToken);
        }
        catch (SymbolPipelineUnavailableException exception)
        {
            pipeline.NeedsRepair = true;
            logger.LogError(
                exception,
                "Market-state pipeline unavailable for {Symbol}; rejecting the event and requiring REST repair.",
                symbol);
            return new MarketBarApplyResult(
                MarketBarDisposition.BackpressureRejected,
                symbol,
                marketEvent.Bar.Timestamp.ToUniversalTime(),
                "Per-symbol market-state pipeline is full or evicting; REST gap repair is required.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            pipeline.NeedsRepair = true;
            throw;
        }
    }

    public Task<CompletedMarketState> GetCompletedStateAsync(
        string symbol,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        var pipeline = GetPipeline(symbol);
        return EnqueueAsync(
            pipeline,
            () => Task.FromResult(BuildCompletedState(pipeline, asOfUtc)),
            waitForCapacity: true,
            cancellationToken);
    }

    /// <summary>
    /// Stops accepting work for a symbol, drains accepted work, and then removes
    /// its in-memory state. A later request creates a fresh pipeline.
    /// </summary>
    public async Task<bool> EvictSymbolAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeSymbol(symbol);
        if (!pipelines.TryGetValue(normalized, out var pipeline))
        {
            return false;
        }

        if (!pipeline.TryBeginEviction())
        {
            await pipeline.EvictionCompletion.Task.WaitAsync(cancellationToken);
            return false;
        }

        try
        {
            pipeline.Block.Complete();
            // Once eviction starts it must drain accepted persistence work even
            // if the requesting caller is later cancelled.
            await pipeline.Block.Completion;
            return true;
        }
        finally
        {
            pipelines.TryRemove(new KeyValuePair<string, SymbolPipeline>(normalized, pipeline));
            pipeline.EvictionCompletion.TrySetResult();
        }
    }

    public async Task RemoveSymbolsAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        await Task.WhenAll(symbols
            .Where(symbol => !String.IsNullOrWhiteSpace(symbol))
            .Select(symbol => EvictSymbolAsync(symbol, cancellationToken)));
    }

    /// <summary>
    /// Returns the symbols that currently own an in-memory pipeline. The stream
    /// owner uses this snapshot to remove state left by a prior connection.
    /// </summary>
    public IReadOnlyCollection<string> GetTrackedSymbols() => pipelines.Keys.ToArray();

    public bool RequiresRepair(string symbol) =>
        pipelines.TryGetValue(NormalizeSymbol(symbol), out var pipeline) && pipeline.NeedsRepair;

    private CompletedMarketState BuildCompletedState(
        SymbolPipeline pipeline,
        DateTimeOffset asOfUtc)
    {
        var utc = asOfUtc.ToUniversalTime();
        var baseBars = pipeline.Bars.Values
            .Select(versions => SelectVersionKnownAsOf(versions, utc))
            .Where(bar => bar is not null && bar.Timestamp.AddMinutes(1) <= utc)
            .Select(bar => bar!)
            .OrderBy(bar => bar.Timestamp)
            .ToArray();
        var byTimeframe = new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase)
        {
            ["1m"] = baseBars
        };
        var resampler = Volatile.Read(ref barResampler);
        foreach (var timeframe in options.DerivedTimeframes
                     .Where(value => !value.Equals("1m", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            byTimeframe[timeframe] = BuildCompleteDerivedBars(
                baseBars,
                pipeline.ConfirmedNoTradeMinutes,
                timeframe,
                utc,
                resampler);
        }

        pipeline.FencingToken = Math.Max(
            pipeline.FencingToken,
            Volatile.Read(ref fencingFloor));
        return new CompletedMarketState(
            pipeline.Symbol,
            pipeline.FencingToken,
            byTimeframe,
            ComputeFingerprint(byTimeframe));
    }

    private static IReadOnlyList<OhlcvBar> BuildCompleteDerivedBars(
        IReadOnlyList<OhlcvBar> sourceBars,
        IReadOnlySet<DateTimeOffset> confirmedNoTradeMinutes,
        string targetTimeframe,
        DateTimeOffset asOfUtc,
        BarResampler resampler)
    {
        if (sourceBars.Count == 0)
        {
            return [];
        }

        var sourceTimestamps = sourceBars
            .Select(bar => bar.Timestamp.ToUniversalTime())
            .ToHashSet();
        return resampler.Resample(sourceBars, targetTimeframe)
            .Where(bar =>
            {
                var window = resampler.GetBucketWindow(bar.Timestamp, targetTimeframe);
                if (window.EndUtc > asOfUtc)
                {
                    return false;
                }

                var expectedCount = checked((int)(window.EndUtc - window.StartUtc).TotalMinutes);
                if (expectedCount <= 0)
                {
                    return false;
                }

                for (var minute = 0; minute < expectedCount; minute++)
                {
                    var timestamp = window.StartUtc.AddMinutes(minute);
                    if (!sourceTimestamps.Contains(timestamp) &&
                        !confirmedNoTradeMinutes.Contains(timestamp))
                    {
                        return false;
                    }
                }

                return true;
            })
            .ToArray();
    }

    private async Task<MarketBarApplyResult> ApplyAsync(
        SymbolPipeline pipeline,
        MarketBarEvent marketEvent,
        CancellationToken cancellationToken)
    {
        var bar = CanonicalMarketBarValidator.Normalize(marketEvent.Bar);
        if (!bar.Ticker.Equals(pipeline.Symbol, StringComparison.Ordinal))
        {
            return Result(MarketBarDisposition.Invalid, bar, "Event symbol does not match its pipeline.");
        }

        if (!CanonicalMarketBarValidator.IsValid(bar, requireOneMinuteSource: true, out var validationFailure))
        {
            return Result(
                MarketBarDisposition.Invalid,
                bar,
                validationFailure);
        }

        var eventFeed = NormalizeFeed(marketEvent.Feed);
        var barFeed = NormalizeFeed(bar.DataFeed);
        if (barFeed.Equals("unspecified", StringComparison.Ordinal))
        {
            bar = bar with { DataFeed = eventFeed };
            barFeed = eventFeed;
        }

        if (!barFeed.Equals(eventFeed, StringComparison.Ordinal))
        {
            return Result(
                MarketBarDisposition.Invalid,
                bar,
                $"Market event feed '{eventFeed}' does not match bar feed '{barFeed}'.");
        }

        if (pipeline.DataFeed is not null &&
            !pipeline.DataFeed.Equals("unspecified", StringComparison.Ordinal) &&
            !pipeline.DataFeed.Equals(eventFeed, StringComparison.Ordinal))
        {
            pipeline.NeedsRepair = true;
            return Result(
                MarketBarDisposition.Invalid,
                bar,
                $"Live feed '{eventFeed}' does not match warm-state feed '{pipeline.DataFeed}'.");
        }

        pipeline.DataFeed = eventFeed;

        if (marketEvent.Kind is not (MarketBarEventKind.Replay or MarketBarEventKind.ReplayRevision))
        {
            if (marketEvent.FencingToken <= 0)
            {
                return Result(MarketBarDisposition.Invalid, bar, "Live provider events require a positive fencing token.");
            }

            var requiredFence = Math.Max(
                pipeline.FencingToken,
                Volatile.Read(ref fencingFloor));
            if (marketEvent.FencingToken < requiredFence)
            {
                return Result(MarketBarDisposition.StaleLease, bar, "Event was published by a stale stream owner.");
            }

            if (Volatile.Read(ref activeOwnershipFence) != marketEvent.FencingToken)
            {
                return Result(
                    MarketBarDisposition.StaleLease,
                    bar,
                    "Live event does not belong to the active stream ownership generation.");
            }

            pipeline.FencingToken = Math.Max(pipeline.FencingToken, marketEvent.FencingToken);
        }

        if (pipeline.Bars.TryGetValue(bar.Timestamp, out var versions))
        {
            if (versions.Any(existing => existing == bar))
            {
                return Result(MarketBarDisposition.Duplicate, bar, "Identical logical bar already exists.");
            }

            if (marketEvent.Kind is not (MarketBarEventKind.ProviderRevision or MarketBarEventKind.ReplayRevision))
            {
                return Result(MarketBarDisposition.ConflictingDuplicate, bar, "A non-revision event conflicts with a completed bar.");
            }

            var revisionDeadline = bar.Timestamp
                .Add(TimeframeParser.Parse(bar.Timeframe))
                .Add(options.RevisionAcceptanceWindow);
            if (marketEvent.ObservedAtUtc.ToUniversalTime() > revisionDeadline)
            {
                return Result(MarketBarDisposition.RevisionOutsideWindow, bar, "Provider revision arrived outside the configured acceptance window.");
            }

            await PersistIfLiveAsync(marketEvent, bar, cancellationToken);
            versions.Add(bar);
            versions.Sort(static (left, right) =>
                ResolveKnownAtUtc(left).CompareTo(ResolveKnownAtUtc(right)));
            TrimRetention(pipeline);
            return Result(MarketBarDisposition.RevisionAccepted, bar, "Provider revision added a point-in-time bar version.");
        }

        if (pipeline.Bars.Count > 0 && bar.Timestamp < pipeline.Bars.Keys.Max())
        {
            RequireRestReconciliation(pipeline, bar.Timestamp);
            return Result(MarketBarDisposition.OutOfOrder, bar, "Late non-matching logical bar cannot rewrite completed history.");
        }

        if (marketEvent.Kind is MarketBarEventKind.ProviderRevision or MarketBarEventKind.ReplayRevision)
        {
            RequireRestReconciliation(pipeline, bar.Timestamp);
            return Result(MarketBarDisposition.OutOfOrder, bar, "Revision has no original completed bar to revise.");
        }

        var latestTimestamp = pipeline.Bars.Count > 0 ? pipeline.Bars.Keys.Max() : (DateTimeOffset?)null;
        var gapDetected = latestTimestamp is { } latest && HasUnexpectedIntraSessionGap(latest, bar.Timestamp);
        pipeline.PendingMissingMinutes.Remove(bar.Timestamp);
        pipeline.ConfirmedNoTradeMinutes.Remove(bar.Timestamp);
        await PersistIfLiveAsync(marketEvent, bar, cancellationToken);
        pipeline.Bars[bar.Timestamp] = [bar];
        TrimRetention(pipeline);
        if (gapDetected)
        {
            if (marketEvent.Kind is not (MarketBarEventKind.Replay or MarketBarEventKind.ReplayRevision))
            {
                RecordMissingMinutes(pipeline, latestTimestamp!.Value, bar.Timestamp);
                pipeline.NeedsRepair = true;
            }

            return Result(
                MarketBarDisposition.GapDetected,
                bar,
                "Completed bar accepted after a missing intra-session minute; REST reconciliation is required for live state.");
        }

        return Result(MarketBarDisposition.Accepted, bar, "Completed bar accepted.");
    }

    private Task PersistIfLiveAsync(
        MarketBarEvent marketEvent,
        OhlcvBar bar,
        CancellationToken cancellationToken) =>
        marketEvent.Kind is MarketBarEventKind.Replay or MarketBarEventKind.ReplayRevision
            ? Task.CompletedTask
            : candleStore.UpsertBarsAsync(
                new CandleStoreWriteRequest(
                    storeContext,
                    PersistedSource,
                    [bar],
                    marketEvent.FencingToken),
                cancellationToken);

    private static OhlcvBar? SelectVersionKnownAsOf(
        IReadOnlyList<OhlcvBar> versions,
        DateTimeOffset asOfUtc)
    {
        var utc = asOfUtc.ToUniversalTime();
        return versions
            .Where(version => ResolveKnownAtUtc(version) <= utc)
            .OrderBy(ResolveKnownAtUtc)
            .LastOrDefault();
    }

    private static DateTimeOffset ResolveKnownAtUtc(OhlcvBar bar) =>
        bar.KnownAtUtc?.ToUniversalTime() ??
        bar.Timestamp.ToUniversalTime().Add(TimeframeParser.Parse(bar.Timeframe));

    private SymbolPipeline GetPipeline(string symbol)
    {
        var normalized = NormalizeSymbol(symbol);
        return pipelines.GetOrAdd(
            normalized,
            value => new SymbolPipeline(
                value,
                options.SymbolPipelineCapacity,
                Volatile.Read(ref fencingFloor)));
    }

    private async Task<T> EnqueueAsync<T>(
        SymbolPipeline pipeline,
        Func<Task<T>> operation,
        bool waitForCapacity,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var faultRegistration = pipeline.RegisterFault(
            exception => completion.TrySetException(exception));
        async Task ExecuteAsync()
        {
            try
            {
                completion.TrySetResult(await operation());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        try
        {
            var accepted = !pipeline.IsEvicting && (waitForCapacity
                ? await pipeline.Block.SendAsync(ExecuteAsync, cancellationToken)
                : pipeline.Block.Post(ExecuteAsync));
            if (!accepted)
            {
                completion.TrySetException(new SymbolPipelineUnavailableException(pipeline.Symbol));
            }

            // Once the block accepts work, ownership cannot detach from it. The
            // operation still receives the caller token and may cancel itself,
            // but its caller observes completion so connection drain and symbol
            // eviction never declare success while persistence is still running.
            return await completion.Task;
        }
        catch (OperationCanceledException)
        {
            completion.TrySetCanceled(cancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
            throw;
        }
        finally
        {
            pipeline.UnregisterFault(faultRegistration);
        }
    }

    private void TrimRetention(SymbolPipeline pipeline)
    {
        if (pipeline.Bars.Count == 0)
        {
            return;
        }

        var newest = pipeline.Bars.Keys.Max();
        var cutoff = newest.AddDays(-options.RecoveryLookbackDays);
        foreach (var timestamp in pipeline.Bars.Keys.TakeWhile(value => value < cutoff).ToArray())
        {
            pipeline.Bars.Remove(timestamp);
        }

        var maximumBars = checked(options.RecoveryLookbackDays * SourceBarsPerDay);
        while (pipeline.Bars.Count > maximumBars)
        {
            pipeline.Bars.Remove(pipeline.Bars.Keys.First());
        }

        pipeline.PendingMissingMinutes.RemoveWhere(timestamp => timestamp < cutoff);
        pipeline.ConfirmedNoTradeMinutes.RemoveWhere(timestamp => timestamp < cutoff);
    }

    private static void DetectUnconfirmedGaps(SymbolPipeline pipeline)
    {
        DateTimeOffset? previous = null;
        foreach (var timestamp in pipeline.Bars.Keys)
        {
            if (previous is { } prior && HasUnexpectedIntraSessionGap(prior, timestamp))
            {
                RecordMissingMinutes(pipeline, prior, timestamp);
            }

            previous = timestamp;
        }
    }

    private static void RecordMissingMinutes(
        SymbolPipeline pipeline,
        DateTimeOffset previousUtc,
        DateTimeOffset currentUtc)
    {
        for (var timestamp = previousUtc.AddMinutes(1);
             timestamp < currentUtc;
             timestamp = timestamp.AddMinutes(1))
        {
            if (GetSessionIdentity(timestamp) == GetSessionIdentity(previousUtc))
            {
                var normalized = timestamp.ToUniversalTime();
                if (!pipeline.ConfirmedNoTradeMinutes.Contains(normalized))
                {
                    pipeline.PendingMissingMinutes.Add(normalized);
                }
            }
        }
    }

    private static void RequireRestReconciliation(
        SymbolPipeline pipeline,
        DateTimeOffset timestamp)
    {
        var normalized = timestamp.ToUniversalTime();
        pipeline.ConfirmedNoTradeMinutes.Remove(normalized);
        pipeline.PendingMissingMinutes.Add(normalized);
        pipeline.NeedsRepair = true;
    }

    private static OhlcvBar Normalize(OhlcvBar bar) => CanonicalMarketBarValidator.Normalize(bar);

    private static bool IsValidSourceBar(OhlcvBar bar) =>
        CanonicalMarketBarValidator.IsValid(bar, requireOneMinuteSource: true, out _);

    private static bool HasUnexpectedIntraSessionGap(
        DateTimeOffset previousUtc,
        DateTimeOffset currentUtc)
    {
        if (currentUtc <= previousUtc.AddMinutes(1))
        {
            return false;
        }

        var previous = GetSessionIdentity(previousUtc);
        var current = GetSessionIdentity(currentUtc);
        return previous == current;
    }

    private static (DateOnly SessionDate, string Segment) GetSessionIdentity(DateTimeOffset utc)
    {
        var exchange = TimeZoneInfo.ConvertTime(utc, ExchangeTimeZone);
        var time = exchange.TimeOfDay;
        if (time >= new TimeSpan(20, 0, 0))
        {
            return (DateOnly.FromDateTime(exchange.Date), "overnight");
        }

        if (time < new TimeSpan(4, 0, 0))
        {
            return (DateOnly.FromDateTime(exchange.Date.AddDays(-1)), "overnight");
        }

        if (time < new TimeSpan(9, 30, 0))
        {
            return (DateOnly.FromDateTime(exchange.Date), "premarket");
        }

        if (time < new TimeSpan(16, 0, 0))
        {
            return (DateOnly.FromDateTime(exchange.Date), "regular");
        }

        return (DateOnly.FromDateTime(exchange.Date), "postmarket");
    }

    private static string NormalizeSymbol(string symbol) =>
        !String.IsNullOrWhiteSpace(symbol)
            ? symbol.Trim().ToUpperInvariant()
            : throw new ArgumentException("Market symbol is required.", nameof(symbol));

    private static bool IsExtendedSessionActive(DateTimeOffset utc)
    {
        var exchangeTime = TimeZoneInfo.ConvertTime(utc, ExchangeTimeZone);
        return exchangeTime.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
               exchangeTime.TimeOfDay >= new TimeSpan(4, 0, 0) &&
               exchangeTime.TimeOfDay <= new TimeSpan(20, 0, 0);
    }

    private static bool HasLatestCompletedTimeframe(
        IReadOnlyList<OhlcvBar> bars,
        string timeframe,
        DateTimeOffset asOfUtc,
        BarResampler resampler)
    {
        if (bars.Count == 0)
        {
            return false;
        }

        var duration = TimeframeParser.Parse(timeframe);
        if (duration <= TimeSpan.FromMinutes(1) || duration >= TimeSpan.FromDays(1))
        {
            return true;
        }

        var current = resampler.GetBucketWindow(asOfUtc.ToUniversalTime().AddTicks(-1), timeframe);
        DateTimeOffset expectedStart;
        if (current.EndUtc <= asOfUtc.ToUniversalTime())
        {
            expectedStart = current.StartUtc;
        }
        else
        {
            var previous = resampler.GetBucketWindow(current.StartUtc.AddTicks(-1), timeframe);
            if (!previous.SessionSegment.Equals(current.SessionSegment, StringComparison.Ordinal))
            {
                return false;
            }

            expectedStart = previous.StartUtc;
        }

        return bars[^1].Timestamp.ToUniversalTime() >= expectedStart;
    }

    private static TimeZoneInfo ResolveExchangeTimeZone()
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

    private static MarketBarApplyResult Result(
        MarketBarDisposition disposition,
        OhlcvBar bar,
        string detail) => new(disposition, bar.Ticker, bar.Timestamp, detail);

    private static string? ResolveSingleFeed(IEnumerable<OhlcvBar> bars)
    {
        var feeds = bars
            .Select(bar => NormalizeFeed(bar.DataFeed))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (feeds.Length > 1)
        {
            throw new InvalidOperationException(
                $"Market state contains mixed data feeds: {String.Join(", ", feeds)}.");
        }

        return feeds.SingleOrDefault();
    }

    private static string NormalizeFeed(string? feed) =>
        String.IsNullOrWhiteSpace(feed) ? "unspecified" : feed.Trim().ToLowerInvariant();

    private static long AdvanceMaximum(ref long location, long candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref location);
            if (candidate <= current)
            {
                return current;
            }

            if (Interlocked.CompareExchange(ref location, candidate, current) == current)
            {
                return candidate;
            }
        }
    }

    private static string ComputeFingerprint(
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> barsByTimeframe)
    {
        var canonical = String.Join('\n', barsByTimeframe
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(pair => pair.Value.OrderBy(bar => bar.Timestamp).Select(bar => String.Join(
                '|',
                pair.Key,
                bar.Ticker,
                bar.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                bar.Open.ToString(CultureInfo.InvariantCulture),
                bar.High.ToString(CultureInfo.InvariantCulture),
                bar.Low.ToString(CultureInfo.InvariantCulture),
                bar.Close.ToString(CultureInfo.InvariantCulture),
                bar.Volume.ToString(CultureInfo.InvariantCulture)))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private sealed class SymbolPipeline
    {
        private int isEvicting;
        private long nextFaultRegistration;
        private readonly ConcurrentDictionary<long, Action<Exception>> faultRegistrations = [];

        public SymbolPipeline(string symbol, int capacity, long initialFencingToken)
        {
            Symbol = symbol;
            FencingToken = initialFencingToken;
            Block = new ActionBlock<Func<Task>>(
                action => action(),
                new ExecutionDataflowBlockOptions
                {
                    BoundedCapacity = capacity,
                    MaxDegreeOfParallelism = 1,
                    EnsureOrdered = true
                });
            _ = ObserveBlockCompletionAsync();
        }

        public string Symbol { get; }
        public ActionBlock<Func<Task>> Block { get; }
        public SortedDictionary<DateTimeOffset, List<OhlcvBar>> Bars { get; } = [];
        public HashSet<DateTimeOffset> PendingMissingMinutes { get; } = [];
        public HashSet<DateTimeOffset> ConfirmedNoTradeMinutes { get; } = [];
        public long FencingToken { get; set; }
        public bool IsReady { get; set; }
        public bool NeedsRepair { get; set; }
        public string? DataFeed { get; set; }
        public bool IsEvicting => Volatile.Read(ref isEvicting) != 0;
        public TaskCompletionSource EvictionCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryBeginEviction() => Interlocked.CompareExchange(ref isEvicting, 1, 0) == 0;

        public long RegisterFault(Action<Exception> callback)
        {
            var registration = Interlocked.Increment(ref nextFaultRegistration);
            faultRegistrations[registration] = callback;
            return registration;
        }

        public void UnregisterFault(long registration) => faultRegistrations.TryRemove(registration, out _);

        private async Task ObserveBlockCompletionAsync()
        {
            try
            {
                await Block.Completion;
            }
            catch (Exception exception)
            {
                var failure = exception.GetBaseException();
                foreach (var callback in faultRegistrations.Values)
                {
                    callback(failure);
                }
            }
        }
    }

    private sealed class SymbolPipelineUnavailableException(string symbol)
        : InvalidOperationException($"Market-state pipeline for {symbol} is full, completed, or evicting.");
}

public interface IMarketStateSnapshotProvider
{
    Task<TickerMarketState?> GetTickerStateAsync(
        string symbol,
        IReadOnlyCollection<string> requiredTimeframes,
        int minimumBarsPerTimeframe,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken);
}
