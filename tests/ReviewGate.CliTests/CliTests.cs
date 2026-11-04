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
    [Fact] public async Task LiveBlockedBeforeStateCreation()
    {
        using var e=new TestEnvironment();var r=await TestEnvironment.Invoke([..e.PlanArgs(),"--execution-mode","live"]);
        Assert.Equal(14,r.Exit);Assert.False(Directory.Exists(e.State));Assert.Equal("live_not_available",Data.Parse<Envelope>(r.Stdout).ErrorCode);
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
