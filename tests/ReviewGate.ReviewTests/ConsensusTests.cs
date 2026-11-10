using System.Text.Json.Nodes;
using ReviewGate.Contracts;
using ReviewGate.Core.Execution;
using ReviewGate.Core.Planning;
using ReviewGate.Infrastructure;
using ReviewGate.Providers;
using ReviewGate.Testing;

namespace ReviewGate.ReviewTests;

// Ideas taken from comparable open-source reviewers (docs/COMPARISON.md): grouping verified findings across reviewers,
// typed reasons for parts a reviewer could not review, a shared severity rubric, and a minimum of distinct developers.
public sealed class ConsensusTests
{
    const string Text = "int result = 1 / 0;\nint other = 2;\n";
    static FixtureFinding Finding(string claim, string quote) => new(claim, "detail", quote, Assumptions: []);
    static JsonNode Result(StateStore s, CommandResult r) => JsonNode.Parse(File.ReadAllText(Path.Combine(s.Root, r.Result.Artifacts.Single(a => a.Path.EndsWith("result.json", StringComparison.Ordinal)).Path)))!;
    static string Report(StateStore s, CommandResult r) => File.ReadAllText(Path.Combine(s.Root, r.Result.Artifacts.Single(a => a.Path.EndsWith("report.md", StringComparison.Ordinal)).Path));
    static Plan Build(TestEnvironment e, StateStore s, int minVendors) => Planner.Build(s, new("test", e.Source, e.Source, "synthetic", "Review supplied source", e.File("profile.json"),
        [e.File("reviewer.json"), e.File("reviewer-b.json")], e.File("budget.json"), e.File("prices.json"), e.File("fixture.json"), ["Sample.cs"], [], "independent", MinVendors: minVendors));

