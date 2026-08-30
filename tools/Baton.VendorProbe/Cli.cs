using System.Diagnostics;
using System.Text;

namespace Baton.VendorProbe;

/// <summary>
/// Runs a vendor CLI. Deliberately <b>not</b> through a shell.
/// </summary>
/// <remarks>
/// <para>
/// Arguments go into <see cref="ProcessStartInfo.ArgumentList"/>, so nothing between this code and
/// the CLI interprets them. That is not tidiness — it is the fix for a real failure. Probing
/// <c>claude -p "/usage"</c> through Git Bash on Windows produced a confident wrong answer, because
/// MSYS path conversion rewrote the leading <c>/usage</c> into <c>C:/Program Files/Git/usage</c>
/// <em>before the CLI saw it</em>. The model then answered about that path, which reads exactly like
/// "the command does not exist". A shell-free invocation cannot have that class of bug.
/// </para>
/// <para>
/// The environment is scrubbed of every <c>CLAUDE*</c> variable. A nested <c>claude</c> inherits the
/// parent session's tool set and MCP servers, which no daemon-spawned worker ever has — an earlier
/// probe stripped only <c>CLAUDE_CODE_*</c>, missed <c>CLAUDECODE</c>, <c>CLAUDE_EFFORT</c>,
/// <c>CLAUDE_PID</c> and <c>CLAUDE_JOB_DIR</c>, and produced a result we nearly wrote down as fact.
/// </para>
/// </remarks>
public static class Cli
{
    public sealed record Run(int ExitCode, string StdOut, string StdErr, bool TimedOut)
    {
        public string All => StdOut + "\n" + StdErr;
    }

    public static Run Invoke(string program, IEnumerable<string> args, TimeSpan? timeout = null, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = program,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        // Scrub the parent session's identity so the probe sees what a worker would see.
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            psi.Environment.Remove(key);
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            // A missing CLI is a legitimate probe outcome, not a crash: the matrix should say the
            // vendor was not installed rather than the suite falling over on a machine that has one.
            return new Run(-1, string.Empty, $"could not start '{program}': {ex.Message}", TimedOut: false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var limit = timeout ?? TimeSpan.FromMinutes(3);
        if (!process.WaitForExit((int)limit.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            return new Run(-1, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }

        process.WaitForExit(); // flush the async readers
        return new Run(process.ExitCode, stdout.ToString(), stderr.ToString(), TimedOut: false);
    }

    public static bool IsInstalled(string program) =>
        Invoke(program, ["--version"], TimeSpan.FromSeconds(30)).ExitCode == 0;

    /// <summary>The version a finding is attributed to. Results expire when this moves.</summary>
    public static string? Version(string program)
    {
        var run = Invoke(program, ["--version"], TimeSpan.FromSeconds(30));
        if (run.ExitCode != 0)
        {
            return null;
        }

        var line = run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    /// <summary>
    /// A throwaway working directory, removed afterwards. The suite must never mutate the operator's
    /// environment — no writes to <c>~/.claude</c>, <c>~/.gemini</c>, or any settings file.
    /// </summary>
    public static void InScratch(Action<string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "aer-vendor-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            body(dir);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }
}
