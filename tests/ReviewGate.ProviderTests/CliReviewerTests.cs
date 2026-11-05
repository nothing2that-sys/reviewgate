using System.Net;
using System.Text.Json.Nodes;
using ReviewGate.Contracts;
using ReviewGate.Core.Execution;
using ReviewGate.Core.Planning;
using ReviewGate.Infrastructure;
using ReviewGate.Providers;
using ReviewGate.Testing;
using ReviewGate.Transport.Cli;
using ReviewGate.Transport.Http;

namespace ReviewGate.ProviderTests;

// Agent CLIs (Claude Code, Codex, Antigravity) as reviewers: their raw outputs are normalized by the CLI
// transport and parsed like any other provider. No process is started here; the engine tests use a fake CLI.
public sealed class CliReviewerTests
{
    static readonly DateTimeOffset Clock = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    static string Profiles => Path.Combine(TestEnvironment.Repository, "profiles");
    static string ReviewerFile(string id) => Path.Combine(Profiles, "reviewers", id + ".json");
    static ReviewerProfile Shipped(string id) => Data.Read<ReviewerProfile>(ReviewerFile(id));
    const string NoFinding = "{\"status\":\"success\",\"disposition\":\"no_finding\",\"findings\":[]}";

    // Shapes as captured from the installed CLIs on 2026-11-05 (values shortened).
    const string ClaudeOk = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"duration_ms\":5123,\"num_turns\":2,\"result\":\"\",\"session_id\":\"s\",\"total_cost_usd\":0.0123," +
        "\"usage\":{\"input_tokens\":3,\"cache_creation_input_tokens\":5000,\"cache_read_input_tokens\":12000,\"output_tokens\":150,\"service_tier\":\"standard\"}," +
        "\"modelUsage\":{\"claude-sonnet-5-5\":{\"inputTokens\":3,\"outputTokens\":150}},\"structured_output\":" + NoFinding + "}";
    const string CodexOk = "{\"type\":\"thread.started\",\"thread_id\":\"t\"}\n{\"type\":\"turn.started\"}\n" +
        "{\"type\":\"item.completed\",\"item\":{\"id\":\"item_0\",\"type\":\"error\",\"message\":\"unknown config key in config.toml\"}}\n" +
        "{\"type\":\"item.completed\",\"item\":{\"id\":\"item_1\",\"type\":\"agent_message\",\"text\":\"done\"}}\n" +
        "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":8000,\"cached_input_tokens\":3000,\"output_tokens\":200,\"reasoning_output_tokens\":64}}\n";

