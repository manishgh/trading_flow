using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Preparation;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Wishlists;
namespace TradingFlow.Data.Preparation;

public sealed class SqlitePreparationRepository(IDbContextFactory<TradingFlowDbContext> factory) : IPreparationRepository
{
    public async Task<long> CursorAsync(Guid workflowId,CancellationToken token)
    {await using var db=await factory.CreateDbContextAsync(token);return (await db.PreparationScheduleCursors.FindAsync([workflowId],token))?.ThroughTicks ?? 0;}
    public async Task AdvanceCursorAsync(Guid workflowId,Guid definitionId,long throughTicks,CancellationToken token)
    {
        await using var db=await factory.CreateDbContextAsync(token);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO preparation_schedule_cursors (WorkflowId,ThroughTicks)
            SELECT {workflowId},{throughTicks} FROM preparation_definitions d
            WHERE d.Id={definitionId} AND d.WorkflowId={workflowId}
            AND NOT EXISTS (SELECT 1 FROM preparation_definitions newer WHERE newer.WorkflowId=d.WorkflowId AND newer.Revision>d.Revision)
            ON CONFLICT(WorkflowId) DO UPDATE SET ThroughTicks = MAX(ThroughTicks,excluded.ThroughTicks);
            """,token);
    }
    public async Task<PreparationDefinitionRecord[]> DefinitionsAsync(CancellationToken token)
    { await using var db = await factory.CreateDbContextAsync(token); return await db.PreparationDefinitions.AsNoTracking().ToArrayAsync(token); }
    public async Task<PreparationDefinitionRecord> SaveDefinitionAsync(PreparationDefinitionRecord definition, int expectedRevision, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await db.Database.OpenConnectionAsync(token);
        await using var tx = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred:false);
        await db.Database.UseTransactionAsync(tx,token);
        var previous = await db.PreparationDefinitions.Where(x => x.WorkflowId == definition.WorkflowId).OrderByDescending(x => x.Revision).FirstOrDefaultAsync(token);
        if ((previous?.Revision ?? 0) != expectedRevision || previous is not null && previous.OwnerId != definition.OwnerId)
            throw new InvalidOperationException("Workflow changed. Reload before saving.");
        if(previous is null && System.Text.Json.JsonDocument.Parse(definition.SettingsJson).RootElement.GetProperty("enabled").GetBoolean())
            throw new InvalidOperationException("New workflows must start disabled.");
        if (definition.Revision != expectedRevision+1) throw new InvalidOperationException("Invalid definition revision.");
        db.PreparationDefinitions.Add(definition); await db.SaveChangesAsync(token); await tx.CommitAsync(token); return definition;
    }
    public async Task<PreparationOccurrenceRecord[]> OccurrencesAsync(Guid workflowId, CancellationToken token)
    { await using var db=await factory.CreateDbContextAsync(token); return await db.PreparationOccurrences.AsNoTracking().Where(x=>x.WorkflowId==workflowId).OrderByDescending(x=>x.ScheduledTicks).ToArrayAsync(token); }
    public async Task<PreparationOccurrenceRecord?> OccurrenceAsync(Guid id, CancellationToken token)
    { await using var db=await factory.CreateDbContextAsync(token); return await db.PreparationOccurrences.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,token); }
    public async Task<PreparationOccurrenceRecord?> ReserveAsync(PreparationOccurrenceRecord occurrence, PersistedJob job, CancellationToken token, Wishlist? sourceWishlist = null, PreparationReviewRecord? sourceBinding = null)
    {
        if(occurrence.Id!=job.Id) throw new InvalidOperationException("Occurrence job identity mismatch.");
        await using var db=await factory.CreateDbContextAsync(token); await db.Database.OpenConnectionAsync(token);
        await using var tx=((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred:false); await db.Database.UseTransactionAsync(tx,token);
        var existing=await db.PreparationOccurrences.SingleOrDefaultAsync(x=>x.WorkflowId==occurrence.WorkflowId && x.SlotKey==occurrence.SlotKey,token);
        if(existing is not null)
        {
            if(sourceBinding is not null)
            {
                var binding=await db.PreparationReviews.SingleOrDefaultAsync(x=>x.OwnerId==sourceBinding.OwnerId && x.IdempotencyKey==sourceBinding.IdempotencyKey,token);
                if(binding?.RequestSha256!=sourceBinding.RequestSha256)throw new InvalidOperationException("Curation key already used for a different change.");
            }
            return existing;
        }
        var latest=await db.PreparationDefinitions.Where(x=>x.WorkflowId==occurrence.WorkflowId).OrderByDescending(x=>x.Revision).FirstAsync(token);
        if(latest.Id!=occurrence.DefinitionId) return null;
        if(job.Status=="queued")
        {
            var busy=await (from o in db.PreparationOccurrences join j in db.Jobs on o.Id equals j.Id
                where o.WorkflowId==occurrence.WorkflowId && (j.Status=="queued" || j.Status=="running" || j.Status=="cancelling") select o.Id).AnyAsync(token);
            if(busy || await db.Jobs.CountAsync(x=>x.JobType=="preparation" && (x.Status=="queued" || x.Status=="running" || x.Status=="cancelling"),token)>=32) return null;
        }
        db.PreparationOccurrences.Add(occurrence); db.Jobs.Add(job);
        if(sourceWishlist is not null)db.Wishlists.Add(sourceWishlist);if(sourceBinding is not null)db.PreparationReviews.Add(sourceBinding);
        await db.SaveChangesAsync(token); await tx.CommitAsync(token); return occurrence;
    }
    public async Task<bool> PublishAsync(Guid occurrenceId, Guid leaseToken, string resultJson, string? eventJson, CancellationToken token)
    {
        await using var db=await factory.CreateDbContextAsync(token); await db.Database.OpenConnectionAsync(token);
        await using var tx=((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred:false); await db.Database.UseTransactionAsync(tx,token);
        var nowTicks=await db.Database.SqlQueryRaw<long>("SELECT CAST((julianday('now') - 1721425.5) * 864000000000 AS INTEGER) AS Value").SingleAsync(token);
        var job=await db.Jobs.SingleAsync(x=>x.Id==occurrenceId,token);
        if(job.LeaseToken!=leaseToken || job.LeaseExpiresAtUtcTicks<=nowTicks || job.CancellationRequestedAtUtc is not null || job.Status!="running") return false;
        var row=await db.PreparationOccurrences.SingleAsync(x=>x.Id==occurrenceId,token);
        var definition=await db.PreparationDefinitions.SingleAsync(x=>x.Id==row.DefinitionId,token);
        // Recheck the persisted execution principal inside the publication transaction.
        if(!Guid.TryParse(definition.OwnerId,out var userId)) return false;
        var user=await db.Users.SingleOrDefaultAsync(x=>x.Id==userId,token);
        if(user is null || user.SecurityStamp!=definition.AuthorizationStamp || user.LockoutEnd > new DateTimeOffset(nowTicks,TimeSpan.Zero)) return false;
        if(row.ResultJson is not null) return true;
        var serializerOptions=new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var slot=System.Text.Json.JsonSerializer.Deserialize<TradingFlow.Contracts.Preparation.PreparationSlot>(row.SlotJson,serializerOptions)!;
        var now=new DateTimeOffset(nowTicks,TimeSpan.Zero);
        if(now>=slot.DeadlineUtc)
        {
            var snapshot=System.Text.Json.JsonSerializer.Deserialize<TradingFlow.Contracts.Preparation.PreparationSnapshot>(resultJson,serializerOptions)!;
            snapshot=snapshot with {Readiness="unavailable",Outcome="blocked",ExpiresAtUtc=now,PublishedAtUtc=now,Reasons=[..snapshot.Reasons,"Publication deadline passed at durable commit."]};
            resultJson=System.Text.Json.JsonSerializer.Serialize(snapshot,serializerOptions);
            var settings=System.Text.Json.JsonSerializer.Deserialize<TradingFlow.Contracts.Preparation.PreparationSettings>(definition.SettingsJson,serializerOptions)!;
            eventJson=settings.NotifyBlocked?System.Text.Json.JsonSerializer.Serialize(new {workflowId=row.WorkflowId,occurrenceId=row.Id,revisionId=snapshot.RevisionId,readiness="unavailable"},serializerOptions):null;
        }
        row.ResultJson=resultJson; job.SnapshotJson=resultJson;
        // Publication is the terminal linearization point. Retain the token briefly so
        // the common processor can idempotently finish clearing its lease metadata.
        job.Status="completed"; job.FinishedAt=new(nowTicks,TimeSpan.Zero); job.ResultReference=occurrenceId.ToString();
        if(eventJson is not null) db.ApplicationEvents.Add(new ApplicationEventRecord { StreamName="preparation:"+definition.OwnerId, EventType="preparation_changed", OccurredAtUtc=new(nowTicks,TimeSpan.Zero), PayloadJson=eventJson });
        await db.SaveChangesAsync(token); await tx.CommitAsync(token); return true;
    }
    public async Task PublishFailureAsync(Guid id,string resultJson,string? eventJson,CancellationToken token)
    {
        await using var db=await factory.CreateDbContextAsync(token); await db.Database.OpenConnectionAsync(token);
        await using var tx=((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred:false); await db.Database.UseTransactionAsync(tx,token);
        var row=await db.PreparationOccurrences.SingleOrDefaultAsync(x=>x.Id==id,token);
        var job=await db.Jobs.SingleOrDefaultAsync(x=>x.Id==id,token);
        if(row is null || row.ResultJson is not null || job?.Status!="failed") return;
        var definition=await db.PreparationDefinitions.SingleAsync(x=>x.Id==row.DefinitionId,token);
        row.ResultJson=resultJson;
        if(eventJson is not null) db.ApplicationEvents.Add(new ApplicationEventRecord { StreamName="preparation:"+definition.OwnerId,EventType="preparation_failed",OccurredAtUtc=job.FinishedAt ?? DateTimeOffset.UtcNow,PayloadJson=eventJson });
        await db.SaveChangesAsync(token); await tx.CommitAsync(token);
    }
    public async Task<PreparationReviewRecord?> ReviewAsync(string ownerId,string key,CancellationToken token)
    { await using var db=await factory.CreateDbContextAsync(token); return await db.PreparationReviews.AsNoTracking().SingleOrDefaultAsync(x=>x.OwnerId==ownerId && x.IdempotencyKey==key,token); }
    public async Task<PreparationReviewRecord> HandoffAsync(PreparationReviewRecord review,Wishlist wishlist,UniversePreviewRecord preview,CancellationToken token)
    {
        await using var db=await factory.CreateDbContextAsync(token); await db.Database.OpenConnectionAsync(token);
        await using var tx=((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred:false); await db.Database.UseTransactionAsync(tx,token);
        var existing=await db.PreparationReviews.SingleOrDefaultAsync(x=>x.OwnerId==review.OwnerId && x.IdempotencyKey==review.IdempotencyKey,token);
        if(existing is not null) { if(existing.RequestSha256!=review.RequestSha256) throw new InvalidOperationException("Handoff key already used for another selection."); return existing; }
        var nowTicks=await db.Database.SqlQueryRaw<long>("SELECT CAST((julianday('now') - 1721425.5) * 864000000000 AS INTEGER) AS Value").SingleAsync(token);
        if(preview.ExpiresAtUtc.UtcTicks<=nowTicks) throw new InvalidOperationException("Evidence expired during handoff. Refresh the morning review.");
        db.PreparationReviews.Add(review); db.Wishlists.Add(wishlist); db.UniversePreviews.Add(preview);
        await db.SaveChangesAsync(token); await tx.CommitAsync(token); return review;
    }
}
