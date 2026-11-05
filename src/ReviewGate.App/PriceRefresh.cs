using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ReviewGate.Contracts;

namespace ReviewGate.App;

// Price refresh: two agent CLIs read the official pricing pages independently, the app cross-checks their answers
// mechanically, and the user approves. Only rows both CLIs agree on (with the numbers present in their quotes) can
// enter a new snapshot; anything else is left out, so that model shows "no price" until the next refresh.
public record PriceSources(string SchemaVersion, Dictionary<string, string> Pages);
public record PriceTarget(string Provider, string ModelId, string CanonicalModelKey, string Page);
public record PriceLookupRow(string ModelId, bool Found, decimal? InputPerMillion, decimal? CachedInputPerMillion, decimal? OutputPerMillion,
    string FreeTier, string Quote, string SourceUrl, string? NextChangeDate, string? NextChangeQuote);
public record PriceLookup(string Cli, string Status, string? Error, List<PriceLookupRow> Models);
public record PriceProposalRow(PriceTarget Target, PriceRow? Current, string Status, decimal? InputPerMillion, decimal? CachedInputPerMillion,
    decimal? OutputPerMillion, string FreeTier, DateOnly? NextChange, List<string> Notes, List<string> Quotes)
{
    public bool Agreed => Status == "agreed";
    public bool FreeTierAvailable => Target.Provider == "google" && FreeTier != "no";
}
public record PriceProposal(DateTimeOffset LookedUpAt, List<PriceLookup> Lookups, List<PriceProposalRow> Rows);

public static class PriceRefresh
{
    public const int NoticeDays = 7, MaxValidityDays = 30;
    public static readonly string[] Clis = ["claude-cli", "codex-cli"];
    static readonly TimeSpan Kst = TimeSpan.FromHours(9);

