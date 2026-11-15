using TradingFlow.Application.Preparation;
using TradingFlow.Earnings;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Web.Services;

public sealed record WorkspaceStatus(
    string TradingRuntime, string MarketSession, DateTimeOffset? MarketObservedAtUtc,
    string MarketFeeds, string? OpeningRestriction, string? ExitRestriction,
    PreparationWorkerStatus PreparationWorker, NewsRefreshStatus News,
    EarningsMonitorStatus Earnings, DateTimeOffset ObservedAtUtc,
    TradingFlow.Contracts.Preparation.PreparationWorkspaceSummary? Preparation = null,
    string? PreparationReadError = null);

/// <summary>Reads only the operations facts needed in workspace chrome, without polling model or broker-account health.</summary>
public sealed class WorkspaceStatusService(
    RuntimeHostedServiceOptions runtime, OperationalStatusService operations,
    IEntryAdmissionControl admission, IPreparationCollectionCapability preparation,
    NewsFeedService news, EarningsMonitor earnings, TimeProvider clock)
{
    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private (string Label, DateTimeOffset? ObservedAtUtc, DateTimeOffset CheckedAtUtc)? market;

    // Distinguish a global pause from an entry-only stop so independently authorized exits remain available.
    public async Task<WorkspaceStatus> ReadAsync(CancellationToken token)
    {
        var session = await ReadSessionAsync(token);
        var gate = admission.GetSnapshot();
        var newsStatus = news.GetRefreshStatus();
        var earningsStatus = earnings.GetStatus();
        var restrictions = DescribeRestrictions(runtime.TradingMutationsPaused, gate);
        var feedState = newsStatus.Running || earningsStatus.IsRunning ? "Market feeds active" :
            runtime.Enabled || runtime.EnableMarketFeeds ? "Market feeds configured · running state unconfirmed" :
            "Market feeds paused";
        return new(runtime.TradingMutationsPaused ? "Trading paused" : "Trading enabled", session.Label, session.ObservedAtUtc,
            feedState, restrictions.Opening, restrictions.Exit, preparation.Read(), newsStatus,
            earningsStatus, clock.GetUtcNow());
    }

    // Global host pause is the only broad refusal here; entry admission does not govern protection or exits.
    internal static (string? Opening, string? Exit) DescribeRestrictions(bool mutationsPaused, EntryAdmissionSnapshot admission)
    {
        const string pause = "Trading is paused. This host rejects order and position changes, including exits and protection changes.";
        if (mutationsPaused) return (pause, pause);
        if (admission.EntriesAllowed) return (null, null);
        return ($"New entries are paused: {String.Join("; ", admission.Blocks.Select(block => block.Detail))}", null);
    }

    // Coalesce readers across tabs and put a short deadline on the existing calendar read.
    private async Task<(string Label, DateTimeOffset? ObservedAtUtc)> ReadSessionAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await sessionGate.WaitAsync(deadline.Token);
            try
            {
                var now = clock.GetUtcNow();
                if (market is { } cached && now - cached.CheckedAtUtc < TimeSpan.FromSeconds(15))
                    return (cached.Label, cached.ObservedAtUtc);
                var result = await operations.GetTradingSessionAsync(deadline.Token);
                var label = result?.Session switch
                {
                    EquityTradingSession.Closed => "Market closed",
                    EquityTradingSession.Regular => "Regular session",
                    EquityTradingSession.Premarket => "Premarket",
                    EquityTradingSession.AfterHours => "After hours",
                    EquityTradingSession.Overnight => "Overnight",
                    _ => "Market session unavailable (calendar could not be read)"
                };
                market = (label, result?.ObservedAtUtc, clock.GetUtcNow());
                return (label, market.Value.ObservedAtUtc);
            }
            finally { sessionGate.Release(); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return ("Market session unavailable (calendar timed out)", null);
        }
    }
}
