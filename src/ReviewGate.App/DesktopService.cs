using System.Globalization;
using System.Text.RegularExpressions;
using ReviewGate.Contracts;
using ReviewGate.Core.Execution;
using ReviewGate.Core.Planning;
using ReviewGate.Infrastructure;
using ReviewGate.Providers;
using ReviewGate.Transport.Http;

namespace ReviewGate.App;

public record ModelOption(string Id, string Provider, string ModelId, string CanonicalModelKey, string File, bool Custom,
    decimal? InputPerMillion, decimal? OutputPerMillion, string? Tier, int MaxOutputTokens, string? ReasoningEffort)
{
    public bool Priced => InputPerMillion is not null;
    public bool Free => Tier == "free";
    public string PriceText => !Priced ? "가격 없음 (실행 불가)" : Free ? "무료 티어" : Tier == "subscription" ? "구독 (CLI 로그인, 토큰 단가 없음)" :
        $"입력 ${InputPerMillion!.Value.ToString(CultureInfo.InvariantCulture)} / 출력 ${OutputPerMillion!.Value.ToString(CultureInfo.InvariantCulture)} (100만 token)";
    public string Display => $"{Provider} · {ModelId}{(Custom ? " (사용자 추가)" : "")}";
}
// Per provider: the primary model and an optional fallback used only when the primary gives no answer.
public record DesktopSettings(string SchemaVersion, bool GoogleFreeTier, Dictionary<string, string> DefaultModels, Dictionary<string, string>? FallbackModels = null);
// HTTP providers list named keys; CLI providers use their own login, so only the executable location is shown.
public record ProviderKeys(ProviderInfo Provider, IReadOnlyList<CredentialEntry> Keys, string? CliPath = null)
{
    public bool Ready => Provider.IsCli ? CliPath is not null : Keys.Any(k => k.Active);
    public string Status => Provider.IsCli ? (CliPath is null ? $"{Provider.Endpoint} 설치되지 않음" : $"CLI 로그인 사용 · {CliPath}") :
        Keys.SingleOrDefault(k => k.Active) is { } active ? $"사용 중: {active.Name}" : Keys.Count == 0 ? "키 없음" : "사용할 키를 선택하세요";
}
public record QuickTestRequest(List<string> ReviewerIds, string ProfileKind, string Instruction, string? Text, string? SourcePath, bool ConfirmedNonSensitive, List<string?>? FallbackIds = null);
public record QuickPreview(string PlanId, string Mode, int Requests, decimal ReservationUsd, List<string> Recipients, List<string> Warnings, int SourceFiles, long SourceBytes, List<string> Exclusions);
public record FindingView(string Reviewer, string Kind, string Severity, string Location, string Claim, string Detail, string Quote, string Evidence);
public record QuickResult(string? RunId, string Status, string? ErrorCode, decimal UsageUsd, decimal UnknownHoldUsd, decimal? ProcessingRatio,
    List<FindingView> Findings, List<string> UnitNotes, string? ReportPath);
public record PriceStatus(string SnapshotId, DateTimeOffset EffectiveTo, int DaysLeft, bool Expired, bool UserApproved)
{
    public bool NoticeDue => Expired || DaysLeft <= PriceRefresh.NoticeDays;
    public string Text => Expired ? $"가격표가 만료되었습니다({EffectiveTo.ToOffset(TimeSpan.FromHours(9)):yyyy-MM-dd HH:mm} KST). 유료 모델은 갱신 전까지 실행되지 않습니다." :
        $"가격표 {SnapshotId}{(UserApproved ? " (사용자 승인)" : "")} · {EffectiveTo.ToOffset(TimeSpan.FromHours(9)):yyyy-MM-dd HH:mm} KST 만료 ({DaysLeft}일 남음)";
}
public record RunView(string RunId, string Project, string Status, string? ErrorCode, DateTimeOffset Created, decimal UsageUsd, decimal UnknownHoldUsd);