    public static string Schema() => new JsonObject
    {
        ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("models"),
        ["properties"] = new JsonObject
        {
            ["models"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object", ["additionalProperties"] = false,
                    ["required"] = new JsonArray("model_id", "found", "input_per_million", "cached_input_per_million", "output_per_million", "free_tier", "quote", "source_url", "next_change_date", "next_change_quote"),
                    ["properties"] = new JsonObject
                    {
                        ["model_id"] = new JsonObject { ["type"] = "string" },
                        ["found"] = new JsonObject { ["type"] = "boolean" },
                        ["input_per_million"] = new JsonObject { ["type"] = new JsonArray("number", "null") },
                        ["cached_input_per_million"] = new JsonObject { ["type"] = new JsonArray("number", "null") },
                        ["output_per_million"] = new JsonObject { ["type"] = new JsonArray("number", "null") },
                        ["free_tier"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("yes", "no", "unknown") },
                        ["quote"] = new JsonObject { ["type"] = "string", ["description"] = "Exact page text that contains the prices, copied verbatim." },
                        ["source_url"] = new JsonObject { ["type"] = "string" },
                        ["next_change_date"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["description"] = "First announced future price change, YYYY-MM-DD, or null." },
                        ["next_change_quote"] = new JsonObject { ["type"] = new JsonArray("string", "null") }
                    }
                }
            }
        }
    }.ToJsonString();
    public const string System = "You read official pricing pages and report prices exactly as printed. Use only the given official pages (and pages they link to on the same site). Do not guess, estimate or use memory: if a number is not on the page, report found=false and nulls. Copy quotes verbatim. Do not modify any file or run commands. Answer only with JSON matching the schema.";
    public static string Prompt(IEnumerable<PriceTarget> targets, DateTimeOffset now)
    {
        var text = new StringBuilder();
        text.Append("Today is ").Append(now.ToOffset(Kst).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(" (KST).\n");
        text.Append("For each model below, open its official pricing page and report the Standard tier (not batch, flex or priority), short-context price in USD per 1M tokens that is in effect today: input, cached input, and output (output including thinking/reasoning tokens). ");
        text.Append("Put in quote the exact page text that shows those numbers. Report free_tier as yes, no or unknown. If the page announces a future price change for the model, give the first future effective date as YYYY-MM-DD with its quote; otherwise null. ");
        text.Append("If the model is not on the page, set found=false and all numbers null. Keep model_id exactly as given.\n\n");
        foreach (var t in targets) text.Append("- model_id: ").Append(t.ModelId).Append(" | provider: ").Append(t.Provider).Append(" | page: ").Append(t.Page).Append('\n');
        return text.ToString();
    }

    // Normalized CLI answer ({status, error, structured_output}) to rows; anything unreadable is a failed lookup.
    public static PriceLookup Read(string cli, TransportResult result)
    {
        if (result.Body is null) return new(cli, "failed", result.ErrorCode ?? "no_answer", []);
        var root = JsonNode.Parse(result.Body)!;
        if (root["status"]?.GetValue<string>() != "completed" || root["structured_output"]?["models"] is not JsonArray models)
            return new(cli, "failed", root["error"]?.GetValue<string>() ?? "invalid_payload", []);
        static decimal? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue(out decimal d) && d >= 0 ? d : null;
        static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        var rows = models.OfType<JsonObject>().Select(m => new PriceLookupRow(Str(m["model_id"]) ?? "", m["found"] is JsonValue f && f.TryGetValue(out bool b) && b,
            Num(m["input_per_million"]), Num(m["cached_input_per_million"]), Num(m["output_per_million"]), Str(m["free_tier"]) ?? "unknown",
            Str(m["quote"]) ?? "", Str(m["source_url"]) ?? "", Str(m["next_change_date"]), Str(m["next_change_quote"]))).ToList();
        return new(cli, "completed", null, rows);
    }

    // A printed price such as "$0.75", "0.075" or "$2.00" must appear in the quote the CLI gave.
    public static bool InQuote(decimal value, string quote)
    {
        string plain = value.ToString("0.##########", CultureInfo.InvariantCulture);
        var forms = new[] { plain, value.ToString("0.00##########", CultureInfo.InvariantCulture) };
        // A whole number like "2" alone matches too much text; require "$2" or "2.00".
        return forms.Distinct().Any(f => f.Contains('.') ? quote.Contains(f, StringComparison.Ordinal) : quote.Contains("$" + f, StringComparison.Ordinal));
    }

    public static PriceProposal Compare(IReadOnlyList<PriceTarget> targets, IReadOnlyList<PriceLookup> lookups, PriceSnapshot current, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.ToOffset(Kst).DateTime);
        var rows = new List<PriceProposalRow>();
        foreach (var t in targets)
        {
            var currentRow = current.Prices.SingleOrDefault(p => p.CanonicalModelKey == t.CanonicalModelKey);
            var answers = lookups.Select(l => (l.Cli, Row: l.Models.FirstOrDefault(m => string.Equals(m.ModelId, t.ModelId, StringComparison.OrdinalIgnoreCase)))).ToList();
            var notes = new List<string>();var quotes = answers.Where(a => a.Row is { Quote.Length: > 0 }).Select(a => $"{a.Cli}: {a.Row!.Quote}").ToList();
            string free = answers.Select(a => a.Row?.FreeTier ?? "unknown").Distinct().Count() == 1 ? answers[0].Row?.FreeTier ?? "unknown" : "unknown";
            if (answers.Any(a => a.Row is null || !a.Row.Found || a.Row.InputPerMillion is null || a.Row.OutputPerMillion is null))
            {
                notes.Add(string.Join(", ", answers.Where(a => a.Row is null || !a.Row.Found || a.Row.InputPerMillion is null || a.Row.OutputPerMillion is null).Select(a => a.Cli)) + ": 가격을 찾지 못함");
                rows.Add(new(t, currentRow, "missing", null, null, null, free, null, notes, quotes));continue;
            }
            var first = answers[0].Row!;
            bool same = answers.All(a => a.Row!.InputPerMillion == first.InputPerMillion && a.Row.OutputPerMillion == first.OutputPerMillion && (a.Row.CachedInputPerMillion ?? 0) == (first.CachedInputPerMillion ?? 0));
            if (!same)
            {
                notes.Add("두 CLI의 값이 다름: " + string.Join(" / ", answers.Select(a => $"{a.Cli} {Show(a.Row!.InputPerMillion)}·{Show(a.Row.CachedInputPerMillion)}·{Show(a.Row.OutputPerMillion)}")));
                rows.Add(new(t, currentRow, "disagree", null, null, null, free, null, notes, quotes));continue;
            }
            var unquoted = answers.Where(a => !InQuote(a.Row!.InputPerMillion!.Value, a.Row.Quote) || !InQuote(a.Row.OutputPerMillion!.Value, a.Row.Quote)
                || a.Row.CachedInputPerMillion is decimal c && c > 0 && !InQuote(c, a.Row.Quote)).Select(a => a.Cli).ToList();
            if (unquoted.Count > 0)
            {
                notes.Add(string.Join(", ", unquoted) + ": 인용 문구에 숫자가 없음");
                rows.Add(new(t, currentRow, "unverified_quote", null, null, null, free, null, notes, quotes));continue;
            }
            if (answers.Select(a => a.Row!.FreeTier).Distinct().Count() > 1) notes.Add("무료 티어 판단이 다름: " + string.Join(" / ", answers.Select(a => $"{a.Cli} {a.Row!.FreeTier}")));
            // The earliest announced future change both CLIs agree on (or either one reports) ends the snapshot window.
            var changes = answers.Select(a => DateOnly.TryParseExact(a.Row!.NextChangeDate ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d > today ? d : (DateOnly?)null).ToList();
            DateOnly? next = changes.OfType<DateOnly>().DefaultIfEmpty().Min() is var m && m != default ? m : null;
            if (changes.Distinct().Count() > 1) notes.Add("예정된 가격 변경일 판단이 다름: 더 이른 날을 만료일로 씀");
            if (currentRow is not null && currentRow.Tier != "free" && Big(currentRow.InputPerMillion, first.InputPerMillion!.Value) | Big(currentRow.OutputPerMillion, first.OutputPerMillion!.Value))
                notes.Add($"큰 변경: 입력 {Show(currentRow.InputPerMillion)} → {Show(first.InputPerMillion)}, 출력 {Show(currentRow.OutputPerMillion)} → {Show(first.OutputPerMillion)}");
            rows.Add(new(t, currentRow, "agreed", first.InputPerMillion, first.CachedInputPerMillion ?? 0, first.OutputPerMillion, free, next, notes, quotes));
        }
        return new(now, [.. lookups], rows);
    }
    static bool Big(decimal before, decimal after) => before > 0 && (after >= before * 2 || after * 2 <= before);
    static string Show(decimal? v) => v is decimal d ? "$" + d.ToString("0.##########", CultureInfo.InvariantCulture) : "없음";

    // The approved snapshot pair: paid rows, and a Google free-tier variant with the user-confirmed free models at 0.
    // Agent CLI rows are always included at 0 (subscription). Valid from now until the earliest announced change of
    // an included model or MaxValidityDays, whichever comes first.
    public static (PriceSnapshot Paid, PriceSnapshot Free, string Excerpt) Build(PriceProposal proposal, IReadOnlySet<string> freeTierModels, PriceSources sources, DateTimeOffset now)
    {
        var agreed = proposal.Rows.Where(r => r.Agreed).ToList();
        var until = now.AddDays(MaxValidityDays);
        foreach (var r in agreed.Where(r => r.NextChange is not null))
        {
            var change = new DateTimeOffset(r.NextChange!.Value.ToDateTime(TimeOnly.MinValue), Kst);
            if (change > now && change < until) until = change;
        }
        // Google output prices include thinking tokens; Google usage reports them separately, so they get the output rate.
        static PriceRow Paid(PriceProposalRow r) => r.Target.Provider == "google"
            ? new(r.Target.CanonicalModelKey, r.InputPerMillion!.Value, r.OutputPerMillion!.Value, r.CachedInputPerMillion ?? 0, false, r.OutputPerMillion!.Value, "standard", "global")
            : new(r.Target.CanonicalModelKey, r.InputPerMillion!.Value, r.OutputPerMillion!.Value, r.CachedInputPerMillion ?? 0, true, 0, r.Target.Provider == "together" ? "serverless" : "standard", "global");
        static PriceRow Zero(string key, string tier) => new(key, 0, 0, 0, false, 0, tier, "global");
        var cli = ProviderCatalog.All.Where(p => p.IsCli).Select(p => p.Id switch
        {
            "claude-cli" => "anthropic/claude-code-default", "codex-cli" => "openai/codex-default", _ => "google/antigravity-default"
        }).Select(k => Zero(k, "subscription")).ToList();
        var paidRows = agreed.Select(Paid).Concat(cli).ToList();
        // Free tier: only Google models the user confirmed, and never one that both CLIs say has no free tier.
        var freeKeys = proposal.Rows.Where(r => r.FreeTierAvailable && freeTierModels.Contains(r.Target.CanonicalModelKey)).Select(r => r.Target.CanonicalModelKey).ToHashSet();
        var freeRows = agreed.Where(r => !freeKeys.Contains(r.Target.CanonicalModelKey)).Select(Paid)
            .Concat(freeKeys.Select(k => Zero(k, "free"))).Concat(cli).ToList();

        string stamp = now.ToOffset(Kst).ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        var excerpt = new StringBuilder();
        excerpt.Append("# Price refresh ").Append(stamp).Append(" (KST)\n\n");
        excerpt.Append("Looked up by ").Append(string.Join(" and ", proposal.Lookups.Select(l => l.Cli))).Append(" on ").Append(proposal.LookedUpAt.ToString("O", CultureInfo.InvariantCulture))
            .Append(" from ").Append(string.Join(", ", sources.Pages.Values)).Append("; approved by the user in the desktop app on ").Append(now.ToString("O", CultureInfo.InvariantCulture)).Append(".\n\n");
        excerpt.Append("| Model | Status | Input | Cached | Output | Free tier | Next change | Notes |\n|---|---|---:|---:|---:|---|---|---|\n");
        foreach (var r in proposal.Rows)
            excerpt.Append($"| {r.Target.ModelId} | {r.Status} | {Show(r.InputPerMillion)} | {Show(r.CachedInputPerMillion)} | {Show(r.OutputPerMillion)} | {r.FreeTier}{(freeKeys.Contains(r.Target.CanonicalModelKey) ? " (user-confirmed use)" : "")} | {r.NextChange?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-"} | {string.Join("; ", r.Notes).Replace("|", "/")} |\n");
        excerpt.Append("\nQuotes:\n\n");
        foreach (var r in proposal.Rows) foreach (var q in r.Quotes) excerpt.Append("- ").Append(r.Target.ModelId).Append(" — ").Append(q.Replace("\n", " ")).Append('\n');
        excerpt.Append("\nValid until ").Append(until.ToString("O", CultureInfo.InvariantCulture)).Append(". Rows that were not agreed are left out.\n");
        string text = excerpt.ToString(), hash = Data.Hash(text);
        string source = "CLI price lookup (" + string.Join(", ", proposal.Lookups.Select(l => l.Cli)) + ") of " + string.Join(" ", sources.Pages.Values) + ", approved by the user in the desktop app";
        var paid = new PriceSnapshot(Data.Version, "user-" + stamp, now, now, until, source, hash, paidRows);
        var free = paid with { Id = paid.Id + "-google-free", Source = source + " ; Google free tier (user-confirmed): " + string.Join(", ", freeKeys.Order()), Prices = freeRows };
        return (paid, free, text);
    }
}
