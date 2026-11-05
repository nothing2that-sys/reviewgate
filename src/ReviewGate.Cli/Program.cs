using System.Text;
using ReviewGate.Cli;
using ReviewGate.Contracts;

Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = Data.Utf8;
if (args is ["--version"]) { Console.WriteLine("ReviewGate 0.2.0-live-preview"); return 0; }
if (args is ["--help"]) { Console.WriteLine("reviewgate review \"<instruction>\" --source-root <dir> [--input <path>]... --reviewer <provider>[=<model>] [--reviewer ...] [--no-fallback] [--profile code|document|novel]\n  One-shot live review for agents, allowed by the standing policy (see docs/AGENT_USAGE.md).\nreviewgate implement \"<task>\" --repo <git root> --worker <provider>[=<model>] [--input <file|folder>]... [--reference <file>]... (--reviewer <provider>[=<model>]... | --no-review) [--check \"<allowed command>\"]... [--base <rev> | --branch reviewgate/impl-...] [--max-minutes N]\n  A worker changes the repository on its own branch (no commands, never merged): a CLI edits files itself, an API model (needs --input) answers with edits ReviewGate applies; allowed checks run; other models review.\nreviewgate discard --repo <git root> --branch reviewgate/impl-...\nreviewgate policy set|set-implement|status|revoke  (set, set-implement: interactive console only)\nreviewgate plan|exec|status|cancel|resume|export|grant|credential [options]\n  Synthetic fixtures by default; live models via plan --execution-mode live, an interactive grant, then exec --plan. See docs/OFFLINE_USAGE.md and docs/LIVE_CONNECTION_V0_2.md."); return 0; }
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var result = await CliHost.RunAsync(args, cancellation.Token);
if (result.OutputPath is string output)
{
    try { Data.CreateFile(output, Data.Json(result.Result)); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ReviewError)
    { result = new(21, result.Result with { ErrorCode = "output_write_failed", RequiredAction = "inspect_run_with_status" }); }
}
// A failed stdout delivery is reported as an output failure; the saved bundle stays readable via status.
try { Console.WriteLine(Data.Json(result.Result)); Console.Out.Flush(); }
catch (IOException) { return 21; }
return result.ExitCode;
