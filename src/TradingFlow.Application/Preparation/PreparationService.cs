using TradingFlow.Application.Candidates;
using TradingFlow.Application.Jobs;
using TradingFlow.Contracts.Preparation;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Preparation;
using TradingFlow.Domain.Wishlists;
namespace TradingFlow.Application.Preparation;

public sealed class PreparationService(IPreparationRepository repository, IDurableJobRepository jobs,
    IPreparationCalendar calendar, IPreparationAuthorization authorization, IUniverseSourceResolver sources,
    IPreparationEvidence evidence, IWishlistRepository wishlists, TimeProvider clock) : IDurableJobHandler
{
    public string JobType => "preparation";
    public DurableJobRecoveryMode RecoveryMode => DurableJobRecoveryMode.ReplayFromStart;
    public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
    public Task RecoverAsync(PersistedJob job, CancellationToken token) => Task.CompletedTask;
    public async Task OnTerminalPublishedAsync(PersistedJob job,CancellationToken token)
    {
        if(job.Status!="failed") return;
        var row=await repository.OccurrenceAsync(job.Id,token); if(row is null || row.ResultJson is not null) return;
        var definition=(await repository.DefinitionsAsync(token)).Single(x=>x.Id==row.DefinitionId);
        var settings=PreparationJson.Read<PreparationSettings>(definition.SettingsJson);
        var result=Unavailable(row.Id,job.ErrorMessage ?? "Discovery failed.","failed");
        await repository.PublishFailureAsync(job.Id,PreparationJson.Write(result),settings.NotifyFailed?PreparationJson.Write(new { workflowId=row.WorkflowId,occurrenceId=row.Id,revisionId=result.RevisionId,readiness="failed" }):null,token);
    }
    public async Task RefreshProjectionAsync(CancellationToken token)
    { foreach(var job in await jobs.GetJobsAsync(JobType,token)) if(job.Status=="failed") await OnTerminalPublishedAsync(job,token); }

    public async Task<PreparationDefinition[]> ListAsync(string owner,CancellationToken token) =>
        (await repository.DefinitionsAsync(token)).Where(x=>x.OwnerId==owner).GroupBy(x=>x.WorkflowId)
        .Select(x=>PreparationJson.View(x.MaxBy(y=>y.Revision)!)).OrderBy(x=>x.Settings.Name).ToArray();

    public async Task<PreparationDefinition> SaveAsync(string owner,SavePreparationRequest request,CancellationToken token)
    {
        PreparationPlanning.Validate(request.Settings);
        var stamp=await authorization.StampAsync(owner,token) ?? throw new InvalidOperationException("Operator access is no longer valid.");
        if(request.ExpectedRevision==0 && request.Settings.Enabled || request.WorkflowId==Guid.Empty && request.ExpectedRevision!=0)
            throw new InvalidOperationException("New workflows must be saved disabled. Review the schedule before enabling.");
        foreach(var source in request.Settings.Sources.Where(x=>x.Kind=="wishlist"))
            if(await wishlists.GetByIdAsync(source.WishlistId ?? Guid.Parse(source.Key),token) is null)
                throw new InvalidOperationException("A selected wishlist no longer exists.");
        if(request.Settings.Enabled) await PlannedAsync(request.Settings,clock.GetUtcNow(),token);
        var row=new PreparationDefinitionRecord { Id=Guid.NewGuid(),WorkflowId=request.WorkflowId==Guid.Empty?Guid.NewGuid():request.WorkflowId,
            Revision=request.ExpectedRevision+1,OwnerId=owner,AuthorizationStamp=stamp,EffectiveTicks=clock.GetUtcNow().UtcTicks,
            SettingsJson=PreparationJson.Write(request.Settings),ContentSha256=PreparationJson.Hash(request.Settings) };
        return PreparationJson.View(await repository.SaveDefinitionAsync(row,request.ExpectedRevision,token));
    }
    private async Task<PreparationDefinitionRecord> OwnedAsync(string owner,Guid id,CancellationToken token) =>
        (await repository.DefinitionsAsync(token)).Where(x=>x.OwnerId==owner && x.WorkflowId==id).MaxBy(x=>x.Revision)
        ?? throw new KeyNotFoundException("Preparation workflow not found.");
    private async Task<PreparationSlot[]> PlannedAsync(PreparationSettings settings,DateTimeOffset effective,CancellationToken token)
    {
        var now=clock.GetUtcNow();
        var result=await calendar.LoadAsync(DateOnly.FromDateTime(effective.UtcDateTime).AddDays(-7),DateOnly.FromDateTime(now.UtcDateTime).AddDays(10),token);
        return PreparationPlanning.Plan(settings,result,clock.GetUtcNow(),effective);
    }
    public async Task<PreparationDetail> DetailAsync(string owner,Guid id,CancellationToken token)
    {
        var def=await OwnedAsync(owner,id,token); var view=PreparationJson.View(def);
        PreparationSlot[] next=[], collectionWindows=[]; string? planningError=null;
        var readAt=clock.GetUtcNow();
        try
        {
            // One authoritative plan drives both upcoming slots and manual collection availability.
            var slots=await PlannedAsync(view.Settings,readAt.AddDays(-7),token);
            readAt=clock.GetUtcNow();
            next=slots.Where(x=>x.ScheduledAtUtc>=readAt).Take(20).ToArray();
            collectionWindows=CurrentCollectionWindows(slots,readAt);
        }
        catch(Exception ex) when(ex is not OperationCanceledException) { planningError="Exchange calendar unavailable. Check provider access; scheduling requires authoritative session data."; }
        var all=(await repository.DefinitionsAsync(token)).ToDictionary(x=>x.Id);
        var history=new List<PreparationOccurrence>();
        foreach(var row in await repository.OccurrencesAsync(id,token)) history.Add(await ViewAsync(row,all[row.DefinitionId].Revision,token));
        return new(view,next,history.ToArray(),planningError,collectionWindows,readAt);
    }
    private static PreparationSlot[] CurrentCollectionWindows(PreparationSlot[] slots,DateTimeOffset now) =>
        slots.Where(x=>x.ScheduledAtUtc<=now && now<x.DeadlineUtc)
            .GroupBy(x=>x.Phase,StringComparer.Ordinal)
            .Select(x=>x.MaxBy(slot=>slot.ScheduledAtUtc)!).ToArray();
    private async Task<PreparationOccurrence> ViewAsync(PreparationOccurrenceRecord row,int revision,CancellationToken token)
    {
        var job=await jobs.GetJobAsync(row.Id,token); var snapshot=row.ResultJson is null?null:PreparationJson.Read<PreparationSnapshot>(row.ResultJson);
        if(snapshot is not null) snapshot=snapshot with { Symbols=snapshot.Symbols.Select(x=>x with {InputEvidenceJson=null}).ToArray() };
        return new(row.Id,row.WorkflowId,revision,PreparationJson.Read<PreparationSlot>(row.SlotJson),
            job?.Status is "cancelled" or "failed" ? job.Status : snapshot?.Outcome ?? job?.Status ?? "unavailable",job?.ErrorMessage,snapshot);
    }
    public async Task TickAsync(CancellationToken token)
    {
        var definitions=(await repository.DefinitionsAsync(token)).GroupBy(x=>x.WorkflowId).Select(x=>x.MaxBy(y=>y.Revision)!).ToArray();
        foreach(var def in definitions)
        {
            var settings=PreparationJson.Read<PreparationSettings>(def.SettingsJson);
            if(!settings.Enabled || await authorization.StampAsync(def.OwnerId,token)!=def.AuthorizationStamp) continue;
            var now=clock.GetUtcNow();
            var after=new DateTimeOffset(Math.Max(def.EffectiveTicks,await repository.CursorAsync(def.WorkflowId,token)),TimeSpan.Zero);
            var through=after.AddDays(7)<now?after.AddDays(7):now;
            var data=await calendar.LoadAsync(DateOnly.FromDateTime(after.UtcDateTime).AddDays(-7),DateOnly.FromDateTime(through.UtcDateTime).AddDays(10),token);
            var plan=PreparationPlanning.Plan(settings,data,clock.GetUtcNow(),new(def.EffectiveTicks,TimeSpan.Zero));
            var advance=through;
            foreach(var slot in plan.Where(x=>x.ScheduledAtUtc>after && x.ScheduledAtUtc<=through))
            {
                // One poll interval is scheduling tolerance, not permission for retrospective work.
                var missed=now>=slot.DeadlineUtc || now-slot.ScheduledAtUtc>TimeSpan.FromMinutes(settings.CatchUpMinutes)+TimeSpan.FromSeconds(30);
                if(await ReserveAsync(def,slot,$"{slot.TargetDate:yyyy-MM-dd}:{slot.Phase}:{slot.ScheduledAtUtc:O}",missed,token) is null)
                {advance=slot.ScheduledAtUtc.AddTicks(-1);break;}
            }
            await repository.AdvanceCursorAsync(def.WorkflowId,def.Id,advance.UtcTicks,token);
        }
    }
    public async Task<PreparationOccurrence> RunAsync(string owner,Guid workflowId,PreparationRunRequest request,CancellationToken token)
    {
        RequireKey(request.IdempotencyKey);
        var def=await OwnedAsync(owner,workflowId,token);
        if(await authorization.StampAsync(owner,token)!=def.AuthorizationStamp) throw new InvalidOperationException("Save the workflow again after authorization changed.");
        var s=PreparationJson.Read<PreparationSettings>(def.SettingsJson);
        if(request.Phase is not ("nightly" or "morning")) throw new InvalidOperationException("Choose nightly or morning.");
        var existing=(await repository.OccurrencesAsync(workflowId,token)).FirstOrDefault(x=>x.SlotKey=="manual:"+request.IdempotencyKey);
        if(existing is not null)
        {
            if(PreparationJson.Read<PreparationSlot>(existing.SlotJson).Phase!=request.Phase) throw new InvalidOperationException("Run key already used for another phase.");
            var old=(await repository.DefinitionsAsync(token)).Single(x=>x.Id==existing.DefinitionId);
            return await ViewAsync(existing,old.Revision,token);
        }
        var slots=await PlannedAsync(s,clock.GetUtcNow().AddDays(-7),token);
        var now=clock.GetUtcNow();
        var slot=CurrentCollectionWindows(slots,now).SingleOrDefault(x=>x.Phase==request.Phase)
            ?? throw new InvalidOperationException("Run now is outside the configured exchange-session collection window.");
        var row=await ReserveAsync(def,slot with { ScheduledAtUtc=now },"manual:"+request.IdempotencyKey,false,token)
            ?? throw new InvalidOperationException("This workflow already has active work, or the discovery queue is full.");
        return await ViewAsync(row,def.Revision,token);
    }
    private async Task<PreparationOccurrenceRecord?> ReserveAsync(PreparationDefinitionRecord def,PreparationSlot slot,string key,bool missed,CancellationToken token)
    {
        var now=clock.GetUtcNow(); var id=Guid.NewGuid();
        var snapshot=missed?new PreparationSnapshot(id,null,now,now,now,"unavailable","missed",["Scheduled collection window was missed."],[],[],[]):null;
        var row=new PreparationOccurrenceRecord { Id=id,WorkflowId=def.WorkflowId,DefinitionId=def.Id,SlotKey=key,
            ScheduledTicks=slot.ScheduledAtUtc.UtcTicks,SlotJson=PreparationJson.Write(slot),ResultJson=snapshot is null?null:PreparationJson.Write(snapshot) };
        return await repository.ReserveAsync(row,new PersistedJob { Id=id,JobType=JobType,RunName="preparation-"+id.ToString("N"),
            RequestJson=PreparationJson.Write(new { occurrenceId=id }),Status=missed?"completed":"queued",CreatedAt=now,CreatedAtUtcTicks=now.UtcTicks,
            FinishedAt=missed?now:null,SnapshotJson=row.ResultJson },token);
    }
    public async Task<PreparationOccurrence> CancelAsync(string owner,Guid workflowId,Guid occurrenceId,CancellationToken token)
    {
        await OwnedAsync(owner,workflowId,token);
        var row=await repository.OccurrenceAsync(occurrenceId,token);
        if(row?.WorkflowId!=workflowId) throw new KeyNotFoundException("Occurrence not found.");
        await jobs.RequestCancellationAsync(occurrenceId,clock.GetUtcNow(),token);
        var def=(await repository.DefinitionsAsync(token)).Single(x=>x.Id==row.DefinitionId);
        return await ViewAsync(row,def.Revision,token);
    }
    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context,CancellationToken token)
    {
        var row=await repository.OccurrenceAsync(context.Job.Id,token) ?? throw new InvalidOperationException("Occurrence missing.");
        if(row.ResultJson is not null) return DurableJobCompletion.Completed(row.ResultJson,row.Id.ToString());
        var def=(await repository.DefinitionsAsync(token)).Single(x=>x.Id==row.DefinitionId);
        var s=PreparationJson.Read<PreparationSettings>(def.SettingsJson); var slot=PreparationJson.Read<PreparationSlot>(row.SlotJson);
        if(await authorization.StampAsync(def.OwnerId,token)!=def.AuthorizationStamp) throw new InvalidOperationException("Scheduled operator authorization was revoked.");
        PreparationSnapshot? result=null;
        for(var attempt=0;attempt<s.MaximumAttempts;attempt++)
        {
            var remaining=slot.DeadlineUtc-clock.GetUtcNow();
            if(remaining<=TimeSpan.Zero) break;
            using var budget=CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(remaining<TimeSpan.FromSeconds(s.CollectionTimeoutSeconds)?remaining:TimeSpan.FromSeconds(s.CollectionTimeoutSeconds));
            try { result=await CollectAsync(row,slot,s,context.Job.RequestJson,budget.Token); }
            catch(OperationCanceledException) when(!token.IsCancellationRequested) { result=Unavailable(row.Id,"Collection timed out.","blocked"); }
            if(result?.Readiness=="complete" || clock.GetUtcNow().AddSeconds(s.RetrySeconds)>=slot.DeadlineUtc) break;
            if(attempt+1<s.MaximumAttempts) await Task.Delay(TimeSpan.FromSeconds(s.RetrySeconds),token);
        }
        result ??= Unavailable(row.Id,"Publication deadline passed.","missed");
        if(clock.GetUtcNow()>=slot.DeadlineUtc && result.Outcome!="missed") result=result with { Readiness="unavailable",Outcome="blocked",ExpiresAtUtc=clock.GetUtcNow(),Reasons=[..result.Reasons,"Publication deadline passed."] };
        if(await authorization.StampAsync(def.OwnerId,token)!=def.AuthorizationStamp) throw new InvalidOperationException("Scheduled operator authorization was revoked.");
        var prior=(await repository.OccurrencesAsync(row.WorkflowId,token)).Where(x=>x.Id!=row.Id && x.ResultJson is not null && x.ScheduledTicks<row.ScheduledTicks && PreparationJson.Read<PreparationSlot>(x.SlotJson).TargetDate==slot.TargetDate && PreparationJson.Read<PreparationSlot>(x.SlotJson).Phase==slot.Phase)
            .OrderByDescending(x=>x.ScheduledTicks).Select(x=>PreparationJson.Read<PreparationSnapshot>(x.ResultJson!)).FirstOrDefault();
        var notify=(result.Readiness=="complete"?s.NotifyReady:s.NotifyBlocked) && (prior is null || prior.Readiness!=result.Readiness || !prior.Symbols.Select(x=>(x.Symbol,x.Readiness)).SequenceEqual(result.Symbols.Select(x=>(x.Symbol,x.Readiness))));
        var json=PreparationJson.Write(result);
        if(!await repository.PublishAsync(row.Id,context.LeaseToken,json,notify?PreparationJson.Write(new { workflowId=row.WorkflowId,occurrenceId=row.Id,revisionId=result.RevisionId,readiness=result.Readiness }):null,token))
            throw new DurableJobLeaseLostException(row.Id);
        return DurableJobCompletion.Completed((await repository.OccurrenceAsync(row.Id,token))!.ResultJson,row.Id.ToString());
    }
    private PreparationSnapshot Unavailable(Guid id,string reason,string outcome) => new(id,null,clock.GetUtcNow(),clock.GetUtcNow(),clock.GetUtcNow(),"unavailable",outcome,[reason],[],[],[]);
    private async Task<PreparationSnapshot> CollectAsync(PreparationOccurrenceRecord row,PreparationSlot slot,PreparationSettings s,string requestJson,CancellationToken token)
    {
        using var command=System.Text.Json.JsonDocument.Parse(requestJson);
        var curation=command.RootElement.TryGetProperty("curation",out var extra)?PreparationJson.Read<PreparationCurationEvidence>(extra.GetRawText()):null;
        var previous=(await repository.OccurrencesAsync(row.WorkflowId,token)).Where(x=>x.Id!=row.Id && x.ResultJson is not null)
            .Select(x=>(Slot:PreparationJson.Read<PreparationSlot>(x.SlotJson),Snapshot:PreparationJson.Read<PreparationSnapshot>(x.ResultJson!)))
            .Where(x=>x.Slot.TargetDate==slot.TargetDate && x.Slot.Phase=="nightly" && x.Snapshot.Readiness=="complete")
            .OrderByDescending(x=>x.Snapshot.PublishedAtUtc).Select(x=>x.Snapshot).FirstOrDefault();
        var problems=new List<string>(); var blocked=false;
        if(slot.Phase=="morning" && previous is null && !s.StandaloneMorning) { blocked=true; problems.Add("Complete nightly preparation is missing."); }
        var captures=new List<PreparationSourceEvidence>();
        foreach(var source in s.Sources)
        {
            try
            {
                var capture=await sources.ResolveAsync(new(source.Kind,source.Key,source.WishlistId,source.Query),"swing",token);
                if(capture.IsDiagnostic || capture.ObservedAtUtc.Offset!=TimeSpan.Zero || capture.ObservedAtUtc>clock.GetUtcNow() || capture.ExpiresAtUtc<=capture.ObservedAtUtc)
                    throw new InvalidOperationException("Invalid source observation provenance.");
                var names=capture.Symbols.Select(x=>x.Symbol.Trim().ToUpperInvariant()).Distinct().Order().ToArray();
                captures.Add(new(source,"complete",null,capture.ObservedAtUtc,capture.ExpiresAtUtc,PreparationJson.Hash(capture),capture.ProviderReference,names));
            }
            catch(Exception ex) when(ex is not OperationCanceledException)
            { captures.Add(new(source,"failed",ex.Message,null,null,null,null,[])); problems.Add($"{source.Kind} {source.Key}: {ex.Message}"); blocked|=source.Required; }
        }
        if(s.Merge=="intersection" && captures.Any(x=>x.Status!="complete"))
        { blocked=true; problems.Add("Intersection membership cannot be established while any selected source is unavailable."); }
        var valid=captures.Where(x=>x.Status=="complete").ToArray();
        var namesSet=valid.Length==0?new HashSet<string>():valid[0].Symbols.ToHashSet();
        foreach(var source in valid.Skip(1)) { if(s.Merge=="intersection") namesSet.IntersectWith(source.Symbols); else namesSet.UnionWith(source.Symbols); }
        if(slot.Phase=="morning" && !s.FreshDiscoveries && previous is not null) namesSet.IntersectWith(previous.Symbols.Select(x=>x.Symbol));
        if(curation is not null)
        {
            var persisted=await wishlists.GetByIdAsync(curation.WishlistId,token) ?? throw new InvalidOperationException("Curated source revision is missing.");
            var additions=persisted.Items.Where(x=>x.Active).Select(x=>x.Ticker).Order().ToArray();
            if(!additions.SequenceEqual((curation.EffectiveAddSymbols ?? curation.Request.AddSymbols).Distinct().Order()))throw new InvalidOperationException("Curated source integrity failed.");
            var observed=clock.GetUtcNow();
            namesSet.UnionWith(additions);
            namesSet.ExceptWith(curation.EffectiveRemoveSymbols ?? curation.Request.RemoveSymbols);
            var source=new PreparationSource("wishlist",curation.WishlistId.ToString(),true,curation.WishlistId);
            captures.Add(new(source,"complete",null,observed,observed.AddMinutes(3),PreparationJson.Hash(new{curation,observed,additions}),"operator-curation:"+curation.Request.ParentRevisionId,additions));
        }
        var capacityExceeded=namesSet.Count>s.MaximumSymbols;
        if(capacityExceeded) { blocked=true; problems.Add($"Capacity exceeded: {namesSet.Count} discovered symbols; limit {s.MaximumSymbols}. No complete handoff is available."); }
        var observations=new System.Collections.Concurrent.ConcurrentBag<PreparationSymbol>();
        await Parallel.ForEachAsync(namesSet.Order().Take(s.MaximumSymbols),new ParallelOptions { MaxDegreeOfParallelism=s.ParallelSymbols,CancellationToken=token },async(symbol,ct)=>
        {
            var item=await evidence.CollectAsync(symbol,slot,s,clock.GetUtcNow(),ct);
            observations.Add(item with { Change=slot.Phase=="nightly"?"prepared":previous?.Symbols.Any(x=>x.Symbol==symbol)==true?"retained":"new" });
        });
        var cutoff=clock.GetUtcNow();
        if(slot.Phase=="morning")
        {
            foreach(var source in captures.Where(x=>x.Status=="complete").ToArray())
                if(source.ExpiresAtUtc<=cutoff || cutoff-source.ObservedAtUtc>TimeSpan.FromMinutes(s.MaximumSourceAgeMinutes))
                { captures[captures.IndexOf(source)]=source with { Status="stale",Reason="Source validity ended during collection." }; blocked|=source.Source.Required || s.Merge=="intersection"; problems.Add($"Stale source: {source.Source.Key}"); }
        }
        if(curation is not null)
            foreach(var removed in (curation.EffectiveRemoveSymbols ?? curation.Request.RemoveSymbols).Distinct())
                observations.Add(new(removed,"removed","excluded",["Operator removed: "+curation.Request.Reason],null,0,null,null,null,null,null,null,null,"not_collected","not_collected",PreparationJson.Hash(new{curation,removed}),[],["Excluded before resource counting and collection. Parent evidence remains in the prior revision."]));
        var symbols=observations.OrderBy(x=>x.Symbol).ToArray();
        var gaps=problems.Count>0 || symbols.Any(x=>x.Readiness=="incomplete");
        blocked|=symbols.Any(x=>x.Readiness=="incomplete");
        var expiry=slot.Phase=="nightly"?slot.HandoffUntilUtc:new[]{slot.HandoffUntilUtc,cutoff.AddMinutes(s.SnapshotLifetimeMinutes)}.Concat(captures.Where(x=>x.Status=="complete").Select(x=>x.ExpiresAtUtc!.Value)).Min();
        return new(row.Id,curation?.Request.ParentRevisionId ?? previous?.RevisionId,cutoff,clock.GetUtcNow(),expiry,blocked?"unavailable":gaps?"partial":"complete",
            blocked?"blocked":gaps?"completed_with_gaps":"completed",problems.ToArray(),captures.ToArray(),symbols,
            previous?.Symbols.Select(x=>x.Symbol).Except(namesSet).Order().ToArray() ?? [],curation);
    }
    public async Task<PreparationOccurrence> CurateAsync(string owner,Guid workflowId,Guid occurrenceId,PreparationCurationRequest request,CancellationToken token)
    {
        var definition=await OwnedAsync(owner,workflowId,token);RequireKey(request.IdempotencyKey);
        if(String.IsNullOrWhiteSpace(request.Reason)||request.Reason.Length>1000||request.AddSymbols is null||request.RemoveSymbols is null||request.AddSymbols.Length+request.RemoveSymbols.Length is <1 or >500 ||
            request.AddSymbols.Concat(request.RemoveSymbols).Any(x=>String.IsNullOrWhiteSpace(x)||x.Length>15||x.Any(c=>!Char.IsAsciiLetterOrDigit(c)&&c is not ('.' or '-'))||x!=x.ToUpperInvariant()) || request.AddSymbols.Intersect(request.RemoveSymbols).Any())
            throw new InvalidOperationException("Provide uppercase symbols, a review reason and a bounded nonempty change.");
        var key="curation:"+request.IdempotencyKey;var hash=PreparationJson.Hash(new{occurrenceId,request});
        var prior=(await repository.OccurrencesAsync(workflowId,token)).FirstOrDefault(x=>x.SlotKey==key);
        if(prior is not null)
        {
            var original=await jobs.GetJobAsync(prior.Id,token);using var json=System.Text.Json.JsonDocument.Parse(original!.RequestJson);
            if(json.RootElement.GetProperty("requestHash").GetString()!=hash)throw new InvalidOperationException("Curation key was used for another change.");
            return await ViewAsync(prior,(await repository.DefinitionsAsync(token)).Single(x=>x.Id==prior.DefinitionId).Revision,token);
        }
        var parent=await repository.OccurrenceAsync(occurrenceId,token);
        if(parent?.WorkflowId!=workflowId||parent.ResultJson is null)throw new KeyNotFoundException("Parent snapshot not found.");
        var snapshot=PreparationJson.Read<PreparationSnapshot>(parent.ResultJson);var slot=PreparationJson.Read<PreparationSlot>(parent.SlotJson);var now=clock.GetUtcNow();
        if(slot.Phase!="morning"||snapshot.RevisionId!=request.ParentRevisionId||now>=slot.DeadlineUtc||now<slot.ScheduledAtUtc)throw new InvalidOperationException("Curation collection requires an active morning window.");
        if(request.RemoveSymbols.Any(x=>!snapshot.Symbols.Any(y=>y.Symbol==x)))throw new InvalidOperationException("Only existing members can be removed.");
        if(await authorization.StampAsync(owner,token)!=definition.AuthorizationStamp)throw new InvalidOperationException("Save the workflow after authorization changed.");
        var additions=(snapshot.Curation?.EffectiveAddSymbols ?? snapshot.Curation?.Request.AddSymbols ?? []).Union(request.AddSymbols).Except(request.RemoveSymbols).Order().ToArray();
        var removals=(snapshot.Curation?.EffectiveRemoveSymbols ?? snapshot.Curation?.Request.RemoveSymbols ?? []).Union(request.RemoveSymbols).Except(request.AddSymbols).Order().ToArray();
        if(additions.Length+removals.Length>500)throw new InvalidOperationException("Cumulative curation exceeds 500 symbols.");
        var id=Guid.NewGuid();var wishlistId=Guid.NewGuid();var curation=new PreparationCurationEvidence(request,owner,wishlistId,now,additions,removals);
        var row=new PreparationOccurrenceRecord {Id=id,WorkflowId=workflowId,DefinitionId=definition.Id,SlotKey=key,ScheduledTicks=now.UtcTicks,SlotJson=PreparationJson.Write(slot with{ScheduledAtUtc=now})};
        var job=new PersistedJob {Id=id,JobType=JobType,RunName="preparation-"+id.ToString("N"),CreatedAt=now,CreatedAtUtcTicks=now.UtcTicks,RequestJson=PreparationJson.Write(new{curation,requestHash=hash})};
        var wishlist=new Wishlist {Id=wishlistId,Name="Preparation additions "+id.ToString("N"),CreatedAtUtc=now,UpdatedAtUtc=now,
            Items=additions.Select(x=>new WishlistItem {Id=Guid.NewGuid(),WishlistId=wishlistId,Ticker=x,AddedAtUtc=now,Notes=request.Reason}).ToList()};
        var binding=new PreparationReviewRecord {Id=Guid.NewGuid(),OccurrenceId=id,WishlistId=wishlistId,OwnerId=owner,IdempotencyKey=key,RequestSha256=hash,CreatedAtUtc=now,Json=PreparationJson.Write(curation)};
        var stored=await repository.ReserveAsync(row,job,token,wishlist,binding)??throw new InvalidOperationException("This workflow already has active work or the queue is full.");
        return await ViewAsync(stored,definition.Revision,token);
    }
    public async Task<PreparationHandoff> HandoffAsync(string owner,Guid workflowId,Guid occurrenceId,PreparationHandoffRequest request,CancellationToken token)
    {
        await OwnedAsync(owner,workflowId,token); RequireKey(request.IdempotencyKey);
        if(request.Symbols is null || request.Symbols.Length is <1 or >500 || String.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length>1000)
            throw new InvalidOperationException("Select members and provide a review reason.");
        var hash=PreparationJson.Hash(new {workflowId,occurrenceId,request});
        var existing=await repository.ReviewAsync(owner,request.IdempotencyKey,token);
        if(existing is not null) { if(existing.RequestSha256!=hash) throw new InvalidOperationException("Handoff key already used for another selection."); return PreparationJson.Read<PreparationHandoff>(existing.Json); }
        var row=await repository.OccurrenceAsync(occurrenceId,token);
        if(row?.WorkflowId!=workflowId || row.ResultJson is null) throw new KeyNotFoundException("Published occurrence not found.");
        if((await jobs.GetJobAsync(occurrenceId,token))?.Status!="completed") throw new InvalidOperationException("Occurrence has not completed successfully.");
        var snapshot=PreparationJson.Read<PreparationSnapshot>(row.ResultJson);
        var slot=PreparationJson.Read<PreparationSlot>(row.SlotJson); var now=clock.GetUtcNow();
        if(slot.Phase!="morning" || snapshot.RevisionId!=request.RevisionId || snapshot.Readiness=="unavailable" || now>=snapshot.ExpiresAtUtc || now>=slot.HandoffUntilUtc)
            throw new InvalidOperationException("A current morning review with valid evidence is required. Run a fresh review.");
        var selected=request.Symbols.Distinct(StringComparer.Ordinal).Order().ToArray();
        if(selected.Any(x=>!snapshot.Symbols.Any(y=>y.Symbol==x && y.Readiness=="complete")))
            throw new InvalidOperationException("Only complete, included discovery members can be selected. New symbols require source collection.");
        var contributing=snapshot.Sources.Where(x=>x.Status=="complete" && x.Symbols.Intersect(selected).Any()).ToArray();
        if(contributing.Any(x=>x.ExpiresAtUtc<=now) || selected.Any(x=>!contributing.Any(y=>y.Symbols.Contains(x))))
            throw new InvalidOperationException("Selected members depend on expired or missing source evidence.");
        var expiry=new[]{snapshot.ExpiresAtUtc,slot.HandoffUntilUtc}.Concat(contributing.Select(x=>x.ExpiresAtUtc!.Value)).Min();
        var id=Guid.NewGuid(); var wishlistId=Guid.NewGuid(); var previewId=Guid.NewGuid();
        var members=selected.Select(symbol=>new UniverseMemberResponse(symbol,"discovered",contributing.Where(x=>x.Symbols.Contains(symbol))
            .Select(x=>new UniverseSourceEvidenceResponse(x.Source.Kind,x.Source.Key,x.ObservedAtUtc!.Value,x.ExpiresAtUtc!.Value,x.ContentSha256!,false,x.Reference)).ToArray())).ToArray();
        var preview=new UniversePreviewResponse(ContractVersions.VersionOne,previewId,"swing",now,expiry,"",members,
            snapshot.Symbols.Where(x=>!selected.Contains(x.Symbol)).Select(x=>new UniverseExclusionResponse(x.Symbol,"review_excluded",request.Reason)).ToArray(),snapshot.CutoffUtc);
        preview=preview with {ContentSha256=UniverseDiscoveryService.ContentHash(preview)};
        var record=new UniversePreviewRecord {UniverseSnapshotId=previewId,Horizon="swing",RequestSha256=hash,ContentSha256=preview.ContentSha256,
            ResolvedAtUtc=now,ExpiresAtUtc=expiry,PreviewJson=System.Text.Json.JsonSerializer.Serialize(preview)};
        UniverseDiscoveryService.ReadPreview(record,previewId);
        var result=new PreparationHandoff(id,wishlistId,previewId,expiry,snapshot.RevisionId,selected,request.Reason);
        var review=new PreparationReviewRecord {Id=id,OccurrenceId=occurrenceId,WishlistId=wishlistId,OwnerId=owner,IdempotencyKey=request.IdempotencyKey,RequestSha256=hash,CreatedAtUtc=now,Json=PreparationJson.Write(result)};
        var wishlist=new Wishlist {Id=wishlistId,Name="Preparation "+id.ToString("N"),Description=$"Immutable review {id}; parent {snapshot.RevisionId}; {request.Reason}",CreatedAtUtc=now,UpdatedAtUtc=now,
            Items=selected.Select(x=>new WishlistItem {Id=Guid.NewGuid(),WishlistId=wishlistId,Ticker=x,AddedAtUtc=now,Notes="Original preparation evidence: "+snapshot.RevisionId}).ToList()};
        return PreparationJson.Read<PreparationHandoff>((await repository.HandoffAsync(review,wishlist,record,token)).Json);
    }
    public async Task<PreparationHandoff> ReadHandoffAsync(string owner,string key,CancellationToken token)
    {
        var row=await repository.ReviewAsync(owner,key,token)??throw new KeyNotFoundException("Accepted review not found.");
        var result=PreparationJson.Read<PreparationHandoff>(row.Json);
        if(result.UniverseSnapshotId==Guid.Empty)throw new KeyNotFoundException("Accepted review not found.");
        return result;
    }
    public async Task<PreparationSymbol> EvidenceAsync(string owner,Guid workflowId,Guid occurrenceId,string symbol,CancellationToken token)
    {
        await OwnedAsync(owner,workflowId,token);
        var row=await repository.OccurrenceAsync(occurrenceId,token);
        if(row?.WorkflowId!=workflowId || row.ResultJson is null) throw new KeyNotFoundException();
        return PreparationJson.Read<PreparationSnapshot>(row.ResultJson).Symbols.SingleOrDefault(x=>x.Symbol==symbol) ?? throw new KeyNotFoundException();
    }
    private static void RequireKey(string key) { if(String.IsNullOrWhiteSpace(key)||key.Length>100) throw new InvalidOperationException("An idempotency key of at most 100 characters is required."); }
}
