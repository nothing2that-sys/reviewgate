using System.Diagnostics;
using System.Text.Json.Nodes;
using ReviewGate.Contracts;
using ReviewGate.Core.Execution;
using ReviewGate.Hglos;
using ReviewGate.Testing;

namespace ReviewGate.HglosTests;
public sealed class HglosTests
{
    const string SchemaHash="e1cb5ce06619bb501956e215bfb51304c74e99190b48e4b82a5474fced8caad5";
    const string ValidatorHash="a2cbea5da71019e0e2feb263087d474d7f22cc4f26970188c616e6555abb4bb0";
    [Fact] public async Task ExportPassesPinnedRealValidatorAndTamperingFails()
    {
        using var e=new TestEnvironment(responses:[new(Disposition:"finding",Findings:[new("Wrong value","Reason","result",ReportedEvidenceGrade:"observed",Assumptions:["Assumption"])],Usage:new(100,20))]);using var s=e.Store();var p=e.MakePlan(s);var engine=new ReviewEngine(s);var r=await engine.ExecuteAsync(p,e.Grant(p));Assert.Equal(0,r.ExitCode);
        var export=engine.Export(r.Result.RunId!,"hglos-findings",false);Assert.Equal(0,export.ExitCode);BundleWriter.Verify(s.Root,export.Result.Artifacts);
        string raw=Path.Combine(s.Root,export.Result.Artifacts.Single(a=>a.Path.EndsWith("hglos.findings.synthetic.json",StringComparison.Ordinal)).Path);
        Assert.Equal("inferred",JsonNode.Parse(File.ReadAllText(raw))!["findings"]![0]!["evidence"]!.GetValue<string>());
        string dir=Path.Combine(TestEnvironment.Repository,"tests","fixtures","hglos"),schema=Path.Combine(dir,"findings-schema.json"),validator=Path.Combine(dir,"validate_findings.py");
        Assert.Equal(SchemaHash,Data.FileHash(schema));Assert.Equal(ValidatorHash,Data.FileHash(validator));
        async Task<int> Validate(string input)
        {
            string python=Environment.GetEnvironmentVariable("REVIEWGATE_TEST_PYTHON")??throw new InvalidOperationException("REVIEWGATE_TEST_PYTHON must point to a verified Python interpreter; no silent skip");
            var start=new ProcessStartInfo(python){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var a in new[]{validator,input,"--schema",schema})start.ArgumentList.Add(a);
            using var process=Process.Start(start)!;using var timeout=new CancellationTokenSource(10000);try{await process.WaitForExitAsync(timeout.Token);}catch{if(!process.HasExited)process.Kill(true);throw;}return process.ExitCode;
        }
        Assert.Equal(0,await Validate(raw));string bad=e.File("tampered.json");var value=JsonNode.Parse(File.ReadAllText(raw))!;value["findings"]![0]!["evidence"]="quoted source is not an enum";File.WriteAllText(bad,value.ToJsonString());Assert.Equal(2,await Validate(bad));
        File.AppendAllText(raw," ");Assert.Equal(13,Assert.Throws<ReviewError>(()=>BundleWriter.Verify(s.Root,export.Result.Artifacts)).ExitCode);
    }
    [Fact] public async Task IncompleteZeroFindingsNeverExportsNormalEmpty()
    {
        using var e=new TestEnvironment(responses:[new(Status:"truncated",Usage:new(100,20))]);using var s=e.Store();var p=e.MakePlan(s);var engine=new ReviewEngine(s);var r=await engine.ExecuteAsync(p,e.Grant(p));Assert.NotEqual(0,r.ExitCode);
        var strict=engine.Export(r.Result.RunId!,"hglos-findings",false);Assert.NotEqual(0,strict.ExitCode);Assert.Empty(strict.Result.Artifacts);
        var partial=engine.Export(r.Result.RunId!,"hglos-findings",true);Assert.Equal(20,partial.ExitCode);Assert.DoesNotContain(partial.Result.Artifacts,a=>a.Path.EndsWith("hglos.findings.synthetic.json",StringComparison.Ordinal));Assert.Contains(partial.Result.Artifacts,a=>a.Path.EndsWith("provenance.json",StringComparison.Ordinal));
    }
    [Fact] public async Task SuggestionsAndMissingRepositoryCoordinatesAreExplicitExclusions()
    {
        using var e=new TestEnvironment(responses:[new(Disposition:"finding",Findings:[new("Improve name","Reason","result",Kind:"suggestion",Assumptions:[])],Usage:new(100,20))]);using var s=e.Store();var p=e.MakePlan(s);var engine=new ReviewEngine(s);var r=await engine.ExecuteAsync(p,e.Grant(p));Assert.Equal(0,r.ExitCode);Assert.Equal(20,engine.Export(r.Result.RunId!,"hglos-findings",false).ExitCode);
    }
}
