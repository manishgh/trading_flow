using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Persistence;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

/// <summary>Read one paper run's immutable orders, admission evidence, dispatch stamps and unit fills.</summary>
public sealed class PaperExecutionComparisonService(IDbContextFactory<TradingFlowDbContext> database,
    ILogger<PaperExecutionComparisonService> logger)
{
    // Run identity is the immutable paper job ID. Reads never contact the broker.
    public async Task<PaperExecutionComparisonResult> ReadAsync(Guid runId, CancellationToken token)
    {
        try
        {
            await using var db = await database.CreateDbContextAsync(token);
            await db.Database.OpenConnectionAsync(token);
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            await using var transaction = connection.BeginTransaction(deferred: true);
            db.Database.UseTransaction(transaction);
            var intents = db.OrderIntents.AsNoTracking().Where(intent => intent.RunId == runId);
            var orderFacts = await intents.Select(intent => new
            {
                Intent = intent,
                State = db.OrderEvents.Where(evt => evt.RunId == runId && evt.ClientOrderId == intent.ClientOrderId)
                    .OrderByDescending(evt => evt.EventId).Select(evt => evt.NewState).FirstOrDefault(),
                Filled = db.OrderEvents.Where(evt => evt.RunId == runId && evt.ClientOrderId == intent.ClientOrderId &&
                    evt.FilledQuantity != null).OrderByDescending(evt => evt.EventId).Select(evt => evt.FilledQuantity).FirstOrDefault()
            }).ToArrayAsync(token);

            var admissions = await db.Set<OrderAdmissionTelemetryRecord>().AsNoTracking()
                .Where(row => intents.Any(intent => intent.IntentId == row.IntentId)).ToDictionaryAsync(row => row.IntentId, token);
            var attempts = (await db.Set<OrderDispatchAttemptTelemetryRecord>().AsNoTracking()
                .Where(row => intents.Any(intent => intent.IntentId == row.IntentId)).ToArrayAsync(token))
                .ToLookup(row => row.IntentId);
            var positionEvents = await db.PositionEvents.AsNoTracking()
                .Where(row => row.RunId == runId && row.Source == "broker_stream" &&
                    intents.Any(intent => intent.ClientOrderId == row.ClientOrderId && intent.AccountId == row.AccountId))
                .ToArrayAsync(token);
            var fills = PaperExecutionComparison.ReadUnitFills(positionEvents);
            var details = orderFacts.Select(row =>
            {
                var intent = row.Intent;
                var admission = admissions.GetValueOrDefault(intent.IntentId);
                return new PaperOrderExecutionDetail(intent, row.State ?? "Lifecycle unavailable", row.Filled, admission,
                    PaperExecutionComparison.ReadIdentity(intent, admission),
                    attempts[intent.IntentId].OrderBy(attempt => attempt.AttemptNumber).ToArray(),
                    fills.GetValueOrDefault(intent.ClientOrderId) ?? []);
            }).ToArray();
            return PaperExecutionComparison.Build(details);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidDataException)
        {
            logger.LogWarning(exception, "Could not read paper execution evidence for run {RunId}", runId);
            return new([], [], [], 0, "The durable order or fill evidence could not be read.");
        }
    }
}
