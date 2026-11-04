using ReviewGate.Contracts;
using ReviewGate.Core.Execution;
using ReviewGate.Core.Planning;
using ReviewGate.Infrastructure;
using ReviewGate.Testing;

namespace ReviewGate.OfflineE2E;
public sealed class OfflineE2E
{
    static async Task WaitDispatch(TestEnvironment e,string run)
    {
        var until=DateTime.UtcNow.AddSeconds(10);
        while(DateTime.UtcNow<until)
        {using(var store=e.Store())if(store.Attempts(run).Any(a=>a.Phase=="dispatch_intent"))return;await Task.Delay(25);}
        throw new TimeoutException("Owned worker did not dispatch fixture");
    }
    [Fact] public async Task CancelInFlightHoldsUnknownAndNoSecondRequest()
    {
        using var e=new TestEnvironment("first line\nsecond line\n",[new(DelayMs:5000,Usage:new(100,20))],chunk:12);
        string run;Plan p;
        using(var s=e.Store()){p=e.MakePlan(s);run=s.NewRun(p).Id;e.Put("grant.json",e.Grant(p,run));}
        using var worker=TestEnvironment.Start(e.Control("resume",run,"--grant-file",e.File("grant.json")));
        var stdout=worker.StandardOutput.ReadToEndAsync();var stderr=worker.StandardError.ReadToEndAsync();
        try
        {
            await WaitDispatch(e,run);var cancel=await TestEnvironment.Invoke(e.Control("cancel",run));Assert.Equal(0,cancel.Exit);Assert.True(Data.Parse<Envelope>(cancel.Stdout).CancelRequested);
            using var timeout=new CancellationTokenSource(10000);await worker.WaitForExitAsync(timeout.Token);Assert.Equal(13,worker.ExitCode);Assert.Equal("needs_reconciliation",Data.Parse<Envelope>(await stdout).Status);Assert.Equal("",await stderr);
            using var s=e.Store();Assert.Single(s.Attempts(run));Assert.Equal("unknown",s.Attempts(run)[0].Phase);
            var recovery=await TestEnvironment.Invoke(e.Control("resume",run,"--recover-only"));Assert.Equal(13,recovery.Exit);Assert.Single(s.Attempts(run));
        }
        finally{if(!worker.HasExited){worker.Kill(true);await worker.WaitForExitAsync();}}
    }
    [Fact] public async Task ForceKilledOwnedWorkerRecoversIntentAsUnknown()
    {
        using var e=new TestEnvironment(responses:[new(DelayMs:10000,Usage:new(100,20))]);string run;
        using(var s=e.Store()){var p=e.MakePlan(s);run=s.NewRun(p).Id;e.Put("grant.json",e.Grant(p,run));}
        using var worker=TestEnvironment.Start(e.Control("resume",run,"--grant-file",e.File("grant.json")));
        try{await WaitDispatch(e,run);worker.Kill(true);await worker.WaitForExitAsync();}
        finally{if(!worker.HasExited){worker.Kill(true);await worker.WaitForExitAsync();}}
        var r=await TestEnvironment.Invoke(e.Control("resume",run,"--recover-only"));Assert.Equal(13,r.Exit);
        using var store=e.Store();Assert.Single(store.Attempts(run));Assert.Equal("unknown",store.Attempts(run)[0].Phase);Assert.False(store.OwnerAlive("test"));
    }
    [Theory][InlineData("profile")][InlineData("fixture")][InlineData("source")][InlineData("addition")]
    public async Task UnapprovedChangesCannotReachDispatch(string changed)
    {
        using var e=new TestEnvironment();using var s=e.Store();var input=new PlanInput("test",e.Source,e.Source,"synthetic","Review",e.File("profile.json"),[e.File("reviewer.json")],e.File("budget.json"),e.File("prices.json"),e.File("fixture.json"),["."],[]);var p=Planner.Build(s,input);
        string target=changed switch{"profile"=>e.File("profile.json"),"fixture"=>e.File("fixture.json"),"source"=>Path.Combine(e.Source,"Sample.cs"),_=>Path.Combine(e.Source,"Added.cs")};File.AppendAllText(target," ");
        var r=await ReviewGate.Cli.CliHost.RunAsync(["exec","--state-root",e.State,"--plan",p.Id]);Assert.Equal(13,r.ExitCode);Assert.Empty(s.Attempts());
    }
    [Fact] public async Task ProviderErrorSentinelNeverAppearsInEnvelopeOrReport()
    {
        const string sentinel="TEST_SECRET_SENTINEL_NOT_A_REAL_KEY";
        using var e=new TestEnvironment(responses:[new(WireBody:System.Text.Json.Nodes.JsonNode.Parse("{\"error\":{\"message\":\""+sentinel+"\"}}"),HttpStatus:401)]);
        using var s=e.Store();var p=e.MakePlan(s);e.Put("grant.json",e.Grant(p));
        var result=await TestEnvironment.Invoke(["exec","--state-root",e.State,"--plan",p.Id,"--grant-file",e.File("grant.json")]);Assert.Equal(13,result.Exit);Assert.DoesNotContain(sentinel,result.Stdout+result.Stderr);
        var r=Data.Parse<Envelope>(result.Stdout);foreach(var a in r.Artifacts)Assert.DoesNotContain(sentinel,File.ReadAllText(Path.Combine(s.Root,a.Path)));
    }
    [Fact] public async Task SimultaneousReservationsRespectSharedMonthlyCap()
    {
        using var e=new TestEnvironment();e.Put("budget.json",new BudgetPolicy(Data.Version,1,1,.01m));Plan p1,p2;RunRow r1,r2;
        using(var s=e.Store()){p1=e.MakePlan(s,project:"one");p2=e.MakePlan(s,project:"two");r1=s.NewRun(p1);r2=s.NewRun(p2);}
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> Reserve(Plan p,RunRow r){await gate.Task;return await Task.Run(()=>{using var s=e.Store();try{s.Reserve(r,p,p.Requests[0],e.Grant(p));return 0;}catch(ReviewError ex){return ex.ExitCode;}});}
        var a=Reserve(p1,r1);var b=Reserve(p2,r2);gate.SetResult();var results=await Task.WhenAll(a,b);Assert.Equal(1,results.Count(x=>x==0));Assert.Equal(1,results.Count(x=>x==12));using var store=e.Store();Assert.Single(store.Attempts());
    }
    [Fact] public void ProductionAssembliesContainNoNetworkOrCredentialImplementation()
    {
        // This suite fails on accidental addition of the forbidden production edges. CLI only uses the OS path to its state directory.
        string[] forbidden=["HttpClient", "WebRequest", "Socket(", "CredentialManager", "PasswordVault", "OPENAI_API_KEY", "TOGETHER_API_KEY", "GOOGLE_API_KEY", "Process.Start", "GetEnvironmentVariable"];
        foreach(var file in Directory.EnumerateFiles(Path.Combine(TestEnvironment.Repository,"src"),"*.cs",SearchOption.AllDirectories).Where(p=>!p.Contains(Path.DirectorySeparatorChar+"obj"+Path.DirectorySeparatorChar)))
            foreach(var word in forbidden)Assert.DoesNotContain(word,File.ReadAllText(file).Replace("// Pure wire transformations. No HttpClient, socket, SDK, keyring, credential or environment access.","",StringComparison.Ordinal));
    }
}
