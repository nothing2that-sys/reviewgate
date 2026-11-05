using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReviewGate.Contracts;

namespace ReviewGate.Transport.Cli;

// Runs an installed agent CLI (Claude Code, Codex, Antigravity) headless as a reviewer, with its own login.
// This is the only place in the product that starts a process. The CLI runs in the project folder named by the
// request ("workdir": repository root or source root), so it can read surrounding files; ReviewGate's own files
// (schema, request text, last message) go to a separate temp folder and never into the project. A request without
// a workdir (or whose folder is gone) runs in that empty temp folder. The answer is normalized to one shape:
//   {cli, status: completed|failed, error, structured_output, usage{input,cached_input,output,thinking}, model, estimated_cost_usd, exit_code}
// No sandbox is imposed (by the user's choice). Claude Code gets read-only tools in a project and none otherwise.
public sealed class CliTransport(Func<string, string?>? locate = null, Func<ProcessStartInfo, Process?>? start = null) : IModelTransport
{
    public const int MaxOutputBytes = 8 * 1024 * 1024;
    public const string ReadOnlyTools = "Read,Grep,Glob";
    public static string AgyInstruction(string requestFile) => $"Read the review request in \"{requestFile}\" and do exactly what it says. Do not modify any file, run commands that change anything, or use the web. Answer only with JSON matching the schema.";
    readonly Func<string, string?> locate = locate ?? DefaultLocate;
    readonly Func<ProcessStartInfo, Process?> start = start ?? Process.Start;

