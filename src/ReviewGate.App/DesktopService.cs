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
// A headless review request: 1 or 2 reviewers by provider (model optional), the folder and the inputs within it.
public record ReviewerPick(string Provider, string? Model)
{
    // "openai", "openai=gpt-6.1-sol", "openai=*" (policies only).
    public static ReviewerPick Parse(string text) => text.Split('=', 2) is [var p, var m] ? new(p.Trim(), m.Trim() is { Length: > 0 } x ? x : null) : new(text.Trim(), null);
}
public record ReviewRequest(List<ReviewerPick> Reviewers, string Instruction, string SourceRoot, List<string>? Inputs = null, List<string>? References = null,
    string? RepositoryRoot = null, string ProfileKind = "code", bool UseFallbacks = true, string? Project = null);
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
// One API worker call: answer status (success, truncated, refused, error, unknown, over_cost_cap), the answer text,
// the settled cost, the reservation it was held against, how the cost was decided, and the reported usage.
public record ApiJobResult(string Status, string Text, decimal CostUsd, decimal ReservationUsd, string Basis, Usage? Usage);
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
    public static PriceSnapshot ShippedPrices(string folder, bool googleFree, DateTimeOffset now, string? approvedFolder = null) =>
        PriceSnapshots.Select(folder, googleFree, now, approvedFolder).Snapshot;

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
        return Prepare(Project, reviewers, fallbacks, request.ProfileKind, request.Instruction, root, null, inputs, []);
    }

    // ---- Headless review (agents): reviewers by provider with the configured default model, or a named model.
    // Resolves one pick: no model → the provider's default model in settings; a model → that model of the provider
    // (reviewer id such as "openai-gpt-6.1-sol" or model id such as "gpt-6.1-sol"). The fallback is the provider's
    // configured fallback, unless it is turned off or would duplicate a primary.
    public List<(ModelOption Primary, ModelOption? Fallback)> ResolveReviewers(IReadOnlyList<ReviewerPick> picks, bool useFallbacks = true)
    {
        Data.Require(picks.Count is 1 or 2, "reviewer_count_mismatch");
        var settings = LoadSettings();var models = Models();
        ModelOption ById(string id) => models.SingleOrDefault(m => m.Id == id) ?? throw new ReviewError(2, "unknown_model");
        var primaries = picks.Select(p =>
        {
            string provider = ProviderCatalog.Get(p.Provider).Id;
            if (p.Model is not string named)
                return ById(settings.DefaultModels.TryGetValue(provider, out var d) ? d : throw new ReviewError(2, "no_default_model"));
            return models.FirstOrDefault(m => m.Provider == provider && (m.Id == named || string.Equals(m.ModelId, named, StringComparison.OrdinalIgnoreCase)))
                ?? throw new ReviewError(2, "unknown_model");
        }).ToList();
        return primaries.Select(m =>
        {
            ModelOption? fallback = useFallbacks && settings.FallbackModels?.TryGetValue(m.Provider, out var f) == true ? models.SingleOrDefault(x => x.Id == f) : null;
            if (fallback is not null && primaries.Any(p => p.Id == fallback.Id)) fallback = null;
            return (m, fallback);
        }).ToList();
    }
    public QuickPreview PrepareReview(ReviewRequest request)
    {
        Data.Require(!string.IsNullOrWhiteSpace(request.Instruction), "instruction_required");
        Data.Require(request.ProfileKind is "code" or "document" or "novel", "invalid_profile");
        var picked = ResolveReviewers(request.Reviewers, request.UseFallbacks);
        string root = Path.GetFullPath(request.SourceRoot);Data.Require(Directory.Exists(root), "input_unavailable");
        string folderName = Regex.Replace(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)), "[^A-Za-z0-9_-]", "-");folderName = folderName[..Math.Min(folderName.Length, 80)];
        string project = request.Project ?? (folderName.Length > 0 ? folderName : "review");
        return Prepare(project, picked.Select(p => p.Primary).ToList(), picked.Select(p => p.Fallback).ToList(), request.ProfileKind, request.Instruction,
            root, request.RepositoryRoot is null ? null : Path.GetFullPath(request.RepositoryRoot), request.Inputs is { Count: > 0 } i ? i : ["."], request.References ?? []);
    }
    QuickPreview Prepare(string project, List<ModelOption> reviewers, List<ModelOption?> fallbacks, string profileKind, string instruction, string root, string? repositoryRoot, List<string> inputs, List<string> references)
    {
        Data.Require(reviewers.Concat(fallbacks.OfType<ModelOption>()).All(m => m.Priced), "price_missing", 12);
        using var store = Open();
        var plan = Planner.Build(store, new(project, root, repositoryRoot, "nonsensitive", instruction, Path.Combine(profilesRoot, profileKind + ".json"),
            reviewers.Select(m => m.File).ToList(), Path.Combine(profilesRoot, "budget-live.json"), MergedPrices().Path, null, inputs, references, reviewers.Count == 2 ? "independent" : "single",
            FallbackReviewerFiles: fallbacks.Select(m => m?.File).ToList()));
        var warnings = new List<string>();
        foreach (var m in reviewers.Concat(fallbacks.OfType<ModelOption>()))
        {
            if (m.Free) warnings.Add($"{m.Provider} {m.ModelId}: 무료 티어 — 제공자가 보낸 내용을 제품 개선에 쓰고 사람이 검토할 수 있습니다. 비공개·회사 자료는 보내지 마세요.");
            if (m.Provider == "together") warnings.Add("Together: store 옵션을 무시합니다. 보관 여부는 Together 계정 설정을 따릅니다.");
            if (m.Custom) warnings.Add($"{m.ModelId}: 사용자가 추가한 모델이며 가격은 사용자가 입력한 값입니다.");
            if (ProviderCatalog.Get(m.Provider).IsCli)
                warnings.Add($"{ProviderCatalog.Get(m.Provider).DisplayName}: 로그인한 구독 계정으로 처리됩니다(학습 사용·보관은 그 계정 설정을 따름). 비용은 구독 사용량이며 ReviewGate는 token 수만 기록합니다. 샌드박스 없이 검수 폴더에서 실행되며 주변 파일을 읽을 수 있습니다.");
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
    // ---- Standing policy for headless callers.
    public StandingPolicy? ActivePolicy() { using var store = Open(); return store.ActivePolicy(); }
    // A draft to show before issuing. Picks are expanded to concrete reviewers now: a provider alone means its
    // default and fallback models in settings; "provider=*" means every priced model of that provider.
    public StandingPolicy DraftPolicy(IReadOnlyList<string> sourceRoots, IReadOnlyList<ReviewerPick>? reviewers, decimal maxRunUsd, int maxRequestsPerRun, int days)
    {
        Data.Require(sourceRoots.Count > 0 && maxRunUsd >= 0 && maxRequestsPerRun > 0 && days is >= 1 and <= 365, "invalid_policy");
        var roots = sourceRoots.Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Data.Require(roots.All(Directory.Exists), "input_unavailable");
        var settings = LoadSettings();var models = Models().Where(m => m.Priced).ToList();
        var picks = reviewers is { Count: > 0 } ? reviewers : ProviderCatalog.All.Select(p => new ReviewerPick(p.Id, null)).ToList();
        var allowed = new List<ModelOption>();
        foreach (var pick in picks)
        {
            string provider = ProviderCatalog.Get(pick.Provider).Id;
            if (pick.Model == "*") allowed.AddRange(models.Where(m => m.Provider == provider));
            else if (pick.Model is null)
            {
                foreach (var id in new[] { settings.DefaultModels.GetValueOrDefault(provider), settings.FallbackModels?.GetValueOrDefault(provider) }.OfType<string>())
                    if (models.SingleOrDefault(m => m.Id == id) is { } m) allowed.Add(m);
            }
            else allowed.Add(models.FirstOrDefault(m => m.Provider == provider && (m.Id == pick.Model || string.Equals(m.ModelId, pick.Model, StringComparison.OrdinalIgnoreCase)))
                ?? throw new ReviewError(2, "unknown_model"));
        }
        Data.Require(allowed.Count > 0, "invalid_policy");
        return new(Data.Version, Data.Id("policy"), Now, Now.AddDays(days), roots,
            allowed.DistinctBy(m => m.Id).Select(m => new AllowedReviewer(Path.GetFileNameWithoutExtension(m.File), m.CanonicalModelKey)).ToList(), maxRunUsd, maxRequestsPerRun, ["nonsensitive"]);
    }
    public void IssuePolicy(StandingPolicy policy, string channel) { using var store = Open(); store.RecordPolicy(policy, channel); }
    public bool RevokePolicy() { using var store = Open(); return store.RevokePolicies(); }
    // A plan inside the active policy gets its grant without asking; otherwise every reason is returned.
    public (Grant? Grant, List<string> Violations) ApproveByPolicy(string planId, IReadOnlyList<string>? alsoAllowed = null)
    {
        using var store = Open();var plan = store.GetPlan(planId);var policy = store.ActivePolicy();
        var reasons = PolicyGate.Violations(policy, plan, store.Now, alsoAllowed);
        return reasons.Count > 0 ? (null, reasons) : (PolicyGate.Issue(store, policy!, plan, alsoAllowed), []);
    }
    // ---- API implementation worker: one structured call outside review plans, on the live budget.
    // The reservation (worst case for this request) must fit maxUsd, the run cap and the month cap. The call is
    // settled from the provider's usage; an unknown outcome is settled at the full reservation.
    public decimal ApiJobReservation(ModelOption model, string body)
    {
        var profile = Data.Read<ReviewerProfile>(model.File);var prices = MergedPrices().Snapshot;
        var row = prices.Prices.SingleOrDefault(x => x.CanonicalModelKey == profile.CanonicalModelKey) ?? throw new ReviewError(12, "price_missing");
        Data.Require(PriceRules.InWindow(prices, Now) || PriceRules.IsZero(row), "price_snapshot_expired", 12);
        return WireAdapters.Reservation(profile, row, body);
    }
    public async Task<ApiJobResult> RunApiJobAsync(string implementId, ModelOption model, string system, string prompt, System.Text.Json.Nodes.JsonObject schema, decimal maxUsd, CancellationToken cancellation = default)
    {
        var profile = Data.Read<ReviewerProfile>(model.File);
        string body = WireAdapters.BuildJob(profile, system, prompt, schema, "reviewgate_edits");
        decimal reserve = ApiJobReservation(model, body);
        var row = MergedPrices().Snapshot.Prices.Single(x => x.CanonicalModelKey == profile.CanonicalModelKey);
        if (reserve > maxUsd) return new("over_cost_cap", "", 0, reserve, "not_sent", null);
        var budget = Data.Read<BudgetPolicy>(Path.Combine(profilesRoot, "budget-live.json"));
        using var transport = new HttpTransport(credentials, handler?.Invoke());
        Data.Require(transport.HasCredential(profile.HostingProvider), "credential_missing", 11);
        string id;
        using (var store = Open()) { id = store.ReserveImplementCall(implementId, profile.HostingProvider, profile.CanonicalModelKey, reserve, budget);store.DispatchImplementCall(id); }
        ApiJobResult Settle(string status, string text, decimal cost, string basis, Usage? usage)
        { using var store = Open();store.SettleImplementCall(id, cost, basis);return new(status, text, cost, reserve, basis, usage); }
        TransportResult sent;
        try { sent = await transport.SendAsync(profile, body, cancellation); }
        catch (OperationCanceledException) { Settle("unknown", "", reserve, "conservative_full_reservation: cancelled_in_flight", null);throw; }
        if (sent.Outcome != "received" || sent.Body is null) return Settle("unknown", "", reserve, "conservative_full_reservation: " + (sent.ErrorCode ?? "no_response"), null);
        try
        {
            var (status, text, usage) = WireAdapters.ReadJob(profile, sent.Body, sent.StatusCode);
            if (usage.Known) return Settle(status, text, WireAdapters.Cost(usage, row), "provider_usage", usage);
            // An HTTP error without usage is not billed; any other answer without usage is booked at the reservation.
            return sent.StatusCode >= 400 ? Settle(status, text, 0, "http_error_no_usage", usage) : Settle(status, text, reserve, "conservative_full_reservation: usage_missing", usage);
        }
        catch (ReviewError e) { return Settle("error", "", reserve, "conservative_full_reservation: " + e.Code, null); }
    }

    // ---- Implementation policy (see Implementer).
    public ImplementPolicy? ActiveImplementPolicy() { using var store = Open(); return store.ActiveImplementPolicy(); }
    public void IssueImplementPolicy(ImplementPolicy policy, string channel) { using var store = Open(); store.RecordImplementPolicy(policy, channel); }
    public bool RevokeImplementPolicy() { using var store = Open(); return store.RevokeImplementPolicies(); }
    // Workers are picked like reviewers: a provider alone means its default model in settings. API workers need a
    // per-call cost cap (maxWorkerUsd); without any API worker it stays null.
    public ImplementPolicy DraftImplementPolicy(IReadOnlyList<string> repositories, IReadOnlyList<ReviewerPick> workers, IReadOnlyList<string> checks, int maxMinutes, int days, decimal? maxWorkerUsd = null)
    {
        Data.Require(repositories.Count > 0 && workers.Count > 0 && maxMinutes is >= 1 and <= 240 && days is >= 1 and <= 365, "invalid_policy");
        Data.Require(checks.All(c => !string.IsNullOrWhiteSpace(c) && c.Length <= 400 && !c.Contains('\n') && !c.Contains('\r')), "invalid_policy");
        var repos = repositories.Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Data.Require(repos.All(Directory.Exists), "input_unavailable");
        Data.Require(workers.All(w => w.Model != "*"), "invalid_worker");
        bool api = workers.Any(w => !ProviderCatalog.Get(w.Provider).IsCli);
        Data.Require(maxWorkerUsd is null or >= 0, "invalid_policy");
        var allowed = workers.Select(w => ResolveReviewers([w], false)[0].Primary).DistinctBy(m => m.Id)
            .Select(m => new AllowedReviewer(Path.GetFileNameWithoutExtension(m.File), m.CanonicalModelKey)).ToList();
        return new(Data.Version, Data.Id("ipolicy"), Now, Now.AddDays(days), repos, allowed, checks.Select(c => c.Trim()).Distinct().ToList(), maxMinutes, api ? maxWorkerUsd ?? 1m : null);
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
