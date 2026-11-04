using ReviewGate.Contracts;
using ReviewGate.Core.Execution;
using ReviewGate.Testing;

namespace ReviewGate.StateTests;
public sealed class StateTests
{
    [Fact] public void GrantBoundToOneRunAndCumulativeRequestLimit()
    {
        using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);var run=s.NewRun(p);var grant=e.Grant(p)with{MaxRequests=1};s.Reserve(run,p,p.Requests[0],grant);
        Assert.Equal(12,Assert.Throws<ReviewError>(()=>s.Reserve(run,p,p.Requests[0],grant)).ExitCode);
        Assert.Equal(10,Assert.Throws<ReviewError>(()=>s.Reserve(s.NewRun(p),p,p.Requests[0],grant)).ExitCode);
    }
    [Fact] public void ReservedCrashReleasesButIntentCrashHoldsAndCannotClaimNotSent()
    {
        using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);var r=s.NewRun(p);var g=e.Grant(p);var a=s.Reserve(r,p,p.Requests[0],g);s.Recover(r,p);Assert.Equal("released",s.Attempts(r.Id).Single().Phase);
        var b=s.Reserve(r,p,p.Requests[0],g);s.Dispatch(b.Id);s.Recover(r,p);Assert.Equal("unknown",s.Attempts(r.Id).Single(x=>x.Id==b.Id).Phase);
        var resolution=new ResolutionFixture(Data.Version,p.Hash,[new(b.Id,"not_sent","claim","tester",DateTimeOffset.UtcNow)]);Assert.Equal("unproven_not_sent",Assert.Throws<ReviewError>(()=>s.Resolve(r,p,resolution)).Code);
        s.Resolve(r,p,resolution with{Resolutions=[new(b.Id,"conservative_full_reservation","synthetic assumption","tester",DateTimeOffset.UtcNow)]});Assert.Equal(b.ReservationUsd,s.Attempts(r.Id).Single(x=>x.Id==b.Id).CostUsd);Assert.Empty(s.Units(r.Id));
    }
    [Fact] public void FileLockRejectsSecondOwnerAndStaleCancelDoesNotCrossGeneration()
    {
        using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);var r=s.NewRun(p);using(var owner=s.Own(p.ProjectId)){Assert.True(s.OwnerAlive(p.ProjectId));Assert.Equal(13,Assert.Throws<ReviewError>(()=>s.Own(p.ProjectId)).ExitCode);}
        s.Cancel(r);Assert.True(s.CancelRequested(r));s.NewGeneration(r.Id);Assert.False(s.CancelRequested(s.GetRun(r.Id)));
    }
    [Fact] public void UnknownCarriesIntoNewMonthAcrossProjects()
    {
        using var e=new TestEnvironment();DateTimeOffset clock=new(2026,11,30,14,59,0,TimeSpan.Zero);
        e.Put("prices.json",new PriceSnapshot(Data.Version,"test",clock,clock.AddDays(-1),clock.AddDays(2),"synthetic",Data.Hash("test"),[new("fixture/a",100,100)]));e.Put("budget.json",new BudgetPolicy(Data.Version,1,1,.6m));
        using var s=new ReviewGate.Infrastructure.StateStore(e.State,()=>clock);var p=e.MakePlan(s);var r=s.NewRun(p);var a=s.Reserve(r,p,p.Requests[0],e.Grant(p)with{ExpiresAt=clock.AddHours(1)});s.Dispatch(a.Id);s.Unknown(a.Id,"unknown");clock=clock.AddMinutes(2);
        var p2=e.MakePlan(s,project:"other");Assert.Equal("2026-12",ReviewGate.Infrastructure.StateStore.Month(clock));Assert.Equal(12,Assert.Throws<ReviewError>(()=>s.Reserve(s.NewRun(p2),p2,p2.Requests[0],e.Grant(p2)with{ExpiresAt=clock.AddHours(1)})).ExitCode);
    }
#if DEBUG
    [Fact] public async Task StorageWriteFailureReturnsRunAndLeavesRecoverableState()
    {
        using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);e.Put("grant.json",e.Grant(p));
        ReviewEngine.FaultObserver=stage=>{if(stage=="dispatch_intent"){var attempt=s.Attempts().Single();Data.CreateFile(s.ResponseFile(attempt),"synthetic incomplete response");}};
        ReviewGate.Contracts.CommandResult r;
        try{r=await ReviewGate.Cli.CliHost.RunAsync(["exec","--state-root",e.State,"--plan",p.Id,"--grant-file",e.File("grant.json")]);}
        finally{ReviewEngine.FaultObserver=null;}
        Assert.Equal(21,r.ExitCode);Assert.NotNull(r.Result.RunId);Assert.Equal("storage_failure",r.Result.ErrorCode);Assert.NotEqual("completed",s.GetRun(r.Result.RunId).Status);
        var recovery=await new ReviewEngine(s).ExecuteAsync(p,null,r.Result.RunId,true);Assert.Equal(13,recovery.ExitCode);Assert.Single(s.Attempts());
    }
    [Fact] public async Task BundleCrashNeverCommitsCompletedWithoutManifest()
    {
        using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);var g=e.Grant(p);
        ReviewEngine.FaultObserver=stage=>{if(stage=="bundle_precommit")throw new InvalidOperationException("synthetic crash");};
        try{await Assert.ThrowsAsync<InvalidOperationException>(()=>new ReviewEngine(s).ExecuteAsync(p,g));}
        finally{ReviewEngine.FaultObserver=null;}
        string run=s.Attempts().Single().RunId;Assert.NotEqual("completed",s.GetRun(run).Status);Assert.Empty(s.Artifacts(run));
        var r=await new ReviewEngine(s).ExecuteAsync(p,g,run);Assert.Equal(0,r.ExitCode);Assert.NotEmpty(r.Result.Artifacts);Assert.Single(s.Attempts());
    }
    [Fact] public async Task ResumeAfterSavedResponseCrashDoesNotRedispatchOrDoubleSettle()
    {
        using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);var grant=e.Grant(p);string? run=null;
        ReviewGate.Infrastructure.StateStore.FaultObserver=_=>throw new InvalidOperationException("synthetic crash");
        try{await Assert.ThrowsAsync<InvalidOperationException>(()=>new ReviewEngine(s).ExecuteAsync(p,grant));run=s.Attempts().Single().RunId;}
        finally{ReviewGate.Infrastructure.StateStore.FaultObserver=null;}
        var engine=new ReviewEngine(s);var result=await engine.ExecuteAsync(p,grant,run);Assert.Equal(0,result.ExitCode);Assert.Single(s.Attempts());decimal? cost=s.Attempts().Single().CostUsd;
        Assert.Equal(0,(await engine.ExecuteAsync(p,grant,run)).ExitCode);Assert.Single(s.Attempts());Assert.Equal(cost,s.Attempts().Single().CostUsd);
    }
#endif
    [Fact] public void RevokedGrantCannotReserveAgain()
    {using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);var r=s.NewRun(p);var g=e.Grant(p);s.Reserve(r,p,p.Requests[0],g);s.RevokeGrant(g.Id);Assert.Equal("grant_revoked",Assert.Throws<ReviewError>(()=>s.Reserve(r,p,p.Requests[0],g)).Code);}
    [Fact] public async Task BudgetPreventsDispatch()
    {using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);var r=await new ReviewEngine(s).ExecuteAsync(p,e.Grant(p,max:.000001m));Assert.Equal(12,r.ExitCode);Assert.Empty(s.Attempts());}
}
