using ReviewGate.Contracts;
using ReviewGate.Core.Execution;
using ReviewGate.Core.Planning;
using ReviewGate.Testing;

namespace ReviewGate.ReviewTests;
public sealed class ReviewTests
{
    static FixtureFinding Finding(string quote="result",string grade="observed",List<string>? assumptions=null)=>new("Wrong value","Concrete reason",quote,ReportedEvidenceGrade:grade,Assumptions:assumptions??[]);
    [Fact] public async Task ExactEvidenceAndIndependentContradictionsArePreserved()
    {
        using var e=new TestEnvironment(responses:[new(Reviewer:"a",Disposition:"finding",Findings:[Finding()],Usage:new(100,20)),new(Reviewer:"b",Usage:new(100,20))]);
        using var s=e.Store();var p=e.MakePlan(s,"independent");Assert.Equal(2,p.Requests.Count);
        Assert.DoesNotContain("Wrong value",p.Requests[1].Body);
        var r=await new ReviewEngine(s).ExecuteAsync(p,e.Grant(p));Assert.Equal(0,r.ExitCode);Assert.Equal(2,s.Units(r.Result.RunId!).Count);Assert.Single(s.Units(r.Result.RunId!).SelectMany(u=>u.Findings));
        string report=File.ReadAllText(Path.Combine(s.Root,r.Result.Artifacts.Single(a=>a.Path.EndsWith("result.json",StringComparison.Ordinal)).Path));Assert.Contains("comparisons",report);Assert.Contains("no_finding",report);
    }
    [Theory][InlineData("absent")][InlineData("result")]
    public void MissingOrAmbiguousQuoteRejected(string quote)
    {
        // A missing or ambiguous quote is kept as an unverified finding (no location, never exported), not trusted as evidence.
        using var e=new TestEnvironment("result result\n");using var s=e.Store();var p=e.MakePlan(s);var f=Assert.Single(ReviewEngine.ValidateFindings(p,p.Requests[0],[Finding(quote)]));
        Assert.False(f.QuoteValid);Assert.False(f.LocationValid);Assert.Null(f.Line);Assert.Equal("unconfirmed",f.ExportedEvidenceGrade);
    }
    [Theory][InlineData("observed",true,"inferred")][InlineData("unconfirmed",true,"unconfirmed")][InlineData("observed",false,"observed")]
    public void GradesDoNotPromoteAssertions(string reported,bool assumption,string expected)
    {using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);var f=ReviewEngine.ValidateFindings(p,p.Requests[0],[Finding(grade:reported,assumptions:assumption?["Assumed runtime"]:[])]).Single();Assert.Equal(expected,f.ExportedEvidenceGrade);Assert.Equal(1,f.Line);}
    [Fact] public void SameCanonicalModelAcrossHostsIsRejected()
    {using var e=new TestEnvironment();e.Put("reviewer-b.json",e.Reviewer with{Id="b",HostingProvider="together",ApiProfile="chat_completions"});using var s=e.Store();Assert.Equal("duplicate_reviewer_model",Assert.Throws<ReviewError>(()=>e.MakePlan(s,"independent")).Code);}
    [Fact] public async Task ChangedSourceAndFixtureStopBeforeDispatch()
    {using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);File.AppendAllText(Path.Combine(e.Source,"Sample.cs"),"changed");Assert.Equal(13,(await Assert.ThrowsAsync<ReviewError>(()=>new ReviewEngine(s).ExecuteAsync(p,e.Grant(p)))).ExitCode);Assert.Empty(s.Attempts());}
    [Fact] public void CoverageUsesExactUnitSetAndIndependentBytePartition()
    {using var e=new TestEnvironment(new string('a',50)+"\n",chunk:16);using var s=e.Store();var p=e.MakePlan(s);Assert.True(p.Requests.Count>1);var c=ReviewEngine.CoverageFor(p,[]);Assert.Equal(51,c.TotalCoreBytes);Assert.Equal(0,c.ValidCoreBytes);Assert.False(c.RequiredCoverageComplete);Assert.Null(c.ActualModelProcessingRatio);p.Chunks.RemoveAt(0);Assert.Throws<ReviewError>(()=>ReviewEngine.CoverageFor(p,[]));}
    [Fact] public async Task VerifyModePinsSelectedFinding()
    {
        using var e=new TestEnvironment(responses:[new(Disposition:"finding",Findings:[Finding()],Usage:new(100,20))]);using var s=e.Store();var p=e.MakePlan(s);var r=await new ReviewEngine(s).ExecuteAsync(p,e.Grant(p));var f=s.Units(r.Result.RunId!).Single().Findings.Single();
        var verify=e.MakePlan(s,"verify",baseRun:r.Result.RunId,findings:[f.Id]);Assert.Contains(f.Id,verify.Requests.Single().Body);Assert.Equal("verify",verify.Mode);
    }
}
