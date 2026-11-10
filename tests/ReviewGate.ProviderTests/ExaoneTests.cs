using System.Text.Json.Nodes;
using ReviewGate.Contracts;
using ReviewGate.Providers;
using ReviewGate.Testing;
using ReviewGate.Transport.Http;

namespace ReviewGate.ProviderTests;

// LG EXAONE API (docs.exaone.ai, 2026-11): OpenAI-style chat completions, Bearer key, reasoning always on
// (reasoning_effort low|high|max, reasoning text in message.reasoning_content, usage.reasoning_tokens inside
// completion_tokens), errors as {result_code, description}.
public sealed class ExaoneTests
{
    static ReviewerProfile Shipped => Data.Read<ReviewerProfile>(Path.Combine(TestEnvironment.Repository, "profiles", "reviewers", "exaone-k-exaone-v2.5.json"));
    const string NoFinding = "{\"status\":\"success\",\"disposition\":\"no_finding\",\"findings\":[]}";

    [Fact] public void TheRequestIsAChatCompletionWithTheSchemaInTheSystemTextAndReasoningEffort()
    {
        var r = Shipped;Assert.Equal(("exaone", "k-exaone_v2.5", "lgai/k-exaone-v2.5"), (r.HostingProvider, r.ModelId, r.CanonicalModelKey));
        var body = JsonNode.Parse(WireAdapters.BuildJob(r, "SYSTEM", "PROMPT", WireAdapters.PayloadSchema(), "reviewgate_findings"))!;
        Assert.Equal("k-exaone_v2.5", body["model"]!.GetValue<string>());
        Assert.Equal(["system", "user"], body["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("PROMPT", body["messages"]![1]!["content"]!.GetValue<string>());
        // No constrained decoding: the schema travels in the system text and the reply is validated afterwards.
        Assert.Null(body["response_format"]);
        string system = body["messages"]![0]!["content"]!.GetValue<string>();
        Assert.StartsWith("SYSTEM", system);Assert.Contains(WireAdapters.PayloadSchema().ToJsonString(), system);
        Assert.Equal("high", body["reasoning_effort"]!.GetValue<string>());Assert.Equal(16384, body["max_tokens"]!.GetValue<int>());
        Assert.Null(body["store"]);Assert.Null(body["reasoning"]);
        Assert.Equal("https://api.exaone.ai/v1/chat/completions", HttpTransport.Endpoints["exaone"].ToString());
        // EXAONE has no "none" or "medium" effort.
        foreach (var bad in new[] { "none", "medium" }) Assert.Equal("unsupported_reasoning_effort", Assert.Throws<ReviewError>(() => WireAdapters.Validate(r with { ReasoningEffort = bad })).Code);
    }
    [Fact] public void TheAnswerIsReadWithReasoningCountedInsideTheOutput()
    {
        // The documented response: content may start with blank lines; reasoning is separate; tool fields are null.
        var response = new JsonObject
        {
            ["id"] = "chatcmpl-1", ["object"] = "chat.completion", ["model"] = "k-exaone_v2.5",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0, ["finish_reason"] = "stop",
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "\n\n" + NoFinding, ["reasoning_content"] = "thinking...", ["tool_calls"] = null, ["function_call"] = null }
            }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 22, ["completion_tokens"] = 218, ["total_tokens"] = 240, ["reasoning_tokens"] = 209 }
        }.ToJsonString();
        var parsed = WireAdapters.Parse(Shipped, response);
        Assert.Equal(("success", "no_finding"), (parsed.Status, parsed.Disposition));
        Assert.Equal((22L, 218L, 209L, true), (parsed.Usage.InputTokens, parsed.Usage.OutputTokens, parsed.Usage.ReasoningTokens, parsed.Usage.Known));
        // The announced price is free and counts reasoning inside output, so the cost is 0 and the usage is consistent.
        var row = Assert.Single(Data.Read<List<PriceRow>>(Path.Combine(TestEnvironment.Repository, "profiles", "prices", "announced.json")));
        Assert.Equal(("lgai/k-exaone-v2.5", "free", true), (row.CanonicalModelKey, row.Tier, row.OutputIncludesReasoning));
        Assert.Equal(0m, WireAdapters.Cost(parsed.Usage, row));
        // A fenced answer is unwrapped.
        var fenced = response.Replace("\"\\n\\n{", "\"```json\\n{").Replace("[]}\"", "[]}\\n```\"");
        Assert.Contains("```json", fenced);Assert.Equal(("success", "no_finding"), (WireAdapters.Parse(Shipped, fenced).Status, WireAdapters.Parse(Shipped, fenced).Disposition));
        var cut = response.Replace("\"stop\"", "\"length\"");Assert.Equal("truncated", WireAdapters.Parse(Shipped, cut).Status);
    }
    [Fact] public void ErrorBodiesKeepOnlyTheCodeAndAMaskedDescription()
    {
        string redacted = WireAdapters.RedactErrorBody("{\"result_code\":401,\"description\":\"invalid key sk-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123\"}");
        var e = JsonNode.Parse(redacted)!["error"]!;
        Assert.Equal("401", e["code"]!.GetValue<string>());Assert.Contains("[masked]", e["message"]!.GetValue<string>());
        Assert.DoesNotContain("ABCDEFGHIJ", redacted);
        Assert.Equal("unauthorized", WireAdapters.Parse(Shipped, redacted, 401).Status);
    }
}
