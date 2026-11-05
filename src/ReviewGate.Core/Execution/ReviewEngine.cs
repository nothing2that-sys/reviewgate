using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ReviewGate.Contracts;
using ReviewGate.Core.Planning;
using ReviewGate.Core.Sources;
using ReviewGate.Hglos;
using ReviewGate.Infrastructure;
using ReviewGate.Providers;

namespace ReviewGate.Core.Execution;

// live is the network transport for live plans; synthetic plans always use their pinned fixture and never touch it.
public sealed class ReviewEngine(StateStore store,IModelTransport? live=null)
{
    public string? CurrentRunId {get;private set;}
    public string? CurrentPlanId {get;private set;}
    public async Task<CommandResult> ExecuteAsync(Plan p,Grant? grant,string? resumeRun=null,bool recoverOnly=false,
        ResolutionFixture? resolution=null,CancellationToken cancellation=default)
    {
        Data.Require(p.ExecutionMode==store.Namespace,"execution_mode_mismatch",13);
        // Recovery and unknown resolution only touch the ledger and saved responses, so they must stay possible
        // after the user edits original files; unchanged inputs are required only before a new dispatch.
        if(recoverOnly)Data.Require(p.Hash==Data.Hash(Data.Json(p with{Hash=""})),"plan_hash_mismatch",13);
        else Planner.CheckPins(p);
        using var owner=store.Own(p.ProjectId);
        RunRow run=resumeRun is null?store.NewRun(p):store.GetRun(resumeRun);
        CurrentRunId=run.Id;CurrentPlanId=p.Id;
        Data.Require(run.PlanId==p.Id,"run_plan_mismatch",13);
        string command=resumeRun is null?"exec":"resume";
        if(run.Status=="completed")return new(0,View(run.Id,command));
        store.Recover(run,p);
        if(resolution is not null){Data.Require(recoverOnly,"resolution_requires_recover_only");store.Resolve(run,p,resolution);}
        // Saved responses need no dispatch: they are settled from the response file and the pinned snapshot,
        // without a grant or unchanged originals, so an expired grant or an edited source cannot strand their hold.
        foreach(var saved in store.Attempts(run.Id).Where(a=>a.Phase=="response_saved").ToList())ProcessSaved(p,run,saved);
        SettleFreeUnknowns(p,run);
        bool unknown=store.Attempts(run.Id).Any(a=>a.Phase=="unknown");
        // An unknown covered by a pre-approved fallback does not block resuming; it still needs reconciliation at the end.
        if(BlockingUnknown(p,run)||(recoverOnly&&unknown))return new(13,SaveReport(p,run with{Status="needs_reconciliation",ErrorCode="reconciliation_required"},command)with{RequiredAction="reconcile_unknown_attempts"});
        if(recoverOnly)return new(0,SaveReport(p,run with{Status=HasValidUnits(p,run)?"partial":"blocked",ErrorCode=null},command)with{RequiredAction="resume_after_recovery"});
        store.NewGeneration(run.Id);run=store.GetRun(run.Id);
        // Nothing left to dispatch: finishing the bundle needs no grant.
        if(grant is null&&CoverageFor(p,store.Units(run.Id)).RequiredCoverageComplete&&!store.Attempts(run.Id).Any(a=>a.CostUsd>a.ReservationUsd))
            return new(0,SaveReport(p,run with{Status="completed",ErrorCode=null},command));
        if(grant is null)return new(10,SaveReport(p,run with{Status=HasValidUnits(p,run)?"partial":"blocked",ErrorCode="grant_required"},command)with{RequiredAction="supply_simulation_grant"});
        store.Status(run.Id,"running");
        string? error=null;int exit=0;bool retryExhausted=false;
        try
        {
            if(store.Attempts(run.Id).Any(a=>a.CostUsd>a.ReservationUsd))throw new ReviewError(12,"reservation_overrun");
            // Every key the plan may need (fallbacks included) is checked before anything is sent.
            if(p.ExecutionMode=="live")foreach(var reviewer in p.Reviewers)Data.Require(live is not null&&live.HasCredential(reviewer.HostingProvider),"credential_missing",11);
            foreach(var primary in p.Requests.Where(q=>q.FallbackFor is null))
            {
                CheckCancel(run,cancellation);
                // A slot is the primary request plus its optional pre-approved fallback; a valid answer from either fills it.
                var candidates=Slot(p,primary);
                if(candidates.Any(q=>UnitValid(run,q)))continue;
                if(BlockingUnknown(p,run))throw new ReviewError(13,"reconciliation_required");
                for(int i=0;i<candidates.Count;i++)
                {
                    var request=candidates[i];bool last=i==candidates.Count-1;
                    // A candidate that already gave no answer hands over to the next; one that answered is never replaced.
                    if(!last&&Unanswered(run,request))continue;
                    // A request whose retries are used up stays incomplete without blocking the remaining requests.
                    if(store.RetryExhausted(run,p,request,grant)){store.RecordUnit(run.Id,Invalid(p,request,"retry_exhausted"));retryExhausted=true;break;}
                    var settled=await DispatchAsync(p,run,request,grant,cancellation);
                    if(settled.CostUsd>settled.ReservationUsd)throw new ReviewError(12,"reservation_overrun");
                    if(UnitValid(run,request))break;
                    if(!last&&Unanswered(run,request))continue;
                    if(settled.Phase=="unknown")throw new ReviewError(13,"reconciliation_required");
                    if(settled.ErrorCode=="unauthorized")throw new ReviewError(11,"unauthorized");
                    if(settled.ErrorCode is "rate_limited" or "error")throw new ReviewError(21,settled.ErrorCode);
                    break;
                }
            }
        }
        catch(OperationCanceledException){exit=130;error="cancelled";}
        catch(ReviewError e){exit=e.ExitCode;error=e.Code;}
        // Skipped retry-exhausted requests are a request-limit shortfall (contract exit 12), not a plain partial.
        if(exit==0&&retryExhausted){exit=12;error="retry_limit_reached";}
        // A known pre-dispatch cancellation leaves a reserved row that can safely be released.
        store.Recover(run,p);SettleFreeUnknowns(p,run);
        var coverage=CoverageFor(p,store.Units(run.Id));var attempts=store.Attempts(run.Id);
        string status;
        if(attempts.Any(a=>a.Phase=="unknown")){status="needs_reconciliation";exit=13;error="reconciliation_required";}
        else if(exit==130)status="cancelled";
        else if(exit is 10 or 11 or 12)status=coverage.ValidUnits>0?"partial":"blocked";
        else if(coverage.RequiredCoverageComplete&&exit==0)status="completed";
        else{status=coverage.ValidUnits>0?"partial":"failed";if(exit==0)exit=coverage.ValidUnits>0?20:21;error??="partial_review";}
        var envelope=SaveReport(p,store.GetRun(run.Id) with{Status=status,ErrorCode=error},command);
        return new(exit,envelope);
    }
    void CheckCancel(RunRow run,CancellationToken ct){ct.ThrowIfCancellationRequested();if(store.CancelRequested(run))throw new OperationCanceledException();}
    static List<RequestPlan> Slot(Plan p,RequestPlan primary)=>[primary,..p.Requests.Where(q=>q.FallbackFor==primary.Id)];
    bool UnitValid(RunRow run,RequestPlan q)=>store.Units(run.Id).Any(u=>u.UnitId==q.UnitId&&u.Valid);
    // "No answer": the latest attempt is unknown (timeout, lost connection, cost unknown) or settled without a usable
    // reply because the provider was busy or failing (429/5xx). A cancelled send is not handed over to a fallback.
    bool Unanswered(RunRow run,RequestPlan q)
    {
        var a=store.Attempts(run.Id).LastOrDefault(x=>x.RequestId==q.Id&&x.Phase!="released");
        if(a is null||a.ErrorCode=="cancelled_in_flight")return false;
        if(a.Phase=="unknown")return true;
        var u=store.Units(run.Id).SingleOrDefault(x=>x.RequestId==q.Id);
        return a.Phase=="settled"&&(u is null||u is {Valid:false,ValidationState:"failed",ErrorCode:"rate_limited" or "error"});
    }
    // An unknown attempt stops new dispatch unless a pre-approved fallback stands behind it in the same slot.
    bool BlockingUnknown(Plan p,RunRow run)=>store.Attempts(run.Id).Where(a=>a.Phase=="unknown").Any(a=>
    {
        var q=p.Requests.Single(x=>x.Id==a.RequestId);var slot=Slot(p,p.Requests.Single(x=>x.Id==(q.FallbackFor??q.Id)));
        return a.ErrorCode=="cancelled_in_flight"||slot[^1].Id==q.Id;
    });
    // When every rate for the model is zero (a free tier), any usage costs nothing, so an unknown attempt is settled at 0
    // from the pinned price snapshot instead of waiting for manual reconciliation.
    void SettleFreeUnknowns(Plan p,RunRow run)
    {
        foreach(var a in store.Attempts(run.Id).Where(a=>a.Phase=="unknown"))
        {
            var reviewer=p.Reviewers.Single(r=>r.Id==p.Requests.Single(q=>q.Id==a.RequestId).ReviewerId);
            var price=p.Prices.Prices.Single(x=>x.CanonicalModelKey==reviewer.CanonicalModelKey);
            if(price is {InputPerMillion:0,CachedInputPerMillion:0,OutputPerMillion:0}&&(price.OutputIncludesReasoning||price.ReasoningPerMillion==0))store.SettleZeroPrice(a.Id);
        }
    }
    async Task<AttemptRow> DispatchAsync(Plan p,RunRow run,RequestPlan request,Grant grant,CancellationToken cancellation)
    {
        StateStore.CheckGrant(grant,p,run,store.Now);store.EnsureGrantActive(grant.Id);Planner.CheckPins(p);
        var reviewer=p.Reviewers.Single(r=>r.Id==request.ReviewerId);
        if(p.ExecutionMode=="live")
        {
            // A missing key is found before any reservation or dispatch, so it never leaves an unknown attempt.
            Data.Require(live is not null&&live.HasCredential(reviewer.HostingProvider),"credential_missing",11);
            // A hand-written grant file is not an approval: live grants must come from the interactive grant command.
            Data.Require(store.IsGrantIssued(grant),"grant_not_issued",10);
        }
        // Bound in its own transaction before the first reservation, so a failed reservation cannot free it for another run.
        store.BindGrant(run,p,grant);
        var a=store.Reserve(run,p,request,grant);
        Checkpoint("reserved");CheckCancel(run,cancellation);
        StateStore.CheckGrant(grant,p,run,store.Now);store.EnsureGrantActive(grant.Id);Planner.CheckPins(p);
        Data.Require(store.Now>=p.Prices.EffectiveFrom&&store.Now<p.Prices.EffectiveTo,"price_snapshot_expired",12);
        store.Dispatch(a.Id);Checkpoint("dispatch_intent");
        try
        {
            var (wire,http)=p.ExecutionMode=="live"?await SendLive(run,a,reviewer,request,cancellation):await SendFixture(p,run,a,reviewer,request,cancellation);
            if(http>=400)wire=WireAdapters.RedactErrorBody(wire);
            store.SaveResponse(a,new(a.Id,request.Id,p.Hash,wire,http));
            Checkpoint("response_saved");
            ProcessSaved(p,run,store.Attempts(run.Id).Single(x=>x.Id==a.Id));
            Checkpoint("settled");
        }
        // The send already recorded the attempt as unknown; the caller decides between fallback and stopping.
        catch(ReviewError e)when(e.Code=="reconciliation_required"){}
        SettleFreeUnknowns(p,run);
        return store.Attempts(run.Id).Single(x=>x.Id==a.Id);
    }
    async Task<(string Wire,int Http)> SendFixture(Plan p,RunRow run,AttemptRow a,ReviewerProfile reviewer,RequestPlan request,CancellationToken cancellation)
    {
        var fixture=Planner.SelectFixture(p,request);
        try
        {
            for(int waited=0;waited<fixture.DelayMs;waited+=25){CheckCancel(run,cancellation);await Task.Delay(Math.Min(25,fixture.DelayMs-waited),cancellation);}
            CheckCancel(run,cancellation);
        }
        catch(OperationCanceledException){store.Unknown(a.Id,"cancelled_in_flight");throw;}
        if(fixture.Status is "unknown" or "timeout" or "network_error" or "not_sent")
        {store.Unknown(a.Id,fixture.Status=="not_sent"?"synthetic_not_sent_confirmed":"dispatch_outcome_unknown");throw new ReviewError(13,"reconciliation_required");}
        return(WireAdapters.WrapFixture(reviewer,fixture),fixture.HttpStatus);
    }
    async Task<(string Wire,int Http)> SendLive(RunRow run,AttemptRow a,ReviewerProfile reviewer,RequestPlan request,CancellationToken cancellation)
    {
        // The request in flight is cancelled through a linked token when Ctrl+C arrives or the cancel control file appears.
        using var watch=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var watcher=WatchCancel(run,watch);
        TransportResult sent;
        try{sent=await live!.SendAsync(reviewer,request.Body,watch.Token);}
        catch(OperationCanceledException){store.Unknown(a.Id,"cancelled_in_flight");throw;}
        catch(ReviewError e){store.Unknown(a.Id,e.Code);throw new ReviewError(13,"reconciliation_required");}
        finally{await watch.CancelAsync();await watcher;}
        if(sent.Outcome!="received"){store.Unknown(a.Id,sent.ErrorCode??"dispatch_outcome_unknown");throw new ReviewError(13,"reconciliation_required");}
        return(sent.Body??"{}",sent.StatusCode);
    }
    async Task WatchCancel(RunRow run,CancellationTokenSource cts)
    {
        try
        {
            while(!cts.IsCancellationRequested)
            {
                bool requested;try{requested=store.CancelRequested(run);}catch(ReviewError){requested=true;}
                if(requested){await cts.CancelAsync();return;}
                await Task.Delay(250,cts.Token);
            }
        }
        catch(OperationCanceledException){}
    }
    void ProcessSaved(Plan p,RunRow run,AttemptRow a)
    {
        var req=p.Requests.Single(q=>q.Id==a.RequestId);var reviewer=p.Reviewers.Single(r=>r.Id==req.ReviewerId);
        Data.Require(a.ResponsePath is not null&&File.Exists(a.ResponsePath)&&Data.FileHash(a.ResponsePath)==a.ResponseHash,"response_changed_or_missing",13);
        var saved=Data.Read<SavedReply>(a.ResponsePath);Data.Require(saved.AttemptId==a.Id&&saved.RequestId==req.Id&&saved.PlanHash==p.Hash,"response_identity_mismatch",13);
        ReviewResponse response;
        try{response=WireAdapters.Parse(reviewer,saved.ProviderBody,saved.HttpStatus);}
        catch(ReviewError e){store.Uncertain(a,Invalid(p,req,e.Code),e.Code);return;}
        UnitResult unit;
        try
        {
            var findings=response.Status=="success"?ValidateFindings(p,req,response.Findings):[];
            bool valid=response.Status=="success"&&response.Disposition is "finding" or "no_finding";
            string validation=response.Status switch{"success"=>"valid","truncated"=>"truncated","refused"=>"refused",WireAdapters.InvalidPayload=>"invalid",_=>"failed"};
            // A model that reports "error" in a valid payload is an unusable unit, not a provider failure that stops the pass.
            string? code=valid?null:response.Status=="success"?response.Disposition:response.Status==WireAdapters.InvalidPayload?"invalid_reviewer_payload":
                response.Status=="error"&&saved.HttpStatus<400?"model_reported_error":response.Status;
            unit=new(req.UnitId,req.Id,req.ReviewerId,req.CriterionId,req.SourceId,req.Stage,"raw",Transport(p),validation,response.Disposition,valid,CoreBytes(p,req),findings,code);
        }
        catch(ReviewError e){unit=Invalid(p,req,e.Code);}
        if(!response.Usage.Known){store.Uncertain(a,unit,"cost_unknown");return;}
        var price=p.Prices.Prices.Single(x=>x.CanonicalModelKey==reviewer.CanonicalModelKey);
        decimal cost;
        try{cost=WireAdapters.Cost(response.Usage,price);}catch(ReviewError e){store.Uncertain(a,unit,e.Code);return;}
        store.Settle(a,unit,cost);
    }
    static string Transport(Plan p)=>p.ExecutionMode=="live"?"response_received":"synthetic";
    static List<string> Limits(Plan p)=>p.ExecutionMode=="live"?Envelope.LiveLimitations:new Envelope().Limitations;
    static UnitResult Invalid(Plan p,RequestPlan req,string error)=>new(req.UnitId,req.Id,req.ReviewerId,req.CriterionId,req.SourceId,req.Stage,"raw",Transport(p),"invalid","not_reported",false,CoreBytes(p,req),[],error);
    static int CoreBytes(Plan p,RequestPlan req)=>req.Stage=="relation"?0:p.Chunks.Single(c=>c.SourceId==req.SourceId&&c.Index==req.ChunkIndex).End-p.Chunks.Single(c=>c.SourceId==req.SourceId&&c.Index==req.ChunkIndex).Start;
    public static List<Finding> ValidateFindings(Plan p,RequestPlan req,IReadOnlyList<FixtureFinding> candidates)
    {
        Data.Require(candidates.Count<=1000,"finding_limit",21);var result=new List<Finding>();int ordinal=0;
        foreach(var f in candidates)
        {
            Data.Require(!string.IsNullOrWhiteSpace(f.Claim)&&!f.Claim.Contains('\n')&&!f.Claim.Contains('\r')&&f.Claim.Length<=2000&&!string.IsNullOrWhiteSpace(f.Detail)
                &&f.Kind is "defect" or "suggestion" or "question" or "verification_gap"&&f.ReportedEvidenceGrade is "observed" or "inferred" or "unconfirmed"
                &&f.Severity is "critical" or "high" or "medium" or "low" or "style"&&f.Assumptions is not null&&!string.IsNullOrEmpty(f.Quote),"invalid_finding",21);
            // Evidence is checked per finding. A quote that is missing, ambiguous or from an unknown source does not
            // void the whole review unit: the finding is kept with quote_valid=false and never exported to HGLOS.
            string? sid=f.SourceId=="$source"?req.SourceId:p.Sources.SingleOrDefault(s=>s.Id==f.SourceId||s.LogicalPath==f.SourceId)?.Id;
            var source=p.Sources.Single(s=>s.Id==(sid??req.SourceId));byte[] bytes=File.ReadAllBytes(source.TextPath),quote=Data.Utf8.GetBytes(f.Quote);
            Data.Require(Data.Hash(bytes)==source.TextSha256,"snapshot_corrupt",13);
            var hits=new List<int>();
            if(sid is not null)foreach(var span in req.AllowedSpans.Where(s=>s.SourceId==sid))
                for(int i=span.Start;i<=span.End-quote.Length;i++)
                    if(SourceReader.Boundary(bytes,i)&&bytes.AsSpan(i,quote.Length).SequenceEqual(quote))hits.Add(i);
            hits=hits.Distinct().ToList();
            // Reported line/start_byte only disambiguate repeated quotes; the location itself is computed from the exact quote.
            // A unique quote whose reported position disagrees stays quote-valid but location-invalid (a moved or miscounted line).
            var hinted=hits.Where(i=>(f.StartByte is null||f.StartByte==i)&&(f.Line is null||Line(bytes,i)==f.Line)).ToList();
            int? found=hits.Count==1?hits[0]:hinted.Count==1?hinted[0]:null;
            bool quoteValid=found is not null,locationValid=quoteValid&&hinted.Contains(found!.Value);
            int start=found??-1,end=quoteValid?start+quote.Length:-1;
            string grade=!quoteValid||f.ReportedEvidenceGrade=="unconfirmed"?"unconfirmed":f.ReportedEvidenceGrade=="inferred"||f.Assumptions.Count>0?"inferred":"observed";
            string id="finding_"+Data.Hash(req.Id+"\n"+(ordinal++)+"\n"+f.Claim+"\n"+f.Quote)[..24];
            int? line=quoteValid?Line(bytes,start):null;
            // In a diff snapshot the line is the patch display line; before/after lines are carried separately and never substituted.
            DiffLine? diff=quoteValid&&Planner.IsDiff(source.LogicalPath)?UnifiedDiff.Parse(Data.Utf8.GetString(bytes)).SingleOrDefault(d=>d.DisplayLine==line):null;
            result.Add(new(id,req.Id,req.ReviewerId,req.CriterionId,source.Id,source.LogicalPath,source.ExportPath,line,start,end,quoteValid?SourceReader.RawOffset(source,start):-1,quoteValid?SourceReader.RawOffset(source,end):-1,f.Kind,f.Category,f.Severity,f.Claim,f.Detail,f.Quote,f.ReportedEvidenceGrade,f.Assumptions,grade,quoteValid,locationValid,f.Confidence,f.Suggestion,f.Impact,[],
                Planner.IsDiff(source.LogicalPath)?"diff":"text",diff?.BeforeLine,diff?.AfterLine,diff?.OldPath,diff?.NewPath));
        }
        return result;
    }
    static int Line(byte[] bytes,int start)=>1+bytes.AsSpan(0,start).Count((byte)10);
    public static Coverage CoverageFor(Plan p,IReadOnlyList<UnitResult> units)
    {
        SourceReader.VerifyPartition(p.Sources.Where(s=>s.Role=="target"),p.Chunks);
        Data.Require(p.Requests.Select(q=>q.UnitId).Distinct().Count()==p.Requests.Count,"duplicate_unit",13);
        // Required work is the primary requests; a slot counts as done when the primary or its pre-approved fallback is valid.
        bool Answered(RequestPlan q)=>units.Any(u=>u.UnitId==q.UnitId&&u.RequestId==q.Id&&u.Valid&&u.Exposure=="raw"&&u.TransportState==Transport(p)&&u.ValidationState=="valid"&&u.ReviewDisposition is "finding" or "no_finding");
        var primaries=p.Requests.Where(q=>q.FallbackFor is null).ToList();
        var valid=primaries.Where(q=>Slot(p,q).Any(Answered)).ToList();
        long total=primaries.Sum(q=>(long)CoreBytes(p,q)),done=valid.Sum(q=>(long)CoreBytes(p,q));
        var validIds=valid.Select(q=>q.Id).ToHashSet();bool live=p.ExecutionMode=="live";decimal? ratio=total==0?null:(decimal)done/total;
        var pairs=primaries.GroupBy(q=>(q.CriterionId,q.ReviewerId,q.Stage)).Select(g=>
        {
            long pairTotal=g.Sum(q=>(long)CoreBytes(p,q)),pairDone=g.Where(q=>validIds.Contains(q.Id)).Sum(q=>(long)CoreBytes(p,q));
            decimal? pairRatio=pairTotal==0?null:(decimal)pairDone/pairTotal;
            return new PairCoverage(g.Key.CriterionId,g.Key.ReviewerId,g.Key.Stage,g.Count(),g.Count(q=>validIds.Contains(q.Id)),pairTotal,pairDone,live?null:pairRatio,live?pairRatio:null);
        }).ToList();
        var empty=p.Sources.Where(s=>s.Role=="target"&&s.ByteLength==0).Select(s=>s.Id).ToList();
        return new(primaries.Count,valid.Count,total,done,live?null:ratio,live?ratio:null,valid.Count==primaries.Count,primaries.Where(q=>!validIds.Contains(q.Id)).Select(q=>q.UnitId).ToList(),pairs,empty);
    }
    bool HasValidUnits(Plan p,RunRow run)=>CoverageFor(p,store.Units(run.Id)).ValidUnits>0;
    public Envelope View(string runId,string command="status")
    {
        var r=store.GetRun(runId);var p=store.GetPlan(r.PlanId);var artifacts=store.Artifacts(runId);
        Data.Require(r.Status!="completed"||artifacts.Count>0,"completed_bundle_missing",13);
        if(artifacts.Count>0)BundleWriter.Verify(store.Root,artifacts);
        return new(){Command=command,ExecutionMode=p.ExecutionMode,Limitations=Limits(p),RunId=r.Id,PlanId=p.Id,Status=r.Status,ErrorCode=command is "status" or "cancel"?null:r.ErrorCode,RunErrorCode=r.ErrorCode,Coverage=CoverageFor(p,store.Units(runId)),StaleSourceIds=p.Sources.Where(s=>!Data.SourceCurrent(s)).Select(s=>s.Id).ToList(),Cost=store.Costs(runId),OwnerAlive=store.OwnerAlive(r.ProjectId),Artifacts=artifacts};
    }
    Envelope SaveReport(Plan p,RunRow r,string command)
    {
        var units=store.Units(r.Id);var findings=LinkFindings(units.SelectMany(u=>u.Findings).ToList());var coverage=CoverageFor(p,units);
        var comparisons=p.Requests.GroupBy(q=>(q.SourceId,q.ChunkIndex,q.CriterionId,q.Stage)).Select(g=>new{source_id=g.Key.SourceId,chunk=g.Key.ChunkIndex,criterion_id=g.Key.CriterionId,
            reviewers=g.Select(q=>new{reviewer_id=q.ReviewerId,result=units.SingleOrDefault(u=>u.UnitId==q.UnitId)}).ToList()}).ToList();
        string markdown=(p.ExecutionMode=="live"
            ?"# ReviewGate review (live models)\n\nFindings come from real model output checked against the pinned snapshot. Quote and location checks do not prove the claims; this is not an HGLOS round.\n\n"
            :"# ReviewGate synthetic review\n\nThis is fixture processing, not actual model review or an HGLOS round.\n\n")+Data.Json(coverage)+"\n\n";
        // Model-authored text is escaped so it cannot add links, HTML or headings to the report.
        foreach(var f in findings)
        {
            string evidence=!f.QuoteValid?" · quote not found in the pinned source (not verified)":!f.LocationValid?" · reported line/offset disagrees with the quote":"";
            string location=f.CoordinateSystem=="diff"?$"{Md(f.Path)} diff display line {f.Line} (before {f.DiffBeforeLine?.ToString(CultureInfo.InvariantCulture)??"-"}, after {f.DiffAfterLine?.ToString(CultureInfo.InvariantCulture)??"-"})":$"{Md(f.Path)}:{f.Line}";
            string fence=new('`',Math.Max(3,Regex.Matches(f.Quote,"`+").Select(m=>m.Length+1).DefaultIfEmpty(0).Max()));
            markdown+=$"## {Md(f.Id)}: {Md(f.Claim)}\n\n{location} · {Md(f.Severity)} · {Md(f.ExportedEvidenceGrade)}{evidence}\n\n{Md(f.Detail)}\n\n{fence}text\n{f.Quote}\n{fence}\n\nSuggestion: {Md(f.Suggestion)}\n\n";
        }
        string dir=store.PathFor("runs",r.Id,"bundles",Data.Id("bundle"));
        var files=new Dictionary<string,string>{["result.json"]=Data.Json(new{schema_version=Data.Version,execution_mode=p.ExecutionMode,run_id=r.Id,plan_id=p.Id,plan_hash=p.Hash,run_status=r.Status,coverage,findings,units,comparisons,independence=HglosExporter.Independence(p),cost=store.Costs(r.Id),attempts=store.Attempts(r.Id).Select(a=>a with{ResponsePath=a.ResponsePath is null?null:Path.GetRelativePath(store.Root,a.ResponsePath).Replace('\\','/')}),stale_sources=p.Sources.Where(s=>!File.Exists(s.OriginalPath)||Data.FileHash(s.OriginalPath)!=s.RawSha256).Select(s=>s.Id)}),["report.md"]=markdown};
        var artifacts=BundleWriter.Write(store.Root,dir,files,p.ExecutionMode);BundleWriter.Verify(store.Root,artifacts);Checkpoint("bundle_precommit");store.CompleteBundle(r.Id,r.Status,r.ErrorCode,artifacts);
        return View(r.Id,command);
    }
    static string Md(string text)=>Regex.Replace(text.Replace("\r"," ",StringComparison.Ordinal).Replace("\n"," ",StringComparison.Ordinal),@"([\\`*_{}\[\]()#+\-.!|<>~])",@"\$1");
    static List<Finding> LinkFindings(List<Finding> all)=>all.Select(f=>f with{RelatedFindingIds=all.Where(x=>x.Id!=f.Id&&x.SourceId==f.SourceId&&x.StartByte<f.EndByte&&x.EndByte>f.StartByte).Select(x=>x.Id).ToList()}).ToList();
    public CommandResult Export(string runId,string format,bool allowPartial)
    {
        var run=store.GetRun(runId);var plan=store.GetPlan(run.PlanId);
        using var owner=store.Own(run.ProjectId);var units=store.Units(runId);var coverage=CoverageFor(plan,units);
        string dir=store.PathFor("runs",runId,"exports",Data.Id("export"));
        if(format=="hglos-findings")return HglosExporter.Export(store.Root,dir,plan,run,coverage,units,store.Attempts(runId),allowPartial,false);
        Data.Require(format is "json" or "markdown","unsupported_export_format");
        var artifacts=store.Artifacts(runId);Data.Require(artifacts.Count>0,"export_not_ready",13);BundleWriter.Verify(store.Root,artifacts);
        string name=format=="json"?"result.json":"report.md";var input=artifacts.Single(a=>Path.GetFileName(a.Path)==name);
        var result=BundleWriter.Write(store.Root,dir,new Dictionary<string,string>{{name,File.ReadAllText(Path.Combine(store.Root,input.Path))}},plan.ExecutionMode);
        return new(run.Status=="completed"?0:20,new Envelope{Command="export",ExecutionMode=plan.ExecutionMode,Limitations=Limits(plan),RunId=runId,PlanId=plan.Id,Status=run.Status,Artifacts=result,Coverage=coverage,StaleSourceIds=plan.Sources.Where(s=>!Data.SourceCurrent(s)).Select(s=>s.Id).ToList()});
    }
    static void Checkpoint(string stage)
    {
#if DEBUG
        FaultObserver?.Invoke(stage);
#endif
    }
#if DEBUG
    public static Action<string>? FaultObserver {get;set;}
#endif
}
