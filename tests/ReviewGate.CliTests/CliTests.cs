using ReviewGate.Cli;
using ReviewGate.Contracts;
using ReviewGate.Testing;

namespace ReviewGate.CliTests;
public sealed class CliTests
{
    [Fact] public async Task ClosedStdinPlanThenGrantExecStatusAndExport()
    {
        using var e=new TestEnvironment();string output=e.File("output.json");
        var planned=await TestEnvironment.Invoke([..e.PlanArgs(),"--output",output]);
        Assert.Equal(0,planned.Exit);Assert.Equal("",planned.Stderr);Assert.Equal(planned.Stdout.Trim(),File.ReadAllText(output));
        var env=Data.Parse<Envelope>(planned.Stdout);Assert.Equal("planned",env.Status);
        using var store=e.Store();var p=store.GetPlan(env.PlanId!);e.Put("grant.json",e.Grant(p));
        var executed=await TestEnvironment.Invoke(["exec","--state-root",e.State,"--plan",p.Id,"--grant-file",e.File("grant.json")]);
        Assert.Equal(0,executed.Exit);var result=Data.Parse<Envelope>(executed.Stdout);Assert.Equal("completed",result.Status);Assert.True(result.Coverage!.RequiredCoverageComplete);
        Assert.Null(result.Coverage.ActualModelProcessingRatio);Assert.NotEmpty(result.Artifacts);
        var status=await TestEnvironment.Invoke(e.Control("status",result.RunId!));Assert.Equal(0,status.Exit);Assert.False(Data.Parse<Envelope>(status.Stdout).OwnerAlive);
        var exported=await TestEnvironment.Invoke(e.Control("export",result.RunId!,"--format","hglos-findings"));Assert.Equal(0,exported.Exit);
        Assert.Equal("int result = 1;\n",File.ReadAllText(Path.Combine(e.Source,"Sample.cs")));
    }
    [Fact] public async Task ValidatedOutputAlsoReceivesErrorEnvelope()
    {
        using var e=new TestEnvironment();using(var s=e.Store()){var p=e.MakePlan(s);e.Put("grant.json",e.Grant(p));File.AppendAllText(Path.Combine(e.Source,"Sample.cs"),"changed");
        string output=e.File("error.json");var r=await TestEnvironment.Invoke(["exec","--state-root",e.State,"--plan",p.Id,"--grant-file",e.File("grant.json"),"--output",output]);
        Assert.Equal(13,r.Exit);Assert.Equal(r.Stdout.Trim(),File.ReadAllText(output));Assert.Equal("snapshot_changed_or_missing",Data.Parse<Envelope>(File.ReadAllText(output)).ErrorCode);}
    }
    [Fact] public async Task OutputCannotWriteSourceEvenOnFailure()
    {
        using var e=new TestEnvironment();string file=Path.Combine(e.Source,"new.json");
        var r=await TestEnvironment.Invoke([..e.PlanArgs(),"--output",file]);Assert.Equal(2,r.Exit);Assert.False(File.Exists(file));
    }
    [Theory]
    [InlineData("--unknown","x")]
    [InlineData("--project","other")]
    public async Task InvalidOrDuplicateOptionsFail(string option,string value)
    {using var e=new TestEnvironment();Assert.Equal(2,(await CliHost.RunAsync([..e.PlanArgs(),option,value])).ExitCode);}
    [Fact] public async Task LiveNeedsPlanThenInteractiveGrantAndNeverTakesKeysOrFixtures()
    {
        using var e=new TestEnvironment();
        // Direct live exec is refused before any state exists.
        var direct=await TestEnvironment.Invoke([..e.PlanArgs("exec"),"--execution-mode","live"]);
        Assert.Equal(2,direct.Exit);Assert.False(Directory.Exists(e.State));Assert.Equal("live_requires_plan_then_grant",Data.Parse<Envelope>(direct.Stdout).ErrorCode);
        var withFixture=await TestEnvironment.Invoke([..e.PlanArgs(),"--execution-mode","live"]);Assert.Equal("fixture_not_allowed_in_live",Data.Parse<Envelope>(withFixture.Stdout).ErrorCode);
        string[] liveArgs=[..e.PlanArgs().TakeWhile(a=>a!="--fixture"),..e.PlanArgs().SkipWhile(a=>a!="--fixture").Skip(2),"--execution-mode","live"];
        var planned=await TestEnvironment.Invoke(liveArgs);Assert.Equal(0,planned.Exit);var plan=Data.Parse<Envelope>(planned.Stdout);
        Assert.StartsWith("lplan_",plan.PlanId);Assert.Equal("live",plan.ExecutionMode);Assert.Equal("issue_live_grant_interactively",plan.RequiredAction);
        // Grant and key entry require an interactive console; redirected stdin (any automation) is refused.
        var grant=await TestEnvironment.Invoke(["grant","--state-root",e.State,"--plan",plan.PlanId!,"--max-usd","1"]);
        Assert.Equal(2,grant.Exit);Assert.Equal("interactive_console_required",Data.Parse<Envelope>(grant.Stdout).ErrorCode);
        var key=await TestEnvironment.Invoke(["credential","set","--provider","openai"]);
        Assert.Equal(2,key.Exit);Assert.Equal("interactive_console_required",Data.Parse<Envelope>(key.Stdout).ErrorCode);
        Assert.Equal("unknown_option",Data.Parse<Envelope>((await TestEnvironment.Invoke(["credential","set","--provider","openai","--key","x"])).Stdout).ErrorCode);
    }
    [Fact] public async Task MissingFixtureIsInputErrorAndNoRun()
    {
        using var e=new TestEnvironment();File.Delete(e.File("fixture.json"));var r=await CliHost.RunAsync(e.PlanArgs());Assert.Equal(2,r.ExitCode);
    }
    [Fact] public async Task GrantRequiredDoesNotDispatch()
    {
        using var e=new TestEnvironment();var r=await CliHost.RunAsync(e.PlanArgs("exec"));Assert.Equal(10,r.ExitCode);
        using var store=e.Store();Assert.Empty(store.Attempts(r.Result.RunId));
    }
    [Fact] public async Task ConfigRelativePathsAndClosedStdin()
    {
        using var e=new TestEnvironment();e.Put("config.json",new{schema_version=Data.Version,options=new{state_root="state",project="configured",source_root="source",input_class="synthetic",profile="profile.json",reviewer=new[]{"reviewer.json"},budget_policy="budget.json",prices="prices.json",fixture="fixture.json",input=new[]{"Sample.cs"}}});
        var r=await TestEnvironment.Invoke(["plan","Review","--config",e.File("config.json")]);Assert.Equal(0,r.Exit);
        var empty=await TestEnvironment.Invoke(["plan","--prompt-stdin","--config",e.File("config.json")]);Assert.Equal(2,empty.Exit);
    }
}
