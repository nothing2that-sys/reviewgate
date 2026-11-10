using System.Globalization;
using System.Text.Json.Nodes;
using ReviewGate.App;
using ReviewGate.Contracts;

namespace ReviewGate.Cli;

// Headless use by agents (Claude Code, Codex, ...):
//   review   one call: plan → check against the standing policy → per-run grant → run → findings and report path.
//            Reviewers are picked by provider; each uses the default model set in the desktop app, unless a model is
//            named ("openai=gpt-6.1-sol"). The provider's configured fallback applies unless --no-fallback.
//   policy   set (interactive console only: a person reads the summary and types the confirmation), status, revoke.
// It shares settings, user-added models, approved prices, keys and the live ledger with the desktop app.
static class Headless
{
    public static DesktopService Service(string state) => new(state, CliHost.ProfilesRoot, CliHost.CredentialFactory(), CliHost.HandlerFactory,
        cli: () => CliHost.CliFactory());

    public static async Task<CommandResult> ReviewAsync(CliHost.Options o, string state, CancellationToken ct)
    {
        var svc = Service(state);
        var picks = o.Many("reviewer").Select(ReviewerPick.Parse).ToList();
        Data.Require(picks.Count is 1 or 2, "reviewer_required");
        Data.Require(picks.All(p => p.Model != "*"), "reviewer_model_wildcard_not_allowed");
        var (instruction, requestSha256) = await CliHost.InstructionWithHashAsync(o, ct);
        string kind = o.One("profile") ?? "code";
        int minVendors = o.One("min-vendors") is string mv ? int.TryParse(mv, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 2 ? n : throw new ReviewError(2, "invalid_min_vendors") : 0;
        var preview = svc.PrepareReview(new(picks, instruction, o.Need("source-root"), o.Many("input"), o.Many("reference"), o.One("repository-root"), kind, !o.Has("no-fallback"), o.One("project"), minVendors));
        var plan = new JsonObject
        {
            ["recipients"] = new JsonArray([.. preview.Recipients.Select(r => (JsonNode)r)]), ["warnings"] = new JsonArray([.. preview.Warnings.Select(w => (JsonNode)w)]),
            ["requests"] = preview.Requests, ["reservation_upper_usd"] = preview.ReservationUsd, ["source_files"] = preview.SourceFiles, ["source_bytes"] = preview.SourceBytes,
            ["excluded"] = new JsonArray([.. preview.Exclusions.Select(x => (JsonNode)x)])
        };
        var (grant, violations) = svc.ApproveByPolicy(preview.PlanId);
        if (grant is null)
            return new(10, new Envelope
            {
                Command = "review", ExecutionMode = "live", Limitations = Envelope.LiveLimitations, PlanId = preview.PlanId, Status = "approval_required", ErrorCode = "policy_violation",
                RequiredAction = "ask_user_to_widen_policy_or_grant_plan",
                Details = new JsonObject { ["violations"] = new JsonArray([.. violations.Select(v => (JsonNode)v)]), ["plan"] = plan }
            });
        var result = await svc.RunAsync(preview.PlanId, grant, ct);
        int exit = result.Status == "completed" ? 0 : result.Status == "needs_reconciliation" ? 13 : result.ErrorCode is "credential_missing" or "cli_not_found" ? 11 : 20;
        return new(exit, new Envelope
        {
            Command = "review", ExecutionMode = "live", Limitations = Envelope.LiveLimitations, PlanId = preview.PlanId, RunId = result.RunId, Status = result.Status, ErrorCode = result.ErrorCode,
            RequiredAction = result.Status == "needs_reconciliation" ? "ask_user_to_resolve_unknown_cost" : null,
            Cost = new JsonObject { ["usage_calculated_usd"] = result.UsageUsd, ["unknown_hold_usd"] = result.UnknownHoldUsd, ["mode"] = "live" },
            Details = new JsonObject
            {
                ["plan"] = plan, ["processing_ratio"] = result.ProcessingRatio, ["report_path"] = result.ReportPath,
                ["unit_notes"] = new JsonArray([.. result.UnitNotes.Select(n => (JsonNode)n)]),
                ["findings"] = new JsonArray([.. result.Findings.Select(f => (JsonNode)new JsonObject
                {
                    ["reviewer"] = f.Reviewer, ["kind"] = f.Kind, ["severity"] = f.Severity, ["location"] = f.Location, ["claim"] = f.Claim, ["detail"] = f.Detail,
                    ["quote"] = f.Quote, ["evidence"] = f.Evidence, ["found_by"] = new JsonArray([.. (f.FoundBy ?? []).Select(r => (JsonNode)r)])
                })]),
                ["request_sha256"] = requestSha256, ["attempts"] = ReviewGate.App.Implementer.WithRequestHash(result.Attempts, requestSha256)
            }
        });
    }

    // implement: a CLI worker changes the repository on its own branch in a separate worktree (no commands), then
    // ReviewGate runs the allowed checks and has the named reviewers review the change. Nothing is merged.
    public static Implementer Implementer(string state) => new(Service(state), state, CliHost.WorkerFactory());
    public static async Task<CommandResult> ImplementAsync(CliHost.Options o, string state, CancellationToken ct)
    {
        var (instruction, requestSha256) = await CliHost.InstructionWithHashAsync(o, ct);
        var workers = o.Many("worker");Data.Require(workers.Count == 1, "worker_required");
        int? minutes = o.One("max-minutes") is string m ? int.TryParse(m, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= 1 ? v : throw new ReviewError(2, "invalid_max_minutes") : null;
        var request = new ImplementRequest(instruction, o.Need("repo"), ReviewerPick.Parse(workers[0]), o.Many("reviewer").Select(ReviewerPick.Parse).ToList(), o.One("base"), o.One("branch"),
            o.Many("check"), minutes, !o.Has("no-fallback"), o.Has("no-review"), Files: o.Many("input"), References: o.Many("reference"), RequestSha256: requestSha256);
        var outcome = await Implementer(state).RunAsync(request, ct);
        return new(outcome.ExitCode, new Envelope
        {
            Command = "implement", ExecutionMode = "live", Limitations = Envelope.LiveLimitations, Status = outcome.Status, ErrorCode = outcome.ErrorCode,
            RequiredAction = outcome.RequiredAction, RunId = outcome.Details["review"]?["run_id"]?.GetValue<string>(), Details = outcome.Details
        });
    }
    public static async Task<CommandResult> DiscardAsync(CliHost.Options o, string state, CancellationToken ct) =>
        new(0, new Envelope { Command = "discard", ExecutionMode = "live", Limitations = [], Status = "discarded", Details = await Implementer(state).DiscardAsync(o.Need("repo"), o.Need("branch"), ct) });

    public static CommandResult Policy(CliHost.Options o, string state)
    {
        var svc = Service(state);
        static JsonObject Show(StandingPolicy? p) => p is null ? new JsonObject { ["active"] = false } : new JsonObject
        {
            ["active"] = true, ["id"] = p.Id, ["issued_at"] = p.IssuedAt.ToString("O"), ["expires_at"] = p.ExpiresAt.ToString("O"),
            ["source_roots"] = new JsonArray([.. p.SourceRoots.Select(r => (JsonNode)r)]),
            ["reviewers"] = new JsonArray([.. p.Reviewers.Select(r => (JsonNode)new JsonObject { ["id"] = r.Id, ["model"] = r.CanonicalModelKey })]),
            ["max_run_usd"] = p.MaxRunUsd, ["max_requests_per_run"] = p.MaxRequestsPerRun, ["input_classes"] = new JsonArray([.. p.InputClasses.Select(c => (JsonNode)c)])
        };
        static JsonObject ShowImplement(ImplementPolicy? p) => p is null ? new JsonObject { ["active"] = false } : new JsonObject
        {
            ["active"] = true, ["id"] = p.Id, ["issued_at"] = p.IssuedAt.ToString("O"), ["expires_at"] = p.ExpiresAt.ToString("O"),
            ["repositories"] = new JsonArray([.. p.Repositories.Select(r => (JsonNode)r)]),
            ["workers"] = new JsonArray([.. p.Workers.Select(r => (JsonNode)new JsonObject { ["id"] = r.Id, ["model"] = r.CanonicalModelKey })]),
            ["checks"] = new JsonArray([.. p.Checks.Select(c => (JsonNode)c)]), ["max_minutes"] = p.MaxMinutes
        };
        Envelope Out(string status, StandingPolicy? p, ImplementPolicy? i) => new()
        {
            Command = "policy", ExecutionMode = "live", Limitations = [], Status = status, Details = new JsonObject { ["review"] = Show(p), ["implement"] = ShowImplement(i) }
        };
        switch (o.Instruction)
        {
            case "status": return new(0, Out("ok", svc.ActivePolicy(), svc.ActiveImplementPolicy()));
            // Revoking needs no person and ends both policies.
            case "revoke": return new(0, Out(svc.RevokePolicy() | svc.RevokeImplementPolicy() ? "revoked" : "none_active", null, null));
        }
        // set / set-implement: only a person at an interactive console can issue or widen a policy.
        Data.Require(!Console.IsInputRedirected, "interactive_console_required");
        var e = Console.Error;int days = o.One("expires-days") is string d ? int.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out var k) ? k : throw new ReviewError(2, "invalid_policy") : 90;
        if (o.Instruction == "set-implement")
        {
            int minutes = o.One("max-minutes") is string mm ? int.TryParse(mm, NumberStyles.None, CultureInfo.InvariantCulture, out var x) ? x : throw new ReviewError(2, "invalid_policy") : 30;
            decimal? workerUsd = o.One("max-usd") is string mu ? decimal.TryParse(mu, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var wu) && wu >= 0 ? wu : throw new ReviewError(2, "invalid_policy") : null;
            var ip = svc.DraftImplementPolicy(o.Many("allow-repo"), o.Many("worker").Select(ReviewerPick.Parse).ToList(), o.Many("check"), minutes, days, workerUsd);
            e.WriteLine("ReviewGate implementation policy — agents may have a CLI worker change these repositories without asking each time.");
            e.WriteLine("Each run works on a new branch reviewgate/impl-* in a separate worktree; the worker can edit files but not run commands; nothing is merged.");
            foreach (var repo in ip.Repositories) e.WriteLine($"  repository  {repo}");
            foreach (var w in ip.Workers) e.WriteLine($"  worker      {w.Id}  ({w.CanonicalModelKey}) — " + (w.Id.EndsWith("-cli", StringComparison.Ordinal) ? "signed-in CLI account; repository content is sent to it" : "API key; the files named in each call are sent to it"));
            if (ip.MaxWorkerUsd is decimal cap) e.WriteLine($"  API worker  at most {cap.ToString(CultureInfo.InvariantCulture)} USD reserved per call; budget-live.json caps still apply");
            foreach (var c in ip.Checks) e.WriteLine($"  check       {c}   (run by ReviewGate in the worktree; it executes the changed code)");
            if (ip.Checks.Count == 0) e.WriteLine("  check       none");
            e.WriteLine($"  time        at most {ip.MaxMinutes} minutes per worker run (and per check)");
            e.WriteLine($"  expires     {ip.ExpiresAt.ToOffset(TimeSpan.FromHours(9)):yyyy-MM-dd HH:mm} KST ({days} days). Replaces any active implementation policy.");
            e.WriteLine("  The cross-review after each change still needs the review policy (policy set).");
            e.Write($"Type the last 6 characters of the policy ID ({ip.Id[^6..]}) to approve, anything else to cancel: ");
            Data.Require(Console.ReadLine()?.Trim() == ip.Id[^6..], "policy_not_confirmed", 10);
            svc.IssueImplementPolicy(ip, "interactive_console");
            return new(0, Out("issued", svc.ActivePolicy(), ip));
        }
        decimal maxUsd = o.One("max-usd") is string m ? decimal.TryParse(m, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : throw new ReviewError(2, "invalid_policy") : 1m;
        int maxRequests = o.One("max-requests") is string r ? int.TryParse(r, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new ReviewError(2, "invalid_policy") : 40;
        var policy = svc.DraftPolicy(o.Many("allow-root"), o.Many("reviewer").Select(ReviewerPick.Parse).ToList(), maxUsd, maxRequests, days);
        e.WriteLine("ReviewGate standing policy — agents may run live reviews inside these limits without asking each time.");
        e.WriteLine("Source text inside the folders below is sent to the listed reviewers (API providers, or the CLIs' signed-in accounts).");
        foreach (var root in policy.SourceRoots) e.WriteLine($"  folder      {root}");
        foreach (var rv in policy.Reviewers) e.WriteLine($"  reviewer    {rv.Id}  ({rv.CanonicalModelKey})");
        e.WriteLine($"  per run     at most {policy.MaxRunUsd.ToString(CultureInfo.InvariantCulture)} USD reserved, {policy.MaxRequestsPerRun} requests; budget-live.json caps still apply");
        e.WriteLine($"  input       {string.Join(", ", policy.InputClasses)} only");
        e.WriteLine($"  expires     {policy.ExpiresAt.ToOffset(TimeSpan.FromHours(9)):yyyy-MM-dd HH:mm} KST ({days} days). Replaces any active policy.");
        e.Write($"Type the last 6 characters of the policy ID ({policy.Id[^6..]}) to approve, anything else to cancel: ");
        Data.Require(Console.ReadLine()?.Trim() == policy.Id[^6..], "policy_not_confirmed", 10);
        svc.IssuePolicy(policy, "interactive_console");
        return new(0, Out("issued", policy, svc.ActiveImplementPolicy()));
    }
}
