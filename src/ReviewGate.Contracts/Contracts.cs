using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace ReviewGate.Contracts;

public sealed class ReviewError(int exitCode, string code, string? action = null) : Exception(code)
{
    public int ExitCode { get; } = exitCode;
    public string Code { get; } = code;
    public string? Action { get; } = action;
}

public static class Data
{
    public const string Version = "1.0";
    public static readonly UTF8Encoding Utf8 = new(false, true);
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static string Json<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Parse<T>(string value)
    {
        try { return JsonSerializer.Deserialize<T>(value, Options) ?? throw new ReviewError(2, "invalid_contract"); }
        catch (JsonException) { throw new ReviewError(2, "invalid_contract"); }
    }
    public static T Read<T>(string path)
    {
        try { Require(new FileInfo(path).Length<=10*1024*1024,"contract_size_limit");return Parse<T>(File.ReadAllText(path, Utf8)); }
        catch (IOException) { throw new ReviewError(2, "input_unavailable"); }
        catch (UnauthorizedAccessException) { throw new ReviewError(2, "input_unavailable"); }
        catch (DecoderFallbackException) { throw new ReviewError(2, "invalid_utf8"); }
    }
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string Hash(string text) => Hash(Utf8.GetBytes(text));
    public static string FileHash(string path) => Hash(File.ReadAllBytes(path));
    public static bool SourceCurrent(SourceFile source)
    {
        try
        {
            for(string? p=Path.GetFullPath(source.OriginalPath);p is not null;p=Path.GetDirectoryName(p))
                if((File.Exists(p)||Directory.Exists(p))&&File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint))return false;
            return File.Exists(source.OriginalPath)&&FileHash(source.OriginalPath)==source.RawSha256;
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){return false;}
    }
    public static string Id(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N");
    public static void Require([DoesNotReturnIf(false)] bool condition, string code = "invalid_contract", int exit = 2)
    { if (!condition) throw new ReviewError(exit, code); }
    public static void VersionIs(string version) => Require(version == Version, "unsupported_schema_version");
    public static void CreateFile(string path, string text) => CreateFile(path, Utf8.GetBytes(text));
    public static void CreateFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { f.Write(bytes); f.Flush(true); }
            File.Move(tmp, path, false);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}

public record Criterion(string Id, string Description, string Scope = "chunk");
public record ReviewProfile(string SchemaVersion, string Id, string Kind, List<Criterion> Criteria,
    int ChunkBytes = 8192, int ContextBytes = 256);
public record ReviewerProfile(string SchemaVersion, string Id, string HostingProvider, string ModelId,
    string ApiProfile, string CanonicalModelKey, string ModelDeveloper = "unknown", string ModelVersion = "",
    int InputTokenUpperBound = 4096, int MaxOutputTokens = 1024, int MaxReasoningTokens = 0,
    int MaxRequestBytes = 65536, string? ReasoningEffort = null, int TimeoutSeconds = 300);
public record BudgetPolicy(string SchemaVersion, decimal RunUsd, decimal ProjectUsd, decimal MonthUsd,
    int MaxRequests = 100, int MaxRetries = 0, string Currency = "USD", string Timezone = "+09:00");
public record PriceRow(string CanonicalModelKey, decimal InputPerMillion, decimal OutputPerMillion,
    decimal CachedInputPerMillion = 0, bool OutputIncludesReasoning = true, decimal ReasoningPerMillion = 0,
    string Tier = "standard", string Region = "global");
// Price validity: a snapshot is trusted only inside its effective window, because budget caps and settled costs depend
// on it. A plan whose every model has all-zero rates (free tier, CLI subscription) costs nothing at any price, so it is
// not stopped by the window; one paid model in the plan brings the window back.
public static class PriceRules
{
    public static bool IsZero(PriceRow r) => r is { InputPerMillion: 0, CachedInputPerMillion: 0, OutputPerMillion: 0 } && (r.OutputIncludesReasoning || r.ReasoningPerMillion == 0);
    public static bool AllZero(PriceSnapshot prices, IEnumerable<string> canonicalModelKeys) =>
        canonicalModelKeys.All(k => prices.Prices.SingleOrDefault(x => x.CanonicalModelKey == k) is { } row && IsZero(row));
    public static bool InWindow(PriceSnapshot prices, DateTimeOffset now) => prices.EffectiveFrom <= now && now < prices.EffectiveTo;
    public static bool Usable(Plan p, DateTimeOffset now) => InWindow(p.Prices, now) || AllZero(p.Prices, p.Reviewers.Select(r => r.CanonicalModelKey));
}
public record PriceSnapshot(string SchemaVersion, string Id, DateTimeOffset RetrievedAt,
    DateTimeOffset EffectiveFrom, DateTimeOffset EffectiveTo, string Source, string DocumentSha256,
    List<PriceRow> Prices, string Currency = "USD");
public record Usage(long InputTokens, long OutputTokens, long CachedInputTokens = 0,
    long ReasoningTokens = 0, bool Known = true);
