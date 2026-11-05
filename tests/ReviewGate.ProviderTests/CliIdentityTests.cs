using System.Text.Json.Nodes;
using ReviewGate.Contracts;
using ReviewGate.Transport.Cli;

namespace ReviewGate.ProviderTests;

// The model that answered, as each CLI recorded it: Claude's stream events on stdout, Codex's rollout file.
public sealed class CliIdentityTests
{
    const string NoFinding = "{\"status\":\"success\",\"disposition\":\"no_finding\",\"findings\":[]}";
    // claude -p --output-format stream-json --verbose (shape captured 2026-11-05, values shortened).
    static readonly string ClaudeStream = string.Join("\n",
        "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sess-1\",\"model\":\"claude-opus-5-5\",\"tools\":[]}",
        "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_1\",\"model\":\"claude-haiku-5-5\",\"content\":[{\"type\":\"text\",\"text\":\"x\"}]},\"session_id\":\"sess-1\"}",
        "{\"type\":\"user\",\"message\":{\"content\":[]},\"session_id\":\"sess-1\"}",
        "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_2\",\"model\":\"claude-opus-5-5\",\"content\":[{\"type\":\"tool_use\",\"name\":\"StructuredOutput\"}]},\"session_id\":\"sess-1\"}",
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"session_id\":\"sess-1\",\"total_cost_usd\":0.02,\"usage\":{\"input_tokens\":10,\"output_tokens\":5}," +
        "\"modelUsage\":{\"claude-opus-5-5\":{},\"claude-haiku-5-5\":{}},\"structured_output\":" + NoFinding + "}", "");

    [Fact] public void ClaudeStreamGivesTheResultAndTheModelOfTheLastAssistantEvent()
    {
        var n = CliTransport.FromClaude(ClaudeStream)!;
        Assert.Equal("completed", n["status"]!.GetValue<string>());Assert.Equal("no_finding", n["structured_output"]!["disposition"]!.GetValue<string>());
        var seen = CliTransport.ClaudeObservation(ClaudeStream);
        Assert.Equal("claude-opus-5-5", seen.Final);Assert.Equal(["claude-opus-5-5", "claude-haiku-5-5"], seen.Models);Assert.Equal("sess-1", seen.SessionId);
        Assert.Equal(4, seen.Source!["line"]!.GetValue<int>());Assert.Equal("assistant.message.model", seen.Source["field"]!.GetValue<string>());Assert.Null(seen.Source["file"]);
        // Nothing readable: nothing is filled in.
        var none = CliTransport.ClaudeObservation("not json");
        Assert.Empty(none.Models);Assert.Null(none.Final);Assert.Null(none.Source);
    }

    [Fact] public void CodexModelIsReadFromTheRolloutNamedByTheThreadId()
    {
        string home = Path.Combine(Path.GetTempPath(), "rg-codex-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string thread = "019a2b3c-4d5e-6f70-8192-a3b4c5d6e7f8";
            string day = Path.Combine(home, "sessions", "2026", "11", "05");Directory.CreateDirectory(day);
            string rollout = Path.Combine(day, $"rollout-2026-11-05T10-00-00-{thread}.jsonl");
            File.WriteAllText(rollout, string.Join("\n",
                "{\"type\":\"session_meta\",\"payload\":{\"id\":\"" + thread + "\",\"account_id\":\"acct-secret\"}}",
                "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-6.1-sol\",\"effort\":\"medium\"}}",
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\"}}",
                "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-6.1-sol\",\"effort\":\"medium\"}}", ""));
            string stdout = "{\"type\":\"thread.started\",\"thread_id\":\"" + thread + "\"}\n{\"type\":\"turn.completed\",\"usage\":{}}\n";
            var seen = CliTransport.CodexObservation(stdout, (home, "~/.codex"));
            Assert.Equal(["gpt-6.1-sol"], seen.Models);Assert.Equal("gpt-6.1-sol", seen.Final);Assert.Equal("medium", seen.Effort);Assert.Equal(thread, seen.SessionId);
            Assert.Equal($"~/.codex/sessions/2026/11/05/rollout-2026-11-05T10-00-00-{thread}.jsonl", seen.Source!["file"]!.GetValue<string>());
            Assert.EndsWith(thread + ".jsonl", seen.Source["file"]!.GetValue<string>());
            Assert.Equal(4, seen.Source["line"]!.GetValue<int>());Assert.Equal([2, 4], seen.Source["lines"]!.AsArray().Select(x => x!.GetValue<int>()));
            Assert.Equal("turn_context.payload.model", seen.Source["field"]!.GetValue<string>());
            // Only paths, line numbers and model values are kept: no rollout text, no account id.
            var identity = CliTransport.Identity("codex-cli", "codex-cli 0.166.0", "gpt-6.1-sol", seen, Data.Hash("x"));
            Assert.DoesNotContain("acct-secret", identity.ToJsonString());Assert.DoesNotContain("response_item", identity.ToJsonString());
            // No rollout for the thread, or a thread id that is not one: empty, not guessed.
            var missing = CliTransport.CodexObservation("{\"type\":\"thread.started\",\"thread_id\":\"0000aaaa-0000\"}\n", (home, "~/.codex"));
            Assert.Empty(missing.Models);Assert.Null(missing.Final);Assert.Null(missing.Source);Assert.Equal("0000aaaa-0000", missing.SessionId);
            Assert.Null(CliTransport.CodexObservation("{\"type\":\"thread.started\",\"thread_id\":\"*\"}\n", (home, "~/.codex")).SessionId);
        }
        finally { Directory.Delete(home, true); }
    }

    static async Task<System.Diagnostics.ProcessStartInfo> CommandLine(string provider, string? model)
    {
        System.Diagnostics.ProcessStartInfo? seen = null;
        var transport = new CliTransport(exe => @"C:\fake\" + exe, psi => { seen = psi;return null; });
        await Assert.ThrowsAsync<ReviewError>(() => transport.RunJobAsync(provider, new CliJob("SYSTEM", "PROMPT", "{\"type\":\"object\"}", model), 60, CancellationToken.None));
        return seen!;
    }
    [Fact] public async Task CodexKeepsItsRolloutWithMemoryOffAndClaudeStreamsItsEvents()
    {
        var codex = await CommandLine("codex-cli", "gpt-6.1-sol");
        Assert.DoesNotContain("--ephemeral", codex.ArgumentList);
        int exec = codex.ArgumentList.IndexOf("exec"), disable = codex.ArgumentList.IndexOf("--disable");
        Assert.True(disable > exec);Assert.Equal("memories", codex.ArgumentList[disable + 1]);
        Assert.Contains("memories.use_memories=false", codex.ArgumentList);Assert.Contains("memories.generate_memories=false", codex.ArgumentList);
        Assert.Equal("gpt-6.1-sol", codex.ArgumentList[codex.ArgumentList.IndexOf("-m") + 1]);
        var claude = await CommandLine("claude-cli", "claude-opus-5-5");
        Assert.Equal("stream-json", claude.ArgumentList[claude.ArgumentList.IndexOf("--output-format") + 1]);Assert.Contains("--verbose", claude.ArgumentList);
        Assert.Equal("claude-opus-5-5", claude.ArgumentList[claude.ArgumentList.IndexOf("--model") + 1]);
        var defaultModel = await CommandLine("codex-cli", null);Assert.DoesNotContain("-m", defaultModel.ArgumentList);
    }
}
