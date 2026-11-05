using System.Text.Json.Nodes;
using ReviewGate.App;
using ReviewGate.Cli;
using ReviewGate.Contracts;
using ReviewGate.Testing;
using ReviewGate.Transport.Cli;
using ReviewGate.Transport.Http;

namespace ReviewGate.CliTests;

// A CLI reviewer with a full model id, and the model identity of every call in the review output.
[Collection("CliHost statics")]
public sealed class HeadlessIdentityTests
{
    sealed class Keys : ICredentialStore
    {
        readonly KeyRing ring = new(new MemoryVault());
        public string? Read(string provider) => ring.Read(provider);
        public IReadOnlyList<CredentialEntry> List(string provider) => ring.List(provider);
        public string Add(string provider, string name, string secret) => ring.Add(provider, name, secret);
        public bool Remove(string provider, string id) => ring.Remove(provider, id);
        public void Activate(string provider, string id) => ring.Activate(provider, id);
    }
    // Answers like the CLI transport, including the identity it would have read from the CLI's own record.
    sealed class FakeCli : IModelTransport
    {
        public List<(ReviewerProfile Reviewer, JsonObject Body)> Sent { get; } = [];
        public bool HasCredential(string provider) => ProviderCatalog.Get(provider).IsCli;
        public Task<TransportResult> SendAsync(ReviewerProfile reviewer, string body, CancellationToken cancellation)
        {
            var request = JsonNode.Parse(body)!.AsObject();Sent.Add((reviewer, request));
            string? model = request["model"]?.GetValue<string>();
            var seen = new CliTransport.Observation(["observed-" + (model ?? "default")], "observed-" + (model ?? "default"), "medium",
                new JsonObject { ["file"] = "~/.codex/sessions/x/rollout-1-thread-1.jsonl", ["line"] = 3, ["field"] = "turn_context.payload.model" }, "thread-1");
            return Task.FromResult(new TransportResult("received", 200, new JsonObject
            {
                ["cli"] = reviewer.HostingProvider, ["status"] = "completed", ["error"] = null,
                ["structured_output"] = JsonNode.Parse("{\"status\":\"success\",\"disposition\":\"no_finding\",\"findings\":[]}"),
                ["usage"] = new JsonObject { ["input_tokens"] = 500, ["cached_input_tokens"] = 0, ["output_tokens"] = 60, ["thinking_tokens"] = 0 }, ["exit_code"] = 0,
                ["model_identity"] = CliTransport.Identity(reviewer.HostingProvider, "codex-cli 0.166.0", model, seen, Data.Hash("sent"))
            }.ToJsonString(), null));
        }
    }
    static string Profiles => Path.Combine(TestEnvironment.Repository, "profiles");
    static DesktopService Service(TestEnvironment e) => new(e.State, Profiles, new Keys(), locateCli: _ => null);
    static async Task<(CommandResult Result, FakeCli Cli)> Review(TestEnvironment e, params string[] args)
    {
        var cli = new FakeCli();var oldCli = CliHost.CliFactory;var oldKeys = CliHost.CredentialFactory;var oldProfiles = CliHost.ProfilesRoot;
        CliHost.CliFactory = () => cli;CliHost.CredentialFactory = () => new Keys();CliHost.ProfilesRoot = Profiles;
        try { return (await CliHost.RunAsync(["review", "--state-root", e.State, "--source-root", e.Source, "--input", "Sample.cs", .. args]), cli); }
        finally { CliHost.CliFactory = oldCli;CliHost.CredentialFactory = oldKeys;CliHost.ProfilesRoot = oldProfiles; }
    }

    [Fact] public async Task AFullModelIdRunsTheBuiltInCliProfileAndEveryCallReportsItsIdentity()
    {
        using var e = new TestEnvironment();var svc = Service(e);
        svc.IssuePolicy(svc.DraftPolicy([e.Source], [new("codex-cli", null)], 1m, 40, 30), "interactive_console");
        // An instruction over 64 KiB, with a byte-order mark: the hash is of the file bytes, not the decoded text.
        string prompt = Path.Combine(e.Root, "prompt.md");
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Data.Utf8.GetBytes("Find defects.\n" + new string('x', 70_000))];File.WriteAllBytes(prompt, bytes);
        var (r, cli) = await Review(e, "--prompt-file", prompt, "--reviewer", "codex-cli=gpt-6.1-sol", "--no-fallback");
        Assert.Equal(0, r.ExitCode);Assert.Equal("completed", r.Result.Status);
        // The model id goes to the CLI; the limits are the built-in Codex profile's, not a user-added model's 64 KiB / 300 s.
        var (reviewer, body) = Assert.Single(cli.Sent);
        Assert.Equal("gpt-6.1-sol", body["model"]!.GetValue<string>());Assert.Equal((262144, 900), (reviewer.MaxRequestBytes, reviewer.TimeoutSeconds));
        Assert.Equal(("codex-cli", "openai/codex-default"), (reviewer.Id, reviewer.CanonicalModelKey));
        var attempt = Assert.Single(r.Result.Details!["attempts"]!.AsArray())!;var id = attempt["model_identity"]!;
        Assert.Equal("gpt-6.1-sol", id["model_requested"]!.GetValue<string>());Assert.Equal("observed-gpt-6.1-sol", id["model_final"]!.GetValue<string>());
        Assert.Equal(["observed-gpt-6.1-sol"], id["model_observed"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("thread-1", id["session_id"]!.GetValue<string>());Assert.EndsWith("thread-1.jsonl", id["source"]!["file"]!.GetValue<string>());
        Assert.Equal(Data.Hash(bytes), id["request_sha256"]!.GetValue<string>());Assert.Equal(Data.Hash(bytes), r.Result.Details["request_sha256"]!.GetValue<string>());
        Assert.Equal(Data.Hash("sent"), id["sent_prompt_sha256"]!.GetValue<string>());
        Assert.False(id["fallback_used"]!.GetValue<bool>());Assert.False(attempt["fallback_used"]!.GetValue<bool>());Assert.Equal("settled", attempt["phase"]!.GetValue<string>());
        // A provider alone still runs the CLI's own default model and says it asked for none.
        var (plain, _) = await Review(e, "Find defects.", "--reviewer", "codex-cli");
        var plainId = Assert.Single(plain.Result.Details!["attempts"]!.AsArray())!["model_identity"]!;
        Assert.Null(plainId["model_requested"]);Assert.True(plainId["cli_default_model"]!.GetValue<bool>());
        Assert.Equal(Data.Hash(Data.Utf8.GetBytes("Find defects.")), plainId["request_sha256"]!.GetValue<string>());
    }
    [Fact] public void ModelIdsThatAreNotIdsAreRefused()
    {
        using var e = new TestEnvironment();var svc = Service(e);
        Assert.Equal("invalid_model_id", Assert.Throws<ReviewError>(() => svc.ResolveReviewers([new("codex-cli", "default:codex2")])).Code);
        Assert.Equal("invalid_model_id", Assert.Throws<ReviewError>(() => svc.ResolveReviewers([new("claude-cli", "a b")])).Code);
        // The same id twice gives the same pinned profile file.
        var a = svc.ResolveReviewers([new("claude-cli", "claude-opus-5-5")])[0].Primary;var b = svc.ResolveReviewers([new("claude-cli", "claude-opus-5-5")])[0].Primary;
        Assert.Equal(a.File, b.File);Assert.Equal("claude-cli", Path.GetFileNameWithoutExtension(a.File));Assert.Equal("claude-opus-5-5", Data.Read<ReviewerProfile>(a.File).ModelId);
    }
}
