using ReviewGate.Contracts;
using ReviewGate.Core.Execution;
using ReviewGate.Testing;

namespace ReviewGate.ReviewTests;
public sealed class AdversarialTests
{
    [Theory][InlineData("summary")][InlineData("search")][InlineData("truncated")]
    public async Task NonRawOrTruncatedFixtureCannotClaimFullReading(string status)
    {using var e=new TestEnvironment(responses:[new(Status:status,Usage:new(100,20))]);using var s=e.Store();var p=e.MakePlan(s);var r=await new ReviewEngine(s).ExecuteAsync(p,e.Grant(p));Assert.NotEqual(0,r.ExitCode);Assert.False(r.Result.Coverage!.RequiredCoverageComplete);Assert.Null(r.Result.Coverage.ActualModelProcessingRatio);}
    [Fact] public async Task ManifestTamperingIsRejectedBeforeAnyDispatch()
    {using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);p.Requests[0]=p.Requests[0] with{Body="tampered"};Assert.Equal(13,(await Assert.ThrowsAsync<ReviewError>(()=>new ReviewEngine(s).ExecuteAsync(p,e.Grant(p)))).ExitCode);Assert.Empty(s.Attempts());}
    [Fact] public async Task HistoricalSnapshotRemainsReadableWithCurrentStaleNotice()
    {
        using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);var engine=new ReviewEngine(s);var r=await engine.ExecuteAsync(p,e.Grant(p));File.AppendAllText(Path.Combine(e.Source,"Sample.cs"),"// new revision");
        Assert.Single(engine.View(r.Result.RunId!).StaleSourceIds);Assert.Single(engine.Export(r.Result.RunId!,"json",false).Result.StaleSourceIds);Assert.Single(engine.Export(r.Result.RunId!,"hglos-findings",false).Result.StaleSourceIds);
    }
    [Theory][InlineData("cannot_assess")][InlineData("not_reported")]
    public async Task UnassessedUnitCannotCompleteCoverage(string disposition)
    {using var e=new TestEnvironment(responses:[new(Disposition:disposition,Usage:new(100,20))]);using var s=e.Store();var p=e.MakePlan(s);var r=await new ReviewEngine(s).ExecuteAsync(p,e.Grant(p));Assert.NotEqual(0,r.ExitCode);Assert.False(r.Result.Coverage!.RequiredCoverageComplete);}
    [Theory][InlineData(401,11)][InlineData(429,21)][InlineData(503,21)]
    public async Task KnownUsageHttpErrorsDoNotBecomeSuccess(int http,int exit)
    {using var e=new TestEnvironment(responses:[new(HttpStatus:http,Usage:new(100,20))]);using var s=e.Store();var p=e.MakePlan(s);var r=await new ReviewEngine(s).ExecuteAsync(p,e.Grant(p));Assert.Equal(exit,r.ExitCode);Assert.False(r.Result.Coverage!.RequiredCoverageComplete);Assert.Equal("settled",s.Attempts().Single().Phase);}
    [Fact] public async Task SamePositionDifferentDefectsRemainSeparateAndLinked()
    {
        var f=new FixtureFinding("Issue one","Reason","result",Assumptions:[]);
        using var e=new TestEnvironment(responses:[new(Disposition:"finding",Findings:[f,f with{Claim="Issue two"}],Usage:new(100,20))]);using var s=e.Store();var p=e.MakePlan(s);var r=await new ReviewEngine(s).ExecuteAsync(p,e.Grant(p));Assert.Equal(0,r.ExitCode);
        string json=File.ReadAllText(Path.Combine(s.Root,r.Result.Artifacts.Single(a=>a.Path.EndsWith("result.json",StringComparison.Ordinal)).Path));var result=System.Text.Json.Nodes.JsonNode.Parse(json)!;Assert.Equal(2,result["findings"]!.AsArray().Count);Assert.Single(result["findings"]![0]!["related_finding_ids"]!.AsArray());
    }
    [Fact] public void EmptySourceIsNotReportedAsFullCoverage()
    {using var e=new TestEnvironment("");using var s=e.Store();Assert.Equal("no_reviewable_content",Assert.Throws<ReviewError>(()=>e.MakePlan(s)).Code);}
    [Fact] public async Task ExpiredGrantNeverDispatches()
    {using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);var r=await new ReviewEngine(s).ExecuteAsync(p,e.Grant(p)with{ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1)});Assert.Equal(10,r.ExitCode);Assert.Empty(s.Attempts());}
    [Fact] public async Task ArtifactLossMakesStatusFailInsteadOfClaimingCompleted()
    {using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);var engine=new ReviewEngine(s);var r=await engine.ExecuteAsync(p,e.Grant(p));File.Delete(Path.Combine(s.Root,r.Result.Artifacts[0].Path));Assert.Equal(13,Assert.Throws<ReviewError>(()=>engine.View(r.Result.RunId!)).ExitCode);}
    [Fact] public void QuoteLineConstraintRejectsMovedLine()
    {using var e=new TestEnvironment("header\nresult\n");using var s=e.Store();var p=e.MakePlan(s);Assert.Throws<ReviewError>(()=>ReviewEngine.ValidateFindings(p,p.Requests[0],[new("Issue","Reason","result",Line:1,Assumptions:[])]));}
}
