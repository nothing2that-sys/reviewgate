using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ReviewGate.Contracts;
using ReviewGate.Core.Execution;
using ReviewGate.Core.Planning;
using ReviewGate.Core.Sources;
using ReviewGate.Infrastructure;
using ReviewGate.Transport.Http;

namespace ReviewGate.Cli;
public static class CliHost
{
    static readonly HashSet<string> ValueKeys=["state-root","project","config","source-root","repository-root","input","reference","input-class","profile","mode","base-run","finding","reviewer","budget-policy","prices","execution-mode","fixture","plan","grant-file","run","format","resolve-unknown","prompt-file","output","provider","max-usd","max-requests","max-retries","expires-minutes"];
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
        Data.Require(o.Command is "plan" or "exec" or "status" or "cancel" or "resume" or "export" or "credential" or "grant","unknown_command");
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
        // Live work is planned explicitly, approved with the interactive grant command, then executed by plan ID.
        if(o.One("execution-mode")=="live")Data.Require(o.Command=="plan"||(o.Command=="exec"&&o.Has("plan")),"live_requires_plan_then_grant");
        Data.Require(!o.Has("allow-partial")||o.Command=="export","invalid_option_combination");
        Data.Require(!o.Has("recover-only")||o.Command=="resume","invalid_option_combination");
        Data.Require(!o.Has("resolve-unknown")||(o.Command=="resume"&&o.Has("recover-only")),"invalid_option_combination");
        if(o.Command=="grant")Data.Require(o.Has("plan")&&o.Has("max-usd")&&o.Instruction is null&&!o.Items.Keys.Any(k=>k is not ("plan" or "state-root" or "max-usd" or "max-requests" or "max-retries" or "expires-minutes" or "output")),"invalid_grant_options");
        else if(o.Has("plan"))Data.Require(o.Command=="exec"&&o.Instruction is null&&!o.Items.Keys.Any(k=>k is not ("plan" or "state-root" or "grant-file" or "output" or "execution-mode")),"plan_override_forbidden");
        if(o.Command=="credential")Data.Require(o.Instruction is "set" or "status" or "delete"&&!o.Items.Keys.Any(k=>k is not ("provider" or "output")),"invalid_credential_command");
        if(o.Command is "status" or "cancel" or "resume" or "export")Data.Require(o.Instruction is null&&!o.Has("prompt-file")&&!o.Has("prompt-stdin"),"unexpected_instruction");
        if(o.Has("output")){string file=Path.GetFullPath(o.Need("output"));SafePath.NoLinks(file);Data.Require(!File.Exists(file)&&!Directory.Exists(file),"output_already_exists");}
        return o;
    }
    public static async Task<CommandResult> RunAsync(string[] args,CancellationToken ct=default)
    {
        string command=args.FirstOrDefault()??"";
        ReviewEngine? engine=null;
        // Set only once the output path has passed every source/state overlap check; error envelopes then go there too.
        string? safeOutput=null;
        try
        {
            var o=Parse(args);string state=Path.GetFullPath(o.One("state-root")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ReviewGate"));
            Data.Require(!state.StartsWith(@"\\",StringComparison.Ordinal),"network_state_root_unsupported");SafePath.NoLinks(state);
            if(command=="credential")return Credential(o) with{OutputPath=o.One("output")};
            // The namespace comes from the plan/run ID, so a live run is never opened in the synthetic ledger or vice versa.
            string ns=command is "status" or "cancel" or "resume" or "export"?StateStore.NamespaceOf(o.Need("run")):o.Has("plan")?StateStore.NamespaceOf(o.Need("plan")):o.One("execution-mode")??"synthetic";
            Data.Require(o.One("execution-mode") is null||o.One("execution-mode")==ns,"execution_mode_mismatch");
            using var store=new StateStore(state,null,ns);using var transport=ns=="live"?new HttpTransport(CredentialFactory(),HandlerFactory()):null;engine=new ReviewEngine(store,transport);
            if(command=="grant"){CheckOutput(o,store.GetPlan(o.Need("plan")),store.Root);safeOutput=o.One("output");return IssueGrant(o,store) with{OutputPath=o.One("output")};}
            if(command is "status" or "cancel" or "resume" or "export")
            {
                var run=store.GetRun(o.Need("run"));var plan=store.GetPlan(run.PlanId);CheckOutput(o,plan,store.Root);safeOutput=o.One("output");
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
            if(o.Has("plan")){p=store.GetPlan(o.Need("plan"));CheckOutput(o,p,store.Root);safeOutput=o.One("output");}
            else
            {
                string source=SafePath.Full(o.Need("source-root"));
                if(o.One("output")is string output){Data.Require(!SafePath.Within(output,source)&&!SafePath.Within(output,store.Root),"output_protected_overlap");safeOutput=output;}
                int count=(o.Instruction is not null?1:0)+(o.Has("prompt-file")?1:0)+(o.Has("prompt-stdin")?1:0);Data.Require(count==1,"instruction_required_or_duplicated");
                string instruction=o.Instruction??(o.One("prompt-file")is string pf?ReadInstruction(pf):await ReadStdin(ct));
                p=Planner.Build(store,new(o.Need("project"),source,o.One("repository-root"),o.Need("input-class"),instruction,o.Need("profile"),o.Many("reviewer"),o.Need("budget-policy"),o.Need("prices",12),ns=="live"?o.One("fixture"):o.Need("fixture"),o.Many("input"),o.Many("reference"),o.One("mode")??"single",o.One("base-run"),o.Many("finding")));
            }
            if(command=="plan")return new(0,new Envelope{Command="plan",ExecutionMode=ns,Limitations=ns=="live"?Envelope.LiveLimitations:new Envelope().Limitations,PlanId=p.Id,Status="planned",RequiredAction=ns=="live"?"issue_live_grant_interactively":"provide_simulation_grant",Cost=new JsonObject{["estimated_upper_usd"]=p.Requests.Sum(r=>r.ReservationUsd),["mode"]=ns,["billed_usd"]=null},Artifacts=ReviewGate.Hglos.BundleWriter.Write(store.Root,store.PathFor("plans",p.Id,Data.Id("preview")),new Dictionary<string,string>{{"plan.json",Data.Json(p)}},ns)},o.One("output"));
            return (await engine.ExecuteAsync(p,o.One("grant-file")is string g?Data.Read<Grant>(g):null,cancellation:ct)) with {OutputPath=o.One("output")};
        }
        catch(ReviewError e){return new(e.ExitCode,new Envelope{Command=command,RunId=engine?.CurrentRunId,PlanId=engine?.CurrentPlanId,ErrorCode=e.Code,RequiredAction=e.Action},safeOutput);}
        catch(SqliteException e){return new(13,new Envelope{Command=command,ErrorCode=e.SqliteErrorCode is 5 or 6?"state_store_busy":"state_store_corrupt"},safeOutput);}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){return new(21,new Envelope{Command=command,RunId=engine?.CurrentRunId,PlanId=engine?.CurrentPlanId,ErrorCode="storage_failure",RequiredAction="inspect_run_with_status"},safeOutput);}
        catch(OperationCanceledException){return new(130,new Envelope{Command=command,Status="cancelled",ErrorCode="cancelled"},safeOutput);}
        catch(Exception){return new(21,new Envelope{Command=command,ErrorCode="internal_failure"},safeOutput);}
    }
    // Seams for tests; the shipped CLI uses Windows Credential Manager and the default network handler.
    public static Func<ICredentialStore> CredentialFactory {get;set;}=()=>OperatingSystem.IsWindows()?new WindowsCredentialStore():throw new ReviewError(14,"credential_store_unsupported");
    public static Func<HttpMessageHandler?> HandlerFactory {get;set;}=()=>null;
    static readonly string[] LiveProviders=["openai","together","google"];
    // Keys are typed by the user into this console only; they are never accepted from arguments, files,
    // environment variables or redirected stdin, and never echoed or returned.
    static CommandResult Credential(Options o)
    {
        var credentials=CredentialFactory();string action=o.Instruction!;
        if(action=="status")
        {
            var configured=new JsonObject();foreach(var p in LiveProviders)configured[p]=credentials.Read(p) is {Length:>0};
            return new(0,new Envelope{Command="credential",ExecutionMode="live",Status="ok",Limitations=[],Details=new JsonObject{["configured"]=configured,["store"]="windows_credential_manager"}});
        }
        string provider=o.Need("provider");Data.Require(LiveProviders.Contains(provider),"unsupported_provider");
        if(action=="delete")return new(0,new Envelope{Command="credential",ExecutionMode="live",Status="ok",Limitations=[],Details=new JsonObject{["provider"]=provider,["removed"]=credentials.Delete(provider)}});
        Data.Require(!Console.IsInputRedirected,"interactive_console_required");
        Console.Error.Write($"Enter the {provider} API key for ReviewGate (input hidden, Enter to save, Esc to cancel): ");
        var typed=new StringBuilder();
        while(true)
        {
            var key=Console.ReadKey(true);
            if(key.Key==ConsoleKey.Enter)break;
            if(key.Key==ConsoleKey.Escape){Console.Error.WriteLine();throw new ReviewError(130,"cancelled");}
            if(key.Key==ConsoleKey.Backspace){if(typed.Length>0)typed.Length--;continue;}
            if(!char.IsControl(key.KeyChar))typed.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        try{credentials.Write(provider,typed.ToString());}finally{typed.Clear();}
        return new(0,new Envelope{Command="credential",ExecutionMode="live",Status="stored",Limitations=[],Details=new JsonObject{["provider"]=provider,["stored"]=true}});
    }
    // A live grant is issued only after the user reads the send summary in an interactive console and
    // types the end of the plan ID. This is a local confirmation, not proof that a human approved it.
    static CommandResult IssueGrant(Options o,StateStore store)
    {
        Data.Require(store.Namespace=="live","grant_command_is_live_only");
        var p=store.GetPlan(o.Need("plan"));Planner.CheckPins(p);
        static int Int(string? text,int fallback)=>text is null?fallback:int.TryParse(text,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out int v)?v:throw new ReviewError(2,"invalid_grant");
        Data.Require(decimal.TryParse(o.Need("max-usd"),System.Globalization.NumberStyles.AllowDecimalPoint,System.Globalization.CultureInfo.InvariantCulture,out decimal maxUsd)&&maxUsd>0,"invalid_grant");
        int maxRequests=Int(o.One("max-requests"),p.Requests.Count),retries=Int(o.One("max-retries"),0),minutes=Int(o.One("expires-minutes"),60);
        Data.Require(maxRequests>0&&minutes is >=1 and <=1440,"invalid_grant");
        Data.Require(!Console.IsInputRedirected,"interactive_console_required");
        decimal reserve=p.Requests.Sum(r=>r.ReservationUsd);
        var e=Console.Error;
        e.WriteLine("ReviewGate live grant — this sends source text to the providers below and may incur charges.");
        e.WriteLine($"  plan        {p.Id}  (project {p.ProjectId}, mode {p.Mode})");
        e.WriteLine($"  sources     {p.Sources.Count(s=>s.Role=="target")} target, {p.Sources.Count(s=>s.Role=="reference")} reference, {p.Sources.Sum(s=>(long)s.ByteLength)} bytes from {p.SourceRoot}");
        foreach(var r in p.Reviewers)
        {
            e.WriteLine($"  reviewer    {r.Id}: {r.HostingProvider} {r.ModelId} -> {HttpTransport.Endpoints[r.HostingProvider]}  ({(r.HostingProvider=="together"?"store ignored; retention follows your Together account settings":"store=false sent")})");
            if(p.Prices.Prices.SingleOrDefault(x=>x.CanonicalModelKey==r.CanonicalModelKey)?.Tier=="free")
                e.WriteLine("  WARNING     free tier: the provider may use submitted content to improve its products and humans may review it. Do not send private or company material.");
        }
        e.WriteLine($"  requests    {p.Requests.Count}, reservation upper bound {reserve.ToString(System.Globalization.CultureInfo.InvariantCulture)} USD (usage-based, not an invoice)");
        e.WriteLine($"  this grant  max {maxUsd.ToString(System.Globalization.CultureInfo.InvariantCulture)} USD, {maxRequests} requests, {retries} retries, expires in {minutes} min");
        if(reserve>maxUsd)e.WriteLine("  note        the grant cap is below the full reservation; the run will stop early with exit 12.");
        e.Write($"Type the last 6 characters of the plan ID ({p.Id[^6..]}) to approve, anything else to cancel: ");
        Data.Require(Console.ReadLine()?.Trim()==p.Id[^6..],"grant_not_confirmed",10);
        var g=new Grant(Data.Version,Data.Id("grant"),p.Hash,p.ManifestHash,store.Now.AddMinutes(minutes),maxUsd,maxRequests,retries,"live");
        string file=store.PathFor("grants",g.Id+".json");Directory.CreateDirectory(Path.GetDirectoryName(file)!);Data.CreateFile(file,Data.Json(g));
        store.RecordGrantIssue(p,g,"interactive_console");
        return new(0,new Envelope{Command="grant",ExecutionMode="live",Limitations=Envelope.LiveLimitations,PlanId=p.Id,Status="issued",RequiredAction="exec_with_grant_file",
            Details=new JsonObject{["grant_id"]=g.Id,["grant_file"]=file,["expires_at"]=g.ExpiresAt.ToString("O"),["max_usd"]=maxUsd,["reservation_upper_usd"]=reserve}});
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
