using System.Text.Json.Nodes;
using ReviewGate.App;
using ReviewGate.Contracts;
using ReviewGate.Testing;
using ReviewGate.Transport.Cli;
using ReviewGate.Transport.Http;

namespace ReviewGate.AppTests;

// Price refresh with fake CLI answers: no process is started and no page is fetched.
public sealed class PriceRefreshTests
{
    static readonly DateTimeOffset Clock = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    static string Profiles => Path.Combine(TestEnvironment.Repository, "profiles");
    sealed class NoKeys : ICredentialStore
    {
        readonly KeyRing ring = new(new MemoryVault());
        public string? Read(string provider) => ring.Read(provider);
        public IReadOnlyList<CredentialEntry> List(string provider) => ring.List(provider);
        public string Add(string provider, string name, string secret) => ring.Add(provider, name, secret);
        public bool Remove(string provider, string id) => ring.Remove(provider, id);
        public void Activate(string provider, string id) => ring.Activate(provider, id);
    }
    static JsonObject Row(string model, decimal? input, decimal? cached, decimal? output, string free = "unknown", string? next = null, string? quote = null) => new()
    {
        ["model_id"] = model, ["found"] = input is not null, ["input_per_million"] = input, ["cached_input_per_million"] = cached, ["output_per_million"] = output,
        ["free_tier"] = free, ["quote"] = quote ?? (input is null ? "" : $"{model} Input ${input:0.00##} Cached ${cached:0.00###} Output ${output:0.00##}"),
        ["source_url"] = "https://example.invalid/pricing", ["next_change_date"] = next, ["next_change_quote"] = next is null ? null : "changes " + next
    };
    // What both CLIs report, unless a test changes one of them.
    static List<JsonObject> Page() =>
    [
        Row("gpt-6-luna", 0.12m, 0.012m, 0.60m), Row("gpt-6.1-sol", 2.00m, 0.10m, 10.00m),
        Row("zai-org/GLM-5.3-Flash", 0.15m, 0.03m, 0.50m), Row("Qwen/Qwen3.8-2.4T-A95B", 2.00m, 0.25m, 6.00m),
        Row("gemini-3.8-flash", 0.75m, 0.075m, 3.75m, "yes", "2027-01-01"), Row("gemini-4-argon", 4.00m, 0.20m, 20.00m, "no"),
        Row("gemini-3.5-flash-lite", 0.30m, 0.03m, 2.50m, "yes"), Row("gemini-3.5-flash", null, null, null)
    ];
    static TransportResult Answer(IEnumerable<JsonObject> rows) => new("received", 200,
        new JsonObject { ["status"] = "completed", ["structured_output"] = new JsonObject { ["models"] = new JsonArray([.. rows]) } }.ToJsonString(), null);
    static DesktopService Service(TestEnvironment e, Func<string, CliJob, TransportResult> answer, DateTimeOffset? now = null, List<(string Cli, CliJob Job)>? seen = null) =>
        new(e.State, Profiles, new NoKeys(), clock: () => now ?? Clock, locateCli: _ => null,
            cliJob: (cli, job, _) => { seen?.Add((cli, job)); return Task.FromResult(answer(cli, job)); });