public record FixtureFinding(string Claim, string Detail, string Quote, string SourceId = "$source",
    int? Line = null, int? StartByte = null, string Category = "logic", string Severity = "medium",
    string Kind = "defect", string ReportedEvidenceGrade = "unconfirmed", List<string>? Assumptions = null,
    string Confidence = "medium", string Suggestion = "", string Impact = "");
public record FixtureResponse(string Reviewer = "*", string Criterion = "*", string Source = "*",
    int Chunk = -1, string Status = "success", string Disposition = "no_finding", int DelayMs = 0,
    List<FixtureFinding>? Findings = null, Usage? Usage = null, JsonNode? WireBody = null, int HttpStatus = 200);
public record FixtureBundle(string SchemaVersion, string Id, List<FixtureResponse> Responses);
public record Grant(string SchemaVersion, string Id, string PlanHash, string ManifestHash,
    DateTimeOffset ExpiresAt, decimal MaxUsd, int MaxRequests, int MaxRetries = 0,
    string ExecutionMode = "synthetic", string? RunId = null);
public record ResolutionItem(string AttemptId, string Outcome, string Basis, string Actor,
    DateTimeOffset Timestamp, decimal? AmountUsd = null, Usage? Usage = null);
public record ResolutionFixture(string SchemaVersion, string PlanHash, List<ResolutionItem> Resolutions,
    string ExecutionMode = "synthetic");
public record InputPin(string Path, string Sha256);
public record Exclusion(string Path, string Reason);
public record SourceFile(string Id, string LogicalPath, string OriginalPath, string RawSha256,
    string RawPath, string TextPath, string TextSha256, int ByteLength, int BomBytes,
    List<int> RemovedCrOffsets, string Role, string? ExportPath);
public record Chunk(string Id, string SourceId, int Index, int Start, int End, int ContextStart, int ContextEnd);
public record SpanRef(string SourceId, int Start, int End);
public record RequestPlan(string Id, string UnitId, string ReviewerId, string CriterionId,
    string SourceId, int ChunkIndex, List<SpanRef> AllowedSpans, string Body, string BodyHash,
    decimal ReservationUsd, string Stage, string? FallbackFor = null);
