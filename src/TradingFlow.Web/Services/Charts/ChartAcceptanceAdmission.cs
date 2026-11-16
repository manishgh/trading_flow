using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Contracts.Preparation;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Web.Services.Charts;

public interface IChartAcceptanceAdmission
{
    Task<(DateTimeOffset? RegularCloseUtc, string? Denial)> VerifyAsync(CancellationToken token);
}

/// <summary>Reads the existing paper health, authoritative calendar and flat-account evidence before Start.</summary>
internal sealed class ChartAcceptanceAdmission(OperationalStatusService operations, IAccountScopedBrokerOrderReader orders,
    IBrokerPositionReader positions, BrokerAccountBindingOptions accountBinding, ManualEntryOptions manualEntry,
    IOrderSynchronizationCoordinator synchronization, IAccountReconciliationService reconciliation,
    IDbContextFactory<TradingFlowDbContext> contexts, IOrderDispatchRepository dispatch,
    IOrderIntentRepository intents, IPositionLedgerRepository ledger, IOrderDispatchRecoveryHealth recovery,
    IEntryAdmissionControl admission, MobileAutomationService automation, TimeProvider clock) : IChartAcceptanceAdmission
{
    // Refuse a closed market or a host that cannot prove there is no other account work.
    public async Task<(DateTimeOffset? RegularCloseUtc, string? Denial)> VerifyAsync(CancellationToken token)
    {
        if (manualEntry.Policy != ManualEntryPolicy.StrategyGated || !accountBinding.EnforcementEnabled)
            return (null, "The configured paper account and normal strategy-gated entry policy are required.");
        if (!IsPaperHealthy())
            return (null, "The paper order stream and account reconciliation must be healthy.");
        var session = await operations.GetTradingSessionAsync(token);
        if (session is not { Session: EquityTradingSession.Regular, RegularCloseUtc: { } close } ||
            close - clock.GetUtcNow() < ChartAcceptanceSession.Lifetime)
            return (null, "The verified regular session must have at least five minutes remaining.");
        if (await HasUnrelatedWorkAsync(token))
            return (null, "Wait for all queued or active jobs, automation and order dispatches to finish.");
        if (!await IsFlatAsync(token) || !IsPaperHealthy())
            return (null, "The configured paper account must be active and flat, with no open broker orders.");
        if (await HasUnrelatedWorkAsync(token))
            return (null, "New queued or active work appeared during the flat-account check.");
        return (close, null);
    }

    // Use the same stream and reconciliation conditions shown on Operations.
    private bool IsPaperHealthy()
    {
        var sync = synchronization.GetHealth();
        var health = reconciliation.GetHealth();
        var recoveryHealth = recovery.GetHealth();
        return sync.StreamConnected && health.InitialReconciliationCompleted && health.OutstandingReconciliationId is null &&
            String.Equals(health.Status, "clean", StringComparison.OrdinalIgnoreCase) && health.DifferenceCount == 0 &&
            recoveryHealth.InitialCycleCompleted && recoveryHealth.Healthy && admission.GetSnapshot().EntriesAllowed;
    }

    // Query durable work without loading job history or creating any work.
    private async Task<bool> HasUnrelatedWorkAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var account = accountBinding.ExpectedAccountId;
        return automation.HasActiveSessions ||
            await db.Jobs.AnyAsync(job => job.Status != "completed" && job.Status != "failed" &&
                job.Status != "cancelled" && job.Status != "interrupted", token) ||
            await HasActiveCandidateAsync(db, token) || await HasEnabledPreparationAsync(db, token) ||
            await HasPendingAccountWorkAsync(db, account, token);
    }

    // Avoid relying on SQLite's DateTimeOffset ordering; only stream the expiration clocks of active states.
    private async Task<bool> HasActiveCandidateAsync(TradingFlowDbContext db, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var expirations = db.Candidates.Where(candidate => candidate.State == StrategyCandidateState.Discovered ||
            candidate.State == StrategyCandidateState.DataWarming || candidate.State == StrategyCandidateState.Qualified ||
            candidate.State == StrategyCandidateState.Armed || candidate.State == StrategyCandidateState.Triggered ||
            candidate.State == StrategyCandidateState.Disarmed || candidate.State == StrategyCandidateState.DataError)
            .Select(candidate => candidate.ExpiresAtUtc);
        await foreach (var expiration in expirations.AsAsyncEnumerable().WithCancellation(token))
            if (expiration > now) return true;
        return false;
    }

    // Check the latest revision of each workflow, matching the existing Preparation scheduler's ownership rule.
    private static async Task<bool> HasEnabledPreparationAsync(TradingFlowDbContext db, CancellationToken token)
    {
        var latest = db.PreparationDefinitions.Where(definition => !db.PreparationDefinitions.Any(newer =>
            newer.WorkflowId == definition.WorkflowId && newer.Revision > definition.Revision))
            .Select(definition => definition.SettingsJson);
        await foreach (var settings in latest.AsAsyncEnumerable().WithCancellation(token))
            if (JsonSerializer.Deserialize<PreparationSettings>(settings, StockChartHub.Json) is not { Enabled: false })
                return true;
        return false;
    }

    // A flat broker position alone cannot clear pending journal recovery, replacements, exits or reserved exposure.
    private async Task<bool> HasPendingAccountWorkAsync(TradingFlowDbContext db, string account, CancellationToken token)
    {
        return await db.ProtectiveStopReplacements.AnyAsync(command =>
                command.AccountId == account && command.State == ProtectiveStopReplacementState.Pending, token) ||
            await db.PortfolioRiskReservations.AnyAsync(reservation =>
                reservation.AccountId == account && reservation.State != PortfolioRiskReservationState.Released, token) ||
            await db.OrderIntents.AnyAsync(intent => intent.AccountId == account &&
                (intent.DispatchLeaseToken != null || db.OrderEvents.Where(evt => evt.ClientOrderId == intent.ClientOrderId)
                    .OrderByDescending(evt => evt.EventId).Select(evt => evt.NewState).FirstOrDefault() == "CANCEL_PENDING"), token) ||
            (await dispatch.ListRecoverableIntentIdsAsync(account, token)).Count != 0 ||
            (await intents.ListUnpreparedPositionExitsAsync(account, 1, token)).Count != 0 ||
            (await ledger.ListCurrentAsync(account, token)).Any(position => position.Quantity != 0m);
    }

    // Fetch fresh GET-only broker evidence, without a dependency on dispatch, order mutation or position mutation commands.
    private async Task<bool> IsFlatAsync(CancellationToken token)
    {
        var account = await orders.GetAccountSnapshotAsync(token);
        if (account.AccountId != accountBinding.ExpectedAccountId ||
            account.AccountBlocked || account.TradingBlocked || account.TradeSuspendedByUser ||
            account.LongMarketValue != 0m || account.ShortMarketValue != 0m ||
            !String.Equals(account.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase)) return false;
        return (await positions.GetOpenPositionsAsync(token)).Count == 0 &&
            (await orders.GetOpenOrdersAsync(token)).Count == 0;
    }
}
