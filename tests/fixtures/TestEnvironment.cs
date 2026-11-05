using System.Diagnostics;
using ReviewGate.Contracts;
using ReviewGate.Core.Planning;
using ReviewGate.Infrastructure;

namespace ReviewGate.Testing;

// In-memory stand-in for Windows Credential Manager; KeyRing runs unchanged on top of it.
public sealed class MemoryVault : ICredentialVault
{
    public Dictionary<string, (string UserName, string Secret, DateTimeOffset Written)> Items { get; } = [];
    int clock;
    public string? Read(string target) => Items.TryGetValue(target, out var item) ? item.Secret : null;
    public void Write(string target, string userName, string secret) => Items[target] = (userName, secret, DateTimeOffset.UnixEpoch.AddSeconds(++clock));
    public bool Delete(string target) => Items.Remove(target);
    public IReadOnlyList<VaultItem> Enumerate(string prefix) => Items.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(x => new VaultItem(x.Key, x.Value.UserName, x.Value.Written)).ToList();
}

public sealed class TestEnvironment : IDisposable
{
    public string Root {get;}=Path.Combine(Path.GetTempPath(),"ReviewGate-tests",Guid.NewGuid().ToString("N"));
    public string Source=>Path.Combine(Root,"source");
    public string State=>Path.Combine(Root,"state");
    public string File(string name)=>Path.Combine(Root,name);
    public ReviewerProfile Reviewer {get;}=new(Data.Version,"a","openai","fixture-model","responses","fixture/a","fixture-developer");
    public TestEnvironment(string text="int result = 1;\n", List<FixtureResponse>? responses=null, int chunk=8192)
    {
        Directory.CreateDirectory(Source);Directory.CreateDirectory(Path.Combine(Source,".git"));
        System.IO.File.WriteAllText(Path.Combine(Source,"Sample.cs"),text,Data.Utf8);
        Put("profile.json",new ReviewProfile(Data.Version,"code","code",[new("logic","Check correctness")],chunk,0));
        Put("reviewer.json",Reviewer);
        Put("reviewer-b.json",Reviewer with{Id="b",HostingProvider="together",ModelId="fixture-model-b",ApiProfile="chat_completions",CanonicalModelKey="fixture/b"});
        Put("budget.json",new BudgetPolicy(Data.Version,10,20,30,MaxRetries:2));
        Put("prices.json",new PriceSnapshot(Data.Version,"synthetic",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1),"synthetic://not-live-pricing",Data.Hash("synthetic"),[new("fixture/a",1,2),new("fixture/b",1,2)]));
        Put("fixture.json",new FixtureBundle(Data.Version,"test",responses??[new(Usage:new(100,20))]));
    }
    public void Put<T>(string name,T value)=>System.IO.File.WriteAllText(File(name),Data.Json(value),Data.Utf8);
    public StateStore Store()=>new(State);
    public Plan MakePlan(StateStore store,string mode="single",string project="test",string? baseRun=null,List<string>? findings=null)=>Planner.Build(store,new(project,Source,Source,"synthetic","Review supplied source",File("profile.json"),mode=="independent"?[File("reviewer.json"),File("reviewer-b.json")]:[File("reviewer.json")],File("budget.json"),File("prices.json"),File("fixture.json"),["Sample.cs"],[],mode,baseRun,findings));
    public Grant Grant(Plan p,string? run=null,decimal max=10)=>new(Data.Version,Data.Id("grant"),p.Hash,p.ManifestHash,DateTimeOffset.UtcNow.AddHours(1),max,100,2,RunId:run);
    public string[] PlanArgs(string command="plan")=>[command,"Review supplied source","--state-root",State,"--project","test","--source-root",Source,"--repository-root",Source,"--input-class","synthetic","--profile",File("profile.json"),"--reviewer",File("reviewer.json"),"--budget-policy",File("budget.json"),"--prices",File("prices.json"),"--fixture",File("fixture.json"),"--input","Sample.cs"];
    public string[] Control(string command,string run,params string[] extra)=>[command,"--state-root",State,"--run",run,..extra];
    public static string Repository
    {
        get{for(string? p=AppContext.BaseDirectory;p is not null;p=Path.GetDirectoryName(p))if(System.IO.File.Exists(Path.Combine(p,"ReviewGate.sln")))return p;throw new InvalidOperationException("Repository not found");}
    }
    public static Process Start(string[] args,string? dll=null)
    {
        var start=new ProcessStartInfo("dotnet"){RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};
        start.ArgumentList.Add(dll??Path.Combine(Repository,"src","ReviewGate.Cli","bin","Debug","net10.0","ReviewGate.Cli.dll"));
        foreach(var arg in args)start.ArgumentList.Add(arg);
        var p=Process.Start(start)!;p.StandardInput.Close();return p;
    }
    public static async Task<(int Exit,string Stdout,string Stderr)> Invoke(string[] args,string? dll=null,int timeout=15000)
    {
        using var p=Start(args,dll);var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();
        using var deadline=new CancellationTokenSource(timeout);
        try{await p.WaitForExitAsync(deadline.Token);}catch{if(!p.HasExited)p.Kill(true);throw;}
        return(p.ExitCode,await stdout,await stderr);
    }
    public void Dispose()
    {
        string root=Path.GetFullPath(Root),allowed=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"ReviewGate-tests"))+Path.DirectorySeparatorChar;
        if(!root.StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unsafe test cleanup");
        if(Directory.Exists(root))Directory.Delete(root,true);
    }
}
