using System.Text.Json.Nodes;
using ReviewGate.Contracts;

namespace ReviewGate.Hglos;

public static class BundleWriter
{
    public static List<Artifact> Write(string stateRoot,string directory,IReadOnlyDictionary<string,string> files)
    {
        Data.Require(!Directory.Exists(directory),"bundle_already_exists",13);Directory.CreateDirectory(directory);
        var artifacts=new List<Artifact>();
        foreach(var (name,content) in files)
        {
            Data.Require(Path.GetFileName(name)==name,"invalid_artifact_path",13);
            string path=Path.Combine(directory,name);Data.CreateFile(path,content);
            artifacts.Add(new(Path.GetRelativePath(stateRoot,path).Replace('\\','/'),Data.FileHash(path),new FileInfo(path).Length,Path.GetExtension(name).TrimStart('.')));
        }
        string manifest=Path.Combine(directory,"manifest.json");
        Data.CreateFile(manifest,Data.Json(new{schema_version=Data.Version,bundle_id=Path.GetFileName(directory),execution_mode="synthetic",artifacts}));
        artifacts.Add(new(Path.GetRelativePath(stateRoot,manifest).Replace('\\','/'),Data.FileHash(manifest),new FileInfo(manifest).Length,"manifest"));return artifacts;
    }
    public static void Verify(string stateRoot,IReadOnlyList<Artifact> artifacts)
    {
        Data.Require(artifacts.Count>0&&artifacts.Count(a=>a.Kind=="manifest")==1,"invalid_bundle",13);
        foreach(var a in artifacts)
        {
            string path=Path.GetFullPath(Path.Combine(stateRoot,a.Path));string root=Path.GetFullPath(stateRoot)+Path.DirectorySeparatorChar;
            Data.Require(path.StartsWith(root,StringComparison.OrdinalIgnoreCase)&&File.Exists(path)&&new FileInfo(path).Length==a.Bytes&&Data.FileHash(path)==a.Sha256,"artifact_changed",13);
            for(var p=path;p is not null&&p.StartsWith(root,StringComparison.OrdinalIgnoreCase);p=Path.GetDirectoryName(p))
                Data.Require((File.GetAttributes(p)&FileAttributes.ReparsePoint)==0,"artifact_reparse_point",13);
        }
        var marker=artifacts.Single(a=>a.Kind=="manifest");var node=JsonNode.Parse(File.ReadAllText(Path.Combine(stateRoot,marker.Path)))!;
        var declared=Data.Parse<List<Artifact>>(node["artifacts"]!.ToJsonString());
        Data.Require(Data.Json(declared)==Data.Json(artifacts.Where(a=>a.Kind!="manifest").ToList()),"bundle_manifest_mismatch",13);
        string dir=Path.GetDirectoryName(Path.Combine(stateRoot,marker.Path))!;
        Data.Require(Directory.GetFiles(dir).Length==artifacts.Count,"unexpected_bundle_file",13);
    }
}

