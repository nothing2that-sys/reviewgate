using System.Diagnostics;
using ReviewGate.Contracts;

namespace ReviewGate.Transport.Cli;

// The other processes ReviewGate starts, for headless implementation: git (worktrees, commits, diffs) and the check
// commands a person allowed in the implementation policy. Neither talks to a model. stdin is closed; output is read
// whole; a process still running at the time limit is killed with its children.
public static class LocalCommands
{
    public static async Task<(int Exit, string Out, string Err)> GitAsync(string folder, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git") { ArgumentList = { "-C", folder } };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        var (exit, output, error) = await RunAsync(psi, folder, TimeSpan.FromMinutes(5), ct);
        return (exit ?? -1, output, error);
    }
    // One allowed check command through the system shell (cmd.exe on Windows). Exit null means it timed out.
    // Output and errors are returned together.
    public static async Task<(int? Exit, string Output)> ShellAsync(string command, string folder, TimeSpan limit, CancellationToken ct)
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { Arguments = "/d /s /c \"" + command + "\"" }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        var (exit, output, error) = await RunAsync(psi, folder, limit, ct);
        return (exit, error.Length == 0 ? output : output + (output.Length == 0 || output.EndsWith('\n') ? "" : "\n") + error);
    }
    static async Task<(int? Exit, string Output, string Error)> RunAsync(ProcessStartInfo psi, string folder, TimeSpan limit, CancellationToken ct)
    {
        psi.WorkingDirectory = folder;psi.UseShellExecute = false;psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;psi.RedirectStandardOutput = true;psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Data.Utf8;psi.StandardErrorEncoding = Data.Utf8;
        using var process = Process.Start(psi) ?? throw new ReviewError(21, "process_start_failed");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(limit);
        bool finished = true;
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            finished = false;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
        }
        string output = await stdout, error = await stderr;
        return (finished ? process.ExitCode : null, output, error);
    }
}
