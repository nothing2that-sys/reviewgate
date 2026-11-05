using System.Net;
using System.Text.Json.Nodes;
using ReviewGate.Contracts;
using ReviewGate.Core.Execution;
using ReviewGate.Core.Planning;
using ReviewGate.Infrastructure;
using ReviewGate.Providers;
using ReviewGate.Testing;
using ReviewGate.Transport.Http;

namespace ReviewGate.ProviderTests;

// Live path tests with an in-memory key store and a recording HTTP handler: no real network or credential store.
public sealed class LiveTransportTests
{
    const string Key = "TEST_ONLY_LIVE_KEY_SENTINEL_0123456789";
    // The real KeyRing over an in-memory vault, pre-loaded with one active key per named provider.
    sealed class FakeCredentials : ICredentialStore
    {
        readonly KeyRing ring = new(new MemoryVault());
        public FakeCredentials(params string[] providers) { foreach (var p in providers) ring.Add(p, "test", Key); }
        public string? Read(string provider) => ring.Read(provider);
        public IReadOnlyList<CredentialEntry> List(string provider) => ring.List(provider);
        public string Add(string provider, string name, string secret) => ring.Add(provider, name, secret);
        public bool Remove(string provider, string id) => ring.Remove(provider, id);
        public void Activate(string provider, string id) => ring.Activate(provider, id);
    }
    sealed class Recorder(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Sent { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add((request, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return await respond(request, cancellationToken);
        }
    }
    static Recorder Reply(HttpStatusCode status, string body) => new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));
    static ReviewerProfile Reviewer(string provider) => new(Data.Version, "live", provider, "model-x", provider == "openai" ? "responses" : provider == "together" ? "chat_completions" : "interactions", "test/" + provider, TimeoutSeconds: 1, MaxReasoningTokens: provider == "google" ? 512 : 0);
    // Written independently of WrapFixture so the parser is checked against a hand-built provider body.
    static string OpenAiBody(string payload, long input = 120, long output = 30) => new JsonObject
    {
        ["status"] = "completed",
        ["output"] = new JsonArray(new JsonObject { ["type"] = "message", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = payload }) }),
        ["usage"] = new JsonObject { ["input_tokens"] = input, ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 }, ["output_tokens"] = output, ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 }, ["total_tokens"] = input + output }
    }.ToJsonString();

    [Fact] public async Task OpenAiAndTogetherUseFixedEndpointAndBearerKey()
    {
        foreach (var provider in new[] { "openai", "together" })
        {
            var handler = Reply(HttpStatusCode.OK, "{}");using var transport = new HttpTransport(new FakeCredentials(provider), handler);
            var result = await transport.SendAsync(Reviewer(provider), "{\"x\":1}", CancellationToken.None);
            Assert.Equal("received", result.Outcome);var (request, body) = Assert.Single(handler.Sent);
            Assert.Equal(HttpTransport.Endpoints[provider], request.RequestUri);Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);Assert.Equal(Key, request.Headers.Authorization.Parameter);Assert.Equal("{\"x\":1}", body);
        }
        Assert.Equal("https://api.together.ai/v1/chat/completions", HttpTransport.Endpoints["together"].ToString());
    }
    [Fact] public async Task GoogleUsesApiKeyHeaderAndPinnedRevision()
    {
        var handler = Reply(HttpStatusCode.OK, "{}");using var transport = new HttpTransport(new FakeCredentials("google"), handler);
        await transport.SendAsync(Reviewer("google"), "{}", CancellationToken.None);
        var request = Assert.Single(handler.Sent).Request;Assert.Null(request.Headers.Authorization);
        Assert.Equal(Key, Assert.Single(request.Headers.GetValues("x-goog-api-key")));Assert.Equal(HttpTransport.GoogleApiRevision, Assert.Single(request.Headers.GetValues("Api-Revision")));
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/interactions", request.RequestUri!.ToString());
    }
    [Fact] public async Task TimeoutConnectionLossRedirectAndOversizeAreUnknownNotRetried()
    {
        var slow = new Recorder(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return new HttpResponseMessage(HttpStatusCode.OK); });
        using (var t = new HttpTransport(new FakeCredentials("openai"), slow)) { var r = await t.SendAsync(Reviewer("openai"), "{}", CancellationToken.None); Assert.Equal(("unknown", "timeout"), (r.Outcome, r.ErrorCode)); Assert.Single(slow.Sent); }
        var broken = new Recorder((_, _) => throw new HttpRequestException("connection reset"));
        using (var t = new HttpTransport(new FakeCredentials("openai"), broken)) Assert.Equal("network_error", (await t.SendAsync(Reviewer("openai"), "{}", CancellationToken.None)).ErrorCode);
        using (var t = new HttpTransport(new FakeCredentials("openai"), Reply(HttpStatusCode.Found, ""))) Assert.Equal("unexpected_redirect", (await t.SendAsync(Reviewer("openai"), "{}", CancellationToken.None)).ErrorCode);
        using (var t = new HttpTransport(new FakeCredentials("openai"), Reply(HttpStatusCode.OK, new string('x', HttpTransport.MaxResponseBytes + 1)))) Assert.Equal("response_too_large", (await t.SendAsync(Reviewer("openai"), "{}", CancellationToken.None)).ErrorCode);
    }
    [Fact] public async Task MissingKeyFailsBeforeSending()
    {
        var handler = Reply(HttpStatusCode.OK, "{}");using var transport = new HttpTransport(new FakeCredentials(), handler);
        Assert.False(transport.HasCredential("openai"));
        Assert.Equal(11, (await Assert.ThrowsAsync<ReviewError>(() => transport.SendAsync(Reviewer("openai"), "{}", CancellationToken.None))).ExitCode);Assert.Empty(handler.Sent);
    }
    [Fact] public void DocumentedProviderStatusesAndGoogleThinkingUsage()
    {
        string payload = "{\"status\":\"success\",\"disposition\":\"no_finding\",\"findings\":[]}";
        var eos = WireAdapters.Parse(Reviewer("together"), new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = "eos", ["message"] = new JsonObject { ["content"] = payload } }), ["usage"] = new JsonObject { ["prompt_tokens"] = 5, ["completion_tokens"] = 2, ["total_tokens"] = 7 } }.ToJsonString());
        Assert.Equal("success", eos.Status);
        Assert.Equal("error", WireAdapters.Parse(Reviewer("openai"), "{\"status\":\"failed\",\"output\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}").Status);
        Assert.Equal("refused", WireAdapters.Parse(Reviewer("openai"), "{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"content_filter\"},\"output\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}").Status);
        // Documented example: total 79 = 7 input + 23 output + 49 thought.
        var google = WireAdapters.Parse(Reviewer("google"), new JsonObject { ["status"] = "completed", ["steps"] = new JsonArray(new JsonObject { ["type"] = "model_output", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = payload }) }), ["usage"] = new JsonObject { ["total_input_tokens"] = 7, ["total_output_tokens"] = 23, ["total_thought_tokens"] = 49, ["total_cached_tokens"] = 0, ["total_tool_use_tokens"] = 0, ["total_tokens"] = 79 } }.ToJsonString());
        Assert.True(google.Usage.Known);Assert.Equal(49, google.Usage.ReasoningTokens);
        // Gemini output price includes thinking: thought tokens billed at the output rate through a separate reasoning row.
        Assert.Equal((7 * .75m + 23 * 3.75m + 49 * 3.75m) / 1_000_000m, WireAdapters.Cost(google.Usage, new("test/google", .75m, 3.75m, .075m, OutputIncludesReasoning: false, ReasoningPerMillion: 3.75m)));
    }

    static (StateStore Store, Plan Plan) LivePlan(TestEnvironment e, Action<TestEnvironment>? adjust = null)
    {
        adjust?.Invoke(e);
        var store = new StateStore(e.State, null, "live");
        try { return (store, Planner.Build(store, new("test", e.Source, e.Source, "nonsensitive", "Review supplied source", e.File("profile.json"), [e.File("reviewer.json")], e.File("budget.json"), e.File("prices.json"), null, ["Sample.cs"], []))); }
        catch { store.Dispose(); throw; }
    }
    static Grant Issue(TestEnvironment e, StateStore store, Plan plan)
    {
        var g = new Grant(Data.Version, Data.Id("grant"), plan.Hash, plan.ManifestHash, DateTimeOffset.UtcNow.AddHours(1), 1, 10, 0, "live");
        store.RecordGrantIssue(plan, g, "test");return g;
    }
    static void AssertKeyNowhere(TestEnvironment e)
    {
        foreach (var file in Directory.EnumerateFiles(e.State, "*", SearchOption.AllDirectories))
            Assert.False(File.ReadAllBytes(file).AsSpan().IndexOf(Data.Utf8.GetBytes(Key)) >= 0, file);
    }
    [Fact] public async Task LiveRunSendsPinnedBodyAndSettlesRealUsageWithoutStoringKey()
    {
        using var e = new TestEnvironment();var (store, plan) = LivePlan(e);
        Assert.StartsWith("lplan_", plan.Id);Assert.Equal("live", plan.ExecutionMode);Assert.Equal("live-none", plan.Fixture.Id);
        string payload = "{\"status\":\"success\",\"disposition\":\"finding\",\"findings\":[{\"claim\":\"Value may be wrong\",\"detail\":\"Reason\",\"quote\":\"result = 1\",\"source_id\":\"$source\",\"category\":\"logic\",\"severity\":\"low\",\"kind\":\"defect\",\"reported_evidence_grade\":\"unconfirmed\",\"confidence\":\"low\",\"suggestion\":\"\",\"impact\":\"\",\"line\":1,\"start_byte\":null,\"assumptions\":[]}]}";
        var handler = Reply(HttpStatusCode.OK, OpenAiBody(payload));
        using (store) using (var transport = new HttpTransport(new FakeCredentials("openai"), handler))
        {
            var r = await new ReviewEngine(store, transport).ExecuteAsync(plan, Issue(e, store, plan));
            Assert.Equal(0, r.ExitCode);Assert.Equal("completed", r.Result.Status);Assert.Equal("live", r.Result.ExecutionMode);Assert.StartsWith("lrun_", r.Result.RunId);
            Assert.Equal(plan.Requests.Single().Body, Assert.Single(handler.Sent).Body);
            Assert.Equal(1m, r.Result.Coverage!.ActualModelProcessingRatio);Assert.Null(r.Result.Coverage.SyntheticProcessingRatio);
            Assert.Equal((120 * 1m + 30 * 2m) / 1_000_000m, store.Attempts().Single().CostUsd);
            Assert.Equal("Value may be wrong", store.Units(r.Result.RunId!).Single().Findings.Single().Claim);
            Assert.Contains("live_model_review", r.Result.Limitations);
        }
        AssertKeyNowhere(e);
    }
    [Fact] public async Task LiveRefusesHandMadeGrantAndMissingKeyBeforeAnyDispatch()
    {
        using var e = new TestEnvironment();var (store, plan) = LivePlan(e);var handler = Reply(HttpStatusCode.OK, "{}");
        using (store)
        {
            using (var transport = new HttpTransport(new FakeCredentials("openai"), handler))
            {
                var unissued = new Grant(Data.Version, Data.Id("grant"), plan.Hash, plan.ManifestHash, DateTimeOffset.UtcNow.AddHours(1), 1, 10, 0, "live");
                var r = await new ReviewEngine(store, transport).ExecuteAsync(plan, unissued);Assert.Equal(10, r.ExitCode);Assert.Equal("grant_not_issued", r.Result.RunErrorCode);
            }
            using (var transport = new HttpTransport(new FakeCredentials(), handler))
            {
                var r = await new ReviewEngine(store, transport).ExecuteAsync(plan, Issue(e, store, plan));Assert.Equal(11, r.ExitCode);Assert.Equal("credential_missing", r.Result.RunErrorCode);
            }
            Assert.Empty(handler.Sent);Assert.Empty(store.Attempts());
        }
    }
    [Fact] public async Task LiveAuthErrorIsRedactedAndTimeoutNeedsReconciliation()
    {
        using var e = new TestEnvironment();var (store, plan) = LivePlan(e);
        var echo = Reply(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Incorrect API key provided: " + Key + "\"}}");
        using (store)
        {
            using (var transport = new HttpTransport(new FakeCredentials("openai"), echo))
            {
                var r = await new ReviewEngine(store, transport).ExecuteAsync(plan, Issue(e, store, plan));
                Assert.Equal(13, r.ExitCode);Assert.Equal("unknown", store.Attempts().Single().Phase); // no usage in a 401 body: cost stays unknown
            }
        }
        AssertKeyNowhere(e);
        using var e2 = new TestEnvironment();var (store2, plan2) = LivePlan(e2, x => x.Put("reviewer.json", x.Reviewer with { TimeoutSeconds = 1 }));
        var slow = new Recorder(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return new HttpResponseMessage(HttpStatusCode.OK); });
        using (store2) using (var transport = new HttpTransport(new FakeCredentials("openai"), slow))
        {
            var r = await new ReviewEngine(store2, transport).ExecuteAsync(plan2, Issue(e2, store2, plan2));
            Assert.Equal(13, r.ExitCode);Assert.Equal("needs_reconciliation", r.Result.Status);var a = store2.Attempts().Single();Assert.Equal(("unknown", "timeout"), (a.Phase, a.ErrorCode));Assert.Single(slow.Sent);
        }
    }
    [Fact] public void LivePlanRejectsFixturesAndUnboundedGoogleThinking()
    {
        using var e = new TestEnvironment();using var store = new StateStore(e.State, null, "live");
        Assert.Equal("fixture_not_allowed_in_live", Assert.Throws<ReviewError>(() => Planner.Build(store, new("test", e.Source, e.Source, "nonsensitive", "Review", e.File("profile.json"), [e.File("reviewer.json")], e.File("budget.json"), e.File("prices.json"), e.File("fixture.json"), ["Sample.cs"], []))).Code);
        e.Put("reviewer.json", e.Reviewer with { HostingProvider = "google", ApiProfile = "interactions", MaxReasoningTokens = 0 });
        Assert.Equal("unbounded_request", Assert.Throws<ReviewError>(() => LivePlan(e)).Code);
        e.Put("reviewer.json", e.Reviewer with { HostingProvider = "google", ApiProfile = "interactions", MaxReasoningTokens = 512 });
        Assert.Equal("price_reasoning_rule_mismatch", Assert.Throws<ReviewError>(() => LivePlan(e)).Code);
    }
    // Shape captured from the first real Together GLM-5.3-Flash answer (2026-11-04): no_finding plus a question item,
    // a real source_id and a miscounted start_byte. It must count as a valid review with a location-invalid note.
    [Fact] public async Task RealWorldNoFindingWithQuestionAndMiscountedOffsetIsAValidReview()
    {
        using var e = new TestEnvironment();var (store, plan) = LivePlan(e);var sid = plan.Sources.Single().Id;
        string payload = new JsonObject { ["status"] = "success", ["disposition"] = "no_finding", ["findings"] = new JsonArray(new JsonObject {
            ["claim"] = "Single declaration with no visible defect.", ["detail"] = "No use of the variable occurs in this chunk.", ["quote"] = "int result = 1;", ["source_id"] = sid,
            ["category"] = "errorhandling", ["severity"] = "low", ["kind"] = "question", ["reported_evidence_grade"] = "unconfirmed", ["confidence"] = "high",
            ["suggestion"] = "None required.", ["impact"] = "None", ["line"] = null, ["start_byte"] = 46, ["assumptions"] = new JsonArray("C# top-level statements assumed.") }) }.ToJsonString();
        using (store) using (var transport = new HttpTransport(new FakeCredentials("openai"), Reply(HttpStatusCode.OK, OpenAiBody(payload))))
        {
            var r = await new ReviewEngine(store, transport).ExecuteAsync(plan, Issue(e, store, plan));
            Assert.Equal(0, r.ExitCode);var unit = store.Units(r.Result.RunId!).Single();Assert.True(unit.Valid);Assert.Equal("no_finding", unit.ReviewDisposition);
            var f = unit.Findings.Single();Assert.Equal("question", f.Kind);Assert.True(f.QuoteValid);Assert.False(f.LocationValid);Assert.Equal(1, f.Line);
        }
        // Second captured shape: an all-empty placeholder defect appended under no_finding is dropped, not a contradiction.
        var padded = JsonNode.Parse(payload)!;padded["findings"]!.AsArray().Add(new JsonObject { ["claim"] = "", ["detail"] = "", ["quote"] = "", ["source_id"] = "", ["category"] = "nullderef", ["severity"] = "high", ["kind"] = "defect", ["reported_evidence_grade"] = "unconfirmed", ["confidence"] = "low", ["suggestion"] = "", ["impact"] = "", ["line"] = null, ["start_byte"] = null, ["assumptions"] = new JsonArray() });
        var parsed = WireAdapters.Parse(Reviewer("openai"), OpenAiBody(padded.ToJsonString()));Assert.Equal(("success", "no_finding", 1), (parsed.Status, parsed.Disposition, parsed.Findings.Count));
        // A defect under no_finding is still a contradiction.
        Assert.Equal(WireAdapters.InvalidPayload, WireAdapters.Parse(Reviewer("openai"), OpenAiBody(payload.Replace("\"question\"", "\"defect\"", StringComparison.Ordinal))).Status);
    }
    [Fact] public void NoChargeResolutionIsLiveOnlyAndRecordsZeroCost()
    {
        using var e = new TestEnvironment();var (store, plan) = LivePlan(e);
        using (store)
        {
            var run = store.NewRun(plan);var g = Issue(e, store, plan);var a = store.Reserve(run, plan, plan.Requests[0], g);store.Dispatch(a.Id);store.Unknown(a.Id, "cost_unknown");
            store.Resolve(run, plan, new(Data.Version, plan.Hash, [new(a.Id, "no_charge_confirmed", "Google free tier; user confirmed no billing", "user", DateTimeOffset.UtcNow)], "live"));
            var settled = store.Attempts(run.Id).Single();Assert.Equal(("settled", 0m), (settled.Phase, settled.CostUsd!.Value));Assert.Equal(0m, store.Costs(run.Id)["unknown_hold_usd"]!.GetValue<decimal>());
        }
        using var s = e.Store();var p = e.MakePlan(s);var r = s.NewRun(p);var b = s.Reserve(r, p, p.Requests[0], e.Grant(p));s.Dispatch(b.Id);s.Unknown(b.Id, "unknown");
        Assert.Equal("invalid_resolution", Assert.Throws<ReviewError>(() => s.Resolve(r, p, new(Data.Version, p.Hash, [new(b.Id, "no_charge_confirmed", "claim", "user", DateTimeOffset.UtcNow)]))).Code);
    }
    [Fact] public void ErrorMessageIsKeptWithKeyLikeTokensMasked()
    {
        var redacted = JsonNode.Parse(WireAdapters.RedactErrorBody("{\"error\":{\"code\":\"service_unavailable\",\"message\":\"Model unavailable for key AIzaSyTESTONLYxxxxxxxxxxxxxxxxxxxxxxx. Retry later.\"}}"))!;
        Assert.Equal("service_unavailable", redacted["error"]!["code"]!.GetValue<string>());
        Assert.Equal("Model unavailable for key [masked]. Retry later.", redacted["error"]!["message"]!.GetValue<string>());
    }
    [Fact] public void ShippedLiveProfilesPlanWithinTheSmokeBudget()
    {
        string profiles = Path.Combine(TestEnvironment.Repository, "profiles");
        var reviewers = Directory.GetFiles(Path.Combine(profiles, "reviewers"), "*.json");Assert.Equal(8, reviewers.Length);
        foreach (var reviewer in reviewers)
        {
            using var e = new TestEnvironment();
            using var store = new StateStore(e.State, () => new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero), "live");
            // gemini-3.5-flash has no verified paid price; it is shipped for free-tier use with the free snapshot only.
            bool freeOnly = reviewer.Contains("3.5-flash");
            var prices = Path.Combine(profiles, "prices", freeOnly ? "live-2026-11-04-google-free.json" : "live-2026-11-04.json");
            var plan = Planner.Build(store, new("smoke", e.Source, e.Source, "nonsensitive", "Review supplied source", Path.Combine(profiles, "code.json"), [reviewer], Path.Combine(profiles, "budget-live.json"), prices, null, ["Sample.cs"], []));
            var request = Assert.Single(plan.Requests);
            if (freeOnly) { Assert.Equal(0m, request.ReservationUsd); continue; }
            // The reservation is the declared worst case (full input, output and thinking bounds), not an expected charge.
            // One small file stays well under the 1 USD run budget; the low-cost models stay under 10 cents.
            Assert.InRange(request.ReservationUsd, 0.0001m, 0.5m);
            if (reviewer.Contains("luna") || reviewer.Contains("glm") || reviewer.Contains("3.8-flash")) Assert.True(request.ReservationUsd < 0.10m, reviewer);
        }
    }
    [Fact] public async Task SyntheticRunNeverTouchesTheLiveTransport()
    {
        using var e = new TestEnvironment();using var s = e.Store();var p = e.MakePlan(s);var handler = Reply(HttpStatusCode.OK, "{}");
        using var transport = new HttpTransport(new FakeCredentials("openai"), handler);
        Assert.Equal(0, (await new ReviewEngine(s, transport).ExecuteAsync(p, e.Grant(p))).ExitCode);Assert.Empty(handler.Sent);
        using var live = new StateStore(e.State, null, "live");
        Assert.Equal("execution_mode_mismatch", (await Assert.ThrowsAsync<ReviewError>(() => new ReviewEngine(live, transport).ExecuteAsync(p, e.Grant(p)))).Code);
    }
}
