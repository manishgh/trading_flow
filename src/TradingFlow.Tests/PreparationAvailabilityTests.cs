using Moq;
using TradingFlow.Application.Candidates;
using TradingFlow.Application.Preparation;
using TradingFlow.Contracts.Preparation;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class PreparationAvailabilityTests
{
    [Theory]
    [InlineData("2026-11-27",13,44,"nightly",false)]
    [InlineData("2026-11-27",13,45,"nightly",true)]
    [InlineData("2026-11-28",12,0,"nightly",true)]
    [InlineData("2026-11-30",7,59,"nightly",true)]
    [InlineData("2026-11-30",8,0,"nightly",false)]
    [InlineData("2026-11-30",8,0,"morning",true)]
    [InlineData("2026-11-30",9,14,"morning",true)]
    [InlineData("2026-11-30",9,15,"morning",false)]
    [InlineData("2026-11-30",9,30,"morning",false)]
    [InlineData("2026-03-09",8,0,"morning",true)]
    public async Task DetailAvailabilityMatchesRunAdmissionAtAuthoritativeCalendarBoundaries(
        string date,int hour,int minute,string phase,bool available)
    {
        await using var harness=await PreparationWorkflowTests.Harness.Create();
        harness.Clock.Now=PreparationCalendarAdapter.Utc(DateOnly.Parse(date),new(hour,minute));
        var calendar=new TestCalendar(harness.Clock);
        var service=Service(harness,calendar);
        var definition=await Save(service,harness.Owner);

        var detail=await service.DetailAsync(harness.Owner,definition.WorkflowId,default);
        Assert.False(detail.Definition.Settings.Enabled);
        Assert.Equal(harness.Clock.Now,detail.ReadAtUtc);
        Assert.Null(detail.PlanningError);
        Assert.NotNull(detail.CollectionWindows);
        Assert.Equal(available,detail.CollectionWindows.Any(x=>x.Phase==phase));
        Assert.All(detail.CollectionWindows,x=>Assert.True(x.ScheduledAtUtc<=detail.ReadAtUtc && detail.ReadAtUtc<x.DeadlineUtc));
        Assert.All(detail.Upcoming,x=>Assert.True(x.ScheduledAtUtc>=detail.ReadAtUtc));
        Assert.Equal(1,calendar.LoadCount);
        Assert.Empty(await harness.Jobs.GetJobsAsync("preparation",default));

        if(available)
        {
            var window=Assert.Single(detail.CollectionWindows,x=>x.Phase==phase);
            var occurrence=await service.RunAsync(harness.Owner,definition.WorkflowId,new(phase,Guid.NewGuid().ToString()),default);
            Assert.Equal(window.TargetDate,occurrence.Slot.TargetDate);
            Assert.Equal(window.DeadlineUtc,occurrence.Slot.DeadlineUtc);
            Assert.Equal(window.PreviousSession,occurrence.Slot.PreviousSession);
            Assert.Equal("queued",occurrence.Status);
            if(date.StartsWith("2026-11") && phase=="nightly")
                Assert.Equal(new DateOnly(2026,11,30),window.TargetDate);
        }
        else
        {
            var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>service.RunAsync(
                harness.Owner,definition.WorkflowId,new(phase,Guid.NewGuid().ToString()),default));
            Assert.Contains("outside",error.Message);
        }
    }

    [Fact]
    public async Task CalendarFailureDoesNotInventAvailabilityOrReserveWork()
    {
        await using var harness=await PreparationWorkflowTests.Harness.Create();
        var definition=await harness.Definition();
        var detail=await harness.Service.DetailAsync(harness.Owner,definition.WorkflowId,default);
        Assert.NotNull(detail.PlanningError);
        Assert.NotNull(detail.ReadAtUtc);
        Assert.Empty(detail.CollectionWindows!);
        Assert.Empty(detail.Upcoming);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>harness.Service.RunAsync(
            harness.Owner,definition.WorkflowId,new("morning",Guid.NewGuid().ToString()),default));
        Assert.Empty(await harness.Jobs.GetJobsAsync("preparation",default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CalendarReadCrossingDeadlineCannotExposeOrAdmitExpiredWindow(bool run)
    {
        await using var harness=await PreparationWorkflowTests.Harness.Create();
        harness.Clock.Now=PreparationCalendarAdapter.Utc(new(2026,11,30),new(9,14));
        var calendar=new TestCalendar(harness.Clock){AdvanceDuringRead=TimeSpan.FromMinutes(2)};
        var service=Service(harness,calendar);
        var definition=await Save(service,harness.Owner);
        if(run)
        {
            var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>service.RunAsync(
                harness.Owner,definition.WorkflowId,new("morning",Guid.NewGuid().ToString()),default));
            Assert.Contains("outside",error.Message);
        }
        else
        {
            var detail=await service.DetailAsync(harness.Owner,definition.WorkflowId,default);
            Assert.Equal(harness.Clock.Now,detail.ReadAtUtc);
            Assert.Empty(detail.CollectionWindows!);
        }
        Assert.Empty(await harness.Jobs.GetJobsAsync("preparation",default));
    }

    [Fact]
    public async Task ActiveHistoryBlocksDuplicateWorkWhileRetryKeepsOccurrenceIdentity()
    {
        await using var harness=await PreparationWorkflowTests.Harness.Create();
        harness.Clock.Now=PreparationCalendarAdapter.Utc(new(2026,11,30),new(8,30));
        var service=Service(harness,new TestCalendar(harness.Clock));
        var definition=await Save(service,harness.Owner);
        var request=new PreparationRunRequest("morning",Guid.NewGuid().ToString());
        var first=await service.RunAsync(harness.Owner,definition.WorkflowId,request,default);
        var detail=await service.DetailAsync(harness.Owner,definition.WorkflowId,default);
        Assert.Equal("queued",Assert.Single(detail.History).Status);
        Assert.Single(detail.CollectionWindows!,x=>x.Phase=="morning");
        Assert.Equal(first.Id,(await service.RunAsync(harness.Owner,definition.WorkflowId,request,default)).Id);
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>service.RunAsync(harness.Owner,
            definition.WorkflowId,new("morning",Guid.NewGuid().ToString()),default));
        Assert.Contains("active work",error.Message);
    }

    private static Task<PreparationDefinition> Save(PreparationService service,string owner) =>
        service.SaveAsync(owner,new(Guid.Empty,0,new(){Sources=[new("finviz","fixture")],StandaloneMorning=true}),default);

    private static PreparationService Service(PreparationWorkflowTests.Harness harness,IPreparationCalendar calendar)
    {
        var authorization=new Mock<IPreparationAuthorization>();
        authorization.Setup(x=>x.StampAsync(It.IsAny<string>(),It.IsAny<CancellationToken>())).ReturnsAsync(()=>harness.Stamp);
        return new(harness.Repository,harness.Jobs,calendar,authorization.Object,Mock.Of<IUniverseSourceResolver>(),
            Mock.Of<IPreparationEvidence>(),harness.Wishlists,harness.Clock);
    }

    private sealed class TestCalendar(CandidateWorkflowTestClock clock) : IPreparationCalendar
    {
        public int LoadCount {get;private set;}
        public TimeSpan AdvanceDuringRead {get;init;}
        public Task<PreparationCalendar> LoadAsync(DateOnly start,DateOnly end,CancellationToken token)
        {
            LoadCount++;
            clock.Now+=AdvanceDuringRead;
            var sessions=Enumerable.Range(0,end.DayNumber-start.DayNumber+1).Select(i=>start.AddDays(i)).Select(date=>
                new PreparationSession(date,date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday && date!=new DateOnly(2026,11,26),
                    PreparationCalendarAdapter.Utc(date,new(9,30)),
                    PreparationCalendarAdapter.Utc(date,new(date==new DateOnly(2026,11,27)?13:16,0)))).ToArray();
            return Task.FromResult(new PreparationCalendar(clock.Now,"authoritative-test-fixture",sessions));
        }
    }
}
