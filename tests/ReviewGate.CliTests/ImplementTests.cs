using System.Diagnostics;
using System.Text.Json.Nodes;
using ReviewGate.App;
using ReviewGate.Cli;
using ReviewGate.Contracts;
using ReviewGate.Infrastructure;
using ReviewGate.Testing;
using ReviewGate.Transport.Cli;
using ReviewGate.Transport.Http;

namespace ReviewGate.CliTests;

// Headless implementation as an agent calls it: real git repositories and worktrees, fake CLI workers and reviewers.
[Collection("CliHost statics")]
public sealed class ImplementTests
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
    sealed class FakeReviewer : IModelTransport
    {
        public List<string> Sent { get; } = [];
        public bool HasCredential(string provider) => ProviderCatalog.Get(provider).IsCli;
        public Task<TransportResult> SendAsync(ReviewerProfile reviewer, string body, CancellationToken cancellation)
        {
            Sent.Add(reviewer.HostingProvider + ":" + JsonNode.Parse(body)!["workdir"]?.GetValue<string>());
            string payload = "{\"status\":\"success\",\"disposition\":\"finding\",\"findings\":[{\"claim\":\"Changed constant\",\"detail\":\"result is now 2.\",\"quote\":\"result = 2\",\"source_id\":\"$source\",\"category\":\"logic\",\"severity\":\"low\",\"kind\":\"defect\",\"reported_evidence_grade\":\"observed\",\"confidence\":\"medium\",\"suggestion\":\"\",\"impact\":\"\",\"line\":1,\"start_byte\":null,\"assumptions\":[]}]}";
            return Task.FromResult(new TransportResult("received", 200, new JsonObject
            {
                ["cli"] = reviewer.HostingProvider, ["status"] = "completed", ["error"] = null, ["structured_output"] = JsonNode.Parse(payload),
                ["usage"] = new JsonObject { ["input_tokens"] = 500, ["cached_input_tokens"] = 0, ["output_tokens"] = 60, ["thinking_tokens"] = 0 }, ["exit_code"] = 0
            }.ToJsonString(), null));
        }
    }
    // A worker that applies `edit` to its folder and answers like a CLI.
    sealed class FakeWorker(Action<string>? edit)
    {
        public List<(string Provider, CliJob Job)> Jobs { get; } = [];
        public Task<TransportResult> Run(string provider, CliJob job, int timeout, CancellationToken ct)
        {
            Jobs.Add((provider, job));edit?.Invoke(job.Workdir!);
            return Task.FromResult(new TransportResult("received", 200, new JsonObject
            {
                ["cli"] = provider, ["status"] = "completed", ["error"] = null, ["exit_code"] = 0,
                ["structured_output"] = new JsonObject { ["status"] = "done", ["summary"] = "Changed result to 2.", ["open_questions"] = new JsonArray() },
                ["usage"] = new JsonObject { ["input_tokens"] = 900, ["cached_input_tokens"] = 0, ["output_tokens"] = 80, ["thinking_tokens"] = 0 }
            }.ToJsonString(), null));
        }
    }
    sealed class Repo : IDisposable
    {
        public TestEnvironment Env { get; } = new();
        public string Path => System.IO.Path.Combine(Env.Root, "repo");
        public Repo()
        {
            Directory.CreateDirectory(Path);
            File.WriteAllText(System.IO.Path.Combine(Path, "Sample.cs"), "int result = 1;\n", Data.Utf8);
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
            // git writes read-only object files; clear the flag so the test folder can be deleted.
            foreach (var f in Directory.EnumerateFiles(Env.Root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Env.Dispose();
        }
    }
    static string Profiles => Path.Combine(TestEnvironment.Repository, "profiles");
    static DesktopService Service(TestEnvironment e) => new(e.State, Profiles, new Keys(), locateCli: _ => null);
    static void Policies(Repo r, string reviewer = "claude-cli", string worker = "codex-cli", params string[] checks)
    {
        var svc = Service(r.Env);
        svc.IssueImplementPolicy(svc.DraftImplementPolicy([r.Path], [new(worker, null)], checks, 10, 30), "interactive_console");
        svc.IssuePolicy(svc.DraftPolicy([r.Path], [new(reviewer, null)], 1m, 40, 30), "interactive_console");
    }
    static async Task<(CommandResult Result, FakeReviewer Reviewer)> Implement(Repo r, FakeWorker worker, params string[] extra)
    {
        var reviewer = new FakeReviewer();
        var (oldCli, oldKeys, oldProfiles, oldWorker) = (CliHost.CliFactory, CliHost.CredentialFactory, CliHost.ProfilesRoot, CliHost.WorkerFactory);
        CliHost.CliFactory = () => reviewer;CliHost.CredentialFactory = () => new Keys();CliHost.ProfilesRoot = Profiles;CliHost.WorkerFactory = () => worker.Run;
        try { return (await CliHost.RunAsync(["implement", "Change result to 2.", "--state-root", r.Env.State, "--repo", r.Path, .. extra]), reviewer); }
        finally { (CliHost.CliFactory, CliHost.CredentialFactory, CliHost.ProfilesRoot, CliHost.WorkerFactory) = (oldCli, oldKeys, oldProfiles, oldWorker); }
    }
    static void ToTwo(string folder) => File.WriteAllText(Path.Combine(folder, "Sample.cs"), "int result = 2;\n", Data.Utf8);
    static List<string> Strings(JsonNode? n) => n!.AsArray().Select(v => v!.GetValue<string>()).ToList();

    [Fact] public async Task WithoutAnImplementPolicyNothingStarts()
    {
        using var r = new Repo();var worker = new FakeWorker(ToTwo);
        var (res, _) = await Implement(r, worker, "--worker", "codex-cli", "--reviewer", "claude-cli");
        Assert.Equal(10, res.ExitCode);Assert.Equal("approval_required", res.Result.Status);
        Assert.Contains("no_active_implement_policy", Strings(res.Result.Details!["violations"]));Assert.Contains("no_active_review_policy", Strings(res.Result.Details["violations"]));
        Assert.Empty(worker.Jobs);Assert.DoesNotContain("reviewgate/", r.Git("branch", "--list"));
    }
    [Fact] public async Task TheWorkerChangesItsOwnBranchChecksRunAndOtherModelsReview()
    {
        using var r = new Repo();Policies(r, checks: ["findstr /c:\"result = 2\" Sample.cs"]);
        string head = r.Git("rev-parse", "HEAD").Trim();
        var worker = new FakeWorker(ToTwo);
        var (res, reviewer) = await Implement(r, worker, "--worker", "codex-cli", "--reviewer", "claude-cli", "--check", "findstr /c:\"result = 2\" Sample.cs");
        Assert.Equal(0, res.ExitCode);Assert.Equal("completed", res.Result.Status);var d = res.Result.Details!;
        // The worker got an edit job in a worktree outside the repository and the state root.
        var job = Assert.Single(worker.Jobs);Assert.Equal("codex-cli", job.Provider);Assert.True(job.Job.Edit);Assert.False(job.Job.Web);
        Assert.StartsWith(Implementer.WorktreeRoot(r.Env.State), job.Job.Workdir);Assert.Contains("Change result to 2.", job.Job.Prompt);
        // The change is a commit on reviewgate/impl-*; the caller's checkout is untouched and the worktree is gone.
        string branch = d["branch"]!.GetValue<string>();Assert.StartsWith(Implementer.BranchPrefix, branch);
        Assert.Equal(head, d["base_commit"]!.GetValue<string>());Assert.Equal(head, r.Git("rev-parse", "HEAD").Trim());Assert.Equal("", r.Git("status", "--porcelain"));
        Assert.Equal("main", r.Git("branch", "--show-current").Trim());Assert.Equal("int result = 1;\n", File.ReadAllText(Path.Combine(r.Path, "Sample.cs")));
        Assert.Equal(d["commit"]!.GetValue<string>(), r.Git("rev-parse", branch).Trim());Assert.Equal("int result = 2;\n", r.Git("show", branch + ":Sample.cs").Replace("\r\n", "\n"));
        Assert.True(d["original_checkout_unchanged"]!.GetValue<bool>());Assert.True(d["worktree_removed"]!.GetValue<bool>());Assert.False(Directory.Exists(job.Job.Workdir));
        Assert.Contains("ReviewGate codex-cli", r.Git("log", "-1", "--format=%an", branch));
        Assert.Equal("M", d["changed_files"]![0]!["status"]!.GetValue<string>());Assert.Contains("+int result = 2;", File.ReadAllText(d["diff_path"]!.GetValue<string>()));
        var check = d["checks"]![0]!;Assert.True(check["passed"]!.GetValue<bool>());Assert.True(File.Exists(check["log_path"]!.GetValue<string>()));
        Assert.Equal("done", d["worker_result"]!["answer"]!["status"]!.GetValue<string>());
        // Claude Code reviewed the changed file in the worktree (allowed because its repository is allowed).
        var review = d["review"]!;Assert.Equal("completed", review["status"]!.GetValue<string>());
        Assert.Equal("claude-cli", Assert.Single(reviewer.Sent).Split(':')[0]);Assert.Single(review["findings"]!.AsArray());
        Assert.Equal(review["run_id"]!.GetValue<string>(), res.Result.RunId);Assert.True(File.Exists(Path.Combine(d["run_folder"]!.GetValue<string>(), "result.json")));
    }
    [Fact] public async Task NoChangeLeavesNoBranchAndAFailingCheckIsReportedNotHidden()
    {
        using var r = new Repo();Policies(r, checks: ["findstr /c:\"result = 2\" Sample.cs"]);
        var (none, reviewer) = await Implement(r, new FakeWorker(null), "--worker", "codex-cli", "--reviewer", "claude-cli");
        Assert.Equal((0, "no_changes"), (none.ExitCode, none.Result.Status));Assert.Null(none.Result.Details!["branch"]);Assert.Empty(reviewer.Sent);
        Assert.DoesNotContain("reviewgate/", r.Git("branch", "--list"));
        var (failing, _) = await Implement(r, new FakeWorker(f => File.WriteAllText(Path.Combine(f, "Other.cs"), "int other = 3;\n")), "--worker", "codex-cli", "--no-review", "--check", "findstr /c:\"result = 2\" Sample.cs");
        Assert.Equal("completed", failing.Result.Status);var check = failing.Result.Details!["checks"]![0]!;
        Assert.False(check["passed"]!.GetValue<bool>());Assert.Equal(1, check["exit_code"]!.GetValue<int>());
        Assert.Equal("skipped", failing.Result.Details["review"]!["status"]!.GetValue<string>());Assert.Equal("A", failing.Result.Details["changed_files"]![0]!["status"]!.GetValue<string>());
    }
    [Fact] public async Task EveryReasonOutsideThePoliciesIsReturned()
    {
        using var r = new Repo();Policies(r, reviewer: "codex-cli", worker: "claude-cli");
        var (res, _) = await Implement(r, new FakeWorker(ToTwo), "--worker", "codex-cli", "--reviewer", "codex-cli", "--check", "del /q *.*", "--max-minutes", "99");
        Assert.Equal(10, res.ExitCode);var reasons = Strings(res.Result.Details!["violations"]);
        Assert.Contains(reasons, v => v.StartsWith("worker_not_allowed: codex-cli", StringComparison.Ordinal));
        Assert.Contains(reasons, v => v.StartsWith("reviewer_same_as_worker: codex-cli", StringComparison.Ordinal));
        Assert.Contains("check_not_allowed: del /q *.*", reasons);Assert.Contains(reasons, v => v.StartsWith("over_time_cap: 99 > 10", StringComparison.Ordinal));
        Assert.Equal("worker_must_be_cli", (await Implement(r, new FakeWorker(ToTwo), "--worker", "openai", "--no-review")).Result.Result.ErrorCode);
        Assert.Equal("reviewer_required", (await Implement(r, new FakeWorker(ToTwo), "--worker", "claude-cli")).Result.Result.ErrorCode);
        Assert.Equal("invalid_implement_options", (await Implement(r, new FakeWorker(ToTwo), "--worker", "claude-cli", "--no-review", "--source-root", r.Path)).Result.Result.ErrorCode);
        // Policies for another folder do not cover this repository.
        string other = Path.Combine(r.Env.Root, "other");Directory.CreateDirectory(other);var svc = Service(r.Env);
        svc.IssueImplementPolicy(svc.DraftImplementPolicy([other], [new("codex-cli", null)], [], 10, 30), "interactive_console");
        svc.IssuePolicy(svc.DraftPolicy([other], [new("claude-cli", null)], 1m, 40, 30), "interactive_console");
        var worker = new FakeWorker(ToTwo);
        var (outside, _) = await Implement(r, worker, "--worker", "codex-cli", "--reviewer", "claude-cli");
        var why = Strings(outside.Result.Details!["violations"]);Assert.Empty(worker.Jobs);
        Assert.Contains(why, v => v.StartsWith("repository_not_allowed", StringComparison.Ordinal));Assert.Contains(why, v => v.StartsWith("review_folder_not_allowed", StringComparison.Ordinal));
    }
    [Fact] public async Task RepositoryHooksDoNotRunAndABranchCanBeContinuedThenDiscarded()
    {
        using var r = new Repo();Policies(r);
        // A hook that would fail every commit: ReviewGate's own commit must not run it.
        File.WriteAllText(Path.Combine(r.Path, ".git", "hooks", "pre-commit"), "#!/bin/sh\nexit 1\n");File.WriteAllText(Path.Combine(r.Path, ".git", "hooks", "commit-msg"), "#!/bin/sh\nexit 1\n");
        var (first, _) = await Implement(r, new FakeWorker(ToTwo), "--worker", "codex-cli", "--no-review");
        Assert.Equal("completed", first.Result.Status);string branch = first.Result.Details!["branch"]!.GetValue<string>();
        var (second, _) = await Implement(r, new FakeWorker(f => File.WriteAllText(Path.Combine(f, "Sample.cs"), "int result = 3;\n")), "--worker", "codex-cli", "--no-review", "--branch", branch);
        Assert.Equal("completed", second.Result.Status);Assert.Equal(branch, second.Result.Details!["branch"]!.GetValue<string>());
        Assert.Equal(first.Result.Details["commit"]!.GetValue<string>(), second.Result.Details["base_commit"]!.GetValue<string>());
        Assert.Equal("2", r.Git("rev-list", "--count", "main.." + branch).Trim());
        Assert.Equal("invalid_branch", (await Implement(r, new FakeWorker(ToTwo), "--worker", "codex-cli", "--no-review", "--branch", "main")).Result.Result.ErrorCode);
        // discard deletes only reviewgate branches.
        Assert.Equal("invalid_branch", (await CliHost.RunAsync(["discard", "--state-root", r.Env.State, "--repo", r.Path, "--branch", "main"])).Result.ErrorCode);
        var discarded = await CliHost.RunAsync(["discard", "--state-root", r.Env.State, "--repo", r.Path, "--branch", branch]);
        Assert.Equal("discarded", discarded.Result.Status);Assert.DoesNotContain("reviewgate/", r.Git("branch", "--list"));
    }
    [Fact] public async Task OnlyAPersonCanIssueTheImplementPolicyAndRevokeEndsBoth()
    {
        using var r = new Repo();
        var set = await TestEnvironment.Invoke(["policy", "set-implement", "--state-root", r.Env.State, "--allow-repo", r.Path, "--worker", "codex-cli"]);
        Assert.Equal(2, set.Exit);Assert.Equal("interactive_console_required", Data.Parse<Envelope>(set.Stdout).ErrorCode);
        using var store = new StateStore(r.Env.State, null, "live");
        var policy = new ImplementPolicy(Data.Version, Data.Id("ipolicy"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), [r.Path], [new("codex-cli", "openai/codex-default")], [], 10);
        Assert.Equal("invalid_policy_channel", Assert.Throws<ReviewError>(() => store.RecordImplementPolicy(policy, "agent")).Code);
        Policies(r);
        var status = Data.Parse<Envelope>((await TestEnvironment.Invoke(["policy", "status", "--state-root", r.Env.State])).Stdout);
        Assert.True(status.Details!["implement"]!["active"]!.GetValue<bool>());Assert.True(status.Details["review"]!["active"]!.GetValue<bool>());
        var revoked = Data.Parse<Envelope>((await TestEnvironment.Invoke(["policy", "revoke", "--state-root", r.Env.State])).Stdout);
        Assert.Equal("revoked", revoked.Status);Assert.Null(store.ActiveImplementPolicy());Assert.Null(store.ActivePolicy());
        Assert.Equal("worker_must_be_cli", Assert.Throws<ReviewError>(() => Service(r.Env).DraftImplementPolicy([r.Path], [new("openai", null)], [], 10, 30)).Code);
    }
}