// Everything the desktop screens do, without any UI type. It drives the same planner, ledger, grant and engine
// as the CLI; the only desktop-specific parts are where settings live and how approval is collected.
public sealed class DesktopService(string stateRoot, string profilesRoot, ICredentialStore credentials,
    Func<HttpMessageHandler?>? handler = null, Func<DateTimeOffset>? clock = null, Func<IModelTransport>? cli = null, Func<string, string?>? locateCli = null,
    Func<string, ReviewGate.Transport.Cli.CliJob, CancellationToken, Task<TransportResult>>? cliJob = null)
{
    public const string Project = "desktop";
    public static IEnumerable<string> Providers => ProviderCatalog.Ids;
    public const string SampleCode = "// ReviewGate connection test sample.\nint total = 0;\nfor (int i = 0; i <= 10; i++) total += i;\nConsole.WriteLine(total / 0);\n";
    string DesktopDir => Path.Combine(Path.GetFullPath(stateRoot), "desktop");
    string SettingsFile => Path.Combine(DesktopDir, "settings.json");
    string CustomReviewers => Path.Combine(DesktopDir, "reviewers");
    string CustomPricesFile => Path.Combine(DesktopDir, "custom-prices.json");
    string ApprovedPrices => Path.Combine(DesktopDir, "prices", "approved");
    DateTimeOffset Now => (clock ?? (() => DateTimeOffset.UtcNow))();
    StateStore Open() => new(stateRoot, clock, "live");

    // ---- API keys: names and which one is active; values are written once and never read back to the UI.
    public List<ProviderKeys> Keys() => ProviderCatalog.All.Select(p => p.IsCli
        ? new ProviderKeys(p, [], (locateCli ?? ReviewGate.Transport.Cli.CliTransport.DefaultLocate)(p.Endpoint))
        : new ProviderKeys(p, credentials.List(p.Id))).ToList();
    public Dictionary<string, bool> CredentialStatus() => Keys().ToDictionary(k => k.Provider.Id, k => k.Ready);
    public string AddKey(string provider, string name, string key) => credentials.Add(ProviderCatalog.Get(provider).Id, name, key);
    public bool RemoveKey(string provider, string keyId) => credentials.Remove(ProviderCatalog.Get(provider).Id, keyId);
    public void ActivateKey(string provider, string keyId) => credentials.Activate(ProviderCatalog.Get(provider).Id, keyId);

    // ---- Settings and models.
    public DesktopSettings LoadSettings()
    {
        var defaults = new DesktopSettings(Data.Version, true, new() { ["openai"] = "openai-gpt-6-luna", ["together"] = "together-glm-5.3-flash", ["google"] = "google-gemini-3.5-flash", ["claude-cli"] = "claude-cli", ["codex-cli"] = "codex-cli", ["agy-cli"] = "agy-cli" },
            new() { ["google"] = "google-gemini-3.5-flash-lite" });
        if (!File.Exists(SettingsFile)) return defaults;
        var saved = Data.Read<DesktopSettings>(SettingsFile);
        foreach (var (provider, model) in defaults.DefaultModels) saved.DefaultModels.TryAdd(provider, model);
        return saved with { FallbackModels = saved.FallbackModels ?? defaults.FallbackModels };
    }
    public void SaveSettings(DesktopSettings settings) => Replace(SettingsFile, Data.Json(settings));
    public List<ModelOption> Models()
    {
        var prices = MergedPrices().Snapshot;
        var files = Directory.GetFiles(Path.Combine(profilesRoot, "reviewers"), "*.json").Select(f => (f, false))
            .Concat(Directory.Exists(CustomReviewers) ? Directory.GetFiles(CustomReviewers, "*.json").Select(f => (f, true)) : []);
        return files.Select(x =>
        {
            var r = Data.Read<ReviewerProfile>(x.f);
            var row = prices.Prices.SingleOrDefault(p => p.CanonicalModelKey == r.CanonicalModelKey);
            return new ModelOption(r.Id, r.HostingProvider, r.ModelId, r.CanonicalModelKey, x.f, x.Item2, row?.InputPerMillion, row?.OutputPerMillion, row?.Tier, r.MaxOutputTokens, r.ReasoningEffort);
        }).OrderBy(m => ProviderCatalog.Ids.ToList().IndexOf(m.Provider)).ThenBy(m => m.Custom).ThenBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase).ToList();
    }
    // A user-added model gets its own reviewer profile and price row. Prices are what the user typed (or free);
    // they are recorded as user-entered, not verified against the provider's price page.
    public ModelOption AddCustomModel(string provider, string modelId, bool free, decimal inputPerMillion = 0, decimal outputPerMillion = 0)
    {
        provider = ProviderCatalog.Get(provider).Id;
        modelId = modelId.Trim();Data.Require(Regex.IsMatch(modelId, "^[A-Za-z0-9][A-Za-z0-9._/:-]{0,127}$"), "invalid_model_id");
        Data.Require(free || (inputPerMillion > 0 && outputPerMillion > 0), "price_required");
        string slug = modelId.ToLowerInvariant(), id = "custom-" + provider + "-" + Regex.Replace(slug, "[^a-z0-9.-]", "-");
        var reviewer = new ReviewerProfile(Data.Version, id, provider, modelId, ProviderCatalog.Get(provider).ApiProfile,
            provider + "/" + slug, "unknown", "", 16384, 4096, provider == "google" ? 8192 : 0, 65536, null, 300);
        WireAdapters.Validate(reviewer);
        var rows = File.Exists(CustomPricesFile) ? Data.Read<List<PriceRow>>(CustomPricesFile) : [];
        rows.RemoveAll(r => r.CanonicalModelKey == reviewer.CanonicalModelKey);
        rows.Add(new(reviewer.CanonicalModelKey, free ? 0 : inputPerMillion, free ? 0 : outputPerMillion, 0, provider != "google",
            provider == "google" ? (free ? 0 : outputPerMillion) : 0, free ? "free" : "user_entered", "global"));
        Replace(CustomPricesFile, Data.Json(rows));
        Replace(Path.Combine(CustomReviewers, id + ".json"), Data.Json(reviewer));
        return Models().Single(m => m.Id == id);
    }
    public void RemoveCustomModel(string id)
    {
        var model = Models().SingleOrDefault(m => m.Id == id && m.Custom) ?? throw new ReviewError(2, "unknown_custom_model");
        File.Delete(model.File);
        if (File.Exists(CustomPricesFile)) Replace(CustomPricesFile, Data.Json(Data.Read<List<PriceRow>>(CustomPricesFile).Where(r => r.CanonicalModelKey != model.CanonicalModelKey).ToList()));
    }
    // Shipped snapshots are live-<date>.json (paid) and live-<date>-google-free.json (Google rows at the free tier);
    // snapshots the user approved after a price refresh live in the desktop folder as user-<stamp>[-google-free].json.
    // Among those whose window covers now, the most recently retrieved wins (a fresh approval beats a shipped file).
    // With none in its window, the nearest one is used, so all-zero-price plans keep working and paid plans stop
    // with price_snapshot_expired until prices are refreshed.
    public static PriceSnapshot ShippedPrices(string folder, bool googleFree, DateTimeOffset now, string? approvedFolder = null)
    {
        var files = Directory.GetFiles(folder, "live-*.json").Concat(approvedFolder is not null && Directory.Exists(approvedFolder) ? Directory.GetFiles(approvedFolder, "user-*.json") : []);
        var all = files.Where(f => f.EndsWith("-google-free.json", StringComparison.Ordinal) == googleFree)
            .Select(Data.Read<PriceSnapshot>).OrderBy(s => s.EffectiveFrom).ThenBy(s => s.RetrievedAt).ToList();
        Data.Require(all.Count > 0, "price_snapshot_unavailable", 12);
        return all.Where(s => PriceRules.InWindow(s, now)).MaxBy(s => s.RetrievedAt) ?? all.LastOrDefault(s => s.EffectiveFrom <= now) ?? all[0];
    }

    // ---- Price refresh: expiry notice, CLI lookup, user approval.
    public PriceStatus PriceStatus()
    {
        var s = ShippedPrices(Path.Combine(profilesRoot, "prices"), LoadSettings().GoogleFreeTier, Now, ApprovedPrices);
        bool expired = !PriceRules.InWindow(s, Now);
        return new(s.Id, s.EffectiveTo, expired ? 0 : (int)Math.Ceiling((s.EffectiveTo - Now).TotalDays), expired, s.Id.StartsWith("user-", StringComparison.Ordinal));
    }
    // Google models priced at the free tier in the free-tier snapshot in use (the default for the approval checkboxes).
    public IReadOnlySet<string> CurrentFreeTierModels() =>
        ShippedPrices(Path.Combine(profilesRoot, "prices"), true, Now, ApprovedPrices).Prices.Where(p => p.Tier == "free").Select(p => p.CanonicalModelKey).ToHashSet();
    public PriceSources PriceSources() => Data.Read<PriceSources>(Path.Combine(profilesRoot, "prices", "sources.json"));
    // Shipped API models only: user-added models keep the prices the user typed, and CLIs have no token price.
    public List<PriceTarget> PriceTargets()
    {
        var pages = PriceSources().Pages;
        return Directory.GetFiles(Path.Combine(profilesRoot, "reviewers"), "*.json").Select(Data.Read<ReviewerProfile>)
            .Where(r => !ProviderCatalog.Get(r.HostingProvider).IsCli && pages.ContainsKey(r.HostingProvider))
            .Select(r => new PriceTarget(r.HostingProvider, r.ModelId, r.CanonicalModelKey, pages[r.HostingProvider]))
            .OrderBy(t => ProviderCatalog.Ids.ToList().IndexOf(t.Provider)).ThenBy(t => t.ModelId, StringComparer.Ordinal).ToList();
    }
    string PriceLookups => Path.Combine(DesktopDir, "prices", "lookups");
    // Claude Code and Codex read the pages independently and in parallel; the comparison is against the paid snapshot in use.
    // The whole lookup is saved to desktop\prices\lookups\<id>.json before it is shown, whatever the user decides.
    public async Task<PriceProposal> LookupPricesAsync(CancellationToken cancellation = default)
    {
        var targets = PriceTargets();var now = Now;
        var job = new ReviewGate.Transport.Cli.CliJob(PriceRefresh.System, PriceRefresh.Prompt(targets, now), PriceRefresh.Schema(), Web: true);
        var run = cliJob ?? ((provider, j, ct) => new ReviewGate.Transport.Cli.CliTransport(locateCli).RunJobAsync(provider, j, 900, ct));
        var raw = new System.Collections.Concurrent.ConcurrentDictionary<string, PriceRawAnswer>();
        async Task<PriceLookup> One(string provider)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await run(provider, job, cancellation);
                raw[provider] = new(result.Outcome, result.ErrorCode, result.Body, watch.Elapsed.TotalSeconds);
                return PriceRefresh.Read(provider, result);
            }
            catch (ReviewError e) { raw[provider] = new("error", e.Code, null, watch.Elapsed.TotalSeconds);return new(provider, "failed", e.Code, []); }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
            { raw.TryAdd(provider, new("unreadable", "invalid_payload", null, watch.Elapsed.TotalSeconds));return new(provider, "failed", "invalid_payload", []); }
        }
        var lookups = await Task.WhenAll(PriceRefresh.Clis.Select(One));
        string id = "lookup-" + now.ToOffset(TimeSpan.FromHours(9)).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        var proposal = PriceRefresh.Compare(targets, lookups, ShippedPrices(Path.Combine(profilesRoot, "prices"), false, now, ApprovedPrices), now) with { RecordId = id };
        Data.CreateFile(Path.Combine(PriceLookups, id + ".json"), Data.Json(new PriceLookupRecord(id, now, new(raw), proposal)));
        return proposal;
    }
    public List<PriceLookupRecord> PriceLookupHistory() => Directory.Exists(PriceLookups)
        ? Directory.GetFiles(PriceLookups, "lookup-*.json").Select(Data.Read<PriceLookupRecord>).OrderByDescending(r => r.LookedUpAt).ToList() : [];
    void Decide(PriceProposal proposal, string decision, string? snapshot = null, IEnumerable<string>? free = null)
    {
        if (proposal.RecordId is not string id) return;
        string file = Path.Combine(PriceLookups, id + ".json");
        if (!File.Exists(file)) return;
        Replace(file, Data.Json(Data.Read<PriceLookupRecord>(file) with { Decision = decision, DecidedAt = Now, Snapshot = snapshot, FreeTierModels = free?.Order().ToList() }));
    }
    public void CancelPrices(PriceProposal proposal) => Decide(proposal, "cancelled");
    // Writes the approved pair and its excerpt; from now on they are the snapshots in use until they expire.
    public PriceStatus ApprovePrices(PriceProposal proposal, IReadOnlySet<string> freeTierModels)
    {
        Data.Require(proposal.Rows.Any(r => r.Agreed), "no_agreed_prices", 12);
        var (paid, free, excerpt) = PriceRefresh.Build(proposal, freeTierModels, PriceSources(), Now);
        Data.CreateFile(Path.Combine(ApprovedPrices, paid.Id + ".md"), excerpt);
        Data.CreateFile(Path.Combine(ApprovedPrices, paid.Id + ".json"), Data.Json(paid));
        Data.CreateFile(Path.Combine(ApprovedPrices, free.Id + ".json"), Data.Json(free));
        Decide(proposal, "approved", paid.Id, freeTierModels);
        return PriceStatus();
    }
    // The shipped snapshot (paid, or with Google rows at the free tier) plus user rows, written content-addressed so a
    // plan's pinned price file never changes underneath it.
    public (PriceSnapshot Snapshot, string Path) MergedPrices()
    {
        var snapshot = ShippedPrices(Path.Combine(profilesRoot, "prices"), LoadSettings().GoogleFreeTier, Now, ApprovedPrices);
        var custom = File.Exists(CustomPricesFile) ? Data.Read<List<PriceRow>>(CustomPricesFile) : [];
        if (custom.Count > 0)
            snapshot = snapshot with
            {
                Id = snapshot.Id + "+user", Source = snapshot.Source + " ; user-entered rows: " + string.Join(", ", custom.Select(r => r.CanonicalModelKey)),
                DocumentSha256 = Data.Hash(Data.Json(custom)), Prices = [.. snapshot.Prices.Where(p => custom.All(c => c.CanonicalModelKey != p.CanonicalModelKey)), .. custom]
            };
        string json = Data.Json(snapshot), file = Path.Combine(DesktopDir, "prices", Data.Hash(json)[..16] + ".json");
        if (!File.Exists(file)) Data.CreateFile(file, json);
        return (snapshot, file);
    }

    // ---- Quick test: plan → preview → approve (desktop confirmation) → run.
    public QuickPreview PrepareQuickTest(QuickTestRequest request)
    {
        Data.Require(request.ConfirmedNonSensitive, "input_class_confirmation_required");
        Data.Require(request.ReviewerIds.Count is 1 or 2 && request.ReviewerIds.Distinct().Count() == request.ReviewerIds.Count, "reviewer_count_mismatch");
        Data.Require(request.ProfileKind is "code" or "document" or "novel", "invalid_profile");
        Data.Require(!string.IsNullOrWhiteSpace(request.Instruction), "instruction_required");
        var models = Models();
        ModelOption Find(string id) => models.SingleOrDefault(m => m.Id == id) ?? throw new ReviewError(2, "unknown_model");
        var reviewers = request.ReviewerIds.Select(Find).ToList();
        var fallbacks = (request.FallbackIds ?? []).Select(id => id is null ? null : Find(id)).ToList();
        Data.Require(fallbacks.Count <= reviewers.Count, "fallback_count_mismatch");
        Data.Require(reviewers.Concat(fallbacks.OfType<ModelOption>()).All(m => m.Priced), "price_missing", 12);
        string root; List<string> inputs;
        if (request.Text is { Length: > 0 } text)
        {
            // Pasted text becomes a one-file snapshot source in its own temp folder, outside the state root.
            root = Path.Combine(Path.GetTempPath(), "ReviewGate-quick", Guid.NewGuid().ToString("N"));
            Data.CreateFile(Path.Combine(root, "snippet.txt"), text);inputs = ["snippet.txt"];
        }
        else
        {
            string path = Path.GetFullPath(request.SourcePath ?? throw new ReviewError(2, "input_required"));
            if (Directory.Exists(path)) { root = path; inputs = ["."]; }
            else { Data.Require(File.Exists(path), "input_unavailable"); root = Path.GetDirectoryName(path)!; inputs = [Path.GetFileName(path)]; }
        }
        using var store = Open();
        var plan = Planner.Build(store, new(Project, root, null, "nonsensitive", request.Instruction, Path.Combine(profilesRoot, request.ProfileKind + ".json"),
            reviewers.Select(m => m.File).ToList(), Path.Combine(profilesRoot, "budget-live.json"), MergedPrices().Path, null, inputs, [], reviewers.Count == 2 ? "independent" : "single",
            FallbackReviewerFiles: fallbacks.Select(m => m?.File).ToList()));
        var warnings = new List<string>();
        foreach (var m in reviewers.Concat(fallbacks.OfType<ModelOption>()))
        {
            if (m.Free) warnings.Add($"{m.Provider} {m.ModelId}: 무료 티어 — 제공자가 보낸 내용을 제품 개선에 쓰고 사람이 검토할 수 있습니다. 비공개·회사 자료는 보내지 마세요.");
            if (m.Provider == "together") warnings.Add("Together: store 옵션을 무시합니다. 보관 여부는 Together 계정 설정을 따릅니다.");
            if (m.Custom) warnings.Add($"{m.ModelId}: 사용자가 추가한 모델이며 가격은 사용자가 입력한 값입니다.");
            if (ProviderCatalog.Get(m.Provider).IsCli)
                warnings.Add($"{ProviderCatalog.Get(m.Provider).DisplayName}: 로그인한 구독 계정으로 처리됩니다(학습 사용·보관은 그 계정 설정을 따름). 비용은 구독 사용량이며 ReviewGate는 token 수만 기록합니다. 샌드박스 없이 임시 폴더에서 실행됩니다.");
        }
        return new(plan.Id, plan.Mode, plan.Requests.Count, plan.Requests.Sum(r => r.ReservationUsd),
            reviewers.SelectMany((m, i) => new[] { $"{m.Provider} {m.ModelId} → {ProviderCatalog.Get(m.Provider).Endpoint}" }
                .Concat(i < fallbacks.Count && fallbacks[i] is { } f ? [$"   응답이 없으면 서브: {f.Provider} {f.ModelId} → {ProviderCatalog.Get(f.Provider).Endpoint}"] : Array.Empty<string>())).ToList(), warnings,
            plan.Sources.Count(s => s.Role == "target"), plan.Sources.Sum(s => (long)s.ByteLength), plan.Exclusions.Select(e => $"{e.Path} ({e.Reason})").ToList());
    }
    // The desktop confirmation dialog is the approval channel; like the CLI prompt it is a local confirmation,
    // not proof that a human approved it.
    public Grant Approve(string planId, decimal maxUsd, int expiresMinutes = 60)
    {
        Data.Require(maxUsd > 0 && expiresMinutes is >= 1 and <= 1440, "invalid_grant");
        using var store = Open();var plan = store.GetPlan(planId);
        var grant = new Grant(Data.Version, Data.Id("grant"), plan.Hash, plan.ManifestHash, store.Now.AddMinutes(expiresMinutes), maxUsd, plan.Requests.Count, 0, "live");
        store.RecordGrantIssue(plan, grant, "desktop_confirm");
        return grant;
    }
    public async Task<QuickResult> RunAsync(string planId, Grant grant, CancellationToken cancellation = default)
    {
        using var store = Open();var plan = store.GetPlan(planId);
        using var transport = new ReviewGate.Transport.Cli.RoutingTransport(new HttpTransport(credentials, handler?.Invoke()), cli?.Invoke() ?? new ReviewGate.Transport.Cli.CliTransport());
        var engine = new ReviewEngine(store, transport);
        try
        {
            var result = await engine.ExecuteAsync(plan, grant, cancellation: cancellation);
            return ResultFor(store, result.Result.RunId!, result.Result.RunErrorCode);
        }
        catch (ReviewError e) { return new(engine.CurrentRunId, "failed", e.Code, 0, 0, null, [], [], null); }
    }
    public QuickResult Result(string runId) { using var store = Open(); return ResultFor(store, runId, store.GetRun(runId).ErrorCode); }
    // Settles a run's unknown attempts through the engine's recover-only resolution. no_charge_confirmed records the
    // user's statement (free tier, provider error) as the basis; conservative_full_reservation books the reservation.
    public async Task<QuickResult> ResolveUnknownAsync(string runId, string outcome, string basis)
    {
        Data.Require(outcome is "no_charge_confirmed" or "conservative_full_reservation", "invalid_resolution");
        Data.Require(!string.IsNullOrWhiteSpace(basis), "invalid_resolution");
        using var store = Open();var run = store.GetRun(runId);var plan = store.GetPlan(run.PlanId);
        var items = store.Attempts(runId).Where(a => a.Phase == "unknown").Select(a => new ResolutionItem(a.Id, outcome, basis.Trim(), "desktop user", store.Now)).ToList();
        Data.Require(items.Count > 0, "nothing_to_resolve");
        await new ReviewEngine(store).ExecuteAsync(plan, null, runId, true, new ResolutionFixture(Data.Version, plan.Hash, items, "live"));
        return ResultFor(store, runId, store.GetRun(runId).ErrorCode);
    }
    static QuickResult ResultFor(StateStore store, string runId, string? error)
    {
        var run = store.GetRun(runId);var plan = store.GetPlan(run.PlanId);var units = store.Units(runId);var cost = store.Costs(runId);
        var findings = units.SelectMany(u => u.Findings).Select(f => new FindingView(f.ReviewerId, f.Kind, f.Severity,
            f.Line is null ? f.Path : $"{f.Path}:{f.Line}", f.Claim, f.Detail, f.Quote,
            !f.QuoteValid ? "인용을 원문에서 찾지 못함 (검증 안 됨)" : !f.LocationValid ? "인용 확인 · 모델이 적은 위치와 다름" : "인용·위치 확인")).ToList();
        var notes = plan.Requests.Select(q => (q, units.SingleOrDefault(u => u.RequestId == q.Id))).Where(x => x.Item2 is not { Valid: true })
            .Select(x => $"{x.q.ReviewerId}: {(x.Item2 is null ? "결과 없음" : $"{x.Item2.ValidationState} ({x.Item2.ErrorCode})")}").ToList();
        var report = store.Artifacts(runId).FirstOrDefault(a => a.Path.EndsWith("report.md", StringComparison.Ordinal));
        return new(runId, run.Status, error, cost["usage_calculated_usd"]!.GetValue<decimal>(), cost["unknown_hold_usd"]!.GetValue<decimal>(),
            ReviewEngine.CoverageFor(plan, units).ActualModelProcessingRatio, findings, notes, report is null ? null : Path.Combine(store.Root, report.Path));
    }
    public List<RunView> RecentRuns(int limit = 30)
    {
        using var store = Open();
        return store.ListRuns(limit).Select(r => { var c = store.Costs(r.Id); return new RunView(r.Id, r.ProjectId, r.Status, r.ErrorCode, r.CreatedAt, c["usage_calculated_usd"]!.GetValue<decimal>(), c["unknown_hold_usd"]!.GetValue<decimal>()); }).ToList();
    }
    static void Replace(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Data.CreateFile(tmp, content);File.Move(tmp, path, true);
    }
}