    [Fact] public void ClaudeResultIsNormalizedWithCacheTokensModelAndCost()
    {
        var n = CliTransport.FromClaude(ClaudeOk)!;
        Assert.Equal("completed", n["status"]!.GetValue<string>());Assert.Null(n["error"]);
        Assert.Equal(17003, n["usage"]!["input_tokens"]!.GetValue<long>());Assert.Equal(12000, n["usage"]!["cached_input_tokens"]!.GetValue<long>());Assert.Equal(150, n["usage"]!["output_tokens"]!.GetValue<long>());
        Assert.Equal("claude-sonnet-5-5", n["model"]!.GetValue<string>());Assert.Equal(0.0123m, n["estimated_cost_usd"]!.GetValue<decimal>());
        var limited = CliTransport.FromClaude("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"api_error_status\":429,\"result\":\"API Error\"}")!;
        Assert.Equal(("failed", "rate_limited"), (limited["status"]!.GetValue<string>(), limited["error"]!.GetValue<string>()));
        Assert.Null(CliTransport.FromClaude("not json"));
    }
    [Fact] public void CodexConfigWarningIsNotAFailureButAFailedTurnIs()
    {
        var n = CliTransport.FromCodex(CodexOk, NoFinding)!;
        Assert.Equal("completed", n["status"]!.GetValue<string>());Assert.Equal("no_finding", n["structured_output"]!["disposition"]!.GetValue<string>());
        Assert.Equal((8000L, 3000L, 200L, 64L), (n["usage"]!["input_tokens"]!.GetValue<long>(), n["usage"]!["cached_input_tokens"]!.GetValue<long>(), n["usage"]!["output_tokens"]!.GetValue<long>(), n["usage"]!["thinking_tokens"]!.GetValue<long>()));
        var failed = CliTransport.FromCodex("{\"type\":\"turn.started\"}\n{\"type\":\"turn.failed\",\"error\":{\"message\":\"You've hit your usage limit.\"}}\n", null)!;
        Assert.Equal(("failed", "rate_limited"), (failed["status"]!.GetValue<string>(), failed["error"]!.GetValue<string>()));
        Assert.Null(CliTransport.FromCodex("", null));
    }
    [Fact] public void AgySuccessWithoutStructuredOutputIsAnInvalidReviewNotAnEmptyOne()
    {
        var prose = CliTransport.FromAgy("{\"status\":\"SUCCESS\",\"result\":\"Here is my review in prose.\",\"usage\":{\"input_tokens\":100,\"output_tokens\":20}}")!;
        Assert.Equal("completed", prose["status"]!.GetValue<string>());Assert.Null(prose["structured_output"]);
        Assert.Equal(WireAdapters.InvalidPayload, WireAdapters.Parse(Shipped("agy-cli"), prose.ToJsonString()).Status);
        var ok = CliTransport.FromAgy("{\"status\":\"SUCCESS\",\"structured_output\":" + NoFinding + ",\"usage\":{\"input_tokens\":100,\"output_tokens\":20,\"thinking_tokens\":7}}")!;
        var parsed = WireAdapters.Parse(Shipped("agy-cli"), ok.ToJsonString());
        Assert.Equal(("success", "no_finding"), (parsed.Status, parsed.Disposition));Assert.Equal((100L, 20L, 7L, true), (parsed.Usage.InputTokens, parsed.Usage.OutputTokens, parsed.Usage.ReasoningTokens, parsed.Usage.Known));
        var failed = CliTransport.FromAgy("{\"status\":\"ERROR\",\"error\":\"not logged in\"}")!;
        Assert.Equal("unauthorized", WireAdapters.Parse(Shipped("agy-cli"), failed.ToJsonString()).Status);
    }
    [Fact] public void CliRequestCarriesSchemaAndLeavesDefaultModelToTheCli()
    {
        using var e = new TestEnvironment();using var store = new StateStore(e.State, () => Clock, "live");
        var plan = Planner.Build(store, new("cli", e.Source, e.Source, "nonsensitive", "Review supplied source", Path.Combine(Profiles, "code.json"), [ReviewerFile("claude-cli")], Path.Combine(Profiles, "budget-live.json"), Path.Combine(Profiles, "prices", "live-2026-11-04.json"), null, ["Sample.cs"], []));
        var body = JsonNode.Parse(Assert.Single(plan.Requests).Body)!;
        Assert.Equal("claude-cli", body["cli"]!.GetValue<string>());Assert.Null(body["model"]);Assert.NotNull(body["schema"]);Assert.Contains("Sample.cs", body["prompt"]!.GetValue<string>());
        // The CLI runs in the reviewed folder and is told it may read around but not change anything.
        Assert.Equal(plan.SourceRoot, body["workdir"]!.GetValue<string>());Assert.Equal(WireAdapters.CliSystemInstruction, body["system"]!.GetValue<string>());
        Assert.Equal(0m, plan.Requests[0].ReservationUsd);
    }

