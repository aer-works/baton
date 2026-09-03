using System.Diagnostics;
using System.Text.Json;

namespace Baton.Vendors;

/// <summary>
/// #1680: whether a `deny` decision actually reaches stdout, checked once at resolve time for any
/// agy grant that relies on <c>AgyWorkerAdapter</c>'s <c>PreToolUse</c> hook as its <b>sole</b>
/// narrowing (<see cref="AgyWorkerAdapter.RequiresHookAsSoleNarrowing"/>). On this vendor an absent
/// or unparseable hook response is read as an ALLOW (<c>agy.hook-malformed-stdout-fails-open</c>,
/// <c>tools/vendor-verify/verify.py</c>), so a hook that cannot start turns the most-restricted role
/// into an unscoped shell with network and unbounded writes rather than failing loudly — #710 is the
/// measured incident: a quoting bug meant the gate never fired on a Windows agy worker for a whole
/// release, and nothing checked per invocation that it had.
/// </summary>
/// <remarks>
/// Injectable so <see cref="AgyWorkerAdapter"/>'s unit tests drive it without spawning a real
/// process (CLAUDE.md's <c>right-instrument</c> gate, and this task's own "no live agy" constraint).
/// </remarks>
public interface IAgyHookLivenessProbe
{
    /// <summary>
    /// Runs the hook assembly at <paramref name="hookAssemblyPath"/> the same way agy would (as a
    /// <c>dotnet &lt;assembly&gt; agy-hook-check</c> subprocess) against a synthetic payload that
    /// must be denied, and reports whether a <c>deny</c> decision came back on stdout within
    /// <paramref name="timeout"/>.
    /// </summary>
    AgyHookLivenessResult Probe(string hookAssemblyPath, TimeSpan timeout);
}

/// <param name="IsLive">True only when the hook answered with an explicit <c>deny</c> decision.</param>
/// <param name="Detail">
/// What actually happened — the hook path's non-existence, a timeout, malformed stdout, an
/// unexpected <c>decision</c> value, or the literal <c>deny</c> reason on success. Always populated,
/// so a refusal built from it names something concrete rather than "the probe failed".
/// </param>
public sealed record AgyHookLivenessResult(bool IsLive, string Detail);

/// <summary>
/// The production <see cref="IAgyHookLivenessProbe"/>: spawns the hook assembly directly via
/// <c>dotnet</c> rather than through <c>cmd</c>/<c>sh</c> the way agy's own <c>hooks.json</c> command
/// string does — <see cref="AgyWorkerAdapter.HookAssemblyToken"/>'s escaping rules exist to survive
/// <i>agy's</i> shell hop, which this probe (spawning the process itself, with an explicit
/// <see cref="ProcessStartInfo.ArgumentList"/>) never takes. The unescaped, real path is used here.
/// </summary>
internal sealed class ProcessAgyHookLivenessProbe : IAgyHookLivenessProbe
{
    /// <summary>
    /// A <c>run_command</c> call that must be denied. Deliberately sent with <b>no</b>
    /// <c>BATON_HOOK_*</c> environment variables present (see <see cref="Probe"/>): with the
    /// denied-tool list channel absent, <c>AgyHookCheckCommand.Decide</c> denies unconditionally
    /// ("did not receive its denied-tool list") — a guaranteed deny that depends only on the hook
    /// process starting and answering, not on this invocation's real grant or shell patterns. The
    /// <c>git push</c> command line is illustrative payload shape, not what drives the verdict.
    /// </summary>
    private const string SyntheticDeniedToolCall =
        """{"toolCall":{"name":"run_command","args":{"CommandLine":"git push"}}}""";

    private static readonly string[] EnvironmentVariablesToStrip =
    [
        AgyWorkerAdapter.DeniedToolsVariable,
        AgyWorkerAdapter.ShellPatternsVariable,
        AgyWorkerAdapter.DeniedShellPatternsVariable,
        AgyWorkerAdapter.DeniedShellOptionTokensVariable,
    ];

    public AgyHookLivenessResult Probe(string hookAssemblyPath, TimeSpan timeout)
    {
        if (!File.Exists(hookAssemblyPath))
        {
            return new AgyHookLivenessResult(false, $"'{hookAssemblyPath}' does not exist");
        }

        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            startInfo.ArgumentList.Add(hookAssemblyPath);
            startInfo.ArgumentList.Add("agy-hook-check");
            foreach (var name in EnvironmentVariablesToStrip)
            {
                startInfo.Environment.Remove(name);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new AgyHookLivenessResult(false, "the hook process did not start");
            }

            process.StandardInput.Write(SyntheticDeniedToolCall);
            process.StandardInput.Close();

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited between the WaitForExit timeout and this Kill — the timeout
                    // verdict below still stands; whether it exited a moment late does not un-time-out
                    // the probe.
                }

                return new AgyHookLivenessResult(
                    false, $"the hook did not respond within {timeout.TotalSeconds:0.#}s (timed out)");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            return Evaluate(stdout);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new AgyHookLivenessResult(false, $"the hook process could not be run: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses the hook's stdout the same way agy itself would (<c>agy.hook-malformed-stdout-fails-open</c>):
    /// a syntactically valid <c>{"decision":"deny",...}</c> object is the only accepted shape. Pulled out
    /// as its own method so a unit test can drive every stdout shape without spawning a process.
    /// </summary>
    internal static AgyHookLivenessResult Evaluate(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return new AgyHookLivenessResult(false, "the hook produced no stdout");
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("decision", out var decisionProp)
                && decisionProp.ValueKind == JsonValueKind.String)
            {
                var decision = decisionProp.GetString();
                return decision == "deny"
                    ? new AgyHookLivenessResult(true, "deny")
                    : new AgyHookLivenessResult(false, $"the hook returned decision '{decision}' instead of 'deny'");
            }

            return new AgyHookLivenessResult(false, $"the hook's stdout carried no 'decision' field: {stdout.Trim()}");
        }
        catch (JsonException)
        {
            return new AgyHookLivenessResult(false, $"the hook's stdout was not valid JSON: {stdout.Trim()}");
        }
    }
}