    [Fact] public void StatusWarnsSevenDaysBeforeExpiryAndAfterIt()
    {
        using var e = new TestEnvironment();
        var calm = Service(e, (_, _) => Answer([])).PriceStatus();
        Assert.Equal(("live-2026-11-04-google-free", false, false), (calm.SnapshotId, calm.Expired, calm.NoticeDue));
        Assert.True(Service(e, (_, _) => Answer([]), new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero)).PriceStatus().NoticeDue);
        Assert.True(Service(e, (_, _) => Answer([]), new(2026, 12, 25, 0, 0, 0, TimeSpan.Zero)).PriceStatus().NoticeDue); // the December snapshot ends 2027-01-01 KST
        var expired = Service(e, (_, _) => Answer([]), new(2027, 2, 1, 0, 0, 0, TimeSpan.Zero)).PriceStatus();
        Assert.True(expired.Expired);Assert.True(expired.NoticeDue);
    }
    [Fact] public async Task BothClisAreAskedWithWebAccessAndOnlyAgreedQuotedRowsAreApplied()
    {
        using var e = new TestEnvironment();var seen = new List<(string Cli, CliJob Job)>();
        var app = Service(e, (cli, _) => Answer(Page().Select(r =>
            // Codex reads Together's run-together prices differently: that row must not be applied.
            cli == "codex-cli" && r["model_id"]!.GetValue<string>() == "zai-org/GLM-5.3-Flash" ? Row("zai-org/GLM-5.3-Flash", 0.1503m, 0.03m, 0.50m) :
            // Claude's quote for sol lacks the output number: unverified.
            cli == "claude-cli" && r["model_id"]!.GetValue<string>() == "gpt-6.1-sol" ? Row("gpt-6.1-sol", 2.00m, 0.10m, 10.00m, quote: "gpt-6.1-sol Input $2.00 Cached $0.10") : r)), seen: seen);
        var proposal = await app.LookupPricesAsync();
        Assert.Equal(new[] { "claude-cli", "codex-cli" }, seen.Select(s => s.Cli).Order());
        Assert.All(seen, s => { Assert.True(s.Job.Web);Assert.Null(s.Job.Workdir);Assert.Contains("https://www.together.ai/pricing", s.Job.Prompt); });
        string State(string model) => proposal.Rows.Single(r => r.Target.ModelId == model).Status;
        Assert.Equal("agreed", State("gpt-6-luna"));Assert.Equal("unverified_quote", State("gpt-6.1-sol"));Assert.Equal("disagree", State("zai-org/GLM-5.3-Flash"));
        Assert.Equal("missing", State("gemini-3.5-flash"));Assert.Equal("agreed", State("gemini-4-argon"));
        Assert.Contains(proposal.Rows.Single(r => r.Target.ModelId == "gemini-4-argon").Notes, n => n.StartsWith("큰 변경", StringComparison.Ordinal)); // 2.00 → 4.00
        Assert.False(proposal.Rows.Single(r => r.Target.ModelId == "gemini-4-argon").FreeTierAvailable);

        // The user keeps 3.5-flash and 3.8-flash on the free tier; argon cannot be free even if asked.
        // The lookup is on record before the user decides, with what each CLI answered.
        var pending = Assert.Single(app.PriceLookupHistory());
        Assert.Equal((proposal.RecordId, "pending"), (pending.Id, pending.Decision));
        Assert.All(PriceRefresh.Clis, cli => Assert.Contains("structured_output", pending.Answers[cli].Body));
        var status = app.ApprovePrices(proposal, new HashSet<string> { "google/gemini-3.5-flash", "google/gemini-3.8-flash", "google/gemini-4-argon" });
        var approved = Assert.Single(app.PriceLookupHistory());Assert.Equal("approved", approved.Decision);Assert.Equal(status.SnapshotId.Replace("-google-free", ""), approved.Snapshot);
        Assert.True(status.UserApproved);Assert.False(status.Expired);
        Assert.Equal(Clock.AddDays(30), status.EffectiveTo); // 30 days come before the announced 2027-01-01 change
        var models = app.Models();ModelOption M(string id) => models.Single(m => m.Id == id);
        Assert.True(M("google-gemini-3.5-flash").Free);Assert.True(M("google-gemini-3.8-flash").Free);
        Assert.Equal(4.00m, M("google-gemini-4-argon").InputPerMillion);Assert.Equal(0.12m, M("openai-gpt-6-luna").InputPerMillion);
        Assert.False(M("together-glm-5.3-flash").Priced);Assert.False(M("openai-gpt-6.1-sol").Priced);Assert.Equal("subscription", M("claude-cli").Tier);
        // The snapshot's document hash is the hash of the excerpt written next to it.
        string folder = Path.Combine(e.State, "desktop", "prices", "approved");
        var paid = Data.Read<PriceSnapshot>(Directory.GetFiles(folder, "user-*.json").Single(f => !f.EndsWith("-google-free.json", StringComparison.Ordinal)));
        Assert.Equal(Data.Hash(File.ReadAllText(Path.Combine(folder, paid.Id + ".md"))), paid.DocumentSha256);
        Assert.DoesNotContain(paid.Prices, p => p.CanonicalModelKey == "zai-org/glm-5.3-flash");
        // A paid plan with an approved price can be prepared again.
        Assert.Equal(1, app.PrepareQuickTest(new(["openai-gpt-6-luna"], "code", "Review", DesktopService.SampleCode, null, true)).Requests);
    }
    [Fact] public async Task AnAnnouncedChangeEndsTheSnapshotEarly()
    {
        using var e = new TestEnvironment();
        var app = Service(e, (_, _) => Answer(Page().Select(r => r["model_id"]!.GetValue<string>() == "gpt-6-luna" ? Row("gpt-6-luna", 0.12m, 0.012m, 0.60m, next: "2026-11-20") : r)));
        var status = app.ApprovePrices(await app.LookupPricesAsync(), new HashSet<string>());
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.FromHours(9)), status.EffectiveTo);
    }
    [Fact] public async Task OneCliMissingMeansNothingCanBeApproved()
    {
        using var e = new TestEnvironment();
        var app = Service(e, (cli, _) => cli == "codex-cli" ? throw new ReviewError(11, "cli_not_found") : Answer(Page()));
        var proposal = await app.LookupPricesAsync();
        Assert.Equal("cli_not_found", proposal.Lookups.Single(l => l.Cli == "codex-cli").Error);Assert.All(proposal.Rows, r => Assert.Equal("missing", r.Status));
        Assert.Equal("no_agreed_prices", Assert.Throws<ReviewError>(() => app.ApprovePrices(proposal, new HashSet<string>())).Code);
        Assert.False(Directory.Exists(Path.Combine(e.State, "desktop", "prices", "approved")));
        // The failure is kept so its cause can be read later.
        var record = Assert.Single(app.PriceLookupHistory());
        Assert.Equal(("error", "cli_not_found"), (record.Answers["codex-cli"].Outcome, record.Answers["codex-cli"].ErrorCode));Assert.NotNull(record.Answers["claude-cli"].Body);
    }
    [Fact] public async Task CancelledAndUnreadableLookupsAreRecordedToo()
    {
        using var e = new TestEnvironment();
        var app = Service(e, (cli, _) => cli == "codex-cli" ? new("received", 200, "{\"status\":\"completed\",\"structured_output\":{\"models\":\"not a list\"}}", null) : Answer(Page()));
        var proposal = await app.LookupPricesAsync();
        Assert.Equal("invalid_payload", proposal.Lookups.Single(l => l.Cli == "codex-cli").Error);
        app.CancelPrices(proposal);
        var record = Assert.Single(app.PriceLookupHistory());
        Assert.Equal("cancelled", record.Decision);Assert.NotNull(record.DecidedAt);Assert.Contains("not a list", record.Answers["codex-cli"].Body);
        Assert.Equal("live-2026-11-04-google-free", app.PriceStatus().SnapshotId);
    }
    [Theory]
    [InlineData(0.75, "Input $0.75 / Output", true)][InlineData(0.075, "caching $0.075", true)][InlineData(2, "$2.00 per 1M", true)]
    [InlineData(2, "2 models", false)][InlineData(0.15, "$0.15$0.03 (cached)", true)][InlineData(0.5, "Output $0.50", true)][InlineData(0.3, "$3.00", false)]
    public void PriceMustAppearInTheQuote(decimal value, string quote, bool expected) => Assert.Equal(expected, PriceRefresh.InQuote(value, quote));
}
