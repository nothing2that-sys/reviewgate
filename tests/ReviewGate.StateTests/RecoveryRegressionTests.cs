using ReviewGate.Contracts;
using ReviewGate.Core.Execution;
using ReviewGate.Infrastructure;
using ReviewGate.Testing;

namespace ReviewGate.StateTests;
public sealed class RecoveryRegressionTests
{
    static TestEnvironment TwoChunks(params FixtureResponse[] responses)
    {
        var e=new TestEnvironment("first line\nsecond line\n",[..responses],chunk:12);
        e.Put("budget.json",new BudgetPolicy(Data.Version,10,20,30));return e;
    }
    [Fact] public async Task RetryExhaustedUnitDoesNotBlockRemainingRequestsOnResume()
    {
        using var e=TwoChunks(new(Chunk:0,Status:"truncated",Usage:new(100,20)),new(Chunk:1,Usage:new(100,20)));
        using var s=e.Store();var p=e.MakePlan(s);Assert.Equal(2,p.Requests.Count);var engine=new ReviewEngine(s);
        var first=await engine.ExecuteAsync(p,e.Grant(p)with{MaxRequests=1,MaxRetries=0});
        Assert.Equal(12,first.ExitCode);string run=first.Result.RunId!;Assert.Single(s.Attempts(run));
        var resumed=await engine.ExecuteAsync(p,e.Grant(p,run)with{MaxRetries=0},run);
        Assert.Equal(20,resumed.ExitCode);Assert.Equal("partial",resumed.Result.Status);
        Assert.Single(s.Attempts(run),a=>a.RequestId==p.Requests[0].Id);Assert.Single(s.Attempts(run),a=>a.RequestId==p.Requests[1].Id);
        Assert.Equal("truncated",s.Units(run).Single(u=>u.RequestId==p.Requests[0].Id).ErrorCode);Assert.True(s.Units(run).Single(u=>u.RequestId==p.Requests[1].Id).Valid);
    }
    [Fact] public async Task RecoveryAndResolutionSurviveEditedOriginalButResumeDoesNot()
    {
        using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);var run=s.NewRun(p);var g=e.Grant(p,run.Id);
        var a=s.Reserve(run,p,p.Requests[0],g);s.Dispatch(a.Id);s.Unknown(a.Id,"dispatch_outcome_unknown");
        File.AppendAllText(Path.Combine(e.Source,"Sample.cs"),"// edited after the crash");File.AppendAllText(e.File("profile.json")," ");
        var resolution=new ResolutionFixture(Data.Version,p.Hash,[new(a.Id,"conservative_full_reservation","synthetic assumption","tester",DateTimeOffset.UtcNow)]);
        var recovered=await new ReviewEngine(s).ExecuteAsync(p,null,run.Id,true,resolution);
        Assert.Equal(0,recovered.ExitCode);Assert.Equal("settled",s.Attempts(run.Id).Single().Phase);Assert.Single(recovered.Result.StaleSourceIds);
        Assert.Equal(0m,s.Costs(run.Id)["unknown_hold_usd"]!.GetValue<decimal>());
        Assert.Equal("input_changed",(await Assert.ThrowsAsync<ReviewError>(()=>new ReviewEngine(s).ExecuteAsync(p,g,run.Id))).Code);
    }
    [Fact] public void ReservationCoversRequestBytesWhenDeclaredBoundIsTooSmall()
    {
        using var e=new TestEnvironment();e.Put("reviewer.json",e.Reviewer with{InputTokenUpperBound=1,MaxOutputTokens=1});using var s=e.Store();var p=e.MakePlan(s);var r=p.Requests.Single();
        decimal expected=((decimal)Data.Utf8.GetByteCount(r.Body)+ReviewGate.Providers.WireAdapters.FramingTokenAllowance)*1/1_000_000m+1*2/1_000_000m;
        Assert.Equal(expected,r.ReservationUsd);
    }
    [Fact] public void ProvenNotSentReleasesTheReservationAndRetry()
    {
        using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);var run=s.NewRun(p);var g=e.Grant(p,run.Id)with{MaxRetries=0};
        var a=s.Reserve(run,p,p.Requests[0],g);s.Dispatch(a.Id);s.Unknown(a.Id,"synthetic_not_sent_confirmed");
        s.Resolve(run,p,new(Data.Version,p.Hash,[new(a.Id,"not_sent","mock transport recorded no send","tester",DateTimeOffset.UtcNow)]));
        Assert.Equal("released",s.Attempts(run.Id).Single().Phase);Assert.False(s.RetryExhausted(run,p,p.Requests[0],g));
        Assert.Equal(0m,s.Costs(run.Id)["usage_calculated_usd"]!.GetValue<decimal>());
    }
    [Fact] public async Task GrantBindsAtFirstPreparationEvenWithoutReservation()
    {
        using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);var g=e.Grant(p,max:.000001m);
        Assert.Equal(12,(await new ReviewEngine(s).ExecuteAsync(p,g)).ExitCode);Assert.Empty(s.Attempts());
        var second=await new ReviewEngine(s).ExecuteAsync(p,g);Assert.Equal(10,second.ExitCode);Assert.Equal("grant_already_bound_or_changed",second.Result.RunErrorCode);
    }
    [Fact] public void ChangedGrantContentWithSameIdIsRejected()
    {
        using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);var run=s.NewRun(p);var g=e.Grant(p);s.BindGrant(run,p,g);
        Assert.Equal("grant_already_bound_or_changed",Assert.Throws<ReviewError>(()=>s.Reserve(run,p,p.Requests[0],g with{MaxUsd=9})).Code);
    }
    [Fact] public void CostMonthFollowsDispatchIntentTime()
    {
        DateTimeOffset clock=new(2026,11,30,14,59,59,TimeSpan.Zero);using var e=new TestEnvironment();
        e.Put("prices.json",new PriceSnapshot(Data.Version,"test",clock,clock.AddDays(-1),clock.AddDays(2),"synthetic",Data.Hash("test"),[new("fixture/a",1,2)]));
        using var s=new StateStore(e.State,()=>clock);var p=e.MakePlan(s);var run=s.NewRun(p);var a=s.Reserve(run,p,p.Requests[0],e.Grant(p)with{ExpiresAt=clock.AddHours(1)});
        Assert.Equal("2026-11",a.Month);clock=clock.AddSeconds(2);s.Dispatch(a.Id);Assert.Equal("2026-12",s.Attempts(run.Id).Single().Month);
    }
}
