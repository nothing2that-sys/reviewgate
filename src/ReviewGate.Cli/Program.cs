using System.Text;
using ReviewGate.Cli;
using ReviewGate.Contracts;

Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = Data.Utf8;
if (args is ["--version"]) { Console.WriteLine("ReviewGate 0.2.0-live-preview"); return 0; }
if (args is ["--help"]) { Console.WriteLine("reviewgate plan|exec|status|cancel|resume|export|grant|credential [options]\nSynthetic fixtures by default; live models via plan --execution-mode live, an interactive grant, then exec --plan. See docs/OFFLINE_USAGE.md and docs/LIVE_CONNECTION_V0_2.md."); return 0; }
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
