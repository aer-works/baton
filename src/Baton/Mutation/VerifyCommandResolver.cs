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
/// #1708 M1: what <see cref="VerifyCommandResolver.ReadCommittedRepoDeclarationAsync"/> established about
/// the workspace's declaration — the command line itself, plus whether the commit it came from had been
/// through review.
/// </summary>
/// <param name="CommandLine">
/// The declared command line, or <c>null</c> for anything short of a positive read (no repo, no such
/// path in the tree, <c>git</c> unspawnable, a cancelled probe).
/// </param>
/// <param name="Unreviewed">
/// <see langword="true"/> when the read fell back to <c>HEAD</c> because <c>origin/main</c> does not
/// resolve, so the declaration is whatever the current branch tip holds and a lane's own commit COULD
/// have authored it. Diagnostic only — it changes nothing about what runs, and is journaled as
/// <see cref="Domain.FlowEvent.VerifyDeclarationUnreviewed"/>. Always <see langword="false"/> when
/// <paramref name="CommandLine"/> is <c>null</c>: there is nothing to call unreviewed.
/// </param>
public sealed record CommittedVerifyDeclaration(string? CommandLine, bool Unreviewed)
{
    public static readonly CommittedVerifyDeclaration None = new(null, false);
}

/// <summary>
/// #1702 (contract: <c>spec/baton.md</c> §3, "Verify command resolution"): resolves the verify command
/// for a workspace — precedence order and rationale are stated there, not restated here.
/// <para>
/// #1708 H1: the repo-declaration arm is a value the CALLER read from the workspace's REVIEWED tree
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
    /// The remote-tracking ref the reviewed baseline is taken from (#1708 M1). Deliberately one fixed
    /// name rather than a discovered default branch — spec/baton.md §3 states why, and what that costs.
    /// </summary>
    private const string ReviewedBaselineRef = "origin/main";

    /// <summary>
    /// #1708 H1/M1: the workspace's <c>.baton/verify</c> as the REVIEWED tree holds it — the merge-base
    /// of <c>HEAD</c> with <see cref="ReviewedBaselineRef"/>, so nothing a lane commits on its own branch
    /// can change what grades it. <b>Call this before dispatching the worker</b> — spec/baton.md §3 states
    /// why the timing matters as much as the source.
    /// <para>
    /// When no merge-base can be computed the read falls back to <c>HEAD</c> and reports
    /// <see cref="CommittedVerifyDeclaration.Unreviewed"/> — the causes, and the narrower boundary that
    /// leaves, are scoped in spec/baton.md §3 rather than re-derived here.
    /// </para>
    /// <para>
    /// Anything short of a positive read returns <see cref="CommittedVerifyDeclaration.None"/> — no repo,
    /// no <c>HEAD</c>, the path absent from the tree, <c>git</c> unspawnable, a cancelled probe.
    /// spec/baton.md §3 records what that costs and why it is the safe direction.
    /// </para>
    /// </summary>
    public static async Task<CommittedVerifyDeclaration> ReadCommittedRepoDeclarationAsync(
        string? workspaceDirectory, CancellationToken cancellationToken, string gitProgram = "git")
    {
        if (string.IsNullOrWhiteSpace(workspaceDirectory))
        {
            return CommittedVerifyDeclaration.None;
        }

        // `git merge-base` exits non-zero for BOTH an unresolvable ref (128) and unrelated histories
        // (1); neither is distinguishable from the other in a way that matters here, and both mean "no
        // reviewed baseline exists", so both take the HEAD fallback rather than feeding an empty
        // revision into the read below.
        var (baseExit, baseOutput) = await RunGitAsync(
            gitProgram, ["merge-base", "HEAD", ReviewedBaselineRef], workspaceDirectory, cancellationToken)
            .ConfigureAwait(false);
        var mergeBase = baseExit == 0 ? baseOutput.Trim() : null;
        var unreviewed = string.IsNullOrEmpty(mergeBase);
        var revision = unreviewed ? "HEAD" : mergeBase!;

        // The `./` prefix is load-bearing: `git show <rev>:<path>` resolves <path> against the
        // REPOSITORY ROOT, while `<rev>:./<path>` resolves it against the cwd. Without it, a
        // workspace that is a subdirectory of a repo (a monorepo package) would be graded by the
        // ROOT's declaration -- a file belonging to a directory nobody dispatched -- and the
        // drift comparison, which reads the working tree relative to the workspace, would be
        // comparing two different files.
        var (exitCode, output) = await RunGitAsync(
            gitProgram, ["show", "--no-textconv", $"{revision}:./{RepoDeclarationRelativePath}"], workspaceDirectory, cancellationToken)
            .ConfigureAwait(false);

        var commandLine = exitCode == 0 ? ExtractCommandLine(output.Split('\n')) : null;
        return commandLine is null ? CommittedVerifyDeclaration.None : new CommittedVerifyDeclaration(commandLine, unreviewed);
    }

    /// <summary>
    /// #1708 L3: every <c>git</c> spawn on the pre-dispatch declaration path, run so that neither the
    /// workspace's own contents nor the engine's ambient environment can steer it — spec/baton.md §3
    /// enumerates each measure and what it is for, and this method is the single place they are applied.
    /// <para>
    /// A spawn failure or cancellation is reported as a non-zero exit with empty output rather than
    /// thrown, because this runs ahead of the worker and an optional file must never abort a dispatch.
    /// Every caller reads that as "nothing established", which falls through to the role default and
    /// never to a worker-authored command.
    /// </para>
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunGitAsync(
        string gitProgram, IReadOnlyList<string> args, string workingDirectory, CancellationToken cancellationToken)
    {
        string[] hardened = ["--no-pager", "-c", "core.hooksPath=", .. args];
        try
        {
            return await VerifyRunner.CaptureAsync(
                gitProgram, hardened, workingDirectory, cancellationToken,
                stdoutOnly: true,
                environmentAllowList: GitEnvironmentAllowList,
                environmentOverrides: new Dictionary<string, string> { ["PATH"] = ScrubbedPath(workingDirectory) })
                .ConfigureAwait(false);
        }
        catch (BatonException)
        {
            return (-1, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return (-1, string.Empty);
        }
    }

    /// <summary>
    /// The ambient variables the scrubbed <c>git</c> spawn keeps (#1708 L3) — an ALLOWLIST, so a
    /// variable nobody thought about is excluded by construction rather than by remembering to name it.
    /// Everything here is Windows process plumbing <c>git</c> needs to start and find its own install
    /// (this project ships Windows-only, #1405); no <c>GIT_*</c>, and no <c>HOME</c>/<c>USERPROFILE</c>,
    /// which is how <c>~/.gitconfig</c> stays out of a read whose output is a boundary.
    /// <c>PATH</c> is deliberately NOT here: it is always replaced wholesale by
    /// <see cref="ScrubbedPath"/> rather than inherited.
    /// </summary>
    private static readonly string[] GitEnvironmentAllowList =
        ["PATHEXT", "SystemRoot", "SystemDrive", "windir", "ComSpec", "TEMP", "TMP", "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMDATA"];

    /// <summary>
    /// The ambient <c>PATH</c> with every entry that the WORKSPACE could control removed (#1708 L3):
    /// relative entries (which resolve against the spawn's cwd — the worker-writable workspace) and any
    /// absolute entry at or under the workspace itself. Without this, dropping a <c>git.exe</c> into a
    /// dispatched workspace that also contributes a <c>PATH</c> entry would let the workspace answer the
    /// question of what its own reviewed declaration says.
    /// </summary>
    private static string ScrubbedPath(string workspaceDirectory)
    {
        var ambient = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string workspaceFull;
        try
        {
            workspaceFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unresolvable workspace path cannot be compared against, so keep only the entries that
            // are unambiguously not workspace-relative -- the fail-closed reading.
            workspaceFull = string.Empty;
        }

        var kept = ambient
            .Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => Path.IsPathFullyQualified(entry))
            .Where(entry => workspaceFull.Length == 0 || !IsAtOrUnder(entry, workspaceFull));

        return string.Join(Path.PathSeparator, kept);
    }

    private static bool IsAtOrUnder(string candidate, string root)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Unresolvable, so it cannot be shown NOT to be under the workspace -- drop it.
            return true;
        }

        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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

        // #1708 M2: the workspace positively having NO pixi manifest is evidence of absence in the same
        // class as "pixi task list ran and did not list the task" -- a role default of `pixi run <task>`
        // simply does not exist in a workspace that is not a pixi project. Checked HERE, before the
        // spawn, and by reading the filesystem rather than by interpreting a failed probe: that ordering
        // is what keeps #1708 H2 intact. Once a manifest IS found, every probe failure below (including
        // pixi missing from PATH entirely) stays "the engine's own tool is broken", reports runnable,
        // and lets the real run decide.
        if (!HasPixiManifest(workingDirectory))
        {
            return (false, $"no pixi project: {command.Label}");
        }

        // pixiProgram defaults to the real "pixi" -- overridable only so a test can point this at
        // an unspawnable name and exercise the "pixi itself is broken" fallback below without
        // needing an actually-broken pixi installation.
        return await CheckPixiTaskAsync(command.Label, workingDirectory, pixiProgram, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether <paramref name="workingDirectory"/> is inside a pixi project, mirroring pixi's own
    /// manifest discovery: a <c>pixi.toml</c>, or a <c>pyproject.toml</c> carrying a <c>[tool.pixi]</c>
    /// table, in that directory or ANY ancestor. spec/baton.md §3 states why the ancestor walk is
    /// load-bearing rather than an optimization.
    /// <para>
    /// Every uncertain answer is <see langword="true"/> (no directory to inspect, an unreadable
    /// <c>pyproject.toml</c>, an inaccessible ancestor): "not runnable" is the claim that needs positive
    /// evidence, so an unsure answer takes the fail-closed direction spec/baton.md §3 describes.
    /// </para>
    /// </summary>
    private static bool HasPixiManifest(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return true;
        }

        DirectoryInfo? dir;
        try
        {
            dir = new DirectoryInfo(Path.GetFullPath(workingDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        for (; dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "pixi.toml")))
            {
                return true;
            }

            var pyproject = Path.Combine(dir.FullName, "pyproject.toml");
            if (!File.Exists(pyproject))
            {
                continue;
            }

            try
            {
                if (File.ReadAllText(pyproject).Contains("[tool.pixi", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
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