    // Captures the command line instead of starting the CLI; returning no process ends the send with cli_start_failed.
    static async Task<System.Diagnostics.ProcessStartInfo> CommandLine(string provider, string? workdir)
    {
        System.Diagnostics.ProcessStartInfo? seen = null;string? requestText = null;
        var transport = new CliTransport(exe => @"C:ake\" + exe, psi =>
        {
            seen = psi;
            string temp = psi.ArgumentList.Contains("-o") ? Path.GetDirectoryName(psi.ArgumentList[psi.ArgumentList.IndexOf("-o") + 1])! : psi.WorkingDirectory;
            if (File.Exists(Path.Combine(temp, "review-request.md"))) requestText = File.ReadAllText(Path.Combine(temp, "review-request.md"));
            return null;
        });
        string body = new JsonObject { ["cli"] = provider, ["model"] = null, ["workdir"] = workdir, ["system"] = "SYSTEM", ["prompt"] = "PROMPT", ["schema"] = new JsonObject { ["type"] = "object" } }.ToJsonString();
        Assert.Equal("cli_start_failed", (await Assert.ThrowsAsync<ReviewError>(() => transport.SendAsync(Shipped(provider), body, CancellationToken.None))).Code);
        return seen!;
    }
    [Fact] public async Task CliRunsInTheProjectFolderAndKeepsItsOwnFilesInTemp()
    {
        using var e = new TestEnvironment();string project = e.Source, temp = Path.Combine(Path.GetTempPath(), "ReviewGate-cli");
        var before = Directory.GetFileSystemEntries(project, "*", SearchOption.AllDirectories).Order().ToList();

        var claude = await CommandLine("claude-cli", project);
        Assert.Equal(project, claude.WorkingDirectory);Assert.Equal(CliTransport.ReadOnlyTools, claude.ArgumentList[claude.ArgumentList.IndexOf("--tools") + 1]);
        var codex = await CommandLine("codex-cli", project);
        Assert.Equal(project, codex.WorkingDirectory);Assert.Equal(project, codex.ArgumentList[codex.ArgumentList.IndexOf("-C") + 1]);
        Assert.StartsWith(temp, codex.ArgumentList[codex.ArgumentList.IndexOf("-o") + 1]);Assert.StartsWith(temp, codex.ArgumentList[codex.ArgumentList.IndexOf("--output-schema") + 1]);
        var agy = await CommandLine("agy-cli", project);
        string added = agy.ArgumentList[agy.ArgumentList.IndexOf("--add-dir") + 1];
        Assert.Equal(project, agy.WorkingDirectory);Assert.StartsWith(temp, added);Assert.Contains(Path.Combine(added, "review-request.md"), agy.ArgumentList[0]);
        // Nothing was written into the project, and every temp folder is gone.
        Assert.Equal(before, Directory.GetFileSystemEntries(project, "*", SearchOption.AllDirectories).Order().ToList());
        Assert.False(Directory.Exists(added));Assert.False(Directory.Exists(Path.GetDirectoryName(codex.ArgumentList[codex.ArgumentList.IndexOf("-o") + 1])));
    }
    [Fact] public async Task WithoutAProjectFolderTheCliRunsInAnEmptyTempFolderWithNoTools()
    {
        string temp = Path.Combine(Path.GetTempPath(), "ReviewGate-cli");
        var claude = await CommandLine("claude-cli", null);
        Assert.StartsWith(temp, claude.WorkingDirectory);Assert.Equal("", claude.ArgumentList[claude.ArgumentList.IndexOf("--tools") + 1]);
        var gone = await CommandLine("codex-cli", Path.Combine(temp, "missing-" + Guid.NewGuid().ToString("N")));
        Assert.StartsWith(temp, gone.WorkingDirectory);Assert.Equal(gone.WorkingDirectory, gone.ArgumentList[gone.ArgumentList.IndexOf("-C") + 1]);
        var agy = await CommandLine("agy-cli", null);Assert.DoesNotContain("--add-dir", agy.ArgumentList);
        Assert.False(Directory.Exists(claude.WorkingDirectory));
    }

