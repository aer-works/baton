using System.Security.Cryptography;
using System.Text;
using Baton.Core;

namespace Baton.Mutation;

/// <summary>
/// Which of #1702's three precedence arms produced a <see cref="ResolvedVerifyCommand"/> —
/// <c>--verify</c> beats a repo declaration beats the role's own <c>verify_pixi_task</c>, spec/baton.md
/// §3's resolution order. Carried alongside the resolved command only for <see cref="VerifyCommandResolver.CheckRunnableAsync"/>'s
/// own branching: a <see cref="RoleDefault"/> pixi task is probed by name against <c>pixi task list</c>,
/// and nothing else is probed at all (#1708 H3) — never surfaced past that.
/// </summary>
public enum VerifyCommandSource
{
    Override,
    RepoDeclaration,
    RoleDefault,
}

/// <summary>
/// #1702: the verify command actually spawned, plus enough to describe it in a "task absent"/"executable
/// not found" reason. <see cref="Label"/> is the pixi task name for <see cref="VerifyCommandSource.RoleDefault"/>,
/// or the raw command line otherwise — <see cref="VerifyCommandResolver.CheckRunnableAsync"/>'s own
/// reason text, never re-derived from <see cref="Program"/>/<see cref="Args"/>.
/// </summary>
public sealed record ResolvedVerifyCommand(
    string Program,
    IReadOnlyList<string> Args,
    VerifyCommandSource Source,
    string Label);

/// <summary>
/// #1702 (contract: <c>spec/baton.md</c> §3, "Verify command resolution"): resolves the verify command
/// for a workspace — precedence order and rationale are stated there, not restated here.
/// <para>
/// #1708 H1: the repo-declaration arm is a value the CALLER read from the workspace's COMMITTED tree
/// before the worker ever ran (<see cref="ReadCommittedRepoDeclarationAsync"/>), never a working-tree
/// read taken afterwards. spec/baton.md §3 states why both halves are load-bearing.
/// </para>
/// </summary>
public static class VerifyCommandResolver
{
    /// <summary>The repo-level declaration file — see <see cref="ExtractCommandLine"/> and spec/baton.md §3 for its grammar.</summary>
    public const string RepoDeclarationRelativePath = ".baton/verify";

    /// <param name="committedRepoDeclaration">
    /// The workspace's committed <c>.baton/verify</c> command line, as
    /// <see cref="ReadCommittedRepoDeclarationAsync"/> returned it before dispatch — never a fresh
    /// working-tree read (#1708 H1).
    /// </param>
    public static ResolvedVerifyCommand? Resolve(string? committedRepoDeclaration, string? overrideCommand, string? roleVerifyPixiTask)
    {
        if (!string.IsNullOrWhiteSpace(overrideCommand))
        {
            return FromCommandLine(overrideCommand.Trim(), VerifyCommandSource.Override);
        }

        if (!string.IsNullOrWhiteSpace(committedRepoDeclaration))
        {
            return FromCommandLine(committedRepoDeclaration.Trim(), VerifyCommandSource.RepoDeclaration);
        }

        return string.IsNullOrWhiteSpace(roleVerifyPixiTask)
            ? null
            : new ResolvedVerifyCommand("pixi", ["run", roleVerifyPixiTask], VerifyCommandSource.RoleDefault, roleVerifyPixiTask);
    }

    /// <summary>
    /// #1708 H1: the workspace's <c>.baton/verify</c> as <c>HEAD</c> holds it, via
    /// <c>git show HEAD:.baton/verify</c>. <b>Call this before dispatching the worker</b> — spec/baton.md
    /// §3 states why the timing matters as much as the source.
    /// <para>
    /// Anything short of a positive read returns <c>null</c> — no repo, no <c>HEAD</c>, the path absent
    /// from the tree, <c>git</c> unspawnable, a cancelled probe. spec/baton.md §3 records what that
    /// costs and why it is the safe direction; <see cref="Domain.FlowEvent.VerifyDeclarationIgnored"/> announces
    /// it at runtime.
    /// </para>
    /// </summary>
    public static async Task<string?> ReadCommittedRepoDeclarationAsync(
        string? workspaceDirectory, CancellationToken cancellationToken, string gitProgram = "git")
    {
        if (string.IsNullOrWhiteSpace(workspaceDirectory))
        {
            return null;
        }

        int exitCode;
        string output;
        try
        {
            // The `./` prefix is load-bearing: `git show HEAD:<path>` resolves <path> against the
            // REPOSITORY ROOT, while `HEAD:./<path>` resolves it against the cwd. Without it, a
            // workspace that is a subdirectory of a repo (a monorepo package) would be graded by the
            // ROOT's declaration -- a file belonging to a directory nobody dispatched -- and the
            // drift comparison below, which reads the working tree relative to the workspace, would
            // be comparing two different files.
            (exitCode, output) = await VerifyRunner.CaptureAsync(
                gitProgram, ["show", $"HEAD:./{RepoDeclarationRelativePath}"], workspaceDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BatonException)
        {
            // git unspawnable. Unlike CheckRunnableAsync's own "let the real run decide" arms, there is
            // no later attempt that could recover this one -- the only safe reading of "I could not
            // establish what the repo committed" is "nothing", which costs a fallthrough to the role
            // default and never a worker-authored command.
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        return exitCode == 0 ? ExtractCommandLine(output.Split('\n')) : null;
    }

    /// <summary>
    /// The live working-tree <c>.baton/verify</c>, read only to DETECT drift from the committed
    /// declaration (<see cref="Domain.FlowEvent.VerifyDeclarationIgnored"/>) — never to decide what runs.
    /// </summary>
    public static string? ReadWorkingTreeRepoDeclaration(string? workspaceDirectory)
    {
        if (string.IsNullOrWhiteSpace(workspaceDirectory))
        {
            return null;
        }

        var path = Path.Combine(workspaceDirectory, RepoDeclarationRelativePath);
        return File.Exists(path) ? ExtractCommandLine(File.ReadLines(path)) : null;
    }

    /// <summary>
    /// The declaration's grammar (spec/baton.md §3): the first non-blank, non-<c>#</c>-comment line.
    /// </summary>
    private static string? ExtractCommandLine(IEnumerable<string> rawLines)
    {
        foreach (var rawLine in rawLines)
        {
            var line = rawLine.Trim();
            if (line.Length > 0 && !line.StartsWith('#'))
            {
                return line;
            }
        }

        return null;
    }

    /// <summary>
    /// #1708 H1: SHA-256 of the EXTRACTED command line (not of the file's bytes), so the two digests on
    /// <see cref="Domain.FlowEvent.VerifyDeclarationIgnored"/> compare the same thing the resolver
    /// compares — a line-ending or comment-only edit is not drift, and a changed command always is.
    /// <c>null</c> in means <c>null</c> out: "there is no declaration on this side".
    /// </summary>
    public static string? DeclarationDigest(string? commandLine) =>
        commandLine is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(commandLine)));

