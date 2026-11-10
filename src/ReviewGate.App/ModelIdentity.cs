using System.Text.Json;
using System.Text.Json.Nodes;
using ReviewGate.Contracts;
using ReviewGate.Providers;
using ReviewGate.Transport.Cli;

namespace ReviewGate.App;

// "model_identity" for one call (attempt): the model ReviewGate asked for and what the provider's own record says
// answered. ReviewGate only copies values; whether requested and observed agree is for the caller to judge.
//   provider, cli_version, model_requested (null when a CLI ran its own default model, then cli_default_model: true),
//   effort_requested, model_observed[], model_final, effort_observed, source, session_id, resumed,
//   request_sha256 (the instruction exactly as ReviewGate received it; filled by the command that received it),
//   sent_prompt_sha256 (the prompt text after ReviewGate wrapped it: CLI stdin or request file, API user input),
//   fallback_used (+ primary_model_requested when a fallback answered).
// CLI values come from the CLI transport (Claude stream events, Codex rollout); API values from the response body
// (OpenAI and Together "model", Google "modelVersion" or "model", the response "id"). Unread values stay empty.
public static class ModelIdentity
{
    public static string? Requested(ReviewerProfile r) =>
        ProviderCatalog.Get(r.HostingProvider).IsCli && WireAdapters.UsesCliDefaultModel(r) ? null : r.ModelId;

    // The text the provider received as the prompt, read back from the request body ReviewGate built.
    public static string? SentPrompt(ReviewerProfile r, string requestBody)
    {
        try
        {
            var body = JsonNode.Parse(requestBody);
            if (ProviderCatalog.Get(r.HostingProvider).IsCli)
            {
                string? system = body?["system"]?.GetValue<string>(), prompt = body?["prompt"]?.GetValue<string>();
                return prompt is null ? null : r.HostingProvider == "claude-cli" ? prompt : system + "\n\n" + prompt;
            }
            if (WireAdapters.ChatCompletions(r.HostingProvider)) return (body?["messages"] as JsonArray)?.LastOrDefault()?["content"]?.GetValue<string>();
            return body?["input"]?.GetValue<string>();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    // A call with no readable answer (timeout, lost connection, not sent): only what ReviewGate itself knows.
    public static JsonObject Empty(ReviewerProfile r, string? requestBody) => Finish(r,
        CliTransport.Identity(r.HostingProvider, null, Requested(r), new CliTransport.Observation([], null, null, null, null), ""), requestBody);

    public static JsonObject FromCli(ReviewerProfile r, string? providerBody, string? requestBody)
    {
        JsonObject? recorded = null;
        try { recorded = providerBody is null ? null : (JsonNode.Parse(providerBody) as JsonObject)?["model_identity"]?.DeepClone() as JsonObject; }
        catch (JsonException) { }
        return recorded is null ? Empty(r, requestBody) : Finish(r, recorded, requestBody);
    }

    public static JsonObject FromApi(ReviewerProfile r, string? responseBody, string? requestBody)
    {
        string? model = null, field = null, id = null, effort = null;
        try
        {
            if (responseBody is not null && JsonNode.Parse(responseBody) is JsonObject root)
            {
                foreach (var name in r.HostingProvider == "google" ? new[] { "modelVersion", "model_version", "model" } : ["model"])
                    if (root[name] is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0) { model = s;field = name;break; }
                id = root["id"] is JsonValue i && i.TryGetValue(out string? x) ? x : root["responseId"] is JsonValue ri && ri.TryGetValue(out string? y) ? y : null;
                if (r.HostingProvider == "openai" && root["reasoning"]?["effort"] is JsonValue ev && ev.TryGetValue(out string? e)) effort = e;
            }
        }
        catch (JsonException) { }
        var seen = new CliTransport.Observation(model is null ? [] : [model], model, effort,
            model is null ? null : new JsonObject { ["file"] = null, ["stream"] = "response_body", ["line"] = null, ["field"] = field }, id);
        var identity = CliTransport.Identity(r.HostingProvider, null, r.ModelId, seen, "");
        identity["effort_requested"] = r.ReasoningEffort;
        return Finish(r, identity, requestBody);
    }

    public static JsonObject For(ReviewerProfile r, string? providerBody, string? requestBody) =>
        ProviderCatalog.Get(r.HostingProvider).IsCli ? FromCli(r, providerBody, requestBody) : FromApi(r, providerBody, requestBody);

    // Fields every identity has, whoever produced it.
    static JsonObject Finish(ReviewerProfile r, JsonObject identity, string? requestBody)
    {
        identity["provider"] = r.HostingProvider;
        identity["model_requested"] = Requested(r);
        if (ProviderCatalog.Get(r.HostingProvider).IsCli && WireAdapters.UsesCliDefaultModel(r)) identity["cli_default_model"] = true;
        if (identity["sent_prompt_sha256"]?.GetValue<string>() is not { Length: > 0 })
            identity["sent_prompt_sha256"] = requestBody is not null && SentPrompt(r, requestBody) is string sent ? Data.Hash(sent) : null;
        identity.Remove("request_sha256");identity["request_sha256"] = null;
        identity.Remove("fallback_used");identity["fallback_used"] = false;
        return identity;
    }

    public static void Mark(JsonObject identity, string? requestSha256, ReviewerProfile? primaryOfFallback = null)
    {
        identity["request_sha256"] = requestSha256;
        identity["fallback_used"] = primaryOfFallback is not null;
        if (primaryOfFallback is not null) identity["primary_model_requested"] = Requested(primaryOfFallback) ?? primaryOfFallback.ModelId;
    }
}
