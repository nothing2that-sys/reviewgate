using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ReviewGate.App;
using ReviewGate.Cli;
using ReviewGate.Contracts;
using ReviewGate.Infrastructure;
using ReviewGate.Testing;
using ReviewGate.Transport.Http;

namespace ReviewGate.CliTests;

// API models as implementation workers: they see only the named files and answer with edits that ReviewGate
// applies (all or none) on the run's own branch. The provider is a fake HTTP handler shaped like OpenAI Responses.
[Collection("CliHost statics")]
public sealed class ApiWorkerTests
{
    sealed class Keys : ICredentialStore
    {
        readonly KeyRing ring = new(new MemoryVault());
        public Keys() => ring.Add("openai", "test", "sk-test-not-a-real-key-000000");
        public string? Read(string provider) => ring.Read(provider);
        public IReadOnlyList<CredentialEntry> List(string provider) => ring.List(provider);
        public string Add(string provider, string name, string secret) => ring.Add(provider, name, secret);
        public bool Remove(string provider, string id) => ring.Remove(provider, id);
        public void Activate(string provider, string id) => ring.Activate(provider, id);
    }
    // Answers every request with the given edits JSON and remembers the request bodies.
    sealed class FakeOpenAi(object answer) : HttpMessageHandler
    {
        public List<JsonObject> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            string text = System.Text.Json.JsonSerializer.Serialize(answer);
            var body = new JsonObject
            {
                ["status"] = "completed",
                ["output"] = new JsonArray(new JsonObject { ["type"] = "message", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }) }),
                ["usage"] = new JsonObject { ["input_tokens"] = 1200, ["output_tokens"] = 300, ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 }, ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 } }
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
    sealed class Repo : IDisposable
    {
        public TestEnvironment Env { get; } = new();
        public string Path => System.IO.Path.Combine(Env.Root, "repo");
        public Repo()
        {
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "src"));
            File.WriteAllText(System.IO.Path.Combine(Path, "src", "Calc.cs"), "class Calc\r\n{\r\n    public int Add(int a, int b) => a - b;\r\n}\r\n", Data.Utf8);
            File.WriteAllText(System.IO.Path.Combine(Path, "Secret.txt"), "not for the model\n", Data.Utf8);
            Git("init", "-q", "-b", "main");Git("add", "-A");Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
        }
        public string Git(params string[] args)
        {
            var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "-C", Path } };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;string o = p.StandardOutput.ReadToEnd();p.StandardError.ReadToEnd();p.WaitForExit();
            return p.ExitCode == 0 ? o : throw new InvalidOperationException("git " + string.Join(' ', args) + " failed");
        }
        public void Dispose()
        {
            foreach (var f in Directory.EnumerateFiles(Env.Root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Env.Dispose();
        }
    }
    static string Profiles => Path.Combine(TestEnvironment.Repository, "profiles");
    static (Implementer Impl, DesktopService Svc) Desktop(Repo r, FakeOpenAi api)
    {
        var svc = new DesktopService(r.Env.State, Profiles, new Keys(), handler: () => api, locateCli: _ => null);
        return (new Implementer(svc, r.Env.State), svc);
    }
    static object Answer(params object[] edits) => new { status = "done", summary = "Fixed Add.", open_questions = Array.Empty<string>(), edits };
    static object Replace(string path, string oldText, string newText) => new { path, action = "replace", old_text = oldText, new_text = newText };
    static object Create(string path, string text) => new { path, action = "create", old_text = "", new_text = text };

    [Fact] public async Task TheModelSeesOnlyTheNamedFilesAndItsEditsLandOnTheBranch()
    {
        using var r = new Repo();var api = new FakeOpenAi(Answer(Replace("src/Calc.cs", "=> a - b;", "=> a + b;"), Create("src/CalcTests.cs", "// tests\n")));
        var (impl, svc) = Desktop(r, api);
        var outcome = await impl.RunAsync(new("Add must add.", r.Path, new("openai", null), [], NoReview: true, PersonReviewMaxUsd: 0m, Files: ["src"], PersonWorkerMaxUsd: 1m));
        Assert.Equal("completed", outcome.Status);var w = outcome.Details["worker_result"]!;
        // One request with the folder's tracked file, not the rest of the repository.
        var sent = Assert.Single(api.Requests);string input = sent["input"]!.GetValue<string>();
        Assert.Contains("### src/Calc.cs", input);Assert.Contains("a - b", input);Assert.DoesNotContain("not for the model", input);Assert.False(sent["store"]!.GetValue<bool>());
        Assert.Equal("reviewgate_edits", sent["text"]!["format"]!["name"]!.GetValue<string>());
        Assert.Equal(2, w["edits_applied"]!.AsArray().Count);Assert.Equal("provider_usage", w["cost_basis"]!.GetValue<string>());Assert.True(w["cost_usd"]!.GetValue<decimal>() > 0);
        // The change is on the branch, CRLF kept; the caller's checkout still has the bug.
        string branch = outcome.Details["branch"]!.GetValue<string>();
        Assert.Contains("=> a + b;\r\n", r.Git("show", branch + ":src/Calc.cs").Replace("\r\n", "\n").Replace("\n", "\r\n"));
        Assert.Equal("// tests\r\n", r.Git("show", branch + ":src/CalcTests.cs").Replace("\r\n", "\n").Replace("\n", "\r\n"));
        Assert.Contains("a - b", File.ReadAllText(Path.Combine(r.Path, "src", "Calc.cs")));
        // The call is on the live ledger, settled from the reported usage.
        using var store = new StateStore(r.Env.State, null, "live");
        var call = Assert.Single(store.ImplementCalls(outcome.Details["implement_id"]!.GetValue<string>()));
        Assert.Equal(("settled", "provider_usage"), (call.Phase, call.Basis));Assert.Equal(w["cost_usd"]!.GetValue<decimal>(), call.Cost);
        Assert.Equal(call.Cost, store.ImplementHeld(StateStore.Month(DateTimeOffset.UtcNow)));
    }
    [Fact] public async Task OneEditThatDoesNotMatchMeansNothingIsApplied()
    {
        using var r = new Repo();
        var api = new FakeOpenAi(Answer(Replace("src/Calc.cs", "=> a - b;", "=> a + b;"), Replace("src/Calc.cs", "Multiply", "x")));
        var (impl, _) = Desktop(r, api);
        var outcome = await impl.RunAsync(new("Add must add.", r.Path, new("openai", null), [], NoReview: true, PersonReviewMaxUsd: 0m, Files: ["src/Calc.cs"], PersonWorkerMaxUsd: 1m));
        Assert.Equal("worker_failed", outcome.Status);Assert.Null(outcome.Details["branch"]);
        Assert.StartsWith("edit_not_applicable: src/Calc.cs: old_text", outcome.Details["worker_result"]!["error"]!.GetValue<string>());
        Assert.DoesNotContain("reviewgate/", r.Git("branch", "--list"));
    }
    [Fact] public async Task OverTheCostCapNothingIsSent()
    {
        using var r = new Repo();var api = new FakeOpenAi(Answer());var (impl, _) = Desktop(r, api);
        var outcome = await impl.RunAsync(new("Add must add.", r.Path, new("openai", null), [], NoReview: true, PersonReviewMaxUsd: 0m, Files: ["src/Calc.cs"], PersonWorkerMaxUsd: 0m));
        Assert.Equal("over_cost_cap", outcome.Details["worker_result"]!["error"]!.GetValue<string>());Assert.Empty(api.Requests);Assert.Equal("worker_failed", outcome.Status);
    }
    [Fact] public void EditsStayInsideTheNamedFilesAndFolders()
    {
        using var r = new Repo();string wt = r.Path;var editable = new HashSet<string>(["src/Calc.cs"], StringComparer.OrdinalIgnoreCase);
        JsonArray Edits(params object[] e) => JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(e))!.AsArray();
        string Fails(params object[] e) => Assert.Throws<ReviewError>(() => Implementer.ApplyEdits(wt, Edits(e), editable, ["src"])).Action!;
        Assert.Contains("허용 범위 밖", Fails(Replace("../outside.cs", "a", "b")));
        Assert.Contains("허용 범위 밖", Fails(Create(".git/hooks/pre-commit", "x")));
        Assert.Contains("고칠 수 있는 파일이 아님", Fails(Replace("Secret.txt", "not", "yes")));
        Assert.Contains("지정한 폴더 안에만", Fails(Create("Other.cs", "x")));
        Assert.Contains("여러 곳에", Fails(Replace("src/Calc.cs", "a", "c")));
        Assert.Contains("이미 있는 파일", Fails(Create("src/Calc.cs", "x")));
        // Nothing was written by the failed attempts.
        Assert.Equal("class Calc\r\n{\r\n    public int Add(int a, int b) => a - b;\r\n}\r\n", File.ReadAllText(Path.Combine(wt, "src", "Calc.cs")));
        // Several replaces on one file apply in order.
        Implementer.ApplyEdits(wt, Edits(Replace("src/Calc.cs", "a - b", "a + b"), Replace("src/Calc.cs", "a + b", "checked(a + b)")), editable, []);
        Assert.Contains("=> checked(a + b);\r\n", File.ReadAllText(Path.Combine(wt, "src", "Calc.cs")));
    }
    [Fact] public async Task HeadlessApiWorkersNeedTheirCostCapInThePolicy()
    {
        using var r = new Repo();var api = new FakeOpenAi(Answer(Replace("src/Calc.cs", "=> a - b;", "=> a + b;")));
        var svc = new DesktopService(r.Env.State, Profiles, new Keys(), handler: () => api, locateCli: _ => null);
        // A policy written before API workers existed (no cost cap) does not allow them.
        svc.IssueImplementPolicy(new ImplementPolicy(Data.Version, Data.Id("ipolicy"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), [r.Path],
            [new("openai-gpt-6-luna", "openai/gpt-6-luna")], [], 10), "interactive_console");
        async Task<CommandResult> Run()
        {
            var (oldKeys, oldProfiles, oldHandler) = (CliHost.CredentialFactory, CliHost.ProfilesRoot, CliHost.HandlerFactory);
            CliHost.CredentialFactory = () => new Keys();CliHost.ProfilesRoot = Profiles;CliHost.HandlerFactory = () => api;
            try { return await CliHost.RunAsync(["implement", "Add must add.", "--state-root", r.Env.State, "--repo", r.Path, "--worker", "openai", "--input", "src/Calc.cs", "--no-review"]); }
            finally { (CliHost.CredentialFactory, CliHost.ProfilesRoot, CliHost.HandlerFactory) = (oldKeys, oldProfiles, oldHandler); }
        }
        var refused = await Run();
        var reasons = refused.Result.Details!["violations"]!.AsArray().Select(v => v!.GetValue<string>()).ToList();
        Assert.Contains(reasons, v => v.StartsWith("worker_not_allowed", StringComparison.Ordinal) || v.StartsWith("api_worker_cost_cap_missing", StringComparison.Ordinal));
        Assert.Empty(api.Requests);
        svc.IssueImplementPolicy(svc.DraftImplementPolicy([r.Path], [new("openai", null)], [], 10, 30, 0.5m), "interactive_console");
        var done = await Run();
        Assert.Equal("completed", done.Result.Status);Assert.Single(api.Requests);
        Assert.StartsWith("implement_policy:", done.Result.Details!["approved_by"]!.GetValue<string>());
    }
}
