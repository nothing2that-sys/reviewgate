using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ReviewGate.Contracts;
using ReviewGate.Core.Execution;
using ReviewGate.Core.Planning;
using ReviewGate.Core.Sources;
using ReviewGate.Infrastructure;

namespace ReviewGate.Cli;
public static class CliHost
{
    static readonly HashSet<string> ValueKeys=["state-root","project","config","source-root","repository-root","input","reference","input-class","profile","mode","base-run","finding","reviewer","budget-policy","prices","execution-mode","fixture","plan","grant-file","run","format","resolve-unknown","prompt-file","output"];
    static readonly HashSet<string> Flags=["allow-partial","recover-only","prompt-stdin"];
    static readonly HashSet<string> Repeated=["input","reference","reviewer","finding"];
    public sealed class Options
    {
        public string Command {get;init;}="";
        public Dictionary<string,List<string>> Items {get;}=[];
        public string? Instruction {get;set;}
        public string? One(string key)=>Items.TryGetValue(key,out var values)?values.Single():null;
        public List<string> Many(string key)=>Items.TryGetValue(key,out var values)?values:[];
        public bool Has(string key)=>Items.ContainsKey(key);
        public string Need(string key,int exit=2)=>One(key)??throw new ReviewError(exit,key.Replace('-','_')+"_required");
    }
    public static Options Parse(string[] args)
    {
        Data.Require(args.Length>0,"command_required");var o=new Options{Command=args[0]};
        Data.Require(o.Command is "plan" or "exec" or "status" or "cancel" or "resume" or "export","unknown_command");
        for(int i=1;i<args.Length;i++)
        {
            string key=args[i];if(!key.StartsWith("--",StringComparison.Ordinal)){Data.Require(o.Instruction is null,"duplicate_instruction");o.Instruction=key;continue;}
            key=key[2..];Data.Require(ValueKeys.Contains(key)||Flags.Contains(key),"unknown_option");string value="true";
            if(ValueKeys.Contains(key)){Data.Require(i+1<args.Length&&!args[i+1].StartsWith("--",StringComparison.Ordinal),"option_value_required");value=args[++i];}
            if(!o.Items.TryAdd(key,[value])){Data.Require(Repeated.Contains(key),"duplicate_option");o.Items[key].Add(value);}
        }
        if(o.Has("config"))
        {
            Data.Require(!o.Has("plan"),"plan_override_forbidden");string path=Path.GetFullPath(o.Need("config"));
            var config=Data.Read<JsonObject>(path);Data.Require(config["schema_version"]?.GetValue<string>()==Data.Version&&config["options"] is JsonObject,"invalid_config");
            Data.Require(config.All(x=>x.Key is "schema_version" or "options"),"invalid_config");
            foreach(var (raw,node) in (JsonObject)config["options"]!)
            {
                string key=raw.Replace('_','-');Data.Require(ValueKeys.Contains(key)&&key is not ("config" or "plan" or "run" or "grant-file" or "resolve-unknown"),"invalid_config_option");
                if(o.Has(key))continue;
                List<string> values=node is JsonArray array?array.Select(x=>x?.GetValue<string>()??throw new ReviewError(2,"invalid_config")).ToList():[node?.GetValue<string>()??throw new ReviewError(2,"invalid_config")];
                Data.Require(values.Count>0&&(Repeated.Contains(key)||values.Count==1),"invalid_config_option");
                if(key is "state-root" or "source-root" or "repository-root" or "profile" or "reviewer" or "budget-policy" or "prices" or "fixture" or "prompt-file")values=values.Select(x=>Path.GetFullPath(x,Path.GetDirectoryName(path)!)).ToList();
                o.Items[key]=values;
            }
        }
        Data.Require(o.One("execution-mode") is null or "synthetic" or "live","invalid_execution_mode");
        if(o.One("execution-mode")=="live")throw new ReviewError(14,"live_not_available","offline_fixture_only");
        Data.Require(!o.Has("allow-partial")||o.Command=="export","invalid_option_combination");
        Data.Require(!o.Has("recover-only")||o.Command=="resume","invalid_option_combination");
        Data.Require(!o.Has("resolve-unknown")||(o.Command=="resume"&&o.Has("recover-only")),"invalid_option_combination");
        if(o.Has("plan"))Data.Require(o.Command=="exec"&&o.Instruction is null&&!o.Items.Keys.Any(k=>k is not ("plan" or "state-root" or "grant-file" or "output" or "execution-mode")),"plan_override_forbidden");
        if(o.Command is "status" or "cancel" or "resume" or "export")Data.Require(o.Instruction is null&&!o.Has("prompt-file")&&!o.Has("prompt-stdin"),"unexpected_instruction");
        if(o.Has("output")){string file=Path.GetFullPath(o.Need("output"));SafePath.NoLinks(file);Data.Require(!File.Exists(file)&&!Directory.Exists(file),"output_already_exists");}
        return o;
    }
    public static async Task<CommandResult> RunAsync(string[] args,CancellationToken ct=default)
    {
        string command=args.FirstOrDefault()??"";
        ReviewEngine? engine=null;
        try
        {
            var o=Parse(args);string state=Path.GetFullPath(o.One("state-root")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ReviewGate"));
            Data.Require(!state.StartsWith(@"\\",StringComparison.Ordinal),"network_state_root_unsupported");SafePath.NoLinks(state);
            using var store=new StateStore(state);engine=new ReviewEngine(store);
            if(command is "status" or "cancel" or "resume" or "export")
            {
                var run=store.GetRun(o.Need("run"));var plan=store.GetPlan(run.PlanId);CheckOutput(o,plan,store.Root);
                CommandResult Finish(CommandResult r)=>r with {OutputPath=o.One("output")};
                if(command=="status")return Finish(new(0,engine.View(run.Id)));
                if(command=="cancel")
                {
                    bool terminal=run.Status is "completed" or "failed" or "cancelled";if(!terminal)store.Cancel(run);
                    return Finish(new(0,engine.View(run.Id,"cancel")with{CancelRequested=!terminal,AlreadyTerminal=terminal,RequiredAction=store.OwnerAlive(run.ProjectId)?"wait_for_worker":"resume_recover_only"}));
                }
                if(command=="export")return Finish(engine.Export(run.Id,o.One("format")??"json",o.Has("allow-partial")));
                var grant=o.One("grant-file")is string gf?Data.Read<Grant>(gf):null;
                var resolution=o.One("resolve-unknown")is string rf?Data.Read<ResolutionFixture>(rf):null;
                return Finish(await engine.ExecuteAsync(plan,grant,run.Id,o.Has("recover-only"),resolution,ct));
            }
            Plan p;
            if(o.Has("plan"))p=store.GetPlan(o.Need("plan"));
            else
            {
                int count=(o.Instruction is not null?1:0)+(o.Has("prompt-file")?1:0)+(o.Has("prompt-stdin")?1:0);Data.Require(count==1,"instruction_required_or_duplicated");
                string instruction=o.Instruction??(o.One("prompt-file")is string pf?ReadInstruction(pf):await ReadStdin(ct));
                string source=SafePath.Full(o.Need("source-root"));if(o.Has("output"))Data.Require(!SafePath.Within(o.Need("output"),source),"output_source_overlap");
                p=Planner.Build(store,new(o.Need("project"),source,o.One("repository-root"),o.Need("input-class"),instruction,o.Need("profile"),o.Many("reviewer"),o.Need("budget-policy"),o.Need("prices",12),o.Need("fixture"),o.Many("input"),o.Many("reference"),o.One("mode")??"single",o.One("base-run"),o.Many("finding")));
            }
            CheckOutput(o,p,store.Root);
            if(command=="plan")return new(0,new Envelope{Command="plan",PlanId=p.Id,Status="planned",RequiredAction="provide_simulation_grant",Cost=new JsonObject{["estimated_upper_usd"]=p.Requests.Sum(r=>r.ReservationUsd),["mode"]="synthetic",["billed_usd"]=null},Artifacts=ReviewGate.Hglos.BundleWriter.Write(store.Root,store.PathFor("plans",p.Id,Data.Id("preview")),new Dictionary<string,string>{{"plan.json",Data.Json(p)}})},o.One("output"));
            return (await engine.ExecuteAsync(p,o.One("grant-file")is string g?Data.Read<Grant>(g):null,cancellation:ct)) with {OutputPath=o.One("output")};
        }
        catch(ReviewError e){return new(e.ExitCode,new Envelope{Command=command,RunId=engine?.CurrentRunId,PlanId=engine?.CurrentPlanId,ErrorCode=e.Code,RequiredAction=e.Action});}
        catch(SqliteException e){return new(13,new Envelope{Command=command,ErrorCode=e.SqliteErrorCode is 5 or 6?"state_store_busy":"state_store_corrupt"});}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){return new(21,new Envelope{Command=command,RunId=engine?.CurrentRunId,PlanId=engine?.CurrentPlanId,ErrorCode="storage_failure",RequiredAction="inspect_run_with_status"});}
        catch(OperationCanceledException){return new(130,new Envelope{Command=command,Status="cancelled",ErrorCode="cancelled"});}
        catch(Exception){return new(21,new Envelope{Command=command,ErrorCode="internal_failure"});}
    }
    static void CheckOutput(Options o,Plan p,string state){if(o.One("output")is string file)Data.Require(!SafePath.Within(file,p.SourceRoot)&&!SafePath.Within(file,state),"output_protected_overlap");}
    static string ReadInstruction(string path){Data.Require(new FileInfo(path).Length<=1_048_576,"instruction_limit");return File.ReadAllText(path,Data.Utf8);}
    static async Task<string> ReadStdin(CancellationToken parent)
    {
        using var cts=CancellationTokenSource.CreateLinkedTokenSource(parent);cts.CancelAfter(TimeSpan.FromSeconds(30));using var stream=Console.OpenStandardInput();using var output=new MemoryStream();byte[] buffer=new byte[8192];
        try{int n;while((n=await stream.ReadAsync(buffer,cts.Token))>0){Data.Require(output.Length+n<=1_048_576,"instruction_limit");output.Write(buffer,0,n);}}
        catch(OperationCanceledException)when(!parent.IsCancellationRequested){throw new ReviewError(2,"stdin_timeout");}
        try{return Data.Utf8.GetString(output.ToArray());}catch(DecoderFallbackException){throw new ReviewError(2,"invalid_utf8");}
    }
}
