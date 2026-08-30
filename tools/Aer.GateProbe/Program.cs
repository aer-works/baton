using System.Text.Json;
using Baton.Vendors;
using Baton.Domain;

namespace Aer.GateProbe;

/// <summary>
/// Prints the invocation a real <see cref="IWorkerAdapter"/> would dispatch, as JSON, so a check can
/// run <b>that exact argv</b> against the vendor CLI instead of a hand-written approximation (#550).
/// </summary>
/// <remarks>
/// <para>
/// This exists so a check does not have to write the flag list down — see
/// <c>gate.adapters-own-flag-set-still-gates</c> in <c>tools/vendor-verify/verify.py</c> for why a
/// hand-picked one cannot catch the next suppression.
/// </para>
/// <para>
/// <b>In <c>tools/</c> and not <c>tests/</c>, for a measured reason.</b> It was written under
/// <c>tests/</c> first, alongside <c>Aer.Flow.CrashTestHost</c>, and did not work:
/// <c>tests/Directory.Build.props</c> links <c>BatonHomeRedirect</c> into every project there, giving
/// each one a throwaway per-process <c>BATON_HOME</c>. This probe's whole job is to write AER's real
/// launch config — the settings file carrying the <c>PreToolUse</c> hook — somewhere a SEPARATE
/// process can then read it, and a home that dies with the probe leaves the caller running
/// <c>claude</c> against a <c>--settings</c> path that no longer exists.
/// </para>
/// <para>
/// An <c>aer</c> verb was rejected too: a CLI verb is a product surface AER would owe a contract
/// for, and this is an instrument.
/// </para>
/// <para>
/// Placeholders are left UNEXPANDED — <c>%BATON_OUTPUT_DIR%</c> and friends are what the adapter really
/// produces, and <c>CoreDispatcher</c> expands them at dispatch. The caller substitutes its own
/// directories, which keeps this honest about what the adapter emits.
/// </para>
/// </remarks>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine(
                "usage: Aer.GateProbe <claude|gemini> [--grant-writes] [--prompt <text>]\n" +
                "Prints {program, args, environment} for the adapter's resolved invocation.");
            return 2;
        }

        var vendor = args[0];
        var grantWrites = args.Contains("--grant-writes");
        var promptIndex = Array.IndexOf(args, "--prompt");
        var prompt = promptIndex >= 0 && promptIndex + 1 < args.Length ? args[promptIndex + 1] : "Say OK.";

        // Reads withheld too, so the denied-tools channel carries something on every arm and the
        // difference between arms is exactly the one category under test.
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: grantWrites, RunShellCommands: false, NetworkAccess: false);

        IWorkerAdapter adapter = vendor switch
        {
            "claude" => new ClaudeWorkerAdapter(),
            "agy" => new AgyWorkerAdapter(),
            _ => throw new ArgumentException($"unknown vendor '{vendor}'"),
        };

        var target = adapter.Resolve(
            new WorkerInvocation(prompt, PermissionGrant: grant),
            new WorkerContract("probe", [], [new ProducedOutput("out.txt")], []));

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            program = target.Program,
            args = target.Args,
            environment = (target.Environment ?? []).ToDictionary(e => e.Name, e => e.Value),
        }));

        return 0;
    }
}