    // #1702: an arbitrary declared/overridden command line is run through the platform shell (this
    // project ships Windows-only, #1405) rather than hand-tokenized -- a command line can carry
    // quoting, pipes, or flags a naive whitespace split would mangle. The role's own pixi-task default
    // below stays a direct `pixi run <task>` spawn, unchanged from #1623.
    private static ResolvedVerifyCommand FromCommandLine(string commandLine, VerifyCommandSource source) =>
        new("cmd.exe", ["/d", "/c", commandLine], source, commandLine);

    /// <summary>
    /// #1702 item 2: the pre-flight check that turns "the resolved command doesn't exist in this
    /// workspace" into a distinct not-run outcome instead of a spawn failure indistinguishable from a
    /// broken gate. The probe exists for the <c>pixi run &lt;task&gt;</c> shape ONLY — a
    /// <see cref="VerifyCommandSource.RoleDefault"/> task name checked against <c>pixi task list</c>'s
    /// own output, the exact shape #1702 measured (<c>gates-quiet</c> baked into <c>implement</c>,
    /// absent from a foreign workspace's task list).
    /// <para>
    /// #1708 H3: nothing else is pre-probed — spec/baton.md §3 states why (cmd intrinsics), not
    /// restated here. The command's own exit code decides instead.
    /// </para>
    /// </summary>
    public static async Task<(bool Runnable, string? Reason)> CheckRunnableAsync(
        ResolvedVerifyCommand command, string? workingDirectory, CancellationToken cancellationToken, string pixiProgram = "pixi")
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Source != VerifyCommandSource.RoleDefault)
        {
            return (true, null);
        }

        // pixiProgram defaults to the real "pixi" -- overridable only so a test can point this at
        // an unspawnable name and exercise the "pixi itself is broken" fallback below without
        // needing an actually-broken pixi installation.
        return await CheckPixiTaskAsync(command.Label, workingDirectory, pixiProgram, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(bool, string?)> CheckPixiTaskAsync(
        string task, string? workingDirectory, string pixiProgram, CancellationToken cancellationToken)
    {
        int exitCode;
        string output;
        try
        {
            (exitCode, output) = await VerifyRunner.CaptureAsync(pixiProgram, ["task", "list"], workingDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
        // A cancellation mid-probe must never fabricate a "not runnable" verdict -- reporting runnable
        // here just lets the real attempt below run (VerifyStarted + VerifyRunner.RunProcessAsync),
        // which resolves an already-cancelled token into VerifyFailedKind.Cancelled exactly the same
        // way it always has; MutationInterface's own verify-window cancel handling (dispatched
        // ExecutionCancelled, not VerifyFailed/Indeterminate) is what actually settles it from there.
        catch (BatonException ex) when (ex.ErrorCode == BatonErrorCode.Cancelled || cancellationToken.IsCancellationRequested)
        {
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            return (true, null);
        }
        catch (BatonException)
        {
            // pixi itself refused to spawn -- not installed, or some OTHER engine-environment problem
            // unrelated to whether this particular workspace declares the task. #1702's own defect is
            // "the task is absent from THIS workspace", not "the engine's own tool is broken" -- the
            // latter must keep its pre-#1702 behaviour (a real VerifyRunner spawn attempt below, which
            // hits the identical spawn failure and settles Indeterminate via its own BatonException
            // catch), never soften into a silent not-run/Succeeded pass. Reporting runnable here just
            // defers the verdict to that real attempt, the same "let the real run decide" shape the
            // cancellation arms above already use.
            return (true, null);
        }

        if (exitCode != 0)
        {
            // #1708 H2: a probe FAILURE is not evidence of absence -- it is the same
            // engine-environment class the `catch (BatonException)` arm above already refuses to read
            // as "task absent" (spec/baton.md §3 enumerates the causes). Deferring to the real run
            // fails closed; calling it absence skipped a gate that plainly existed. The not-run
            // outcome is reachable ONLY from the positive read below.
            return (true, null);
        }

        var knownTasks = output.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
        return knownTasks.Any(known => string.Equals(known, task, StringComparison.Ordinal))
            ? (true, null)
            : (false, $"task absent: {task}");
    }

}
