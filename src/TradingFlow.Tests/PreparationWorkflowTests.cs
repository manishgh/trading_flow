using Microsoft.EntityFrameworkCore;
using Moq;
using TradingFlow.Application.Candidates;
using TradingFlow.Application.Jobs;
using TradingFlow.Application.Preparation;
using TradingFlow.Contracts.Preparation;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Identity;
using TradingFlow.Data.Jobs;
using TradingFlow.Data.Preparation;
using TradingFlow.Data.Wishlists;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Preparation;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Wishlists;
namespace TradingFlow.Tests;

public sealed class PreparationWorkflowTests
{
    private static PreparationSettings Settings => new() {Sources=[new("finviz","first")],MaximumAttempts=1,StandaloneMorning=true};
    [Theory]
    [InlineData("2026-07-02",16,45,"2026-07-06")]
    [InlineData("2026-11-27",13,45,"2026-11-30")]
    public void Planner_UsesActualCloseAndNextTradingSession(string previous,int closeHour,int offset,string target)
    {
        var prior=DateOnly.Parse(previous);var next=DateOnly.Parse(target);var now=PreparationCalendarAdapter.Utc(prior,new(12,0));
        var sessions=Enumerable.Range(0,next.DayNumber-prior.DayNumber+1).Select(i=>{
            var date=prior.AddDays(i);return new PreparationSession(date,date==prior||date==next,PreparationCalendarAdapter.Utc(date,new(9,30)),PreparationCalendarAdapter.Utc(date,new(closeHour,0)));}).ToArray();
        var plan=PreparationPlanning.Plan(Settings,new(now,"fixture",sessions),now,now);
        var nightly=Assert.Single(plan,x=>x.Phase=="nightly");Assert.Equal(next,nightly.TargetDate);
        Assert.Equal(PreparationCalendarAdapter.Utc(prior,new(closeHour,offset)),nightly.ScheduledAtUtc);
        Assert.All(plan.Where(x=>x.Phase=="morning"),x=>Assert.True(x.ScheduledAtUtc<x.DeadlineUtc));
    }
    [Theory]
    [InlineData("2026-03-06",14)]
    [InlineData("2026-03-09",13)]
    [InlineData("2026-10-26",13)]
    [InlineData("2026-11-02",14)]
    public void ExchangeTimes_RespectUsDstIndependentlyOfEurope(string date,int utcHour) => Assert.Equal(utcHour,PreparationCalendarAdapter.Utc(DateOnly.Parse(date),new(9,30)).Hour);
    [Fact]
    public void Planner_RejectsCalendarGapsStalenessAndRetroactiveSlots()
    {
        var now=DateTimeOffset.Parse("2026-09-28T12:00:00Z");var date=new DateOnly(2026,9,28);
        PreparationSession Session(DateOnly d)=>new(d,true,PreparationCalendarAdapter.Utc(d,new(9,30)),PreparationCalendarAdapter.Utc(d,new(16,0)));
        Assert.Throws<InvalidOperationException>(()=>PreparationPlanning.Plan(Settings,new(now,"test",[Session(date),Session(date.AddDays(2))]),now,now));
        Assert.Throws<InvalidOperationException>(()=>PreparationPlanning.Plan(Settings,new(now.AddHours(-7),"test",[Session(date),Session(date.AddDays(1))]),now,now));
        Assert.All(PreparationPlanning.Plan(Settings,new(now,"test",[Session(date),Session(date.AddDays(1))]),now,now),x=>Assert.True(x.ScheduledAtUtc>=now));
    }
    [Theory]
    [InlineData(0,4,30)] [InlineData(501,4,30)] [InlineData(500,5,30)] [InlineData(500,4,0)]
    public void ResourceAndFreshnessBounds_AreEnforced(int symbols,int parallel,int freshness)=>Assert.Throws<InvalidOperationException>(()=>PreparationPlanning.Validate(Settings with{MaximumSymbols=symbols,ParallelSymbols=parallel,MaximumSourceAgeMinutes=freshness}));
    [Fact]
    public async Task NewDefinitionsCannotBeEnabledThroughCallerChosenId()
    {
        await using var h=await Harness.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>h.Service.SaveAsync(h.Owner,new(Guid.NewGuid(),0,Settings with{Enabled=true}),default));
    }
    [Fact]
    public async Task DefinitionEdits_AreImmutableAndOptimisticallySerialized()
    {
        await using var h=await Harness.Create();var first=await h.Definition();
        var changed=await h.Service.SaveAsync(h.Owner,new(first.WorkflowId,1,Settings with{Name="Changed"}),default);
        Assert.Equal(2,changed.Revision);Assert.NotEqual(first.ContentSha256,changed.ContentSha256);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>h.Service.SaveAsync(h.Owner,new(first.WorkflowId,1,Settings),default));
        Assert.Equal(2,(await h.Repository.DefinitionsAsync(default)).Length);
    }
    [Fact]
    public async Task AtomicReservation_DeduplicatesTwoWritersAndCrossRevisionSlots()
    {
        await using var h=await Harness.Create();var def=await h.Definition();var pair=h.Rows(def,"same-slot");
        var results=await Task.WhenAll(h.Repository.ReserveAsync(pair.Row,pair.Job,default),h.Repository.ReserveAsync(pair.Row,pair.Job,default));
        Assert.Single(results.Select(x=>x!.Id).Distinct());
        var newer=await h.Service.SaveAsync(h.Owner,new(def.WorkflowId,1,Settings),default);var again=h.Rows(newer,"same-slot");
        Assert.Equal(results[0]!.Id,(await h.Repository.ReserveAsync(again.Row,again.Job,default))!.Id);
        await using var db=h.Database.CreateDbContext();Assert.Equal(1,await db.Jobs.CountAsync());
    }
    [Fact]
    public async Task OneOccurrencePerWorkflow_IsEnforcedAcrossWriters()
    {
        await using var h=await Harness.Create();var def=await h.Definition();var a=h.Rows(def,"a");var b=h.Rows(def,"b");
        Assert.NotNull(await h.Repository.ReserveAsync(a.Row,a.Job,default));Assert.Null(await h.Repository.ReserveAsync(b.Row,b.Job,default));
        await h.Jobs.RequestCancellationAsync(a.Row.Id,h.Clock.Now,default);Assert.NotNull(await h.Repository.ReserveAsync(b.Row,b.Job,default));
    }
    [Fact]
    public async Task Publication_RejectsLeaseLossAndCancellation_AndCommitsTerminalAtomically()
    {
        await using var h=await Harness.Create();var def=await h.Definition();var pair=h.Rows(def,"slot");await h.Repository.ReserveAsync(pair.Row,pair.Job,default);
        var lease=(await h.Jobs.TryAcquireAsync(pair.Row.Id,"worker",h.Clock.Now,TimeSpan.FromMinutes(1),default))!;
        Assert.False(await h.Repository.PublishAsync(pair.Row.Id,Guid.NewGuid(),"{}",null,default));
        Assert.True(await h.Repository.PublishAsync(pair.Row.Id,lease.LeaseToken,"{}","{}",default));
        await h.Jobs.RequestCancellationAsync(pair.Row.Id,h.Clock.Now,default);
        Assert.Equal("completed",(await h.Jobs.GetJobAsync(pair.Row.Id,default))!.Status);
        await using var db=h.Database.CreateDbContext();Assert.Equal(1,await db.ApplicationEvents.CountAsync());
    }
    [Fact]
    public async Task CancelledAndExpiredLeaseOwners_CannotPublish()
    {
        await using var h=await Harness.Create();var def=await h.Definition();var pair=h.Rows(def,"slot");await h.Repository.ReserveAsync(pair.Row,pair.Job,default);
        var lease=(await h.Jobs.TryAcquireAsync(pair.Row.Id,"worker",h.Clock.Now,TimeSpan.FromMinutes(1),default))!;
        await using(var db=h.Database.CreateDbContext())await db.Jobs.Where(x=>x.Id==pair.Row.Id).ExecuteUpdateAsync(x=>x.SetProperty(y=>y.LeaseExpiresAtUtcTicks,1L));
        Assert.False(await h.Repository.PublishAsync(pair.Row.Id,lease.LeaseToken,"{}",null,default));
        var next=(await h.Jobs.TryAcquireAsync(pair.Row.Id,"replacement",h.Clock.Now,TimeSpan.FromMinutes(1),default))!;
        await h.Jobs.RequestCancellationAsync(pair.Row.Id,h.Clock.Now,default);
        Assert.False(await h.Repository.PublishAsync(pair.Row.Id,next.LeaseToken,"{}",null,default));
    }
    [Fact]
    public async Task CompleteDiscovery_HandoffRetainsExpiryAndExactMembership_AndCreatesNoOrders()
    {
        await using var h=await Harness.Create();var (definition,occurrence)=await h.Execute();var snapshot=occurrence.Snapshot!;
        Assert.Equal("complete",snapshot.Readiness);
        var request=new PreparationHandoffRequest(snapshot.RevisionId,["AAPL"],"Reviewed selection","handoff");
        var result=await h.Service.HandoffAsync(h.Owner,definition.WorkflowId,occurrence.Id,request,default);
        var retry=await h.Service.HandoffAsync(h.Owner,definition.WorkflowId,occurrence.Id,request,default);Assert.Equal(PreparationJson.Write(result), PreparationJson.Write(retry));
        var preview=UniverseDiscoveryService.ReadPreview((await h.Database.Workflows.GetAsync(result.UniverseSnapshotId))!,result.UniverseSnapshotId);
        Assert.Equal(snapshot.ExpiresAtUtc,preview.ExpiresAtUtc);Assert.Equal(snapshot.Sources[0].ObservedAtUtc,preview.Members[0].Sources[0].ObservedAtUtc);
        var list=(await h.Wishlists.GetByIdAsync(result.WishlistId,default))!;Assert.Equal(["AAPL"],list.Items.Select(x=>x.Ticker));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>h.Wishlists.DeleteAsync(list.Id,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>h.Wishlists.AddOrUpdateItemAsync(list.Id,"MSFT",null,null,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>h.Wishlists.SaveAsync(list,default));
        var resolver=new UniverseSourceResolver(h.Wishlists,Mock.Of<IScreenerSnapshotSource>(),h.Clock);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>resolver.ResolveAsync(new("wishlist",list.Id.ToString(),list.Id),"swing",default));
        await using var db=h.Database.CreateDbContext();Assert.Equal(0,await db.OrderIntents.CountAsync());Assert.Equal(0,await db.ProductionRuns.CountAsync());
    }
    [Fact]
    public async Task Handoff_RejectsAtExclusiveExpiry_AndOtherOwner()
    {
        await using var h=await Harness.Create();var (def,occ)=await h.Execute();var snapshot=occ.Snapshot!;
        var request=new PreparationHandoffRequest(snapshot.RevisionId,["AAPL"],"review","new");
        await Assert.ThrowsAsync<KeyNotFoundException>(()=>h.Service.HandoffAsync(Guid.NewGuid().ToString(),def.WorkflowId,occ.Id,request,default));
        h.Clock.Now=snapshot.ExpiresAtUtc;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>h.Service.HandoffAsync(h.Owner,def.WorkflowId,occ.Id,request,default));
    }
    [Theory]
    [InlineData(true,"union","unavailable")]
    [InlineData(false,"union","partial")]
    [InlineData(false,"intersection","unavailable")]
    public async Task SourceFailure_HonorsRequiredOptionalAndIntersection(bool required,string merge,string readiness)
    {
        await using var h=await Harness.Create();h.FailSecond=true;
        var(_,occ)=await h.Execute(Settings with{Merge=merge,Sources=[new("finviz","first"),new("finviz","second",required)]});
        Assert.Equal(readiness,occ.Snapshot!.Readiness);Assert.Contains(occ.Snapshot.Sources,x=>x.Status=="failed");
    }
    [Fact]
    public async Task EmptySuccess_IsDistinctFromIncompleteAndCapacity()
    {
        await using var h=await Harness.Create();h.Symbols=[];
        var(_,empty)=await h.Execute();Assert.Equal("complete",empty.Snapshot!.Readiness);Assert.Empty(empty.Snapshot.Symbols);
        h.Symbols=["AAPL","MSFT"];var(_,limited)=await h.Execute(Settings with{MaximumSymbols=1});Assert.Equal("unavailable",limited.Snapshot!.Readiness);Assert.Contains(limited.Snapshot.Reasons,x=>x.Contains("Capacity"));
        h.Incomplete=true;var(_,incomplete)=await h.Execute();Assert.Equal("unavailable",incomplete.Snapshot!.Readiness);Assert.All(incomplete.Snapshot.Symbols,x=>Assert.Equal("incomplete",x.Readiness));
    }
    [Fact]
    public async Task MissingNightlyAndStaleSources_BlockCompleteHandoff()
    {
        await using var h=await Harness.Create();var(_,missing)=await h.Execute(Settings with{StandaloneMorning=false});Assert.Equal("unavailable",missing.Snapshot!.Readiness);
        h.Expired=true;var(_,stale)=await h.Execute();Assert.Equal("unavailable",stale.Snapshot!.Readiness);Assert.Equal("stale",stale.Snapshot.Sources[0].Status);
    }
    [Fact]
    public async Task Revocation_StopsWorkerAndFailureRecoveryIsDeduplicated()
    {
        await using var h=await Harness.Create();var def=await h.Definition();h.Stamp="revoked";var row=h.Rows(def,"revoked");await h.Repository.ReserveAsync(row.Row,row.Job,default);
        await h.Processor.ProcessNextAsync("worker",h.Service,DurableJobWorkerOptions.Default,default);await h.Service.RefreshProjectionAsync(default);await h.Service.RefreshProjectionAsync(default);
        Assert.Equal("failed",(await h.Jobs.GetJobAsync(row.Row.Id,default))!.Status);
        await using var db=h.Database.CreateDbContext();Assert.Equal(1,await db.ApplicationEvents.CountAsync(x=>x.EventType=="preparation_failed"));
    }

    [Fact]
    public async Task Curation_CollectsAddedSymbolsAndPreservesParentAudit()
    {
        await using var h=await Harness.Create();var(def,parent)=await h.Execute(new(){Sources=[new("finviz","first")],MaximumAttempts=1,StandaloneMorning=true,MaximumSymbols=1});
        var request=new PreparationCurationRequest(parent.Snapshot!.RevisionId,["MSFT"],["AAPL"],"Reviewed overnight addition","curate-once");
        var queued=await h.Service.CurateAsync(h.Owner,def.WorkflowId,parent.Id,request,default);
        var retried=await h.Service.CurateAsync(h.Owner,def.WorkflowId,parent.Id,request,default);Assert.Equal(queued.Id,retried.Id);
        await h.Processor.ProcessNextAsync("curation",h.Service,DurableJobWorkerOptions.Default,default);
        var detail=await h.Service.DetailAsync(h.Owner,def.WorkflowId,default);var result=detail.History.Single(x=>x.Id==queued.Id).Snapshot!;
        Assert.Equal(parent.Snapshot.RevisionId,result.ParentRevisionId);Assert.Equal(h.Owner,result.Curation!.Actor);
        Assert.Equal("complete",result.Symbols.Single(x=>x.Symbol=="MSFT").Readiness);Assert.Equal("excluded",result.Symbols.Single(x=>x.Symbol=="AAPL").Readiness);
        Assert.True(await h.Wishlists.IsPreparationRevisionAsync(result.Curation.WishlistId,default));
        Assert.Single(parent.Snapshot.Symbols);
        Assert.Equal("complete",result.Readiness);
        var child=await h.Service.CurateAsync(h.Owner,def.WorkflowId,queued.Id,new(result.RevisionId,["NVDA"],[],"Second review","second-curation"),default);
        await h.Processor.ProcessNextAsync("child",h.Service,DurableJobWorkerOptions.Default,default);
        var inherited=(await h.Service.DetailAsync(h.Owner,def.WorkflowId,default)).History.Single(x=>x.Id==child.Id).Snapshot!;
        Assert.Equal("complete",inherited.Symbols.Single(x=>x.Symbol=="MSFT").Readiness);
        Assert.Equal("excluded",inherited.Symbols.Single(x=>x.Symbol=="AAPL").Readiness);
        Assert.Contains("NVDA",inherited.Curation!.EffectiveAddSymbols!);
        Assert.Equal("unavailable",inherited.Readiness);
    }
    [Fact]
    public async Task Cursor_IsMonotonicAcrossConcurrentSchedulerWriters()
    {
        await using var h=await Harness.Create();var def=await h.Definition();var id=def.WorkflowId;var definitionId=(await h.Repository.DefinitionsAsync(default)).Single().Id;
        await Task.WhenAll(h.Repository.AdvanceCursorAsync(id,definitionId,200,default),h.Repository.AdvanceCursorAsync(id,definitionId,100,default));
        Assert.Equal(200,await h.Repository.CursorAsync(id,default));
        await h.Service.SaveAsync(h.Owner,new(id,1,new(){Sources=[new("finviz","fixture")]}),default);
        await h.Repository.AdvanceCursorAsync(id,definitionId,300,default);
        Assert.Equal(200,await h.Repository.CursorAsync(id,default));
    }

    [Fact]
    public async Task DatabaseDeadlineDowngrade_PreservesIdenticalJobAndOccurrenceEvidence()
    {
        await using var h=await Harness.Create();h.Clock.Now=DateTimeOffset.UtcNow.AddHours(-1);
        var(_,occurrence)=await h.Execute();Assert.Equal("unavailable",occurrence.Snapshot!.Readiness);
        Assert.Contains(occurrence.Snapshot.Reasons,x=>x.Contains("durable commit"));
        Assert.Equal((await h.Repository.OccurrenceAsync(occurrence.Id,default))!.ResultJson,(await h.Jobs.GetJobAsync(occurrence.Id,default))!.SnapshotJson);
    }
    internal sealed class Harness : IAsyncDisposable
    {
        public CandidateWorkflowTestDatabase Database {get;}=new();
        public CandidateWorkflowTestClock Clock {get;}=new(DateTimeOffset.UtcNow);
        public string Owner {get;}=Guid.NewGuid().ToString();
        public string Stamp="stamp";public bool FailSecond,Incomplete,Expired;public string[] Symbols=["AAPL"];
        public SqlitePreparationRepository Repository=>new(Database); public SqliteJobRepository Jobs=>new(Database);public SqliteWishlistRepository Wishlists=>new(Database);
        public PreparationService Service {get;private set;}=null!; public DurableJobProcessor Processor=>new(Jobs,Clock);
        public static async Task<Harness> Create()
        {
            var h=new Harness();await h.Database.InitializeAsync();await using var db=h.Database.CreateDbContext();db.Users.Add(new TradingFlowUser {Id=Guid.Parse(h.Owner),UserName="test",NormalizedUserName="TEST",SecurityStamp=h.Stamp});await db.SaveChangesAsync();
            var auth=new Mock<IPreparationAuthorization>();auth.Setup(x=>x.StampAsync(It.IsAny<string>(),It.IsAny<CancellationToken>())).ReturnsAsync(()=>h.Stamp);
            var source=new Mock<IUniverseSourceResolver>();source.Setup(x=>x.ResolveAsync(It.IsAny<UniverseSourceSelectionRequest>(),"swing",It.IsAny<CancellationToken>())).Returns((UniverseSourceSelectionRequest r,string _,CancellationToken ct)=>{
                if(h.FailSecond&&r.SourceKey=="second")throw new InvalidOperationException("source offline");return Task.FromResult(new UniverseSourceCapture(r.SourceKind,r.SourceKey,h.Clock.Now.AddMinutes(h.Expired?-5:0),h.Clock.Now.AddMinutes(h.Expired?-2:3),h.Symbols.Select(s=>new DiscoverySymbolObservation(s,"{}")).ToArray()));});
            var evidence=new Mock<IPreparationEvidence>();evidence.Setup(x=>x.CollectAsync(It.IsAny<string>(),It.IsAny<PreparationSlot>(),It.IsAny<PreparationSettings>(),It.IsAny<DateTimeOffset>(),It.IsAny<CancellationToken>())).Returns((string symbol,PreparationSlot slot,PreparationSettings s,DateTimeOffset cutoff,CancellationToken token)=>Task.FromResult(new PreparationSymbol(symbol,"",h.Incomplete?"incomplete":"complete",h.Incomplete?["Missing final daily bar"]:[],slot.PreviousSession.Date,126,100,1000000,100000000,100,2,null,null,"sip","all",new string('a',64),[],[])));
            var cal=new Mock<IPreparationCalendar>();cal.Setup(x=>x.LoadAsync(It.IsAny<DateOnly>(),It.IsAny<DateOnly>(),It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("fixture calendar unavailable"));
            h.Service=new(h.Repository,h.Jobs,cal.Object,auth.Object,source.Object,evidence.Object,h.Wishlists,h.Clock);return h;
        }
        public Task<PreparationDefinition> Definition(PreparationSettings? settings=null)=>Service.SaveAsync(Owner,new(Guid.Empty,0,settings??Settings),default);
        public (PreparationOccurrenceRecord Row,PersistedJob Job) Rows(PreparationDefinition definition,string key)
        {
            var id=Guid.NewGuid();var date=DateOnly.FromDateTime(Clock.Now.UtcDateTime);var previous=new PreparationSession(date.AddDays(-1),true,Clock.Now.AddDays(-1).AddHours(-6),Clock.Now.AddDays(-1));
            var slot=new PreparationSlot(date,"morning",Clock.Now,Clock.Now.AddMinutes(30),Clock.Now.AddHours(1),previous,new(Clock.Now,"fixture",[previous]));
            var stored=Repository.DefinitionsAsync(default).GetAwaiter().GetResult().Single(x=>x.WorkflowId==definition.WorkflowId&&x.Revision==definition.Revision);
            return(new() {Id=id,WorkflowId=definition.WorkflowId,DefinitionId=stored.Id,SlotKey=key,ScheduledTicks=Clock.Now.UtcTicks,SlotJson=PreparationJson.Write(slot)},
                new(){Id=id,JobType="preparation",RunName=id.ToString(),CreatedAt=Clock.Now,CreatedAtUtcTicks=Clock.Now.UtcTicks});
        }
        public async Task<(PreparationDefinition Definition,PreparationOccurrence Occurrence)> Execute(PreparationSettings? settings=null)
        {var def=await Definition(settings);var pair=Rows(def,"slot");await Repository.ReserveAsync(pair.Row,pair.Job,default);await Processor.ProcessNextAsync("worker",Service,DurableJobWorkerOptions.Default,default);var detail=await Service.DetailAsync(Owner,def.WorkflowId,default);return(def,Assert.Single(detail.History));}
        public ValueTask DisposeAsync()=>Database.DisposeAsync();
    }
}