    sealed class FakeCli(Func<ReviewerProfile, TransportResult> answer) : IModelTransport
    {
        public List<string> Sent { get; } = [];
        public bool HasCredential(string provider) => ProviderCatalog.Get(provider).IsCli;
        public Task<TransportResult> SendAsync(ReviewerProfile reviewer, string body, CancellationToken cancellation) { Sent.Add(reviewer.HostingProvider); return Task.FromResult(answer(reviewer)); }
    }
    sealed class OneKey : ICredentialStore
    {
        readonly KeyRing ring = new(new MemoryVault());
        public OneKey(string provider) => ring.Add(provider, "test", "TEST_ONLY_CLI_MIX_KEY_0123456789");
        public string? Read(string provider) => ring.Read(provider);
        public IReadOnlyList<CredentialEntry> List(string provider) => ring.List(provider);
        public string Add(string provider, string name, string secret) => ring.Add(provider, name, secret);
        public bool Remove(string provider, string id) => ring.Remove(provider, id);
        public void Activate(string provider, string id) => ring.Activate(provider, id);
    }
    sealed class Http(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Sent { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Sent++; return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }); }
    }
    static TransportResult CliAnswer(ReviewerProfile r, string? structured = NoFinding, string status = "completed", string? error = null) =>
        new("received", 200, new JsonObject
        {
            ["cli"] = r.HostingProvider, ["status"] = status, ["error"] = error, ["structured_output"] = structured is null ? null : JsonNode.Parse(structured),
            ["usage"] = new JsonObject { ["input_tokens"] = 900, ["cached_input_tokens"] = 0, ["output_tokens"] = 40, ["thinking_tokens"] = 0 }, ["model"] = null, ["exit_code"] = 0
        }.ToJsonString(), null);
    static Grant Issue(StateStore store, Plan plan)
    {
        var g = new Grant(Data.Version, Data.Id("grant"), plan.Hash, plan.ManifestHash, Clock.AddHours(1), 1, 10, 0, "live");
        store.RecordGrantIssue(plan, g, "test");return g;
    }
    static string OpenAiAnswer => new JsonObject
    {
        ["status"] = "completed",
        ["output"] = new JsonArray(new JsonObject { ["type"] = "message", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = NoFinding }) }),
        ["usage"] = new JsonObject { ["input_tokens"] = 120, ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 }, ["output_tokens"] = 30, ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 }, ["total_tokens"] = 150 }
    }.ToJsonString();

    [Fact] public async Task ApiAndCliReviewersRunTogetherInOneLiveRun()
    {
        using var e = new TestEnvironment();using var store = new StateStore(e.State, () => Clock, "live");
        var plan = Planner.Build(store, new("mix", e.Source, e.Source, "nonsensitive", "Review supplied source", Path.Combine(Profiles, "code.json"), [ReviewerFile("openai-gpt-6-luna"), ReviewerFile("claude-cli")], Path.Combine(Profiles, "budget-live.json"), Path.Combine(Profiles, "prices", "live-2026-11-04.json"), null, ["Sample.cs"], [], Mode: "independent"));
        var http = new Http(HttpStatusCode.OK, OpenAiAnswer);var cli = new FakeCli(r => CliAnswer(r));
        using var transport = new RoutingTransport(new HttpTransport(new OneKey("openai"), http), cli);
        var r = await new ReviewEngine(store, transport).ExecuteAsync(plan, Issue(store, plan));
        Assert.Equal(0, r.ExitCode);Assert.Equal("completed", r.Result.Status);Assert.Equal(1m, r.Result.Coverage!.ActualModelProcessingRatio);
        Assert.Equal(1, http.Sent);Assert.Equal(new[] { "claude-cli" }, cli.Sent);
        // The CLI runs on the subscription: its attempt is settled at $0, only the API call costs money.
        Assert.Equal((120 * 0.10m + 30 * 0.50m) / 1_000_000m, store.Attempts().Sum(a => a.CostUsd ?? 0));Assert.All(store.Attempts(), a => Assert.Equal("settled", a.Phase));
    }
    [Fact] public async Task CliReviewerStandsInWhenTheApiPrimaryGivesNoAnswer()
    {
        // Free Google primary is busy (503); Codex CLI is its fallback and answers.
        using var e = new TestEnvironment();using var store = new StateStore(e.State, () => Clock, "live");
        var plan = Planner.Build(store, new("fallback", e.Source, e.Source, "nonsensitive", "Review supplied source", Path.Combine(Profiles, "code.json"), [ReviewerFile("google-gemini-3.5-flash")], Path.Combine(Profiles, "budget-live.json"), Path.Combine(Profiles, "prices", "live-2026-11-04-google-free.json"), null, ["Sample.cs"], [],
            FallbackReviewerFiles: [ReviewerFile("codex-cli")]));
        Assert.Equal(2, plan.Requests.Count);Assert.Equal("codex-cli", plan.Fallbacks["google-gemini-3.5-flash"]);
        var http = new Http(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"code\":503,\"status\":\"UNAVAILABLE\"}}");var cli = new FakeCli(r => CliAnswer(r));
        using var transport = new RoutingTransport(new HttpTransport(new OneKey("google"), http), cli);
        var r = await new ReviewEngine(store, transport).ExecuteAsync(plan, Issue(store, plan));
        Assert.Equal(1, http.Sent);Assert.Equal(new[] { "codex-cli" }, cli.Sent);
        Assert.Equal("completed", r.Result.Status);Assert.True(r.Result.Coverage!.RequiredCoverageComplete);Assert.Equal(0m, store.Attempts().Sum(a => a.CostUsd ?? 0));
    }
    [Fact] public async Task CliThatIgnoresTheSchemaLeavesTheUnitInvalidAndDoesNotFakeCoverage()
    {
        using var e = new TestEnvironment();using var store = new StateStore(e.State, () => Clock, "live");
        var plan = Planner.Build(store, new("prose", e.Source, e.Source, "nonsensitive", "Review supplied source", Path.Combine(Profiles, "code.json"), [ReviewerFile("agy-cli")], Path.Combine(Profiles, "budget-live.json"), Path.Combine(Profiles, "prices", "live-2026-11-04.json"), null, ["Sample.cs"], []));
        var cli = new FakeCli(r => CliAnswer(r, structured: null));
        using var transport = new RoutingTransport(new HttpTransport(new OneKey("openai"), new Http(HttpStatusCode.OK, "{}")), cli);
        var r = await new ReviewEngine(store, transport).ExecuteAsync(plan, Issue(store, plan));
        Assert.NotEqual(0, r.ExitCode);Assert.False(r.Result.Coverage!.RequiredCoverageComplete);
        var unit = Assert.Single(store.Units(r.Result.RunId!));Assert.Equal(("invalid", false), (unit.ValidationState, unit.Valid));
    }
}