public sealed record Plan
{
    public string SchemaVersion { get; init; } = Data.Version;
    public string Id { get; init; } = Data.Id("plan");
    public string Hash { get; set; } = "";
    public string ManifestHash { get; set; } = "";
    public string ProjectId { get; init; } = "";
    public string SourceRoot { get; init; } = "";
    public string? RepositoryRoot { get; init; }
    public string Instruction { get; init; } = "";
    public string InstructionHash { get; init; } = "";
    public string InputClass { get; init; } = "";
    public string Mode { get; init; } = "single";
    public string ExecutionMode { get; init; } = "synthetic";
    public string? BaseRun { get; init; }
    public List<string> FindingIds { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public required ReviewProfile Profile { get; init; }
    public required BudgetPolicy Budget { get; init; }
    public required PriceSnapshot Prices { get; init; }
    public required FixtureBundle Fixture { get; init; }
    public List<ReviewerProfile> Reviewers { get; init; } = [];
    // Primary reviewer id → pre-approved fallback reviewer id, used only when the primary gives no answer.
    public Dictionary<string, string> Fallbacks { get; init; } = [];
    public List<InputPin> Pins { get; init; } = [];
    public List<InputPin> DirectoryPins { get; init; } = [];
    public List<SourceFile> Sources { get; init; } = [];
    public List<Exclusion> Exclusions { get; init; } = [];
    public List<Chunk> Chunks { get; init; } = [];
    public List<RequestPlan> Requests { get; set; } = [];
}
public record Finding(string Id, string RequestId, string ReviewerId, string CriterionId, string SourceId,
    string Path, string? ExportPath, int? Line, int StartByte, int EndByte, long RawStart, long RawEnd,
    string Kind, string Category, string Severity, string Claim, string Detail, string Quote,
    string ReportedEvidenceGrade, List<string> Assumptions, string ExportedEvidenceGrade,
    bool QuoteValid, bool LocationValid, string Confidence, string Suggestion, string Impact,
    List<string> RelatedFindingIds, string CoordinateSystem = "text", int? DiffBeforeLine = null,
    int? DiffAfterLine = null, string? DiffOldPath = null, string? DiffNewPath = null);
public record UnitResult(string UnitId, string RequestId, string ReviewerId, string CriterionId,
    string SourceId, string Stage, string Exposure, string TransportState, string ValidationState,
    string ReviewDisposition, bool Valid, int CoreBytes, List<Finding> Findings, string? ErrorCode);
public record ReviewResponse(string Status, string Disposition, List<FixtureFinding> Findings, Usage Usage);
public record RunRow(string Id, string PlanId, string ProjectId, string Status, string Generation,
    string? ErrorCode, DateTimeOffset CreatedAt);
public record AttemptRow(string Id, string RunId, string RequestId, string GrantId, string Phase,
    decimal ReservationUsd, decimal? CostUsd, string Month, string? ResponsePath, string? ResponseHash,
    string? ErrorCode);
public record PairCoverage(string CriterionId, string ReviewerId, string Stage, int RequiredUnits, int ValidUnits,
    long TotalCoreBytes, long ValidCoreBytes, decimal? SyntheticProcessingRatio, decimal? ActualModelProcessingRatio);
public record Coverage(int RequiredUnits, int ValidUnits, long TotalCoreBytes, long ValidCoreBytes,
    decimal? SyntheticProcessingRatio, decimal? ActualModelProcessingRatio, bool RequiredCoverageComplete,
    List<string> MissingUnitIds, List<PairCoverage> ByCriterionReviewer, List<string> EmptySourceIds);
public record Artifact(string Path, string Sha256, long Bytes, string Kind);
public sealed record Envelope
{
    public string SchemaVersion { get; init; } = Data.Version;
    public string Command { get; init; } = "";
    public string ExecutionMode { get; init; } = "synthetic";
    public string? RunId { get; init; }
    public string? PlanId { get; init; }
    public string Status { get; init; } = "failed";
    public string? ErrorCode { get; init; }
    public string? RunErrorCode { get; init; }
    public string? RequiredAction { get; init; }
    public bool? OwnerAlive { get; init; }
    public bool CancelRequested { get; init; }
    public bool AlreadyTerminal { get; init; }
    public List<Artifact> Artifacts { get; init; } = [];
    public Coverage? Coverage { get; init; }
    public List<string> StaleSourceIds {get;init;}=[];
    public JsonNode? Cost { get; init; }
    public JsonObject? Details { get; init; }
    public List<string> Limitations { get; init; } = ["synthetic_only", "no_actual_model_review", "not_an_hglos_round"];
    public static readonly List<string> LiveLimitations = ["live_model_review", "review_quality_not_verified", "usage_based_cost_not_invoice", "not_an_hglos_round"];
}
// Transport for live dispatch. Received means the provider answered (any HTTP status); Unknown means
// the request may have been sent but no complete answer was received (timeout, connection loss, oversize).
public record TransportResult(string Outcome, int StatusCode, string? Body, string? ErrorCode)
{
    public static TransportResult Unknown(string code) => new("unknown", 0, null, code);
}
public interface IModelTransport
{
    bool HasCredential(string provider);
    Task<TransportResult> SendAsync(ReviewerProfile reviewer, string body, CancellationToken cancellation);
}
// A provider can hold several named keys; exactly one is active and used for requests. Values are never listed.
public record CredentialEntry(string Id, string Name, bool Active, DateTimeOffset? Saved);
public interface ICredentialStore
{
    string? Read(string provider);
    IReadOnlyList<CredentialEntry> List(string provider);
    string Add(string provider, string name, string secret);
    bool Remove(string provider, string id);
    void Activate(string provider, string id);
}
// Raw secret storage (Windows Credential Manager in the product, memory in tests). Targets are opaque names.
public record VaultItem(string Target, string UserName, DateTimeOffset? Written);
public interface ICredentialVault
{
    string? Read(string target);
    void Write(string target, string userName, string secret);
    bool Delete(string target);
    IReadOnlyList<VaultItem> Enumerate(string prefix);
}
// Every provider the product can call. Adding a provider means adding one entry here plus its wire adapter;
// the CLI, key screens and model screens read this list instead of hard-coding provider names.
// Kind "http": an API called with a stored key at a fixed endpoint. Kind "cli": a locally installed agent CLI run
// headless with its own login; Endpoint is then the executable file name.
public record ProviderInfo(string Id, string DisplayName, string ApiProfile, string Endpoint, string KeyHeader, string Kind = "http")
{
    public bool IsCli => Kind == "cli";
}
public static class ProviderCatalog
{
    public static readonly IReadOnlyList<ProviderInfo> All =
    [
        new("openai", "OpenAI", "responses", "https://api.openai.com/v1/responses", "Authorization: Bearer"),
        new("together", "Together AI", "chat_completions", "https://api.together.ai/v1/chat/completions", "Authorization: Bearer"),
        new("google", "Google AI", "interactions", "https://generativelanguage.googleapis.com/v1beta/interactions", "x-goog-api-key"),
        new("claude-cli", "Claude Code CLI", "claude_cli", "claude.exe", "login", "cli"),
        new("codex-cli", "Codex CLI", "codex_cli", "codex.exe", "login", "cli"),
        new("agy-cli", "Antigravity CLI", "agy_cli", "agy.exe", "login", "cli"),
    ];
    public static IEnumerable<string> Ids => All.Select(p => p.Id);
    public static ProviderInfo Get(string id) => All.SingleOrDefault(p => p.Id == id) ?? throw new ReviewError(2, "unsupported_provider");
}
public record CommandResult(int ExitCode, Envelope Result, string? OutputPath = null);
