using System.Net;
using System.Text.Json.Nodes;
using ReviewGate.App;
using ReviewGate.Contracts;
using ReviewGate.Testing;
using ReviewGate.Transport.Http;

namespace ReviewGate.AppTests;

public sealed class DesktopServiceTests
{
    const string Key = "TEST_ONLY_DESKTOP_KEY_SENTINEL_0123456789";
    static readonly DateTimeOffset Clock = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    // The real KeyRing over an in-memory vault, so the key-list rules under test are the shipped ones.
    sealed class FakeCredentials : ICredentialStore
    {
        public MemoryVault Vault { get; } = new();
        KeyRing Ring => new(Vault);
        public string? Read(string provider) => Ring.Read(provider);
        public IReadOnlyList<CredentialEntry> List(string provider) => Ring.List(provider);
        public string Add(string provider, string name, string secret) => Ring.Add(provider, name, secret);
        public bool Remove(string provider, string id) => Ring.Remove(provider, id);
        public void Activate(string provider, string id) => Ring.Activate(provider, id);
    }
    sealed class Recorder(string body) : HttpMessageHandler
    {
        public int Sent { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Sent++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }); }
    }
    static string Profiles => Path.Combine(TestEnvironment.Repository, "profiles");
    static DesktopService Service(TestEnvironment e, FakeCredentials keys, Recorder? http = null) => new(e.State, Profiles, keys, () => http, () => Clock);
    static string OpenAiAnswer(string payload) => new JsonObject
    {
        ["status"] = "completed",
        ["output"] = new JsonArray(new JsonObject { ["type"] = "message", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = payload }) }),
        ["usage"] = new JsonObject { ["input_tokens"] = 400, ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 }, ["output_tokens"] = 80, ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 }, ["total_tokens"] = 480 }
    }.ToJsonString();

    [Fact] public void NamedKeysAreListedWithoutValuesAndOneIsActive()
    {
        using var e = new TestEnvironment();var keys = new FakeCredentials();var app = Service(e, keys);
        Assert.All(app.CredentialStatus().Values, Assert.False);
        string personal = app.AddKey("google", "개인", Key + "A");string work = app.AddKey("google", "회사", Key + "B");
        var listed = app.Keys().Single(k => k.Provider.Id == "google").Keys;
        Assert.Equal(new[] { "개인", "회사" }, listed.Select(k => k.Name));Assert.Equal(work, listed.Single(k => k.Active).Id);Assert.Equal("사용 중: 회사", app.Keys().Single(k => k.Provider.Id == "google").Status);
        Assert.DoesNotContain(Key, System.Text.Json.JsonSerializer.Serialize(app.Keys()));
        app.ActivateKey("google", personal);Assert.Equal(Key + "A", keys.Read("google"));
        Assert.Equal("duplicate_key_name", Assert.Throws<ReviewError>(() => app.AddKey("google", "개인", Key)).Code);
        Assert.True(app.RemoveKey("google", personal));Assert.Equal(Key + "B", keys.Read("google")); // the single remaining key is used
        Assert.Equal("unsupported_provider", Assert.Throws<ReviewError>(() => app.AddKey("other", "x", Key)).Code);
    }
    [Fact] public void KeySavedBeforeNamesIsListedAndStaysUsableUntilTheUserChanges()
    {
        using var e = new TestEnvironment();var keys = new FakeCredentials();keys.Vault.Write("ReviewGate/live/openai", "reviewgate", Key);var app = Service(e, keys);
        var legacy = Assert.Single(app.Keys().Single(k => k.Provider.Id == "openai").Keys);Assert.Equal(("legacy", "기존 키", true), (legacy.Id, legacy.Name, legacy.Active));
        Assert.Equal(Key, keys.Read("openai"));
        string added = app.AddKey("openai", "새 키", Key + "N");Assert.Equal(Key + "N", keys.Read("openai"));Assert.True(keys.Vault.Items.ContainsKey("ReviewGate/live/openai"));
        app.ActivateKey("openai", "legacy");Assert.Equal(Key, keys.Read("openai"));
        app.RemoveKey("openai", "legacy");Assert.False(keys.Vault.Items.ContainsKey("ReviewGate/live/openai"));Assert.Equal(Key + "N", keys.Read("openai"));Assert.Equal(added, app.Keys().Single(k => k.Provider.Id == "openai").Keys.Single(k => k.Active).Id);
    }
    [Fact] public async Task PrimaryWithoutAnswerHandsOverToItsFallbackAndFreeTierNeedsNoReconciliation()
    {
        using var e = new TestEnvironment();var keys = new FakeCredentials();keys.Add("google", "test", Key);
        var handler = new ModelSwitch();var app = new DesktopService(e.State, Profiles, keys, () => handler, () => Clock);
        var settings = app.LoadSettings();Assert.Equal("google-gemini-3.5-flash-lite", settings.FallbackModels!["google"]);
        var preview = app.PrepareQuickTest(new(["google-gemini-3.5-flash"], "code", "Review", DesktopService.SampleCode, null, true, ["google-gemini-3.5-flash-lite"]));
        Assert.Equal(2, preview.Requests);Assert.Contains(preview.Recipients, r => r.Contains("응답이 없으면 서브: google gemini-3.5-flash-lite", StringComparison.Ordinal));
        var result = await app.RunAsync(preview.PlanId, app.Approve(preview.PlanId, 0.01m));
        Assert.Equal(new[] { "gemini-3.5-flash", "gemini-3.5-flash-lite" }, handler.Models);
        Assert.Equal("completed", result.Status);Assert.Equal(0m, result.UnknownHoldUsd);Assert.Equal(1m, result.ProcessingRatio);
    }
    // The primary model is busy (503); the fallback answers.
    sealed class ModelSwitch : HttpMessageHandler
    {
        public List<string> Models { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string model = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!["model"]!.GetValue<string>();Models.Add(model);
            if (model == "gemini-3.5-flash") return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":{\"code\":503,\"status\":\"UNAVAILABLE\"}}") };
            string payload = "{\"status\":\"success\",\"disposition\":\"no_finding\",\"findings\":[]}";
            return new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["status"] = "completed", ["steps"] = new JsonArray(new JsonObject { ["type"] = "model_output", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = payload }) }), ["usage"] = new JsonObject { ["total_input_tokens"] = 50, ["total_output_tokens"] = 5, ["total_thought_tokens"] = 0, ["total_tool_use_tokens"] = 0, ["total_tokens"] = 55 } }.ToJsonString()) };
        }
    }
    [Fact] public void ShippedModelsShowPricesAndGoogleFreeTierSetting()
    {
        using var e = new TestEnvironment();var app = Service(e, new());
        var models = app.Models();Assert.Equal(8, models.Count);Assert.All(models, m => Assert.False(m.Custom));
        Assert.True(models.Single(m => m.Id == "google-gemini-3.5-flash").Free);Assert.Equal(0.10m, models.Single(m => m.Id == "openai-gpt-6-luna").InputPerMillion);
        Assert.Equal("google-gemini-3.5-flash", app.LoadSettings().DefaultModels["google"]);
        app.SaveSettings(app.LoadSettings() with { GoogleFreeTier = false });
        var paid = app.Models();Assert.False(paid.Single(m => m.Id == "google-gemini-3.5-flash").Priced);Assert.Equal(0.75m, paid.Single(m => m.Id == "google-gemini-3.8-flash").InputPerMillion);
    }
    [Fact] public void CustomModelCanBeAddedPlannedAndRemoved()
    {
        using var e = new TestEnvironment();var app = Service(e, new());
        var custom = app.AddCustomModel("together", "Org/New-Model-7B", false, 0.2m, 0.6m);
        Assert.True(custom.Custom);Assert.Equal("together/org/new-model-7b", custom.CanonicalModelKey);Assert.Equal(0.6m, custom.OutputPerMillion);
        var preview = app.PrepareQuickTest(new([custom.Id], "code", "Review", DesktopService.SampleCode, null, true));Assert.Equal(1, preview.Requests);
        Assert.Contains(preview.Warnings, w => w.Contains("사용자가 추가한 모델", StringComparison.Ordinal));
        var free = app.AddCustomModel("google", "gemini-new-lite", true);Assert.True(free.Free);
        app.RemoveCustomModel(custom.Id);Assert.DoesNotContain(app.Models(), m => m.Id == custom.Id);
        Assert.Equal("price_required", Assert.Throws<ReviewError>(() => app.AddCustomModel("openai", "gpt-x", false)).Code);
        Assert.Equal("invalid_model_id", Assert.Throws<ReviewError>(() => app.AddCustomModel("openai", "bad id", true)).Code);
    }
    [Fact] public async Task QuickTestRunsThroughTheSameEngineAndShowsVerifiedFindings()
    {
        using var e = new TestEnvironment();var keys = new FakeCredentials();keys.Add("openai", "test", Key);
        string payload = "{\"status\":\"success\",\"disposition\":\"finding\",\"findings\":[{\"claim\":\"Division by zero\",\"detail\":\"total is divided by 0 and throws at runtime.\",\"quote\":\"total / 0\",\"source_id\":\"$source\",\"category\":\"logic\",\"severity\":\"high\",\"kind\":\"defect\",\"reported_evidence_grade\":\"observed\",\"confidence\":\"high\",\"suggestion\":\"Remove the division.\",\"impact\":\"Crash\",\"line\":4,\"start_byte\":null,\"assumptions\":[]}]}";
        var http = new Recorder(OpenAiAnswer(payload));var app = Service(e, keys, http);
        var preview = app.PrepareQuickTest(new(["openai-gpt-6-luna"], "code", "Find defects.", DesktopService.SampleCode, null, true));
        Assert.Equal(("single", 1, 1), (preview.Mode, preview.Requests, preview.SourceFiles));Assert.Contains("api.openai.com", Assert.Single(preview.Recipients));Assert.True(preview.ReservationUsd > 0);
        Assert.Equal(0, http.Sent);
        var grant = app.Approve(preview.PlanId, 0.05m);
        var result = await app.RunAsync(preview.PlanId, grant);
        Assert.Equal(1, http.Sent);Assert.Equal("completed", result.Status);Assert.Equal(1m, result.ProcessingRatio);Assert.Empty(result.UnitNotes);
        var f = Assert.Single(result.Findings);Assert.Equal(("Division by zero", "snippet.txt:4", "인용·위치 확인"), (f.Claim, f.Location, f.Evidence));
        Assert.Equal((400 * 0.10m + 80 * 0.50m) / 1_000_000m, result.UsageUsd);Assert.True(File.Exists(result.ReportPath));
        Assert.Contains(app.RecentRuns(), r => r.RunId == result.RunId && r.Status == "completed");
        foreach (var file in Directory.EnumerateFiles(e.State, "*", SearchOption.AllDirectories))
            Assert.False(File.ReadAllBytes(file).AsSpan().IndexOf(Data.Utf8.GetBytes(Key)) >= 0, file);
    }
    [Fact] public async Task MissingKeyAndUnconfirmedInputStopBeforeAnySend()
    {
        using var e = new TestEnvironment();var http = new Recorder("{}");var app = Service(e, new(), http);
        Assert.Equal("input_class_confirmation_required", Assert.Throws<ReviewError>(() => app.PrepareQuickTest(new(["openai-gpt-6-luna"], "code", "Review", "x", null, false))).Code);
        var preview = app.PrepareQuickTest(new(["openai-gpt-6-luna"], "code", "Review", "int x = 1;\n", null, true));
        var result = await app.RunAsync(preview.PlanId, app.Approve(preview.PlanId, 0.05m));
        Assert.Equal(0, http.Sent);Assert.Equal("credential_missing", result.ErrorCode);
    }
    [Fact] public async Task ProviderBusyLeavesUnknownThatTheUserCanSettle()
    {
        // A paid model: its unknown charge cannot be settled from the price alone, so the user decides.
        using var e = new TestEnvironment();var keys = new FakeCredentials();keys.Add("openai", "test", Key);
        var busy = new BusyHandler();var app = new DesktopService(e.State, Profiles, keys, () => busy, () => Clock);
        var preview = app.PrepareQuickTest(new(["openai-gpt-6-luna"], "code", "Review", DesktopService.SampleCode, null, true));
        var result = await app.RunAsync(preview.PlanId, app.Approve(preview.PlanId, 0.05m));
        Assert.True(result.UnknownHoldUsd > 0);
        Assert.Equal("needs_reconciliation", result.Status);Assert.Contains(result.UnitNotes, n => n.Contains("failed", StringComparison.Ordinal));
        var settled = await app.ResolveUnknownAsync(result.RunId!, "no_charge_confirmed", "Google free tier; provider answered 503.");
        Assert.Equal(0m, settled.UnknownHoldUsd);Assert.NotEqual("needs_reconciliation", settled.Status);
        Assert.Equal("nothing_to_resolve", (await Assert.ThrowsAsync<ReviewError>(() => app.ResolveUnknownAsync(result.RunId!, "no_charge_confirmed", "again"))).Code);
    }
    sealed class BusyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":{\"code\":503,\"status\":\"UNAVAILABLE\",\"message\":\"high demand\"}}") });
    }
    [Fact] public void FreeTierAndFolderInputAreExplainedBeforeApproval()
    {
        using var e = new TestEnvironment();File.WriteAllText(Path.Combine(e.Source, "App.csproj"), "<Project/>");var app = Service(e, new());
        var preview = app.PrepareQuickTest(new(["google-gemini-3.5-flash", "openai-gpt-6-luna"], "code", "Review", null, e.Source, true));
        Assert.Equal(("independent", 2, 1), (preview.Mode, preview.Requests, preview.SourceFiles));
        Assert.Contains(preview.Warnings, w => w.Contains("무료 티어", StringComparison.Ordinal));Assert.Contains(preview.Exclusions, x => x.Contains("App.csproj (unsupported_format)", StringComparison.Ordinal));
    }
}
