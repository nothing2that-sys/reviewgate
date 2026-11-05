using System.Text.Json.Nodes;
using ReviewGate.App;
using ReviewGate.Cli;
using ReviewGate.Contracts;
using ReviewGate.Infrastructure;
using ReviewGate.Testing;
using ReviewGate.Transport.Http;

namespace ReviewGate.CliTests;

// Headless review as an agent calls it: no prompt, the standing policy decides. The CLI reviewers are fakes.
[Collection("CliHost statics")]
public sealed class HeadlessReviewTests
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
    sealed class FakeCli : IModelTransport
    {
        public List<string> Sent { get; } = [];
        public bool HasCredential(string provider) => ProviderCatalog.Get(provider).IsCli;
        public Task<TransportResult> SendAsync(ReviewerProfile reviewer, string body, CancellationToken cancellation)
        {
            Sent.Add(reviewer.HostingProvider);
            string payload = "{\"status\":\"success\",\"disposition\":\"finding\",\"findings\":[{\"claim\":\"Constant result\",\"detail\":\"result is always 1.\",\"quote\":\"result = 1\",\"source_id\":\"$source\",\"category\":\"logic\",\"severity\":\"low\",\"kind\":\"defect\",\"reported_evidence_grade\":\"observed\",\"confidence\":\"medium\",\"suggestion\":\"\",\"impact\":\"\",\"line\":1,\"start_byte\":null,\"assumptions\":[]}]}";
            return Task.FromResult(new TransportResult("received", 200, new JsonObject
            {
                ["cli"] = reviewer.HostingProvider, ["status"] = "completed", ["error"] = null, ["structured_output"] = JsonNode.Parse(payload),
                ["usage"] = new JsonObject { ["input_tokens"] = 500, ["cached_input_tokens"] = 0, ["output_tokens"] = 60, ["thinking_tokens"] = 0 }, ["exit_code"] = 0
            }.ToJsonString(), null));
        }
    }
    static string Profiles => Path.Combine(TestEnvironment.Repository, "profiles");
    static DesktopService Service(TestEnvironment e) => new(e.State, Profiles, new Keys(), locateCli: _ => null);
    static async Task<(CommandResult Result, FakeCli Cli)> Review(TestEnvironment e, params string[] extra)
    {
        var cli = new FakeCli();var oldCli = CliHost.CliFactory;var oldKeys = CliHost.CredentialFactory;var oldProfiles = CliHost.ProfilesRoot;
        CliHost.CliFactory = () => cli;CliHost.CredentialFactory = () => new Keys();CliHost.ProfilesRoot = Profiles;
        try { return (await CliHost.RunAsync(["review", "Find defects.", "--state-root", e.State, "--source-root", e.Source, "--input", "Sample.cs", .. extra]), cli); }
        finally { CliHost.CliFactory = oldCli;CliHost.CredentialFactory = oldKeys;CliHost.ProfilesRoot = oldProfiles; }
    }
    static List<string> Violations(CommandResult r) => r.Result.Details!["violations"]!.AsArray().Select(v => v!.GetValue<string>()).ToList();

    [Fact] public async Task WithoutAPolicyNothingIsSent()
    {
        using var e = new TestEnvironment();
        var (r, cli) = await Review(e, "--reviewer", "claude-cli");
        Assert.Equal(10, r.ExitCode);Assert.Equal(("approval_required", "policy_violation"), (r.Result.Status, r.Result.ErrorCode));
        Assert.Equal(["no_active_policy"], Violations(r));Assert.Empty(cli.Sent);Assert.StartsWith("lplan_", r.Result.PlanId);
        using var store = new StateStore(e.State, null, "live");Assert.Empty(store.Attempts());
    }
    [Fact] public async Task InsideThePolicyTheReviewRunsWithoutAskingAndReturnsFindings()
    {
        using var e = new TestEnvironment();var svc = Service(e);
        svc.IssuePolicy(svc.DraftPolicy([e.Source], [new("claude-cli", null), new("codex-cli", null)], 1m, 40, 30), "interactive_console");
        var (r, cli) = await Review(e, "--reviewer", "claude-cli", "--reviewer", "codex-cli");
        Assert.Equal(0, r.ExitCode);Assert.Equal("completed", r.Result.Status);Assert.Equal(["claude-cli", "codex-cli"], cli.Sent.Order());
        var findings = r.Result.Details!["findings"]!.AsArray();Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal("인용·위치 확인", f!["evidence"]!.GetValue<string>()));
        Assert.True(File.Exists(r.Result.Details["report_path"]!.GetValue<string>()));
        Assert.Equal(0m, r.Result.Cost!["usage_calculated_usd"]!.GetValue<decimal>());
        // The grant came from the standing policy, not from a person at this moment.
        using var store = new StateStore(e.State, null, "live");
        Assert.All(store.Attempts(), a => Assert.Equal("settled", a.Phase));
    }
    [Fact] public async Task OutsideThePolicyFolderOrModelStopsWithEveryReason()
    {
        using var e = new TestEnvironment();var svc = Service(e);string elsewhere = Path.Combine(e.Root, "elsewhere");Directory.CreateDirectory(elsewhere);
        svc.IssuePolicy(svc.DraftPolicy([elsewhere], [new("claude-cli", null)], 1m, 40, 30), "interactive_console");
        var (r, cli) = await Review(e, "--reviewer", "claude-cli", "--reviewer", "openai=gpt-6.1-sol");
        Assert.Equal(10, r.ExitCode);Assert.Empty(cli.Sent);
        var reasons = Violations(r);
        Assert.Contains(reasons, v => v.StartsWith("folder_not_allowed", StringComparison.Ordinal));
        Assert.Contains(reasons, v => v.StartsWith("reviewer_not_allowed: openai-gpt-6.1-sol", StringComparison.Ordinal));
        Assert.DoesNotContain(reasons, v => v.Contains("claude-cli", StringComparison.Ordinal));
        // Revoking leaves no policy; status says so.
        Assert.True(svc.RevokePolicy());Assert.Null(svc.ActivePolicy());
    }
    [Fact] public async Task ExpiredPolicyAndRunCapAreEnforced()
    {
        using var e = new TestEnvironment();var svc = Service(e);
        var policy = svc.DraftPolicy([e.Source], [new("openai", null)], 0.0001m, 1, 30);
        svc.IssuePolicy(policy with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, "interactive_console");
        var (r, _) = await Review(e, "--reviewer", "openai", "--no-fallback");
        var reasons = Violations(r);
        Assert.Contains(reasons, v => v.StartsWith("policy_expired", StringComparison.Ordinal));Assert.Contains(reasons, v => v.StartsWith("over_run_cap", StringComparison.Ordinal));
    }
    [Fact] public void ReviewersUseTheConfiguredDefaultUnlessAModelIsNamed()
    {
        using var e = new TestEnvironment();var svc = Service(e);
        var google = Assert.Single(svc.ResolveReviewers([new("google", null)]));
        Assert.Equal(("google-gemini-3.5-flash", "google-gemini-3.5-flash-lite"), (google.Primary.Id, google.Fallback?.Id));
        var named = Assert.Single(svc.ResolveReviewers([ReviewerPick.Parse("google=gemini-3.8-flash")]));
        Assert.Equal(("google-gemini-3.8-flash", "google-gemini-3.5-flash-lite"), (named.Primary.Id, named.Fallback?.Id));
        Assert.Equal("openai-gpt-6.1-sol", Assert.Single(svc.ResolveReviewers([ReviewerPick.Parse("openai=openai-gpt-6.1-sol")])).Primary.Id);
        Assert.Null(Assert.Single(svc.ResolveReviewers([new("google", null)], useFallbacks: false)).Fallback);
        // A fallback that is the other reviewer's primary is dropped instead of planning a duplicate model.
        var pair = svc.ResolveReviewers([new("google", null), ReviewerPick.Parse("google=gemini-3.5-flash-lite")]);Assert.Null(pair[0].Fallback);
        // The desktop setting is what "default" means.
        svc.SaveSettings(svc.LoadSettings() with { DefaultModels = new(svc.LoadSettings().DefaultModels) { ["openai"] = "openai-gpt-6.1-sol" } });
        Assert.Equal("openai-gpt-6.1-sol", Assert.Single(svc.ResolveReviewers([new("openai", null)])).Primary.Id);
        Assert.Equal("unknown_model", Assert.Throws<ReviewError>(() => svc.ResolveReviewers([ReviewerPick.Parse("openai=gpt-nope")])).Code);
        Assert.Equal("reviewer_count_mismatch", Assert.Throws<ReviewError>(() => svc.ResolveReviewers([])).Code);
    }
    [Fact] public async Task OnlyAPersonCanIssueAPolicy()
    {
        using var e = new TestEnvironment();
        var set = await TestEnvironment.Invoke(["policy", "set", "--state-root", e.State, "--allow-root", e.Source]);
        Assert.Equal(2, set.Exit);Assert.Equal("interactive_console_required", Data.Parse<Envelope>(set.Stdout).ErrorCode);
        var status = await TestEnvironment.Invoke(["policy", "status", "--state-root", e.State]);
        Assert.Equal(0, status.Exit);Assert.False(Data.Parse<Envelope>(status.Stdout).Details!["review"]!["active"]!.GetValue<bool>());
        using var store = new StateStore(e.State, null, "live");
        var policy = new StandingPolicy(Data.Version, Data.Id("policy"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), [e.Source], [new("claude-cli", "anthropic/claude-code-default")], 1, 10, ["nonsensitive"]);
        Assert.Equal("invalid_policy_channel", Assert.Throws<ReviewError>(() => store.RecordPolicy(policy, "agent")).Code);
        Assert.Equal("invalid_review_options", Data.Parse<Envelope>((await TestEnvironment.Invoke(["review", "x", "--state-root", e.State, "--source-root", e.Source, "--reviewer", "claude-cli", "--prices", "p.json"])).Stdout).ErrorCode);
    }
}
