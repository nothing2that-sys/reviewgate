using System.Net;
using System.Text.Json.Nodes;
using ReviewGate.App;
using ReviewGate.Contracts;
using ReviewGate.Testing;

namespace ReviewGate.AppTests;

public sealed class DesktopServiceTests
{
    const string Key = "TEST_ONLY_DESKTOP_KEY_SENTINEL_0123456789";
    static readonly DateTimeOffset Clock = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    sealed class FakeCredentials : ICredentialStore
    {
        public Dictionary<string, string> Keys { get; } = [];
        public string? Read(string provider) => Keys.GetValueOrDefault(provider);
        public void Write(string provider, string secret) => Keys[provider] = secret;
        public bool Delete(string provider) => Keys.Remove(provider);
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

    [Fact] public void KeysAreStoredAndReportedByPresenceOnly()
    {
        using var e = new TestEnvironment();var keys = new FakeCredentials();var app = Service(e, keys);
        Assert.All(app.CredentialStatus().Values, Assert.False);
        app.SaveKey("google", Key);Assert.True(app.CredentialStatus()["google"]);Assert.Equal(Key, keys.Keys["google"]);
        Assert.True(app.DeleteKey("google"));Assert.False(app.CredentialStatus()["google"]);
        Assert.Equal("unsupported_provider", Assert.Throws<ReviewError>(() => app.SaveKey("other", Key)).Code);
    }
    [Fact] public void ShippedModelsShowPricesAndGoogleFreeTierSetting()
    {
        using var e = new TestEnvironment();var app = Service(e, new());
        var models = app.Models();Assert.Equal(7, models.Count);Assert.All(models, m => Assert.False(m.Custom));
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
        using var e = new TestEnvironment();var keys = new FakeCredentials();keys.Write("openai", Key);
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
        using var e = new TestEnvironment();var keys = new FakeCredentials();keys.Write("google", Key);
        var busy = new BusyHandler();var app = new DesktopService(e.State, Profiles, keys, () => busy, () => Clock);
        var preview = app.PrepareQuickTest(new(["google-gemini-3.5-flash"], "code", "Review", DesktopService.SampleCode, null, true));
        var result = await app.RunAsync(preview.PlanId, app.Approve(preview.PlanId, 0.01m));
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
