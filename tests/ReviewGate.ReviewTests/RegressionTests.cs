using System.Text.Json.Nodes;
using ReviewGate.Contracts;
using ReviewGate.Core.Execution;
using ReviewGate.Core.Planning;
using ReviewGate.Hglos;
using ReviewGate.Testing;

namespace ReviewGate.ReviewTests;
public sealed class RegressionTests
{
    static FixtureFinding Defect(string quote,string source="$source")=>new("Wrong value","Concrete reason",quote,SourceId:source,ReportedEvidenceGrade:"observed",Assumptions:[]);
    static Plan PlanFor(TestEnvironment e,Infrastructure.StateStore s,List<string> inputs,List<string>? references=null,string? sourceRoot=null,string? repository=null)
        =>Planner.Build(s,new("test",sourceRoot??e.Source,repository??e.Source,"synthetic","Review",e.File("profile.json"),[e.File("reviewer.json")],e.File("budget.json"),e.File("prices.json"),e.File("fixture.json"),inputs,references??[]));
    static string Artifact(Infrastructure.StateStore s,Envelope env,string name)=>File.ReadAllText(Path.Combine(s.Root,env.Artifacts.Single(a=>a.Path.EndsWith(name,StringComparison.Ordinal)).Path));

    [Theory][InlineData("summary")][InlineData("search")]
    public async Task InvalidPayloadWithKnownUsageSettlesAsInvalidInsteadOfUnknown(string status)
    {
        using var e=new TestEnvironment(responses:[new(Status:status,Usage:new(100,20))]);using var s=e.Store();var p=e.MakePlan(s);
        var r=await new ReviewEngine(s).ExecuteAsync(p,e.Grant(p));
        Assert.Equal(21,r.ExitCode);Assert.Equal("failed",r.Result.Status);Assert.Equal("settled",s.Attempts().Single().Phase);
        var unit=s.Units(r.Result.RunId!).Single();Assert.Equal("invalid",unit.ValidationState);Assert.Equal("invalid_reviewer_payload",unit.ErrorCode);
    }
    [Theory][InlineData("summary")][InlineData("search_excerpt")]
    public void NonRawExposureIsNeverCountedAsFullReading(string exposure)
    {
        using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);var q=p.Requests.Single();
        var unit=new UnitResult(q.UnitId,q.Id,q.ReviewerId,q.CriterionId,q.SourceId,q.Stage,exposure,"synthetic","valid","no_finding",true,17,[],null);
        var c=ReviewEngine.CoverageFor(p,[unit]);Assert.Equal(0,c.ValidUnits);Assert.False(c.RequiredCoverageComplete);Assert.Equal(0,c.ByCriterionReviewer.Single().ValidUnits);
    }
    [Fact] public void SameModelUnderAnotherKeyOrCaseIsRejected()
    {
        using var e=new TestEnvironment();using var s=e.Store();
        e.Put("reviewer-b.json",e.Reviewer with{Id="b",HostingProvider="together",ApiProfile="chat_completions",ModelId="vendor/FIXTURE-MODEL",CanonicalModelKey="fixture/other"});
        Assert.Equal("duplicate_reviewer_model",Assert.Throws<ReviewError>(()=>e.MakePlan(s,"independent")).Code);
        e.Put("reviewer-b.json",e.Reviewer with{Id="b",ModelId="fixture-model-b",CanonicalModelKey="Fixture/A"});
        Assert.Equal("invalid_canonical_model_key",Assert.Throws<ReviewError>(()=>e.MakePlan(s,"independent")).Code);
    }
    [Fact] public async Task CoverageIsReportedPerCriterionAndReviewer()
    {
        using var e=new TestEnvironment(responses:[new(Reviewer:"a",Usage:new(100,20)),new(Reviewer:"b",Status:"truncated",Usage:new(100,20))]);
        using var s=e.Store();var p=e.MakePlan(s,"independent");var r=await new ReviewEngine(s).ExecuteAsync(p,e.Grant(p));
        Assert.Equal(20,r.ExitCode);var pairs=r.Result.Coverage!.ByCriterionReviewer;
        Assert.Equal(1m,pairs.Single(x=>x.ReviewerId=="a").SyntheticProcessingRatio);Assert.Equal(0m,pairs.Single(x=>x.ReviewerId=="b").SyntheticProcessingRatio);
        Assert.All(pairs,x=>Assert.Null(x.ActualModelProcessingRatio));
    }
    [Fact] public void EmptyTargetBesideContentIsRecordedNotSilentlyDropped()
    {
        using var e=new TestEnvironment();File.WriteAllText(Path.Combine(e.Source,"Empty.cs"),"");using var s=e.Store();var p=PlanFor(e,s,["Sample.cs","Empty.cs"]);
        var empty=p.Sources.Single(x=>x.LogicalPath=="Empty.cs");Assert.Equal(new[]{empty.Id},ReviewEngine.CoverageFor(p,[]).EmptySourceIds);
    }
    [Fact] public async Task ResumeWithoutGrantKeepsPartialRunExportable()
    {
        using var e=new TestEnvironment("first line\nsecond line\n",[new(Chunk:0,Usage:new(100,20)),new(Chunk:1,Status:"truncated",Usage:new(100,20))],chunk:12);
        using var s=e.Store();var p=e.MakePlan(s);var engine=new ReviewEngine(s);var r=await engine.ExecuteAsync(p,e.Grant(p));Assert.Equal(20,r.ExitCode);
        var blocked=await engine.ExecuteAsync(p,null,r.Result.RunId);Assert.Equal(10,blocked.ExitCode);Assert.Equal("partial",blocked.Result.Status);
        var export=engine.Export(r.Result.RunId!,"hglos-findings",true);Assert.Equal(20,export.ExitCode);
        Assert.Contains(export.Result.Artifacts,a=>a.Path.EndsWith(HglosExporter.PartialRawName,StringComparison.Ordinal));
        Assert.DoesNotContain(export.Result.Artifacts,a=>a.Path.EndsWith("/"+HglosExporter.CompleteRawName,StringComparison.Ordinal));
    }
    [Fact] public async Task NeedsReconciliationStrictExportIsRejected()
    {
        using var e=new TestEnvironment(responses:[new(Status:"unknown")]);using var s=e.Store();var p=e.MakePlan(s);var engine=new ReviewEngine(s);
        var r=await engine.ExecuteAsync(p,e.Grant(p));Assert.Equal(13,r.ExitCode);
        var export=engine.Export(r.Result.RunId!,"hglos-findings",false);Assert.Equal(13,export.ExitCode);Assert.Equal("reconciliation_required",export.Result.ErrorCode);Assert.Empty(export.Result.Artifacts);
    }
    [Fact] public async Task DiffFindingKeepsDiffCoordinatesAndIsNotProjected()
    {
        using var e=new TestEnvironment(responses:[new(Disposition:"finding",Findings:[Defect("-int removed = 0;")],Usage:new(100,20))]);
        File.WriteAllText(Path.Combine(e.Source,"change.DIFF"),"--- a/Sample.cs\n+++ b/Sample.cs\n@@ -3,2 +3,2 @@\n-int removed = 0;\n+int added = 1;\n same();\n");
        using var s=e.Store();var p=PlanFor(e,s,["change.DIFF"]);var engine=new ReviewEngine(s);var r=await engine.ExecuteAsync(p,e.Grant(p));Assert.Equal(0,r.ExitCode);
        var f=s.Units(r.Result.RunId!).Single().Findings.Single();
        Assert.Equal("diff",f.CoordinateSystem);Assert.Equal(4,f.Line);Assert.Equal(3,f.DiffBeforeLine);Assert.Null(f.DiffAfterLine);Assert.Null(f.ExportPath);
        Assert.Contains("diff display line 4 (before 3, after -)",Artifact(s,r.Result,"report.md"));
        var export=engine.Export(r.Result.RunId!,"hglos-findings",false);Assert.Equal(20,export.ExitCode);Assert.Equal("projection_incomplete",export.Result.ErrorCode);
    }
    [Fact] public async Task ReferenceSourceFindingIsExcludedFromHglosProjection()
    {
        using var e=new TestEnvironment(responses:[new(Disposition:"finding",Findings:[Defect("reference only","Ref.cs")],Usage:new(100,20))]);
        File.WriteAllText(Path.Combine(e.Source,"Ref.cs"),"// reference only\n");using var s=e.Store();var p=PlanFor(e,s,["Sample.cs"],["Ref.cs"]);var engine=new ReviewEngine(s);
        var r=await engine.ExecuteAsync(p,e.Grant(p));Assert.Equal(0,r.ExitCode);
        var export=engine.Export(r.Result.RunId!,"hglos-findings",true);Assert.Equal(20,export.ExitCode);
        Assert.Contains("reference_source_not_review_target",Artifact(s,export.Result,"provenance.json"));
    }
    [Fact] public async Task SourceRootBelowRepositoryRootUsesRepositoryRelativeExportPath()
    {
        using var e=new TestEnvironment(responses:[new(Disposition:"finding",Findings:[Defect("value")],Usage:new(100,20))]);
        string sub=Path.Combine(e.Source,"src");Directory.CreateDirectory(sub);File.WriteAllText(Path.Combine(sub,"Inner.cs"),"int value = 2;\n");
        using var s=e.Store();var p=PlanFor(e,s,["inner.cs"],sourceRoot:sub,repository:e.Source);
        Assert.Equal("Inner.cs",p.Sources.Single().LogicalPath);Assert.Equal("src/Inner.cs",p.Sources.Single().ExportPath);
        var engine=new ReviewEngine(s);var r=await engine.ExecuteAsync(p,e.Grant(p));var export=engine.Export(r.Result.RunId!,"hglos-findings",false);Assert.Equal(0,export.ExitCode);
        Assert.Equal("src/Inner.cs",JsonNode.Parse(Artifact(s,export.Result,HglosExporter.CompleteRawName))!["findings"]![0]!["path"]!.GetValue<string>());
    }
    [Fact] public async Task ModelTextCannotInjectMarkdownLinksOrHtml()
    {
        var f=new FixtureFinding("[click](http://example.invalid) <script>x</script>","# heading","result",Assumptions:[]);
        using var e=new TestEnvironment(responses:[new(Disposition:"finding",Findings:[f],Usage:new(100,20))]);using var s=e.Store();var p=e.MakePlan(s);
        var r=await new ReviewEngine(s).ExecuteAsync(p,e.Grant(p));var report=Artifact(s,r.Result,"report.md");
        Assert.DoesNotContain("<script>",report);Assert.DoesNotContain("](http",report);Assert.Contains("\\# heading",report);
    }
    [Fact] public async Task VerifyBaseRunMustBelongToSameProject()
    {
        using var e=new TestEnvironment(responses:[new(Disposition:"finding",Findings:[Defect("result")],Usage:new(100,20))]);using var s=e.Store();var p=e.MakePlan(s);
        var r=await new ReviewEngine(s).ExecuteAsync(p,e.Grant(p));var id=s.Units(r.Result.RunId!).Single().Findings.Single().Id;
        Assert.Equal("verification_project_mismatch",Assert.Throws<ReviewError>(()=>e.MakePlan(s,"verify","other",r.Result.RunId,[id])).Code);
    }
}
