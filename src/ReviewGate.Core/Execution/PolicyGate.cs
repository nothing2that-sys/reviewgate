using System.Globalization;
using ReviewGate.Contracts;
using ReviewGate.Core.Sources;

namespace ReviewGate.Core.Execution;

// Decides whether a live plan may run under the standing policy without asking anyone, and if so issues its
// per-run grant (channel "standing_policy:<id>"). Every reason it may not is returned, so the caller can show them.
public static class PolicyGate
{
    // alsoAllowed: extra folders the caller vouches for (an implementation worktree of an allowed repository).
    public static List<string> Violations(StandingPolicy? policy, Plan plan, DateTimeOffset now, IReadOnlyList<string>? alsoAllowed = null)
    {
        if (policy is null) return ["no_active_policy"];
        var reasons = new List<string>();
        if (now >= policy.ExpiresAt) reasons.Add($"policy_expired: {policy.ExpiresAt:O}");
        if (plan.ExecutionMode != "live") reasons.Add("not_live");
        if (!policy.InputClasses.Contains(plan.InputClass)) reasons.Add($"input_class_not_allowed: {plan.InputClass}");
        foreach (var root in new[] { plan.SourceRoot, plan.RepositoryRoot }.OfType<string>())
            if (!policy.SourceRoots.Concat(alsoAllowed ?? []).Any(allowed => SafePath.Within(root, allowed))) reasons.Add($"folder_not_allowed: {root}");
        foreach (var r in plan.Reviewers)
            if (!policy.Reviewers.Any(a => a.Id == r.Id && a.CanonicalModelKey == r.CanonicalModelKey)) reasons.Add($"reviewer_not_allowed: {r.Id} ({r.CanonicalModelKey})");
        decimal reserve = plan.Requests.Sum(q => q.ReservationUsd);
        if (reserve > policy.MaxRunUsd) reasons.Add($"over_run_cap: {reserve.ToString(CultureInfo.InvariantCulture)} > {policy.MaxRunUsd.ToString(CultureInfo.InvariantCulture)} USD");
        if (plan.Requests.Count > policy.MaxRequestsPerRun) reasons.Add($"too_many_requests: {plan.Requests.Count} > {policy.MaxRequestsPerRun}");
        return reasons;
    }
    // The grant covers exactly this plan: its reservation (at least the smallest positive amount, since grants
    // must allow something), its request count, no retries, one hour.
    public static Grant Issue(Infrastructure.StateStore store, StandingPolicy policy, Plan plan, IReadOnlyList<string>? alsoAllowed = null)
    {
        var reasons = Violations(policy, plan, store.Now, alsoAllowed);
        Data.Require(reasons.Count == 0, "policy_violation", 10);
        decimal reserve = plan.Requests.Sum(q => q.ReservationUsd);
        var grant = new Grant(Data.Version, Data.Id("grant"), plan.Hash, plan.ManifestHash, store.Now.AddHours(1), Math.Max(reserve, 0.000001m), plan.Requests.Count, 0, "live");
        store.RecordGrantIssue(plan, grant, "standing_policy:" + policy.Id);
        return grant;
    }
}