public static class HglosExporter
{
    static readonly HashSet<string> Categories=["nullderef","bounds","leak","race","logic","apicontract","errorhandling","perf","style","other"];
    static readonly HashSet<string> Severities=["critical","high","medium","low","style"];
    public static CommandResult Export(string root,string directory,Plan plan,RunRow run,Coverage coverage,
        IReadOnlyList<UnitResult> units,IReadOnlyList<AttemptRow> attempts,bool allowPartial,bool ownerAlive)
    {
        if(ownerAlive)throw new ReviewError(13,"owner_conflict");
        if(run.Status is "planned" or "blocked" or "ready" or "running" or "cancelling")throw new ReviewError(13,"export_not_ready");
        Data.Require(plan.Profile.Kind=="code","hglos_code_profile_required",14);
        var excluded=new List<object>();var mapped=new List<object>();var mappings=new List<object>();
        foreach(var f in units.SelectMany(u=>u.Findings))
        {
            string? reason=f.Kind!="defect"?"not_code_defect":!f.QuoteValid||!f.LocationValid?"invalid_evidence":f.ExportPath is null||f.Line is null||f.Line<1?"after_source_coordinate_required":
                !Categories.Contains(f.Category)||!Severities.Contains(f.Severity)?"unsupported_enum":null;
            if(reason is not null){excluded.Add(new{finding_id=f.Id,reason});continue;}
            mapped.Add(new{path=f.ExportPath,line=f.Line,category=f.Category,severity=f.Severity,evidence=f.ExportedEvidenceGrade,claim=f.Claim,detail=f.Detail});
            mappings.Add(new{finding_id=f.Id,raw_index=mapped.Count-1,source_id=f.SourceId,start_byte=f.StartByte,end_byte=f.EndByte,quote=f.Quote,
                reported_evidence_grade=f.ReportedEvidenceGrade,exported_evidence_grade=f.ExportedEvidenceGrade,assumptions=f.Assumptions,reviewer_id=f.ReviewerId});
        }
        bool unresolved=attempts.Any(a=>StateHeld(a.Phase));
        bool complete=run.Status=="completed"&&coverage.RequiredCoverageComplete&&!unresolved&&excluded.Count==0;
        int exit=complete?0:allowPartial?20:run.Status=="needs_reconciliation"?13:20;
        string? error=complete?null:allowPartial?"partial_export":run.Status=="needs_reconciliation"?"reconciliation_required":run.Status=="completed"?"projection_incomplete":"export_incomplete_run";
        var stale=plan.Sources.Where(s=>!Data.SourceCurrent(s)).Select(s=>s.Id).ToList();
        var env=new Envelope{Command="export",RunId=run.Id,PlanId=plan.Id,Status=complete?"completed":"partial",ErrorCode=error,Coverage=coverage,StaleSourceIds=stale};
        if(!complete&&!allowPartial)return new(exit,env);
        var files=new Dictionary<string,string>();
        if(complete||coverage.ValidUnits>0)files["hglos.findings.synthetic.json"]=Data.Json(new{findings=mapped});
        files["provenance.json"]=Data.Json(new{schema_version=Data.Version,execution_mode="synthetic",run_id=run.Id,plan_id=plan.Id,plan_hash=plan.Hash,manifest_hash=plan.ManifestHash,
            run_status=run.Status,stale_source_ids=stale,applies_to="pinned_snapshot_only",required_coverage_complete=coverage.RequiredCoverageComplete,projection_complete=excluded.Count==0,
            unresolved_attempts=attempts.Where(a=>StateHeld(a.Phase)).Select(a=>a.Id),coverage,excluded,mappings,
            reviewers=plan.Reviewers,independence=Independence(plan),sources=plan.Sources.Select(s=>new{s.Id,s.RawSha256,s.TextSha256,s.ExportPath}),
            limitations=new[]{"synthetic_fixture_not_actual_review","not_an_hglos_round","existing_validator_does_not_check_provenance"}});
        var artifacts=BundleWriter.Write(root,directory,files);BundleWriter.Verify(root,artifacts);
        return new(exit,env with{Artifacts=artifacts});
    }
    static bool StateHeld(string s)=>s is "unknown" or "reserved" or "dispatch_intent" or "response_saved";
    public static object Independence(Plan p)=>new{input_isolation=p.Mode=="independent",distinct_reviewer=p.Reviewers.Count==2&&p.Reviewers.Select(r=>r.Id).Distinct().Count()==2,
        distinct_model=p.Reviewers.Count==2&&p.Reviewers.Select(r=>r.CanonicalModelKey).Distinct().Count()==2,
        distinct_developer=p.Reviewers.Count!=2||(p.Reviewers.Any(r=>r.ModelDeveloper=="unknown"))?(bool?)null:p.Reviewers.Select(r=>r.ModelDeveloper).Distinct().Count()==2,
        evaluation_scope="synthetic_fixture",shared_fixture_id=p.Fixture.Id};
}
