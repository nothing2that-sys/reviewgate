using System.Text.RegularExpressions;
using ReviewGate.Contracts;
using ReviewGate.Core.Sources;
using ReviewGate.Infrastructure;
using ReviewGate.Providers;

namespace ReviewGate.Core.Planning;

public record PlanInput(string Project,string SourceRoot,string? RepositoryRoot,string InputClass,
    string Instruction,string ProfileFile,List<string> ReviewerFiles,string BudgetFile,string PriceFile,
    string? FixtureFile,List<string> Inputs,List<string> References,string Mode="single",string? BaseRun=null,List<string>? Findings=null);
public static class Planner
{
    public static Plan Build(StateStore store,PlanInput input)
    {
        StateStore.ValidId(input.Project);
        Data.Require(input.InputClass is "synthetic" or "nonsensitive","input_class_required");
        Data.Require(!string.IsNullOrWhiteSpace(input.Instruction)&&Data.Utf8.GetByteCount(input.Instruction)<=1_048_576,"instruction_required");
        Data.Require(input.Mode is "single" or "independent" or "verify","invalid_review_mode");
        string root=SafePath.Full(input.SourceRoot);SafePath.NoLinks(store.Root);
        Data.Require(!SafePath.Within(store.Root,root)&&!SafePath.Within(root,store.Root),"state_source_overlap");
        string? repo=input.RepositoryRoot is null?null:SafePath.Full(input.RepositoryRoot);
        if(repo is not null){SafePath.NoLinks(repo);Data.Require(SafePath.Within(root,repo)&&(File.Exists(Path.Combine(repo,".git"))||Directory.Exists(Path.Combine(repo,".git"))),"invalid_repository_root");}
        bool live=store.Namespace=="live";
        // Live plans never take a fixture; synthetic plans always need one. Neither can stand in for the other.
        Data.Require(live?input.FixtureFile is null:input.FixtureFile is not null,live?"fixture_not_allowed_in_live":"fixture_required");
        var pinPaths=new[]{input.ProfileFile,input.BudgetFile,input.PriceFile}.Concat(live?Array.Empty<string>():new[]{input.FixtureFile!}).Concat(input.ReviewerFiles).Select(Path.GetFullPath).ToList();
        foreach(var path in pinPaths){SafePath.NoLinks(path);Data.Require(File.Exists(path),"input_unavailable");}
        var pins=pinPaths.Select(p=>new InputPin(p,Data.FileHash(p))).ToList();
        var profile=Data.Read<ReviewProfile>(input.ProfileFile);Validate(profile);
        var reviewers=input.ReviewerFiles.Select(Data.Read<ReviewerProfile>).ToList();
        Data.Require(reviewers.Count==(input.Mode=="independent"?2:1),"reviewer_count_mismatch");
        foreach(var r in reviewers)WireAdapters.Validate(r);
        Data.Require(reviewers.Select(r=>r.Id).Distinct().Count()==reviewers.Count,"duplicate_reviewer_id");
        Data.Require(reviewers.Select(r=>r.CanonicalModelKey).Distinct().Count()==reviewers.Count,"duplicate_reviewer_model");
        // The declared key is not a verified identity: the same model id/version under another key or host is the same model.
        Data.Require(reviewers.Select(ModelIdentity).Distinct().Count()==reviewers.Count,"duplicate_reviewer_model");
        var budget=Data.Read<BudgetPolicy>(input.BudgetFile);Data.VersionIs(budget.SchemaVersion);
        Data.Require(budget.Currency=="USD"&&budget.Timezone=="+09:00"&&budget.RunUsd>0&&budget.ProjectUsd>0&&budget.MonthUsd>0&&budget.MaxRequests>0&&budget.MaxRetries>=0,"invalid_budget");
        var prices=Data.Read<PriceSnapshot>(input.PriceFile);Data.VersionIs(prices.SchemaVersion);
        Data.Require(prices.Prices is not null&&prices.Prices.Count>0&&prices.Currency=="USD"&&prices.RetrievedAt!=default&&!string.IsNullOrWhiteSpace(prices.Source)
            &&Regex.IsMatch(prices.DocumentSha256??"", "^[a-fA-F0-9]{64}$")&&prices.EffectiveFrom<=store.Now&&store.Now<prices.EffectiveTo,"price_snapshot_unavailable",12);
        Data.Require(prices.Prices.Select(x=>x.CanonicalModelKey).Distinct().Count()==prices.Prices.Count,"ambiguous_prices",12);
        foreach(var price in prices.Prices)Data.Require(price.InputPerMillion>=0&&price.OutputPerMillion>=0&&price.CachedInputPerMillion>=0&&price.ReasoningPerMillion>=0,"invalid_price",12);
        foreach(var r in reviewers.Where(r=>live&&r.HostingProvider=="google"))
        {
            // Google thinking cannot be capped through the API; a declared bound and a separately billed reasoning row are required.
            Data.Require(r.MaxReasoningTokens>0,"unbounded_request",12);
            var row=prices.Prices.SingleOrDefault(x=>x.CanonicalModelKey==r.CanonicalModelKey);
            Data.Require(row is null||(!row.OutputIncludesReasoning&&row.ReasoningPerMillion>=row.OutputPerMillion),"price_reasoning_rule_mismatch",12);
        }
        var fixture=live?new FixtureBundle(Data.Version,"live-none",[]):Data.Read<FixtureBundle>(input.FixtureFile!);Data.VersionIs(fixture.SchemaVersion);
        Data.Require(live||fixture.Responses is {Count:>0},"invalid_fixture");
        foreach(var f in fixture.Responses)Data.Require(f.DelayMs>=0&&f.DelayMs<=600_000,"invalid_fixture_delay");
        var selectedFindings=new List<Finding>();
        if(input.Mode=="verify")
        {
            Data.Require(input.BaseRun is not null&&input.Findings is {Count:>0},"verification_target_required");
            var prior=store.GetRun(input.BaseRun);Data.Require(!store.OwnerAlive(prior.ProjectId),"owner_conflict",13);
            Data.Require(prior.ProjectId==input.Project,"verification_project_mismatch");
            Data.Require(prior.Status is "completed" or "partial","verification_base_not_stable",13);
            selectedFindings=store.Units(prior.Id).SelectMany(u=>u.Findings).Where(f=>input.Findings.Contains(f.Id)).ToList();
            Data.Require(selectedFindings.Count==input.Findings.Distinct().Count(),"unknown_verification_finding");
        }
        else Data.Require(input.BaseRun is null&&input.Findings is not {Count:>0},"unexpected_verification_target");
        var directoryPins=input.Inputs.Concat(input.References).Select(x=>SafePath.Input(x,root)).Where(Directory.Exists).Select(x=>new InputPin(x,SourceReader.InventoryHash(x))).ToList();
        string id=store.NewId("plan");var capture=SourceReader.Capture(root,repo,input.Inputs,input.References,store.NewSnapshotDir(id),profile.ChunkBytes,profile.ContextBytes);
        foreach(var s in capture.Sources.Where(s=>IsDiff(s.OriginalPath)))
            Data.CreateFile(Path.Combine(Path.GetDirectoryName(s.RawPath)!,s.Id+".diff-map.json"),Data.Json(UnifiedDiff.Parse(File.ReadAllText(s.TextPath))));
        var sources=capture.Sources.Select(s=>s with{ExportPath=IsDiff(s.OriginalPath)?null:s.ExportPath}).ToList();
        if(input.Mode=="verify")Data.Require(selectedFindings.All(f=>sources.Any(s=>s.Id==f.SourceId)),"verification_snapshot_mismatch",13);
        var p=new Plan{Id=id,ExecutionMode=store.Namespace,ProjectId=input.Project,SourceRoot=root,RepositoryRoot=repo,InputClass=input.InputClass,Instruction=input.Instruction,
            InstructionHash=Data.Hash(input.Instruction),Profile=profile,Reviewers=reviewers,Budget=budget,Prices=prices,Fixture=fixture,
            Pins=pins,DirectoryPins=directoryPins,Sources=sources,Exclusions=capture.Exclusions,Chunks=capture.Chunks,Mode=input.Mode,BaseRun=input.BaseRun,FindingIds=input.Findings??[]};
        foreach(var reviewer in reviewers)
        {
            var price=prices.Prices.SingleOrDefault(x=>x.CanonicalModelKey==reviewer.CanonicalModelKey)??throw new ReviewError(12,"price_missing");
            foreach(var criterion in profile.Criteria)
            {
                var chunks=criterion.Scope=="relation"?[new Chunk("relation",sources.First(s=>s.Role=="target").Id,-1,0,0,0,0)]:p.Chunks;
                foreach(var chunk in chunks)
                {
                    if(input.Mode=="verify"&&!selectedFindings.Any(f=>f.SourceId==chunk.SourceId&&(criterion.Scope=="relation"||(f.StartByte<chunk.End&&f.EndByte>chunk.Start))))continue;
                    var spans=criterion.Scope=="relation"?sources.Select(s=>new SpanRef(s.Id,0,s.ByteLength)).ToList():
                        sources.Where(s=>s.Role=="reference").Select(s=>new SpanRef(s.Id,0,s.ByteLength)).Prepend(new(chunk.SourceId,chunk.ContextStart,chunk.ContextEnd)).ToList();
                    var context=spans.Select(span=>new{source_id=span.SourceId,path=sources.Single(s=>s.Id==span.SourceId).LogicalPath,start_byte=span.Start,end_byte=span.End,
                        text=Data.Utf8.GetString(File.ReadAllBytes(sources.Single(s=>s.Id==span.SourceId).TextPath).AsSpan(span.Start,span.End-span.Start))}).ToList();
                    string payload=Data.Json(new{execution_mode=store.Namespace,instruction=input.Instruction,criterion,core=new{source_id=chunk.SourceId,start_byte=chunk.Start,end_byte=chunk.End},sources=context,
                        verification_target=input.Mode=="verify"?selectedFindings:null});
                    string body=WireAdapters.Build(reviewer,payload);string unit=Data.Hash(reviewer.Id+"\n"+criterion.Id+"\n"+chunk.Id);
                    var request=new RequestPlan(Data.Id("request"),unit,reviewer.Id,criterion.Id,chunk.SourceId,chunk.Index,spans,body,Data.Hash(body),WireAdapters.Reservation(reviewer,price,body),criterion.Scope);
                    if(!live)_=SelectFixture(p,request);p.Requests.Add(request);
                }
            }
        }
        Data.Require(p.Requests.Count>0,"no_reviewable_units");
        p.ManifestHash=Data.Hash(Data.Json(p.Requests));CheckPins(p);p.Hash=Data.Hash(Data.Json(p with{Hash=""}));store.SavePlan(p);return p;
    }
    public static bool IsDiff(string path)=>Path.GetExtension(path).ToLowerInvariant() is ".diff" or ".patch";
    public static string ModelIdentity(ReviewerProfile r)
    {
        string model=r.ModelId.Trim().ToLowerInvariant();
        return model[(model.LastIndexOf('/')+1)..]+"@"+r.ModelVersion.Trim().ToLowerInvariant();
    }
    public static FixtureResponse SelectFixture(Plan p,RequestPlan request)
    {
        string path=p.Sources.Single(s=>s.Id==request.SourceId).LogicalPath;
        var matches=p.Fixture.Responses.Where(f=>(f.Reviewer=="*"||f.Reviewer==request.ReviewerId)&&(f.Criterion=="*"||f.Criterion==request.CriterionId)
            &&(f.Source=="*"||f.Source==path||f.Source==request.SourceId)&&(f.Chunk<0||f.Chunk==request.ChunkIndex))
            .Select(f=>new{Response=f,Score=(f.Reviewer!="*"?1:0)+(f.Criterion!="*"?1:0)+(f.Source!="*"?1:0)+(f.Chunk>=0?1:0)}).ToList();
        Data.Require(matches.Count>0,"missing_fixture_response");int best=matches.Max(m=>m.Score);
        Data.Require(matches.Count(m=>m.Score==best)==1,"ambiguous_fixture_response");return matches.Single(m=>m.Score==best).Response;
    }
    public static void Validate(ReviewProfile p)
    {
        Data.VersionIs(p.SchemaVersion);Data.Require(p.Kind is "code" or "novel" or "document" or "custom","invalid_profile");
        Data.Require(!string.IsNullOrWhiteSpace(p.Id)&&p.Criteria is {Count:>0},"invalid_profile");
        Data.Require(p.Criteria.Select(c=>c.Id).Distinct().Count()==p.Criteria.Count&&p.Criteria.All(c=>!string.IsNullOrWhiteSpace(c.Id)&&!string.IsNullOrWhiteSpace(c.Description)&&c.Scope is "chunk" or "relation"),"invalid_criteria");
    }
    public static void CheckPins(Plan p)
    {
        Data.Require(p.Hash.Length==0||p.Hash==Data.Hash(Data.Json(p with{Hash=""})),"plan_hash_mismatch",13);
        foreach(var pin in p.Pins)Data.Require(File.Exists(pin.Path)&&Data.FileHash(pin.Path)==pin.Sha256,"input_changed",13);
        foreach(var pin in p.DirectoryPins)Data.Require(SourceReader.InventoryHash(pin.Path)==pin.Sha256,"source_inventory_changed",13);
        foreach(var s in p.Sources)
            Data.Require(File.Exists(s.OriginalPath)&&File.Exists(s.RawPath)&&File.Exists(s.TextPath)&&Data.FileHash(s.OriginalPath)==s.RawSha256&&Data.FileHash(s.RawPath)==s.RawSha256&&Data.FileHash(s.TextPath)==s.TextSha256,"snapshot_changed_or_missing",13);
        SourceReader.VerifyPartition(p.Sources.Where(s=>s.Role=="target"),p.Chunks);
        Data.Require(p.ManifestHash==Data.Hash(Data.Json(p.Requests))&&p.Requests.All(r=>Data.Hash(r.Body)==r.BodyHash),"manifest_changed",13);
    }
}