    [Fact] public async Task OverlappingVerifiedQuotesFromTwoReviewersFormOneGroup()
    {
        using var e = new TestEnvironment(Text, [
            new(Reviewer: "a", Disposition: "finding", Findings: [Finding("Divides by zero", "1 / 0")], Usage: new(100, 20)),
            new(Reviewer: "b", Disposition: "finding", Findings: [Finding("Zero divisor", "result = 1 / 0"), Finding("Unused value", "other = 2"), Finding("Invented", "not in the source")], Usage: new(100, 20))]);
        using var s = e.Store();var p = e.MakePlan(s, "independent");var r = await new ReviewEngine(s).ExecuteAsync(p, e.Grant(p));
        Assert.Equal(0, r.ExitCode);
        var groups = Result(s, r)["finding_groups"]!.AsArray();
        // The unverified quote is never grouped; the two overlapping quotes are one group found by both reviewers.
        Assert.Equal(2, groups.Count);
        var both = groups.Single(g => g!["found_by"]!.GetValue<int>() == 2)!;
        Assert.Equal(["a", "b"], both["reviewers"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(2, both["finding_ids"]!.AsArray().Count);Assert.Equal(1, both["line"]!.GetValue<int>());
        Assert.Single(groups.Single(g => g!["found_by"]!.GetValue<int>() == 1)!["finding_ids"]!.AsArray());
        string report = Report(s, r);
        Assert.Contains("found by 2 reviewers (a, b)", report);Assert.Contains("not proof", report);
    }

    [Fact] public async Task AGapReasonIsRecordedAndReported()
    {
        using var e = new TestEnvironment(Text, [new(Reviewer: "a", Disposition: "cannot_assess", GapReason: "files_required", GapDetail: "Math.cs, which defines the divisor", Usage: new(100, 20))]);
        using var s = e.Store();var p = e.MakePlan(s);var r = await new ReviewEngine(s).ExecuteAsync(p, e.Grant(p));
        // cannot_assess leaves the part uncovered; the reason says what would let the reviewer finish.
        Assert.False(r.Result.Coverage!.RequiredCoverageComplete);
        var unit = Assert.Single(s.Units(r.Result.RunId!));
        Assert.Equal(("files_required", "Math.cs, which defines the divisor"), (unit.GapReason, unit.GapDetail));
        var gap = Assert.Single(Result(s, r)["assessment_gaps"]!.AsArray())!;
        Assert.Equal(("a", "files_required", "cannot_assess"), (gap["reviewer_id"]!.GetValue<string>(), gap["reason"]!.GetValue<string>(), gap["disposition"]!.GetValue<string>()));
        Assert.Contains("Not fully reviewed: a on Sample\\.cs", Report(s, r));
    }

    [Fact] public void GapReasonsAreValidatedAndOptional()
    {
        var reviewer = new TestEnvironment().Reviewer;
        string Wire(string payload) => new JsonObject { ["status"] = "completed", ["output"] = new JsonArray(new JsonObject { ["type"] = "message", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = payload }) }), ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 } }.ToJsonString();
        // Older payloads without the fields read as "none".
        var old = WireAdapters.Parse(reviewer, Wire("{\"status\":\"success\",\"disposition\":\"no_finding\",\"findings\":[]}"));
        Assert.Equal(("success", "none", ""), (old.Status, old.GapReason, old.GapDetail));
        var gap = WireAdapters.Parse(reviewer, Wire("{\"status\":\"success\",\"disposition\":\"cannot_assess\",\"findings\":[],\"gap_reason\":\"focused_review_required\",\"gap_detail\":\" too broad \"}"));
        Assert.Equal(("focused_review_required", "too broad"), (gap.GapReason, gap.GapDetail));
        Assert.Equal(WireAdapters.InvalidPayload, WireAdapters.Parse(reviewer, Wire("{\"status\":\"success\",\"disposition\":\"cannot_assess\",\"findings\":[],\"gap_reason\":\"bored\",\"gap_detail\":\"\"}")).Status);
        // The schema asks for both fields, and the rubric is in every reviewer's system text.
        var schema = WireAdapters.PayloadSchema();
        Assert.Contains("gap_reason", schema["required"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(WireAdapters.GapReasons, schema["properties"]!["gap_reason"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains("never inflated or deflated", WireAdapters.SystemInstruction);Assert.Contains("never inflated or deflated", WireAdapters.CliSystemInstruction);
        Assert.Contains("Do not report as a defect", WireAdapters.SystemInstruction);
    }

    [Fact] public async Task TheVendorMinimumIsCheckedWhenPlanningAndAgainstTheAnswers()
    {
        using var e = new TestEnvironment(Text, [new(Reviewer: "a", Usage: new(100, 20)), new(Reviewer: "b", Status: "truncated", Usage: new(100, 20))]);
        using var s = e.Store();
        // Both test reviewers share one developer, so two distinct developers cannot be planned.
        Assert.Equal("too_few_vendors", Assert.Throws<ReviewError>(() => Build(e, s, 2)).Code);
        e.Put("reviewer-b.json", Data.Read<ReviewerProfile>(e.File("reviewer-b.json")) with { ModelDeveloper = "other-developer" });
        var p = Build(e, s, 2);Assert.Equal(2, p.MinVendors);
        // Plans without the rule serialize exactly as before (the field is left out).
        Assert.DoesNotContain("min_vendors", Data.Json(e.MakePlan(s)));Assert.Contains("\"min_vendors\": 2", Data.Json(p));
        // b's answer is truncated, so only one developer answered: the run is partial with too_few_vendors.
        var r = await new ReviewEngine(s).ExecuteAsync(p, e.Grant(p));
        Assert.Equal(20, r.ExitCode);Assert.Equal("partial", r.Result.Status);
        var vendors = Result(s, r)["vendors"]!;
        Assert.Equal((2, false), (vendors["required"]!.GetValue<int>(), vendors["met"]!.GetValue<bool>()));
        Assert.Equal(["fixture-developer"], vendors["answered"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains("Model developers answering: 1, at least 2 required", Report(s, r));
    }

    [Fact] public async Task TheVendorMinimumPassesWhenEnoughDevelopersAnswer()
    {
        using var e = new TestEnvironment(Text, [new(Reviewer: "a", Usage: new(100, 20)), new(Reviewer: "b", Usage: new(100, 20))]);
        e.Put("reviewer-b.json", Data.Read<ReviewerProfile>(e.File("reviewer-b.json")) with { ModelDeveloper = "other-developer" });
        using var s = e.Store();var p = Build(e, s, 2);var r = await new ReviewEngine(s).ExecuteAsync(p, e.Grant(p));
        Assert.Equal((0, "completed"), (r.ExitCode, r.Result.Status));
        Assert.True(Result(s, r)["vendors"]!["met"]!.GetValue<bool>());
    }
}
