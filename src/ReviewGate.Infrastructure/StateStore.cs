using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ReviewGate.Contracts;

namespace ReviewGate.Infrastructure;

public sealed class StateStore : IDisposable
{
    readonly SqliteConnection db;
    readonly Func<DateTimeOffset> now;
    public string Root { get; }
    public DateTimeOffset Now => now();
    public StateStore(string root, Func<DateTimeOffset>? clock=null)
    {
        Root=Path.Combine(Path.GetFullPath(root),"synthetic");now=clock??(()=>DateTimeOffset.UtcNow);
        NoLinks(Root);NoLinks(Path.Combine(Root,"state.db"));
        Directory.CreateDirectory(Root);
        db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(Root,"state.db"),ForeignKeys=true,DefaultTimeout=5,Pooling=false}.ToString());
        db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        Exec("""
          CREATE TABLE IF NOT EXISTS projects(id TEXT PRIMARY KEY);
          CREATE TABLE IF NOT EXISTS plans(id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id), json TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS runs(id TEXT PRIMARY KEY, plan_id TEXT NOT NULL REFERENCES plans(id), project_id TEXT NOT NULL REFERENCES projects(id),status TEXT NOT NULL,generation TEXT NOT NULL,error TEXT,created TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS grants(id TEXT PRIMARY KEY,run_id TEXT NOT NULL REFERENCES runs(id),hash TEXT NOT NULL,json TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS grant_revocations(id TEXT PRIMARY KEY REFERENCES grants(id),revoked_at TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS attempts(id TEXT PRIMARY KEY,run_id TEXT NOT NULL REFERENCES runs(id),request_id TEXT NOT NULL,grant_id TEXT NOT NULL REFERENCES grants(id),phase TEXT NOT NULL,reservation TEXT NOT NULL,cost TEXT,month TEXT NOT NULL,response_path TEXT,response_hash TEXT,error TEXT);
          CREATE TABLE IF NOT EXISTS ledger(id INTEGER PRIMARY KEY,attempt_id TEXT NOT NULL REFERENCES attempts(id),kind TEXT NOT NULL,amount TEXT NOT NULL,basis TEXT NOT NULL, UNIQUE(attempt_id,kind));
          CREATE TABLE IF NOT EXISTS units(run_id TEXT NOT NULL REFERENCES runs(id),unit_id TEXT NOT NULL,json TEXT NOT NULL, PRIMARY KEY(run_id,unit_id));
          CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS bundles(run_id TEXT PRIMARY KEY REFERENCES runs(id),json TEXT NOT NULL);
          """);
        Data.Require(Scalar("PRAGMA foreign_keys;")=="1","state_store_corrupt",13);
    }
    public static void ValidId(string id) => Data.Require(id is not null&&Regex.IsMatch(id,@"^[A-Za-z0-9_-]{1,80}$",RegexOptions.CultureInvariant),"invalid_id");
    static void NoLinks(string path)
    {for(string? p=Path.GetFullPath(path);p is not null;p=Path.GetDirectoryName(p))if((File.Exists(p)||Directory.Exists(p))&&File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint))throw new ReviewError(13,"state_reparse_point");}
    public string PathFor(params string[] parts)
    {
        foreach(var p in parts)Data.Require(!Path.IsPathRooted(p)&&p!=".."&&!p.Contains(".."+Path.DirectorySeparatorChar),"invalid_artifact_path",13);
        string path=Path.GetFullPath(Path.Combine([Root,..parts]));
        Data.Require(path.StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"invalid_artifact_path",13);
        NoLinks(path);return path;
    }
    public string NewSnapshotDir(string planId){ValidId(planId);string dir=PathFor("snapshots",planId);Directory.CreateDirectory(dir);return dir;}
    public void SavePlan(Plan plan)
    {
        Exec("INSERT OR IGNORE INTO projects(id) VALUES($p)",null,("$p",plan.ProjectId));
        Exec("INSERT INTO plans VALUES($id,$p,$j)",null,("$id",plan.Id),("$p",plan.ProjectId),("$j",Data.Json(plan)));
    }
    public Plan GetPlan(string id)
    {
        ValidId(id);string? json=Scalar("SELECT json FROM plans WHERE id=$id",null,("$id",id));
        Data.Require(json is not null,"unknown_plan");var plan=Data.Parse<Plan>(json!);
        Data.Require(plan.Hash==Data.Hash(Data.Json(plan with{Hash=""})),"plan_hash_mismatch",13);return plan;
    }
    public RunRow NewRun(Plan plan)
    {
        var r=new RunRow(Data.Id("run"),plan.Id,plan.ProjectId,"ready",Data.Id("owner"),null,Now);
        Exec("INSERT INTO runs VALUES($i,$p,$j,$s,$g,NULL,$c)",null,("$i",r.Id),("$p",r.PlanId),("$j",r.ProjectId),("$s",r.Status),("$g",r.Generation),("$c",r.CreatedAt.ToString("O")));
        return r;
    }
    public RunRow GetRun(string id)
    {
        ValidId(id);using var c=Cmd("SELECT * FROM runs WHERE id=$i",null,("$i",id));using var r=c.ExecuteReader();
        Data.Require(r.Read(),"unknown_run");return new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),DateTimeOffset.Parse(r.GetString(6),CultureInfo.InvariantCulture));
    }
    public void Status(string id,string status,string? error=null) => Exec("UPDATE runs SET status=$s,error=$e WHERE id=$i",null,("$s",status),("$e",error),("$i",id));
    public List<Artifact> Artifacts(string id){var s=Scalar("SELECT json FROM bundles WHERE run_id=$i",null,("$i",id));return s is null?[]:Data.Parse<List<Artifact>>(s);}
    public void CompleteBundle(string id,string status,string? error,List<Artifact> artifacts)
    {using var tx=db.BeginTransaction();Exec("INSERT OR REPLACE INTO bundles VALUES($i,$j)",tx,("$i",id),("$j",Data.Json(artifacts)));Exec("UPDATE runs SET status=$s,error=$e WHERE id=$i",tx,("$s",status),("$e",error),("$i",id));tx.Commit();}
    public void NewGeneration(string id)=>Exec("UPDATE runs SET generation=$g WHERE id=$i",null,("$g",Data.Id("owner")),("$i",id));
    public FileStream Own(string projectId)
    {
        ValidId(projectId);string file=PathFor("locks",projectId+".lock");Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try{return new FileStream(file,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException){throw new ReviewError(13,"owner_conflict","retry_status");}
    }
    public bool OwnerAlive(string projectId)
    {try{using var owned=Own(projectId);return false;}catch(ReviewError e)when(e.Code=="owner_conflict"){return true;}}
    public bool CancelRequested(RunRow run)
    {
        string path=PathFor("control",run.Id+".json");if(!File.Exists(path))return false;
        try{return JsonNode.Parse(File.ReadAllText(path))?["generation"]?.GetValue<string>()==run.Generation;}
        catch(System.Text.Json.JsonException){throw new ReviewError(13,"control_file_corrupt");}
    }
    public void Cancel(RunRow run)
    {
        string path=PathFor("control",run.Id+".json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        Data.CreateFile(tmp,Data.Json(new{generation=run.Generation,requested_at=Now}));File.Move(tmp,path,true);
    }
    public static void CheckGrant(Grant g,Plan p,RunRow run,DateTimeOffset time)
    {
        Data.VersionIs(g.SchemaVersion);ValidId(g.Id);
        Data.Require(g.ExecutionMode=="synthetic"&&g.PlanHash==p.Hash&&g.ManifestHash==p.ManifestHash
            &&(g.RunId is null||g.RunId==run.Id),"grant_scope_mismatch",10);
        Data.Require(g.ExpiresAt>time,"grant_expired",10);
        Data.Require(g.MaxUsd>0&&g.MaxRequests>0&&g.MaxRetries>=0,"invalid_grant",10);
    }
    // Binds the grant to the run in the run's first preparation, even when no reservation follows.
    public void BindGrant(RunRow run,Plan plan,Grant grant)
    {
        CheckGrant(grant,plan,run,Now);
        using var tx=db.BeginTransaction();BindGrant(run,grant,tx);tx.Commit();
    }
    void BindGrant(RunRow run,Grant grant,SqliteTransaction tx)
    {
        Data.Require(Scalar("SELECT id FROM grant_revocations WHERE id=$i",tx,("$i",grant.Id)) is null,"grant_revoked",10);
        string? bound=Scalar("SELECT run_id FROM grants WHERE id=$g",tx,("$g",grant.Id));
        string hash=Data.Hash(Data.Json(grant));
        if(bound is not null)Data.Require(bound==run.Id&&Scalar("SELECT hash FROM grants WHERE id=$g",tx,("$g",grant.Id))==hash,"grant_already_bound_or_changed",10);
        else Exec("INSERT INTO grants VALUES($g,$r,$h,$j)",tx,("$g",grant.Id),("$r",run.Id),("$h",hash),("$j",Data.Json(grant)));
    }
    public bool RetryExhausted(RunRow run,Plan plan,RequestPlan request,Grant grant)
        =>Attempts(run.Id).Count(a=>a.RequestId==request.Id&&a.Phase!="released")>Math.Min(grant.MaxRetries,plan.Budget.MaxRetries);
    public void RecordUnit(string runId,UnitResult unit)
        =>Exec("INSERT OR IGNORE INTO units VALUES($r,$u,$j)",null,("$r",runId),("$u",unit.UnitId),("$j",Data.Json(unit)));
    public AttemptRow Reserve(RunRow run,Plan plan,RequestPlan request,Grant grant)
    {
        CheckGrant(grant,plan,run,Now);
        Data.Require(Now>=plan.Prices.EffectiveFrom&&Now<plan.Prices.EffectiveTo,"price_snapshot_expired",12);
        using var tx=db.BeginTransaction();
        BindGrant(run,grant,tx);
        string monthly=plan.Budget.MonthUsd.ToString(CultureInfo.InvariantCulture);
        string? monthCap=Scalar("SELECT value FROM meta WHERE key='month_cap'",tx);
        Data.Require(monthCap is null||monthCap==monthly,"budget_policy_conflict",13);
        Exec("INSERT OR IGNORE INTO meta VALUES('month_cap',$v)",tx,("$v",monthly));
        var all=Attempts(null,tx);string month=Month(Now);decimal amount=request.ReservationUsd;
        var runAttempts=all.Where(a=>a.RunId==run.Id&&a.Phase!="released").ToList();
        var grantAttempts=all.Where(a=>a.GrantId==grant.Id&&a.Phase!="released").ToList();
        int retries=runAttempts.Count(a=>a.RequestId==request.Id);
        Data.Require(retries<=Math.Min(grant.MaxRetries,plan.Budget.MaxRetries),"retry_limit_reached",12);
        Data.Require(runAttempts.Count<plan.Budget.MaxRequests&&grantAttempts.Count<grant.MaxRequests,"request_limit_reached",12);
        var projectRuns=RunIdsForProject(plan.ProjectId,tx);
        Data.Require(all.Where(a=>projectRuns.Contains(a.RunId)).Sum(Held)+amount<=plan.Budget.ProjectUsd
            &&runAttempts.Sum(Held)+amount<=plan.Budget.RunUsd&&grantAttempts.Sum(Held)+amount<=grant.MaxUsd
            &&all.Where(a=>a.Month==month||IsHeld(a.Phase)).Sum(Held)+amount<=plan.Budget.MonthUsd,"budget_exhausted",12);
        var row=new AttemptRow(Data.Id("attempt"),run.Id,request.Id,grant.Id,"reserved",amount,null,month,null,null,null);
        Exec("INSERT INTO attempts VALUES($i,$r,$q,$g,'reserved',$a,NULL,$m,NULL,NULL,NULL)",tx,("$i",row.Id),("$r",row.RunId),("$q",row.RequestId),("$g",row.GrantId),("$a",Money(amount)),("$m",month));
        Event(row.Id,"reserve",amount,"synthetic_bound",tx);tx.Commit();return row;
    }
    // Cost month is attributed to the dispatch intent time, not the reservation time.
    public void Dispatch(string id)=>Data.Require(Exec("UPDATE attempts SET phase='dispatch_intent',month=$m WHERE id=$i AND phase='reserved'",null,("$i",id),("$m",Month(Now)))==1,"invalid_request_transition",13);
    public void RevokeGrant(string id){ValidId(id);Exec("INSERT OR IGNORE INTO grant_revocations VALUES($i,$t)",null,("$i",id),("$t",Now.ToString("O")));}
    public void EnsureGrantActive(string id)=>Data.Require(Scalar("SELECT id FROM grant_revocations WHERE id=$i",null,("$i",id)) is null,"grant_revoked",10);
    public string ResponseFile(AttemptRow a)=>PathFor("runs",a.RunId,a.Id+".response.json");
    public void SaveResponse(AttemptRow attempt,SavedReply reply)
    {
        string path=ResponseFile(attempt);Data.CreateFile(path,Data.Json(reply));
#if DEBUG
        FaultObserver?.Invoke("response_file_committed");
#endif
        Adopt(attempt.Id,path);
    }
    void Adopt(string id,string path)=>Exec("UPDATE attempts SET phase='response_saved',response_path=$p,response_hash=$h WHERE id=$i",null,("$p",path),("$h",Data.FileHash(path)),("$i",id));
    public void Unknown(string id,string code)=>Exec("UPDATE attempts SET phase='unknown',error=$e WHERE id=$i",null,("$i",id),("$e",code));
    public void Uncertain(AttemptRow a,UnitResult unit,string error)
    {using var tx=db.BeginTransaction();Exec("INSERT OR REPLACE INTO units VALUES($r,$u,$j)",tx,("$r",a.RunId),("$u",unit.UnitId),("$j",Data.Json(unit)));Exec("UPDATE attempts SET phase='unknown',error=$e WHERE id=$i",tx,("$e",error),("$i",a.Id));tx.Commit();}
    public void Settle(AttemptRow a,UnitResult unit,decimal cost)
    {
        using var tx=db.BeginTransaction();
        string? phase=Scalar("SELECT phase FROM attempts WHERE id=$i",tx,("$i",a.Id));
        if(phase=="settled"){tx.Commit();return;}
        Data.Require(phase=="response_saved","invalid_request_transition",13);
        Exec("INSERT OR REPLACE INTO units VALUES($r,$u,$j)",tx,("$r",a.RunId),("$u",unit.UnitId),("$j",Data.Json(unit)));
        Exec("UPDATE attempts SET phase='settled',cost=$c,error=$e WHERE id=$i",tx,("$c",Money(cost)),("$e",unit.ErrorCode),("$i",a.Id));
        Event(a.Id,"settle",cost,"usage_calculated_synthetic",tx);tx.Commit();
    }
    public List<UnitResult> Units(string runId)
    {using var c=Cmd("SELECT json FROM units WHERE run_id=$r",null,("$r",runId));using var r=c.ExecuteReader();var result=new List<UnitResult>();while(r.Read())result.Add(Data.Parse<UnitResult>(r.GetString(0)));return result;}
    public List<AttemptRow> Attempts(string? runId=null,SqliteTransaction? tx=null)
    {
        using var c=Cmd("SELECT * FROM attempts"+(runId is null?"":" WHERE run_id=$r"),tx,runId is null?[]:[("$r",(object?)runId)]);
        using var r=c.ExecuteReader();var result=new List<AttemptRow>();
        while(r.Read())result.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),decimal.Parse(r.GetString(5),CultureInfo.InvariantCulture),r.IsDBNull(6)?null:decimal.Parse(r.GetString(6),CultureInfo.InvariantCulture),r.GetString(7),r.IsDBNull(8)?null:r.GetString(8),r.IsDBNull(9)?null:r.GetString(9),r.IsDBNull(10)?null:r.GetString(10)));
        return result;
    }
    public void Recover(RunRow run,Plan plan)
    {
        foreach(var a in Attempts(run.Id))
        {
            if(a.Phase=="reserved")
            {using var tx=db.BeginTransaction();Exec("UPDATE attempts SET phase='released',cost='0' WHERE id=$i",tx,("$i",a.Id));Event(a.Id,"release",0,"no_dispatch_intent",tx);tx.Commit();}
            else if(a.Phase=="dispatch_intent")
            {
                string file=ResponseFile(a);
                try
                {
                    var reply=Data.Read<SavedReply>(file);
                    Data.Require(reply.AttemptId==a.Id&&reply.RequestId==a.RequestId&&reply.PlanHash==plan.Hash,"response_identity_mismatch",13);
                    Adopt(a.Id,file);
                }
                catch(ReviewError){Unknown(a.Id,"dispatch_outcome_unknown");}
            }
        }
    }
    public void Resolve(RunRow run,Plan plan,ResolutionFixture fixture)
    {
        Data.VersionIs(fixture.SchemaVersion);Data.Require(fixture.ExecutionMode=="synthetic"&&fixture.PlanHash==plan.Hash,"resolution_scope_mismatch",13);
        Data.Require(fixture.Resolutions.Select(x=>x.AttemptId).Distinct().Count()==fixture.Resolutions.Count,"duplicate_resolution",2);
        using var tx=db.BeginTransaction();
        foreach(var item in fixture.Resolutions)
        {
            var a=Attempts(run.Id,tx).SingleOrDefault(a=>a.Id==item.AttemptId);Data.Require(a is not null&&a.Phase=="unknown","invalid_resolution_attempt",13);
            Data.Require(!string.IsNullOrWhiteSpace(item.Actor)&&!string.IsNullOrWhiteSpace(item.Basis)&&item.Timestamp!=default,"invalid_resolution");
            Data.Require(item.Outcome is "not_sent" or "usage_confirmed" or "conservative_full_reservation","invalid_resolution",13);
            Data.Require(item.Outcome!="not_sent"||a!.ErrorCode=="synthetic_not_sent_confirmed","unproven_not_sent",13);
            decimal value=item.Outcome=="not_sent"?0:item.Outcome=="conservative_full_reservation"?a!.ReservationUsd:item.AmountUsd??throw new ReviewError(13,"resolution_amount_required");
            if(item.Outcome=="usage_confirmed")
            {
                Data.Require(item.Usage is {Known:true},"resolution_usage_required",13);
                var request=plan.Requests.Single(q=>q.Id==a!.RequestId);var reviewer=plan.Reviewers.Single(r=>r.Id==request.ReviewerId);
                var price=plan.Prices.Prices.Single(p=>p.CanonicalModelKey==reviewer.CanonicalModelKey);var usage=item.Usage;
                Data.Require(usage.InputTokens>=0&&usage.OutputTokens>=0&&usage.CachedInputTokens>=0&&usage.CachedInputTokens<=usage.InputTokens&&usage.ReasoningTokens>=0&&(!price.OutputIncludesReasoning||usage.ReasoningTokens<=usage.OutputTokens),"invalid_resolution_usage",13);
                decimal calculated=((usage.InputTokens-usage.CachedInputTokens)*price.InputPerMillion+usage.CachedInputTokens*price.CachedInputPerMillion+usage.OutputTokens*price.OutputPerMillion+(price.OutputIncludesReasoning?0:usage.ReasoningTokens*price.ReasoningPerMillion))/1_000_000m;
                Data.Require(value==calculated,"resolution_cost_mismatch",13);
            }
            Data.Require(value>=0,"invalid_resolution");
            // Proven non-dispatch is equivalent to a released reservation: it consumes no request or retry.
            Exec("UPDATE attempts SET phase=$p,cost=$c,error=$e WHERE id=$i",tx,("$p",item.Outcome=="not_sent"?"released":"settled"),("$c",Money(value)),("$e","resolved_"+item.Outcome),("$i",a!.Id));
            Event(a.Id,"unknown_resolved",value,Data.Json(item),tx);
        }
        tx.Commit();
    }
    public JsonObject Costs(string runId)
    {
        var a=Attempts(runId);return new(){["mode"]="synthetic",["usage_calculated_usd"]=a.Where(x=>x.Phase=="settled"&&x.ErrorCode!="resolved_conservative_full_reservation").Sum(x=>x.CostUsd??0),
            ["assumed_usd"]=a.Where(x=>x.Phase=="settled"&&x.ErrorCode=="resolved_conservative_full_reservation").Sum(x=>x.CostUsd??0),
            ["reserved_usd"]=a.Where(x=>IsHeld(x.Phase)&&x.Phase!="unknown").Sum(x=>x.ReservationUsd),["unknown_hold_usd"]=a.Where(x=>x.Phase=="unknown").Sum(x=>x.ReservationUsd),["billed_usd"]=null};
    }
    public static bool IsHeld(string phase)=>phase is "reserved" or "dispatch_intent" or "response_saved" or "unknown";
    public static decimal Held(AttemptRow a)=>a.Phase=="released"?0:IsHeld(a.Phase)?a.ReservationUsd:a.CostUsd??a.ReservationUsd;
    public static string Month(DateTimeOffset time)=>time.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM",CultureInfo.InvariantCulture);
    HashSet<string> RunIdsForProject(string project,SqliteTransaction tx)
    {using var c=Cmd("SELECT id FROM runs WHERE project_id=$p",tx,("$p",project));using var r=c.ExecuteReader();var ids=new HashSet<string>();while(r.Read())ids.Add(r.GetString(0));return ids;}
    void Event(string id,string kind,decimal value,string basis,SqliteTransaction tx)=>Exec("INSERT INTO ledger(attempt_id,kind,amount,basis) VALUES($i,$k,$v,$b)",tx,("$i",id),("$k",kind),("$v",Money(value)),("$b",basis));
    static string Money(decimal value)=>value.ToString(CultureInfo.InvariantCulture);
    SqliteCommand Cmd(string sql,SqliteTransaction? tx=null,params (string,object?)[] args)
    {var c=db.CreateCommand();c.CommandText=sql;c.Transaction=tx;foreach(var (key,value)in args)c.Parameters.AddWithValue(key,value??DBNull.Value);return c;}
    int Exec(string sql,SqliteTransaction? tx=null,params (string,object?)[] args){using var c=Cmd(sql,tx,args);return c.ExecuteNonQuery();}
    string? Scalar(string sql,SqliteTransaction? tx=null,params (string,object?)[] args){using var c=Cmd(sql,tx,args);var v=c.ExecuteScalar();return v is null or DBNull?null:Convert.ToString(v,CultureInfo.InvariantCulture);}
    public void Dispose()=>db.Dispose();
#if DEBUG
    public static Action<string>? FaultObserver { get; set; }
#endif
}
public record SavedReply(string AttemptId,string RequestId,string PlanHash,string ProviderBody,int HttpStatus);
