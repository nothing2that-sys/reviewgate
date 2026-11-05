using ReviewGate.Contracts;
using ReviewGate.Core.Execution;
using ReviewGate.Core.Planning;
using ReviewGate.Infrastructure;
using ReviewGate.Testing;

namespace ReviewGate.StateTests;

// Fallback reviewers are pre-approved in the plan and used only when the primary gives no answer.
public sealed class FallbackTests
{
    static TestEnvironment Env(decimal primaryPrice, params FixtureResponse[] responses)
    {
        var e = new TestEnvironment(responses: [.. responses]);
        e.Put("reviewer-c.json", e.Reviewer with { Id = "c", ModelId = "fixture-model-c", CanonicalModelKey = "fixture/c" });
        e.Put("prices.json", new PriceSnapshot(Data.Version, "synthetic", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), "synthetic://test", Data.Hash("t"),
            [new("fixture/a", primaryPrice, primaryPrice), new("fixture/b", 1, 2), new("fixture/c", 1, 2)]));
        return e;
    }
    static Plan PlanWithFallback(TestEnvironment e, StateStore s, string? fallback = "reviewer-c.json") => Planner.Build(s, new("test", e.Source, e.Source, "synthetic", "Review",
        e.File("profile.json"), [e.File("reviewer.json")], e.File("budget.json"), e.File("prices.json"), e.File("fixture.json"), ["Sample.cs"], [],
        FallbackReviewerFiles: [fallback is null ? null : e.File(fallback)]));

    [Fact] public async Task BusyPrimaryHandsOverToFallbackAndItsUnknownCostStaysVisible()
    {
        using var e = Env(1, new(Reviewer: "a", HttpStatus: 503), new(Reviewer: "c", Usage: new(100, 20)));
        using var s = e.Store();var p = PlanWithFallback(e, s);
        Assert.Equal(2, p.Requests.Count);Assert.Equal(p.Requests[0].Id, p.Requests[1].FallbackFor);Assert.Equal("c", p.Fallbacks["a"]);
        var r = await new ReviewEngine(s).ExecuteAsync(p, e.Grant(p));
        // The fallback answered, so coverage is complete; the primary's charge is still unknown and must be reconciled.
        Assert.True(r.Result.Coverage!.RequiredCoverageComplete);Assert.Equal(1, r.Result.Coverage.RequiredUnits);
        Assert.Equal(13, r.ExitCode);Assert.Equal("needs_reconciliation", r.Result.Status);
        Assert.Equal(new[] { "unknown", "settled" }, s.Attempts().Select(a => a.Phase));
        // Resuming does not resend anything: the slot is filled and the unknown is covered by the fallback.
        var again = await new ReviewEngine(s).ExecuteAsync(p, e.Grant(p, r.Result.RunId), r.Result.RunId);Assert.Equal(2, s.Attempts().Count);Assert.Equal(13, again.ExitCode);
    }
    [Fact] public async Task FreePrimaryWithoutAnswerSettlesAtZeroAndCompletesThroughFallback()
    {
        using var e = Env(0, new(Reviewer: "a", Status: "timeout"), new(Reviewer: "c", Usage: new(100, 20)));
        using var s = e.Store();var p = PlanWithFallback(e, s);
        var r = await new ReviewEngine(s).ExecuteAsync(p, e.Grant(p));
        Assert.Equal(0, r.ExitCode);Assert.Equal("completed", r.Result.Status);
        var primary = s.Attempts().First();Assert.Equal(("settled", 0m, "zero_price_no_charge"), (primary.Phase, primary.CostUsd!.Value, primary.ErrorCode));
    }
    [Fact] public async Task AnsweredButUnusableOrRejectedKeyDoesNotUseTheFallback()
    {
        using (var e = Env(1, new(Reviewer: "a", Status: "truncated", Usage: new(100, 20)), new(Reviewer: "c", Usage: new(100, 20))))
        {
            using var s = e.Store();var p = PlanWithFallback(e, s);var r = await new ReviewEngine(s).ExecuteAsync(p, e.Grant(p));
            Assert.Single(s.Attempts());Assert.False(r.Result.Coverage!.RequiredCoverageComplete);
        }
        using (var e = Env(1, new(Reviewer: "a", HttpStatus: 401, Usage: new(1, 0)), new(Reviewer: "c", Usage: new(100, 20))))
        {
            using var s = e.Store();var p = PlanWithFallback(e, s);var r = await new ReviewEngine(s).ExecuteAsync(p, e.Grant(p));
            Assert.Equal(11, r.ExitCode);Assert.Single(s.Attempts());
        }
    }
    [Fact] public async Task WithoutFallbackAnUnknownStillStopsTheRun()
    {
        using var e = Env(1, new FixtureResponse(Reviewer: "a", Status: "timeout"));using var s = e.Store();var p = PlanWithFallback(e, s, null);
        Assert.Single(p.Requests);var r = await new ReviewEngine(s).ExecuteAsync(p, e.Grant(p));Assert.Equal(13, r.ExitCode);Assert.Equal("unknown", s.Attempts().Single().Phase);
    }
    [Fact] public void FallbackMustBeAnotherPricedModel()
    {
        using var e = Env(1, new FixtureResponse(Usage: new(1, 1)));using var s = e.Store();
        e.Put("same.json", e.Reviewer with { Id = "same", CanonicalModelKey = "fixture/same" });
        Assert.Equal("duplicate_reviewer_model", Assert.Throws<ReviewError>(() => PlanWithFallback(e, s, "same.json")).Code);
        e.Put("unpriced.json", e.Reviewer with { Id = "u", ModelId = "other-model", CanonicalModelKey = "fixture/unpriced" });
        Assert.Equal("price_missing", Assert.Throws<ReviewError>(() => PlanWithFallback(e, s, "unpriced.json")).Code);
    }
}
