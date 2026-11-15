using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Orders;

public sealed partial class SqliteAccountLossStateStore
{
    // Return the exact immutable stream revision; cancellation events also invalidate an old review.
    public async Task<AccountLossReviewSnapshot> GetReviewAsync(AccountLossScope scope, CancellationToken token)
    {
        await using var context = await contexts.CreateDbContextAsync(token);
        return await ReadReviewAsync(context, scope, token);
    }

    // Fence the reviewed revision, unresolved first submissions and replacement state in one writer transaction.
    public async Task<AccountLossReviewSnapshot> ReviewAsync(AccountLossScope scope, ProductionRun run,
        AccountLossReview review, AccountLossSample sample, AccountLossGuardOptions options,
        DateTimeOffset now, CancellationToken token)
    {
        if (scope.Mode != "paper") throw new InvalidOperationException("Reviewed reset is supported only for the paper account.");
        await using var context = await contexts.CreateDbContextAsync(token);
        await context.Database.OpenConnectionAsync(token);
        await using var transaction = ((SqliteConnection)context.Database.GetDbConnection())
            .BeginTransaction(IsolationLevel.Serializable, deferred: false);
        context.Database.UseTransaction(transaction);
        var previous = await ReadReviewAsync(context, scope, token);
        if (previous.Revision != review.ExpectedRevision || previous.State is null)
            throw new InvalidOperationException("Account loss state changed. Reload and review the latest evidence before retrying.");
        if (await HasUnreviewedOpeningWorkAsync(context, scope, [], token))
            throw new InvalidOperationException("Resolve working or ambiguous opening orders before resetting limits.");
        var next = AccountLossReviewPolicy.Apply(previous.State, review, sample, options, now);
        await ProductionRunPersistence.EnsureAsync(context, run, token);
        AppendState(context, scope, next, "account.loss.reviewed", now);
        context.RiskEvents.Add(new RiskEventRecord
        {
            RunId = run.RunId, SchemaVersion = run.SchemaVersion, CodeVersion = run.CodeVersion,
            ConfigHash = run.ConfigHash, EventType = "account_loss_reviewed_reset", Severity = "information",
            OccurredAtUtc = now, DetailsJson = JsonSerializer.Serialize(new
            {
                scope, review, previous, next, sample.Equity, sample.RequestedAtUtc,
                sample.ObservedAtUtc, sample.Evidence
            }, Json)
        });
        // Fresh evidence can newly breach a daily/weekly limit; retain the pending sweep and its audit.
        foreach (var breach in AccountLossLimitPolicy.NewBreaches(previous.State, next))
            context.RiskEvents.Add(new RiskEventRecord
            {
                RunId = run.RunId, SchemaVersion = run.SchemaVersion, CodeVersion = run.CodeVersion,
                ConfigHash = run.ConfigHash, EventType = $"account_{breach.Code}_loss_breach",
                Severity = "critical", ObservedValue = breach.ObservedPct, LimitValue = breach.LimitPct,
                OccurredAtUtc = breach.ObservedAtUtc, DetailsJson = JsonSerializer.Serialize(new { scope, review, next }, Json)
            });
        await context.SaveChangesAsync(token);
        var result = await ReadReviewAsync(context, scope, token);
        await transaction.CommitAsync(token);
        return result;
    }

    // Read revision and body together so an intervening append cannot mix their identities.
    private static async Task<AccountLossReviewSnapshot> ReadReviewAsync(
        TradingFlowDbContext context, AccountLossScope scope, CancellationToken token)
    {
        var stream = Stream(scope);
        var row = await context.ApplicationEvents.AsNoTracking().Where(item => item.StreamName == stream)
            .OrderByDescending(item => item.EventSequence).FirstOrDefaultAsync(token);
        return row is null ? new(0, null) : new(row.EventSequence,
            JsonSerializer.Deserialize<AccountLossState>(row.PayloadJson, Json)
                ?? throw new InvalidDataException("Account loss evidence is unreadable."));
    }
}
