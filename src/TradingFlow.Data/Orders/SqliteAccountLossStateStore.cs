using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Orders;

public sealed partial class SqliteAccountLossStateStore(IDbContextFactory<TradingFlowDbContext> contexts) : IAccountLossStateStore, IAccountLossReviewStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Read the exact broker account and mode independently from any strategy run.
    public async Task<AccountLossState?> GetAsync(AccountLossScope scope, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        return await ReadAsync(context, scope, cancellationToken);
    }

    // Serialize observation ordering and first-breach events under the same SQLite writer fence.
    public async Task<AccountLossState> RecordAsync(
        AccountLossScope scope, ProductionRun run, AccountLossSample sample,
        AccountLossGuardOptions options, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = ((SqliteConnection)context.Database.GetDbConnection())
            .BeginTransaction(IsolationLevel.Serializable, deferred: false);
        context.Database.UseTransaction(transaction);
        var previous = await ReadAsync(context, scope, cancellationToken);
        var next = AccountLossLimitPolicy.Observe(previous, sample, options.Limits);
        if (next == previous)
        {
            return next;
        }

        AppendState(context, scope, next, "account.loss.observed", next.ObservationUtc);

        var newBreaches = AccountLossLimitPolicy.NewBreaches(previous, next);
        if (newBreaches.Count > 0)
        {
            await ProductionRunPersistence.EnsureAsync(context, run, cancellationToken);
            foreach (var breach in newBreaches)
            {
                context.RiskEvents.Add(new RiskEventRecord
                {
                    RunId = run.RunId,
                    SchemaVersion = run.SchemaVersion,
                    CodeVersion = run.CodeVersion,
                    ConfigHash = run.ConfigHash,
                    EventType = $"account_{breach.Code}_loss_breach",
                    Severity = "critical",
                    ObservedValue = breach.ObservedPct,
                    LimitValue = breach.LimitPct,
                    OccurredAtUtc = breach.ObservedAtUtc,
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        scope, next.Session, next.PriorClose, next.WeekAnchor, next.PeakEpochStartUtc,
                        next.PeakEquity, sample.Equity, sample.RequestedAtUtc, sample.ObservedAtUtc,
                        sample.Evidence?.HistoryArchiveId
                    }, Json)
                });
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return next;
    }

    // A bracket cancellation exception is a durable account-level block. Clear
    // only the exact recorded order after the broker proves its terminal state.
    public async Task SetCancellationReviewAsync(
        AccountLossScope scope, ProductionRun run, AccountLossCancellationReview review,
        bool required, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = ((SqliteConnection)context.Database.GetDbConnection())
            .BeginTransaction(IsolationLevel.Serializable, deferred: false);
        context.Database.UseTransaction(transaction);
        var current = await ReadAsync(context, scope, cancellationToken)
            ?? throw new InvalidOperationException("Account loss state is missing before cancellation review.");
        var pending = (current.CancellationReviews ?? []).ToList();
        var existing = pending.FindIndex(item => item.BrokerOrderId == review.BrokerOrderId &&
            item.ClientOrderId == review.ClientOrderId);
        if (required == (existing >= 0))
        {
            return;
        }
        if (required)
        {
            pending.Add(review);
        }
        else
        {
            pending.RemoveAt(existing);
        }

        await ProductionRunPersistence.EnsureAsync(context, run, cancellationToken);
        var eventType = required ? "account_loss_cancel_review_required" : "account_loss_cancel_review_resolved";
        var now = await ProductionRunPersistence.ReadDatabaseTimeAsync(context, cancellationToken);
        AppendState(context, scope, current with { CancellationReviews = pending }, eventType, now);
        context.RiskEvents.Add(new RiskEventRecord
        {
            RunId = run.RunId,
            SchemaVersion = run.SchemaVersion,
            CodeVersion = run.CodeVersion,
            ConfigHash = run.ConfigHash,
            EventType = eventType,
            Symbol = review.Symbol,
            Severity = required ? "critical" : "information",
            OccurredAtUtc = now,
            DetailsJson = JsonSerializer.Serialize(new { scope, review }, Json)
        });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Finish only the observed generation after its fresh broker sweep. A
    // dispatch that had already reached its first-POST boundary may still be in flight.
    public async Task<bool> CompleteCancellationSweepAsync(
        AccountLossScope scope, long generation, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = ((SqliteConnection)context.Database.GetDbConnection())
            .BeginTransaction(IsolationLevel.Serializable, deferred: false);
        context.Database.UseTransaction(transaction);
        var current = await ReadAsync(context, scope, cancellationToken);
        if (current is not { CancellationSweepPending: true } || current.CancellationSweepGeneration != generation ||
            await HasUnreviewedOpeningWorkAsync(context, scope, current.CancellationReviews ?? [], cancellationToken))
        {
            return false;
        }

        var now = await ProductionRunPersistence.ReadDatabaseTimeAsync(context, cancellationToken);
        AppendState(context, scope, current with { CancellationSweepPending = false }, "account.loss.sweep_completed", now);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    // A missing REST row does not prove that a recorded first POST resolved.
    // Known working orders may leave the sweep only under an exact durable review.
    private static async Task<bool> HasUnreviewedOpeningWorkAsync(
        TradingFlowDbContext context, AccountLossScope scope, IReadOnlyList<AccountLossCancellationReview> reviews,
        CancellationToken cancellationToken)
    {
        var modes = scope.Mode == "paper" ? new[] { "paper", "paper_shadow", "paper_experiment" } : new[] { scope.Mode };
        var opening = from intent in context.OrderIntents
                      join run in context.ProductionRuns on intent.RunId equals run.RunId
                      where intent.AccountId == scope.AccountId && modes.Contains(run.Profile) &&
                            (intent.Kind == OrderIntentKind.StrategyEntry || intent.Kind == OrderIntentKind.OperatorEntry)
                      select intent;
        var latestIds = from evt in context.OrderEvents
                        join intent in opening on evt.ClientOrderId equals intent.ClientOrderId
                        group evt by evt.ClientOrderId into events
                        select events.Max(evt => evt.EventId);
        var possibleWork = await (from intent in opening
                                  join evt in context.OrderEvents on intent.ClientOrderId equals evt.ClientOrderId
                                  where latestIds.Contains(evt.EventId) &&
                                        (evt.NewState == "ACKED" || evt.NewState == "PARTIALLY_FILLED" ||
                                         evt.NewState == "CANCEL_PENDING" ||
                                         (intent.DispatchAttemptCount > 0 && (evt.NewState == "SUBMITTED" || evt.NewState == "INTENT")))
                                  select new { intent.ClientOrderId, evt.BrokerOrderId })
            .ToArrayAsync(cancellationToken);
        return possibleWork.Any(order => !reviews.Any(review =>
            review.ClientOrderId == order.ClientOrderId && review.BrokerOrderId == order.BrokerOrderId));
    }

    // Append an auditable state change without changing the last observed broker clock.
    private static void AppendState(
        TradingFlowDbContext context, AccountLossScope scope, AccountLossState state, string eventType, DateTimeOffset occurred)
    {
        context.ApplicationEvents.Add(new ApplicationEventRecord
        {
            StreamName = Stream(scope),
            EventType = eventType,
            OccurredAtUtc = occurred,
            PayloadJson = JsonSerializer.Serialize(state, Json)
        });
    }

    // Read the latest immutable observation using the existing indexed application event stream.
    internal static async Task<AccountLossState?> ReadAsync(
        TradingFlowDbContext context, AccountLossScope scope, CancellationToken cancellationToken)
    {
        var stream = Stream(scope);
        var json = await context.ApplicationEvents.AsNoTracking().Where(row => row.StreamName == stream)
            .OrderByDescending(row => row.EventSequence).Select(row => row.PayloadJson)
            .FirstOrDefaultAsync(cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<AccountLossState>(json, Json)
            ?? throw new InvalidDataException("Account loss evidence is unreadable.");
    }

    // Hash the account key to avoid storing broker identifiers in indexed stream names.
    private static string Stream(AccountLossScope scope) =>
        "account-loss:" + TradingFlow.Domain.Research.EvidenceCanonicalJson.ComputeSha256(scope);
}
