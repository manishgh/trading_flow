using System.Collections.Concurrent;
using System.Threading.Channels;
using TradingFlow.Alpaca;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Pipeline;

namespace TradingFlow.Web.Services;

public sealed record AlpacaMarketStateStreamOptions(
    string ResourceKey,
    int LeaseSeconds,
    int RenewEverySeconds)
{
    public static AlpacaMarketStateStreamOptions Default { get; } = new(
        "alpaca:paper:sip",
        30,
        10);

    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(LeaseSeconds);
    public TimeSpan RenewInterval => TimeSpan.FromSeconds(RenewEverySeconds);

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ResourceKey);
        if (LeaseSeconds <= 0 || RenewEverySeconds <= 0 || RenewEverySeconds >= LeaseSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RenewEverySeconds),
                "Stream renewal must be positive and shorter than its lease.");
        }
    }
}

/// <summary>
/// Sole owner of the Alpaca SIP websocket. It multiplexes market bars and
/// trading-status evidence for every discovery/run scope behind one fenced lease.
/// </summary>
public sealed class AlpacaMarketStateStreamService : BackgroundService,
    ISecurityTradingStatusProvider,
    IDiscoverySubscriptionSink
{
    private static readonly Guid StatusObservationScope =
        Guid.Parse("76077cd8-c02d-498b-a189-06bc8a11861f");
    private readonly AlpacaCredentialProvider credentials;
    private readonly IAlpacaMarketStateStreamClientFactory streamClientFactory;
    private readonly ILogger<AlpacaMarketStateStreamService> logger;
    private readonly IMarketStreamLeaseRepository leases;
    private readonly StreamingMarketStateProcessor marketState;
    private readonly Lazy<TradingFlow.Engine.Abstractions.IMarketDataProvider> marketDataProvider;
    private readonly AlpacaMarketStateStreamOptions options;
    private readonly TimeProvider timeProvider;
    private readonly string ownerInstanceId = $"{Environment.MachineName}:{Environment.ProcessId}";
    private readonly ConcurrentDictionary<string, SecurityTradingStatus> statuses =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> repairsInProgress =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, HashSet<string>> symbolsByScope = [];
    private readonly Dictionary<string, int> statusObservationCounts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim scopeGate = new(1, 1);
    private readonly Channel<bool> subscriptionChanges = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private long subscriptionGeneration;
    private string runtimeStage = "created";

    public AlpacaMarketStateStreamService(
        AlpacaCredentialProvider credentials,
        IAlpacaMarketStateStreamClientFactory streamClientFactory,
        ILogger<AlpacaMarketStateStreamService> logger,
        IMarketStreamLeaseRepository leases,
        StreamingMarketStateProcessor marketState,
        Lazy<TradingFlow.Engine.Abstractions.IMarketDataProvider> marketDataProvider,
        AlpacaMarketStateStreamOptions options,
        TimeProvider timeProvider)
    {
        this.credentials = credentials;
        this.streamClientFactory = streamClientFactory;
        this.logger = logger;
        this.leases = leases;
        this.marketState = marketState;
        this.marketDataProvider = marketDataProvider;
        this.options = options;
        this.timeProvider = timeProvider;
        options.Validate();
    }

    public async Task EnsureObservedAsync(string symbol, CancellationToken cancellationToken)
    {
        var normalized = Normalize(symbol);
        statuses.TryAdd(normalized, Unknown(normalized));
        await scopeGate.WaitAsync(cancellationToken);
        try
        {
            statusObservationCounts.TryGetValue(normalized, out var count);
            statusObservationCounts[normalized] = checked(count + 1);
            if (count > 0)
            {
                return;
            }

            var observed = symbolsByScope.GetValueOrDefault(StatusObservationScope) ?? [];
            var next = observed.ToHashSet(StringComparer.OrdinalIgnoreCase);
            next.Add(normalized);
            _ = ApplyScope(StatusObservationScope, next);
        }
        finally
        {
            scopeGate.Release();
        }
    }

    public SecurityTradingStatus GetStatus(string symbol)
    {
        var normalized = Normalize(symbol);
        return statuses.TryGetValue(normalized, out var status)
            ? status
            : Unknown(normalized);
    }

    internal Task RuntimeCompletion => ExecuteTask ?? Task.CompletedTask;
    internal string RuntimeStage => Volatile.Read(ref runtimeStage);

    public async Task ReleaseObservationAsync(string symbol, CancellationToken cancellationToken)
    {
        var normalized = Normalize(symbol);
        await scopeGate.WaitAsync(cancellationToken);
        try
        {
            if (!statusObservationCounts.TryGetValue(normalized, out var count))
            {
                return;
            }

            if (count > 1)
            {
                statusObservationCounts[normalized] = count - 1;
                return;
            }

            statusObservationCounts.Remove(normalized);
            if (!symbolsByScope.TryGetValue(StatusObservationScope, out var observed) ||
                !observed.Remove(normalized))
            {
                return;
            }

            if (observed.Count == 0)
            {
                symbolsByScope.Remove(StatusObservationScope);
            }

            subscriptionChanges.Writer.TryWrite(true);
        }
        finally
        {
            scopeGate.Release();
        }
    }

    public async Task ReplaceScopeAsync(
        Guid scopeId,
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken)
    {
        var normalized = symbols
            .Where(symbol => !String.IsNullOrWhiteSpace(symbol))
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await scopeGate.WaitAsync(cancellationToken);
        try
        {
            _ = ApplyScope(scopeId, normalized);
        }
        finally
        {
            scopeGate.Release();
        }
    }

    public async Task RemoveScopeAsync(Guid scopeId, CancellationToken cancellationToken)
    {
        await scopeGate.WaitAsync(cancellationToken);
        try
        {
            if (symbolsByScope.Remove(scopeId))
            {
                subscriptionChanges.Writer.TryWrite(true);
            }
        }
        finally
        {
            scopeGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!credentials.IsConfigured)
        {
            logger.LogWarning("Alpaca market-state stream is disabled because credentials are unavailable.");
            return;
        }

        var reconnectDelay = TimeSpan.FromMilliseconds(500);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Volatile.Write(ref runtimeStage, "acquiring_lease");
                var lease = await leases.TryAcquireAsync(
                    options.ResourceKey,
                    $"{ownerInstanceId}:{Guid.NewGuid():N}",
                    options.LeaseDuration,
                    stoppingToken);
                if (lease is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                ResetToUnknown();
                Volatile.Write(ref runtimeStage, "owned_connection");
                await RunOwnedConnectionAsync(lease, stoppingToken);
                reconnectDelay = TimeSpan.FromMilliseconds(500);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (MarketStateConnectionPoisonedException exception)
            {
                logger.LogCritical(
                    exception,
                    "Alpaca market-state service stopped fail-closed because prior connection work or transport disposal could not be proven complete.");
                ResetToUnknown();
                Volatile.Write(ref runtimeStage, "stopped_fail_closed");
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Alpaca market-state stream failed; symbol states remain UNKNOWN until a fenced reconnect succeeds.");
                ResetToUnknown();
                await Task.Delay(reconnectDelay, stoppingToken);
                reconnectDelay = TimeSpan.FromMilliseconds(Math.Min(reconnectDelay.TotalMilliseconds * 2, 30_000));
            }
        }

        Volatile.Write(ref runtimeStage, "stopped");
    }

    internal async Task ApplyMessageAsync(
        System.Text.Json.JsonElement message,
        DateTimeOffset observedAtUtc,
        long fencingToken,
        CancellationToken cancellationToken = default)
    {
        if (AlpacaMarketDataMessageParser.TryParseBar(
                message,
                observedAtUtc,
                fencingToken,
                out var marketEvent,
                out var parseFailure))
        {
            var result = await marketState.ProcessAsync(marketEvent!, cancellationToken);
            if (result.Disposition is not (
                    MarketBarDisposition.Accepted or
                    MarketBarDisposition.Duplicate or
                    MarketBarDisposition.RevisionAccepted))
            {
                logger.LogWarning(
                    "Rejected Alpaca market bar for {Symbol} at {BarTimestampUtc}. Disposition={Disposition}; Detail={Detail}",
                    result.Symbol,
                    result.BarTimestampUtc,
                    result.Disposition,
                    result.Detail);
            }

            return;
        }

        if (parseFailure is not null)
        {
            LogMalformedBar(parseFailure);
            return;
        }

        ApplyTradingStatusMessage(message, observedAtUtc);
    }

    private async Task RunOwnedConnectionAsync(
        MarketStreamLease lease,
        CancellationToken stoppingToken)
    {
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var leaseState = new OwnedLeaseState(lease);
        var activeSubscriptions = new ConcurrentDictionary<string, long>(
            StringComparer.OrdinalIgnoreCase);
        var workTracker = new ConnectionWorkTracker((operation, exception) =>
            logger.LogError(exception, "Provider-connection work failed. Operation={Operation}", operation));
        var renewal = RenewLeaseAsync(leaseState, connectionCts);
        await RefreshMarketCalendarAsync(connectionCts.Token);
        var calendarRefresh = RefreshMarketCalendarLoopAsync(connectionCts.Token);
        Task receive = Task.CompletedTask;
        Task subscriptions = Task.CompletedTask;
        var drained = false;
        var transportDisposed = false;
        var leaseReleased = false;
        IAlpacaMarketStateStreamClient? stream = null;
        try
        {
            stream = streamClientFactory.Create();
            await marketState.AdvanceFencingFloorAsync(lease.FencingToken, connectionCts.Token);
            await stream.ConnectAsync(connectionCts.Token);
            await RevalidateLeaseOwnershipAsync(leaseState, connectionCts.Token);
            marketState.ActivateOwnership(leaseState.Current.FencingToken);
            receive = ReceiveMessagesAsync(
                stream,
                leaseState,
                activeSubscriptions,
                workTracker,
                connectionCts.Token);
            var initial = await GetDesiredSymbolsAsync(connectionCts.Token);
            var stale = marketState.GetTrackedSymbols()
                .Except(initial, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (stale.Length > 0)
            {
                await marketState.RemoveSymbolsAsync(stale, connectionCts.Token);
            }

            if (initial.Count > 0)
            {
                subscriptions = SubscribeAndWarmAsync(
                    stream,
                    initial,
                    receive,
                    activeSubscriptions,
                    connectionCts.Token);
                var startupCompleted = await Task.WhenAny(subscriptions, renewal, receive);
                if (startupCompleted != subscriptions)
                {
                    await startupCompleted;
                    throw new InvalidOperationException(
                        "Alpaca stream ownership ended before initial subscription warming completed.");
                }

                await subscriptions;
            }

            marketState.MarkSnapshotsReady(leaseState.Current.FencingToken);

            subscriptions = ForwardSubscriptionChangesAsync(
                stream,
                initial,
                receive,
                activeSubscriptions,
                workTracker,
                connectionCts.Token);
            var completed = await Task.WhenAny(receive, renewal, subscriptions, calendarRefresh);
            await completed;
        }
        finally
        {
            marketState.InvalidateOwnership(leaseState.Current.FencingToken);
            Volatile.Write(ref runtimeStage, "cleanup_connection_tasks");
            activeSubscriptions.Clear();
            workTracker.StopAccepting();
            connectionCts.Cancel();
            var connectionTasks = Task.WhenAll(
                ObserveConnectionTaskAsync(receive, "receive"),
                ObserveConnectionTaskAsync(renewal, "lease-renewal"),
                ObserveConnectionTaskAsync(subscriptions, "subscription-forwarding"),
                ObserveConnectionTaskAsync(calendarRefresh, "market-calendar-refresh"));
            try
            {
                Volatile.Write(ref runtimeStage, "draining_connection_work");
                await Task.WhenAll(
                        connectionTasks,
                        workTracker.DrainAllAsync(options.LeaseDuration, CancellationToken.None))
                    .WaitAsync(options.LeaseDuration);
                drained = true;
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                logger.LogCritical(
                    exception,
                    "Provider-connection work did not drain for fence {FencingToken}; the lease will expire instead of being released.",
                    leaseState.Current.FencingToken);
            }

            if (stream is null)
            {
                transportDisposed = true;
            }
            else
            {
                Volatile.Write(ref runtimeStage, "disposing_transport");
                try
                {
                    stream.Dispose();
                    transportDisposed = true;
                }
                catch (Exception exception)
                {
                    logger.LogCritical(
                        exception,
                        "Could not dispose the Alpaca market-state transport; the lease will expire instead of being released.");
                }
            }

            if (drained && transportDisposed)
            {
                Volatile.Write(ref runtimeStage, "releasing_lease");
                leaseReleased = await ReleaseLeaseSafelyAsync(leaseState.Current);
            }

            if (!drained || !transportDisposed || !leaseReleased)
            {
                Volatile.Write(ref runtimeStage, "connection_poisoned");
                throw new MarketStateConnectionPoisonedException(
                    "The prior provider connection or lease could not be retired safely; automatic reconnect is disabled for this host instance.");
            }
        }
    }

    private async Task RefreshMarketCalendarLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6), timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await RefreshMarketCalendarAsync(cancellationToken);
        }
    }

    private async Task RefreshMarketCalendarAsync(CancellationToken cancellationToken)
    {
        if (marketDataProvider.Value is not IMarketSessionScheduleProvider scheduleProvider)
        {
            throw new InvalidOperationException(
                "The active Alpaca market-data provider does not expose an authoritative exchange calendar.");
        }

        var now = timeProvider.GetUtcNow();
        var start = DateOnly.FromDateTime(now.UtcDateTime.Date)
            .AddDays(-marketState.RecoveryLookbackDays - 10);
        var end = DateOnly.FromDateTime(now.UtcDateTime.Date).AddDays(35);
        var schedules = await scheduleProvider.LoadMarketSessionSchedulesAsync(
            start,
            end,
            cancellationToken);
        marketState.UpdateMarketSessionSchedules(schedules);
        logger.LogInformation(
            "Loaded {ScheduleCount} authoritative Alpaca market-session schedules for {StartDate} through {EndDate}.",
            schedules.Count,
            start,
            end);
    }

    private async Task RevalidateLeaseOwnershipAsync(
        OwnedLeaseState leaseState,
        CancellationToken cancellationToken)
    {
        var current = leaseState.Current;
        var renewed = await leases.TryRenewAsync(
            current.ResourceKey,
            current.OwnerId,
            current.FencingToken,
            options.LeaseDuration,
            cancellationToken);
        if (renewed is null)
        {
            throw new InvalidOperationException(
                $"Lost Alpaca market-stream lease {current.ResourceKey} fence {current.FencingToken} before subscription activation.");
        }

        leaseState.Update(renewed);
    }

    private async Task RenewLeaseAsync(
        OwnedLeaseState leaseState,
        CancellationTokenSource connectionCts)
    {
        try
        {
            while (!connectionCts.IsCancellationRequested)
            {
                await Task.Delay(options.RenewInterval, connectionCts.Token);
                var renewed = await leases.TryRenewAsync(
                    leaseState.Current.ResourceKey,
                    leaseState.Current.OwnerId,
                    leaseState.Current.FencingToken,
                    options.LeaseDuration,
                    connectionCts.Token);
                if (renewed is null)
                {
                    logger.LogError(
                        "Lost Alpaca market-stream lease {ResourceKey} fence {FencingToken}; aborting the socket.",
                        leaseState.Current.ResourceKey,
                        leaseState.Current.FencingToken);
                    marketState.InvalidateOwnership(leaseState.Current.FencingToken);
                    connectionCts.Cancel();
                    return;
                }

                leaseState.Update(renewed);
            }
        }
        catch (OperationCanceledException) when (connectionCts.IsCancellationRequested)
        {
        }
        catch
        {
            marketState.InvalidateOwnership(leaseState.Current.FencingToken);
            connectionCts.Cancel();
            throw;
        }
    }

    private async Task ForwardSubscriptionChangesAsync(
        IAlpacaMarketStateStreamClient stream,
        IReadOnlyCollection<string> initial,
        Task receive,
        ConcurrentDictionary<string, long> activeSubscriptions,
        ConnectionWorkTracker workTracker,
        CancellationToken cancellationToken)
    {
        var subscribed = initial.ToHashSet(StringComparer.OrdinalIgnoreCase);
        await foreach (var change in subscriptionChanges.Reader.ReadAllAsync(cancellationToken))
        {
            var desired = (await GetDesiredSymbolsAsync(cancellationToken))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var additions = desired.Except(subscribed, StringComparer.OrdinalIgnoreCase).ToArray();
            var removals = subscribed.Except(desired, StringComparer.OrdinalIgnoreCase).ToArray();
            if (additions.Length > 0)
            {
                await SubscribeAndWarmAsync(
                    stream,
                    additions,
                    receive,
                    activeSubscriptions,
                    cancellationToken);
            }

            if (removals.Length > 0)
            {
                foreach (var removal in removals)
                {
                    activeSubscriptions.TryRemove(removal, out _);
                }

                await stream.UnsubscribeMarketStateAsync(removals, cancellationToken);
                await AwaitSubscriptionAcknowledgementAsync(
                    stream,
                    activeSubscriptions.Keys.ToArray(),
                    receive,
                    cancellationToken);
                foreach (var removal in removals)
                {
                    await workTracker.DrainSymbolAsync(
                        removal,
                        options.LeaseDuration,
                        cancellationToken);
                    await marketState.EvictSymbolAsync(removal, cancellationToken);
                    statuses.TryRemove(removal, out var removedStatus);
                }
            }

            subscribed = desired;
        }
    }

    private async Task SubscribeAndWarmAsync(
        IAlpacaMarketStateStreamClient stream,
        IReadOnlyCollection<string> symbols,
        Task receive,
        ConcurrentDictionary<string, long> activeSubscriptions,
        CancellationToken cancellationToken)
    {
        foreach (var symbol in symbols)
        {
            activeSubscriptions[Normalize(symbol)] = Interlocked.Increment(ref subscriptionGeneration);
        }

        try
        {
            await stream.SubscribeMarketStateAsync(symbols, cancellationToken);
            await AwaitSubscriptionAcknowledgementAsync(
                stream,
                activeSubscriptions.Keys.ToArray(),
                receive,
                cancellationToken);
            await WarmAdditionsAsync(symbols, cancellationToken);
        }
        catch
        {
            foreach (var symbol in symbols)
            {
                activeSubscriptions.TryRemove(Normalize(symbol), out _);
            }

            throw;
        }
    }

    private static async Task AwaitSubscriptionAcknowledgementAsync(
        IAlpacaMarketStateStreamClient stream,
        IReadOnlyCollection<string> expectedSymbols,
        Task receive,
        CancellationToken cancellationToken)
    {
        var acknowledgement = stream.WaitForSubscriptionAcknowledgementAsync(
            expectedSymbols,
            cancellationToken);
        var completed = await Task.WhenAny(acknowledgement, receive);
        if (completed == receive)
        {
            await receive;
            throw new InvalidOperationException("Alpaca stream ended before acknowledging a subscription change.");
        }

        await acknowledgement;
    }

    private async Task ReceiveMessagesAsync(
        IAlpacaMarketStateStreamClient stream,
        OwnedLeaseState leaseState,
        ConcurrentDictionary<string, long> activeSubscriptions,
        ConnectionWorkTracker workTracker,
        CancellationToken cancellationToken)
    {
        await foreach (var message in stream.ReadMessagesAsync(cancellationToken))
        {
            var observedAt = timeProvider.GetUtcNow();
            var fencingToken = leaseState.GetValidFence(observedAt);
            if (AlpacaMarketDataMessageParser.TryParseBar(
                    message,
                    observedAt,
                    fencingToken,
                    out var marketEvent,
                    out var parseFailure))
            {
                var symbol = marketEvent!.Bar.Ticker;
                if (activeSubscriptions.TryGetValue(symbol, out var generation))
                {
                    workTracker.TryRun(
                        symbol,
                        "market-bar",
                        () => ObserveMarketBarAsync(
                            marketEvent,
                            generation,
                            activeSubscriptions,
                            workTracker,
                            cancellationToken));
                }

                continue;
            }

            if (parseFailure is not null)
            {
                LogMalformedBar(parseFailure);
                continue;
            }

            ApplyTradingStatusMessage(message, observedAt);
        }
    }

    private void LogMalformedBar(AlpacaBarParseFailure failure) =>
        logger.LogWarning(
            "Rejected malformed Alpaca market bar. MessageType={MessageType}; Symbol={Symbol}; Detail={Detail}",
            failure.MessageType,
            failure.Symbol ?? "UNKNOWN",
            failure.Detail);

    private async Task ObserveMarketBarAsync(
        MarketBarEvent marketEvent,
        long subscriptionGeneration,
        ConcurrentDictionary<string, long> activeSubscriptions,
        ConnectionWorkTracker workTracker,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!IsActiveSubscription(
                    marketEvent.Bar.Ticker,
                    subscriptionGeneration,
                    activeSubscriptions))
            {
                return;
            }

            var result = await marketState.ProcessAsync(marketEvent, cancellationToken);
            if (result.Disposition is not (
                    MarketBarDisposition.Accepted or
                    MarketBarDisposition.Duplicate or
                    MarketBarDisposition.RevisionAccepted))
            {
                logger.LogWarning(
                    "Rejected Alpaca market bar for {Symbol} at {BarTimestampUtc}. Disposition={Disposition}; Detail={Detail}",
                    result.Symbol,
                    result.BarTimestampUtc,
                    result.Disposition,
                    result.Detail);
            }

            if (marketState.RequiresRepair(result.Symbol) &&
                IsActiveSubscription(result.Symbol, subscriptionGeneration, activeSubscriptions))
            {
                ScheduleRepair(
                    result.Symbol,
                    subscriptionGeneration,
                    activeSubscriptions,
                    workTracker,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Market-state bar processing failed for {Symbol}.", marketEvent.Bar.Ticker);
            if (IsActiveSubscription(
                    marketEvent.Bar.Ticker,
                    subscriptionGeneration,
                    activeSubscriptions))
            {
                ScheduleRepair(
                    marketEvent.Bar.Ticker,
                    subscriptionGeneration,
                    activeSubscriptions,
                    workTracker,
                    cancellationToken);
            }
        }
    }

    private void ScheduleRepair(
        string symbol,
        long subscriptionGeneration,
        ConcurrentDictionary<string, long> activeSubscriptions,
        ConnectionWorkTracker workTracker,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(symbol);
        if (!repairsInProgress.TryAdd(normalized, 0))
        {
            return;
        }

        if (!workTracker.TryRun(
                normalized,
                "market-state-repair",
                () => RepairAsync(
                    normalized,
                    subscriptionGeneration,
                    activeSubscriptions,
                    cancellationToken)))
        {
            repairsInProgress.TryRemove(normalized, out _);
        }
    }

    private async Task RepairAsync(
        string symbol,
        long subscriptionGeneration,
        ConcurrentDictionary<string, long> activeSubscriptions,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!IsActiveSubscription(symbol, subscriptionGeneration, activeSubscriptions))
            {
                return;
            }

            var desired = await GetDesiredSymbolsAsync(cancellationToken);
            if (!desired.Contains(symbol, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            await marketState.BackfillSymbolAsync(symbol, marketDataProvider.Value, cancellationToken);
            desired = await GetDesiredSymbolsAsync(cancellationToken);
            if (!IsActiveSubscription(symbol, subscriptionGeneration, activeSubscriptions) ||
                !desired.Contains(symbol, StringComparer.OrdinalIgnoreCase))
            {
                await marketState.EvictSymbolAsync(symbol, CancellationToken.None);
                return;
            }

            logger.LogInformation("Repaired market-state history for {Symbol} from REST.", symbol);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "REST market-state repair failed for {Symbol}; strategy snapshots remain unavailable.", symbol);
        }
        finally
        {
            repairsInProgress.TryRemove(symbol, out _);
        }
    }

    private static bool IsActiveSubscription(
        string symbol,
        long generation,
        ConcurrentDictionary<string, long> activeSubscriptions) =>
        activeSubscriptions.TryGetValue(Normalize(symbol), out var current) && current == generation;

    private IReadOnlyList<string> ApplyScope(
        Guid scopeId,
        HashSet<string> symbols)
    {
        var before = UnionSymbols();
        var after = symbolsByScope
            .Where(pair => pair.Key != scopeId)
            .SelectMany(pair => pair.Value)
            .Concat(symbols)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = after.Except(before, StringComparer.OrdinalIgnoreCase).ToArray();
        symbolsByScope[scopeId] = symbols;
        if (!before.SetEquals(after))
        {
            subscriptionChanges.Writer.TryWrite(true);
        }

        return additions;
    }

    private async Task WarmAdditionsAsync(
        IReadOnlyCollection<string> additions,
        CancellationToken cancellationToken)
    {
        if (additions.Count == 0)
        {
            return;
        }

        foreach (var addition in additions)
        {
            statuses.TryAdd(addition, Unknown(addition));
        }

        try
        {
            await marketState.BackfillSymbolsAsync(
                additions,
                marketDataProvider.Value,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Initial batched market-state backfill failed for {SymbolCount} symbol(s); live bars remain subscribed and REST evaluation stays available.",
                additions.Count);
        }
    }

    internal async Task<IReadOnlyCollection<string>> GetDesiredSymbolsAsync(CancellationToken cancellationToken)
    {
        await scopeGate.WaitAsync(cancellationToken);
        try
        {
            return UnionSymbols().OrderBy(symbol => symbol, StringComparer.Ordinal).ToArray();
        }
        finally
        {
            scopeGate.Release();
        }
    }

    private HashSet<string> UnionSymbols() => symbolsByScope.Values
        .SelectMany(symbols => symbols)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void ApplyTradingStatusMessage(
        System.Text.Json.JsonElement message,
        DateTimeOffset observedAtUtc)
    {
        if (!message.TryGetProperty("T", out var typeProperty) ||
            !message.TryGetProperty("S", out var symbolProperty))
        {
            return;
        }

        var symbol = symbolProperty.GetString();
        if (String.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        var normalized = Normalize(symbol);
        var type = typeProperty.GetString();
        if (type == "t")
        {
            statuses.AddOrUpdate(
                normalized,
                _ => Trading(normalized, null, null, ReadTimestamp(message), observedAtUtc),
                (_, current) => current.State is SecurityTradingState.Halted or SecurityTradingState.Paused
                    ? current
                    : Trading(
                        normalized,
                        current.ProviderStatusCode,
                        current.ProviderReasonCode,
                        ReadTimestamp(message),
                        observedAtUtc));
            return;
        }

        if (type != "s")
        {
            return;
        }

        var statusCode = ReadString(message, "sc");
        var reasonCode = ReadString(message, "rc");
        statuses[normalized] = new SecurityTradingStatus(
            normalized,
            ResolveState(statusCode, reasonCode),
            statusCode,
            reasonCode,
            ReadTimestamp(message),
            observedAtUtc.ToUniversalTime());
    }

    private void ResetToUnknown()
    {
        foreach (var symbol in statuses.Keys)
        {
            statuses[symbol] = Unknown(symbol);
        }
    }

    private static SecurityTradingState ResolveState(string? statusCode, string? reasonCode)
    {
        if (statusCode is "3" or "T") return SecurityTradingState.TradingObserved;
        if (statusCode is "2" or "H") return SecurityTradingState.Halted;
        if (statusCode is "P" or "F" || reasonCode is "M" or "LUDP" or "LUDS")
        {
            return SecurityTradingState.Paused;
        }

        return SecurityTradingState.Unknown;
    }

    private SecurityTradingStatus Unknown(string symbol) =>
        new(symbol, SecurityTradingState.Unknown, null, null, null, timeProvider.GetUtcNow().ToUniversalTime());

    private static SecurityTradingStatus Trading(
        string symbol,
        string? statusCode,
        string? reasonCode,
        DateTimeOffset? providerTimestamp,
        DateTimeOffset observedAtUtc) =>
        new(
            symbol,
            SecurityTradingState.TradingObserved,
            statusCode,
            reasonCode,
            providerTimestamp,
            observedAtUtc.ToUniversalTime());

    private static string Normalize(string symbol) =>
        !String.IsNullOrWhiteSpace(symbol)
            ? symbol.Trim().ToUpperInvariant()
            : throw new ArgumentException("Symbol is required.", nameof(symbol));

    private static string? ReadString(System.Text.Json.JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) &&
        property.ValueKind == System.Text.Json.JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset? ReadTimestamp(System.Text.Json.JsonElement element) =>
        element.TryGetProperty("t", out var property) && property.TryGetDateTimeOffset(out var timestamp)
            ? timestamp.ToUniversalTime()
            : null;

    private async Task ObserveConnectionTaskAsync(Task task, string operation)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Provider connection task ended with an error during cleanup. Operation={Operation}",
                operation);
        }
    }

    internal async Task<bool> ReleaseLeaseSafelyAsync(MarketStreamLease lease)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            return await leases.TryReleaseAsync(
                lease.ResourceKey,
                lease.OwnerId,
                lease.FencingToken,
                timeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not release market-stream lease {ResourceKey} fence {FencingToken}; it will expire naturally.",
                lease.ResourceKey,
                lease.FencingToken);
            return false;
        }
    }

    private sealed class OwnedLeaseState(MarketStreamLease lease)
    {
        private MarketStreamLease current = lease;

        public MarketStreamLease Current => Volatile.Read(ref current);

        public void Update(MarketStreamLease renewed) => Volatile.Write(ref current, renewed);

        public long GetValidFence(DateTimeOffset observedAtUtc)
        {
            var lease = Current;
            if (observedAtUtc.ToUniversalTime() >= lease.ExpiresAtUtc)
            {
                throw new InvalidOperationException(
                    $"Market-stream lease {lease.ResourceKey} fence {lease.FencingToken} has expired; provider data is rejected.");
            }

            return lease.FencingToken;
        }
    }

    private sealed class MarketStateConnectionPoisonedException(string message) : Exception(message);
}