    // Native executables only (never .cmd/.ps1 shims): PATH, then the CLIs' standard per-user install folders.
    public static string? DefaultLocate(string executable)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat([Path.Combine(home, ".local", "bin"), Path.Combine(local, "Programs", "OpenAI", "Codex", "bin"), Path.Combine(local, "agy", "bin")]);
        foreach (var folder in folders)
        {
            try { string candidate = Path.Combine(folder.Trim('"'), executable); if (File.Exists(candidate)) return Path.GetFullPath(candidate); }
            catch (ArgumentException) { }
        }
        return null;
    }
    public bool HasCredential(string provider) => ProviderCatalog.Get(provider) is { IsCli: true } info && locate(info.Endpoint) is not null;

    public async Task<TransportResult> SendAsync(ReviewerProfile reviewer, string body, CancellationToken cancellation)
    {
        var info = ProviderCatalog.Get(reviewer.HostingProvider);
        Data.Require(info.IsCli, "unsupported_capability", 14);
        string executable = locate(info.Endpoint) ?? throw new ReviewError(11, "cli_not_found");
        var request = JsonNode.Parse(body)!.AsObject();
        string system = request["system"]!.GetValue<string>(), prompt = request["prompt"]!.GetValue<string>(), schema = request["schema"]!.ToJsonString();
        string? model = request["model"]?.GetValue<string>(), project = request["workdir"]?.GetValue<string>();
        if (project is not null && !Directory.Exists(project)) project = null;
        string work = Path.Combine(Path.GetTempPath(), "ReviewGate-cli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string schemaFile = Path.Combine(work, "schema.json"), outFile = Path.Combine(work, "last-message.json");
            await File.WriteAllTextAsync(schemaFile, schema, Data.Utf8, cancellation);
            var psi = new ProcessStartInfo(executable)
            {
                WorkingDirectory = project ?? work, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = Data.Utf8, StandardOutputEncoding = Data.Utf8, StandardErrorEncoding = Data.Utf8
            };
            string stdin = "";
            switch (info.Id)
            {
                case "claude-cli":
                    foreach (var a in new[] { "-p", "--output-format", "json", "--json-schema", schema, "--tools", project is null ? "" : ReadOnlyTools, "--no-session-persistence", "--system-prompt", system }) psi.ArgumentList.Add(a);
                    if (model is not null) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(model); }
                    stdin = prompt;
                    break;
                case "codex-cli":
                    foreach (var a in new[] { "exec", "-", "-C", project ?? work, "--skip-git-repo-check", "--ephemeral", "--json", "--output-schema", schemaFile, "-o", outFile }) psi.ArgumentList.Add(a);
                    if (model is not null) { psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(model); }
                    stdin = system + "\n\n" + prompt;
                    break;
                case "agy-cli":
                    // agy takes the prompt only as an argument (32K command-line limit), so the request goes in a temp file
                    // it reads; in a project the temp folder is added to its workspace next to the project.
                    string requestFile = Path.Combine(work, "review-request.md");
                    await File.WriteAllTextAsync(requestFile, system + "\n\n" + prompt, Data.Utf8, cancellation);
                    foreach (var a in new[] { "-p=" + AgyInstruction(requestFile), "--output-format", "json", "--json-schema", schemaFile }) psi.ArgumentList.Add(a);
                    if (project is not null) { psi.ArgumentList.Add("--add-dir"); psi.ArgumentList.Add(work); }
                    if (model is not null) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(model); }
                    break;
                default: throw new ReviewError(14, "unsupported_capability");
            }
            using var process = start(psi) ?? throw new ReviewError(21, "cli_start_failed");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(reviewer.TimeoutSeconds));
            var stdout = ReadCapped(process.StandardOutput, timeout.Token);var stderr = ReadCapped(process.StandardError, timeout.Token);
            try
            {
                try { await process.StandardInput.WriteAsync(stdin.AsMemory(), timeout.Token);process.StandardInput.Close(); }
                catch (IOException) { /* the CLI closed stdin early; its output still decides */ }
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // A CLI still running when the time is up or the run is cancelled may already have used the model.
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (cancellation.IsCancellationRequested) throw;
                return TransportResult.Unknown("timeout");
            }
            string? output = await stdout, error = await stderr;
            if (output is null) return TransportResult.Unknown("response_too_large");
            var normalized = info.Id switch
            {
                "claude-cli" => FromClaude(output),
                "codex-cli" => FromCodex(output, File.Exists(outFile) ? await File.ReadAllTextAsync(outFile, cancellation) : null),
                _ => FromAgy(output)
            };
            // No parseable answer at all: the CLI may still have called the model, so the outcome stays unknown.
            if (normalized is null) return TransportResult.Unknown(process.ExitCode == 0 ? "cli_unreadable_output" : "cli_failed");
            normalized["cli"] = info.Id;normalized["exit_code"] = process.ExitCode;
            if (normalized["status"]!.GetValue<string>() == "failed" && normalized["error"] is null) normalized["error"] = Classify(error ?? "");
            return new("received", 200, normalized.ToJsonString(), null);
        }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    static async Task<string?> ReadCapped(StreamReader reader, CancellationToken cancellation)
    {
        var text = new StringBuilder();var buffer = new char[16384];int read;
        try
        {
            while ((read = await reader.ReadAsync(buffer, cancellation)) > 0)
            {
                if (text.Length + read > MaxOutputBytes) return null;
                text.Append(buffer, 0, read);
            }
        }
        catch (OperationCanceledException) { }
        return text.ToString();
    }
    static JsonObject? ParseObject(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; } catch (JsonException) { return null; }
    }
    static long Number(JsonNode? node) => node is JsonValue v && v.TryGetValue(out long n) ? n : 0;
    static JsonObject Usage(long input, long cached, long output, long thinking) =>
        new() { ["input_tokens"] = input, ["cached_input_tokens"] = Math.Min(cached, input), ["output_tokens"] = output, ["thinking_tokens"] = thinking };
    static string Classify(string text) =>
        text.Contains("401", StringComparison.Ordinal) || text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) || text.Contains("not logged in", StringComparison.OrdinalIgnoreCase) || text.Contains("authentication", StringComparison.OrdinalIgnoreCase) ? "unauthorized" :
        text.Contains("429", StringComparison.Ordinal) || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || text.Contains("usage limit", StringComparison.OrdinalIgnoreCase) ? "rate_limited" : "error";

    // claude -p --output-format json: one result object.
    public static JsonObject? FromClaude(string stdout)
    {
        if (ParseObject(stdout.Trim()) is not { } root) return null;
        var u = root["usage"];
        bool ok = root["is_error"]?.GetValue<bool>() == false && root["subtype"]?.GetValue<string>() == "success";
        string? error = ok ? null : Number(root["api_error_status"]) switch { 401 or 403 => "unauthorized", 429 => "rate_limited", _ => Classify(root["result"]?.ToString() ?? "") };
        return new()
        {
            ["status"] = ok ? "completed" : "failed", ["error"] = error, ["structured_output"] = root["structured_output"]?.DeepClone(),
            ["usage"] = u is null ? null : Usage(Number(u["input_tokens"]) + Number(u["cache_creation_input_tokens"]) + Number(u["cache_read_input_tokens"]), Number(u["cache_read_input_tokens"]), Number(u["output_tokens"]), Number(u["output_tokens_details"]?["thinking_tokens"])),
            ["model"] = (root["modelUsage"] as JsonObject)?.Select(x => x.Key).FirstOrDefault(), ["estimated_cost_usd"] = root["total_cost_usd"]?.DeepClone()
        };
    }
    // codex exec --json: JSONL events; the final message is written to the -o file. Config warnings arrive as
    // item.completed "error" items and are not failures.
    public static JsonObject? FromCodex(string stdout, string? lastMessage)
    {
        var events = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseObject).OfType<JsonObject>().ToList();
        if (events.Count == 0) return null;
        var completed = events.LastOrDefault(e => e["type"]?.GetValue<string>() == "turn.completed");
        var failed = events.LastOrDefault(e => e["type"]?.GetValue<string>() is "turn.failed" or "error");
        var u = completed?["usage"];
        bool ok = completed is not null && failed is null;
        return new()
        {
            ["status"] = ok ? "completed" : "failed", ["error"] = ok ? null : Classify(failed?.ToJsonString() ?? ""),
            ["structured_output"] = ok && lastMessage is not null ? ParseObject(lastMessage.Trim()) : null,
            ["usage"] = u is null ? null : Usage(Number(u["input_tokens"]), Number(u["cached_input_tokens"]), Number(u["output_tokens"]), Number(u["reasoning_output_tokens"])),
            ["model"] = null, ["estimated_cost_usd"] = null
        };
    }
    // agy -p --output-format json: one result object; status SUCCESS can still lack structured_output.
    public static JsonObject? FromAgy(string stdout)
    {
        if (ParseObject(stdout.Trim()) is not { } root) return null;
        var u = root["usage"];bool ok = root["status"]?.GetValue<string>() == "SUCCESS";
        return new()
        {
            ["status"] = ok ? "completed" : "failed", ["error"] = ok ? null : Classify(root.ToJsonString()),
            ["structured_output"] = root["structured_output"]?.DeepClone(),
            ["usage"] = u is null ? null : Usage(Number(u["input_tokens"]), Number(u["cache_read_tokens"]), Number(u["output_tokens"]), Number(u["thinking_tokens"])),
            ["model"] = null, ["estimated_cost_usd"] = null
        };
    }
}

// Sends each reviewer through its kind of transport: stored-key HTTP APIs or local agent CLIs.
public sealed class RoutingTransport(IModelTransport http, IModelTransport cli) : IModelTransport, IDisposable
{
    IModelTransport For(string provider) => ProviderCatalog.Get(provider).IsCli ? cli : http;
    public bool HasCredential(string provider) => For(provider).HasCredential(provider);
    public Task<TransportResult> SendAsync(ReviewerProfile reviewer, string body, CancellationToken cancellation) => For(reviewer.HostingProvider).SendAsync(reviewer, body, cancellation);
    public void Dispose() { (http as IDisposable)?.Dispose(); (cli as IDisposable)?.Dispose(); }
}
