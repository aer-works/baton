using Baton.Core;

namespace Baton.Mutation;

/// <summary>
/// Which of #1702's three precedence arms produced a <see cref="ResolvedVerifyCommand"/> —
/// <c>--verify</c> beats a repo declaration beats the role's own <c>verify_pixi_task</c>, spec/baton.md
/// §3's resolution order. Carried alongside the resolved command only for <see cref="VerifyCommandResolver.CheckRunnableAsync"/>'s
/// own branching (a pixi task is probed by name against <c>pixi task list</c>; a repo/override command
/// line is probed by whether its own executable resolves) — never surfaced past that.
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
/// for a workspace — precedence order and rationale are stated there, not restated here. Read fresh
/// from disk on every call rather than cached onto a binding, so a redispatch never runs a stale
/// declaration.
/// </summary>
public static class VerifyCommandResolver
{
    /// <summary>The repo-level declaration file — see <see cref="ReadRepoDeclaration"/> and spec/baton.md §3 for its grammar.</summary>
    public const string RepoDeclarationRelativePath = ".baton/verify";

    public static ResolvedVerifyCommand? Resolve(string? workspaceDirectory, string? overrideCommand, string? roleVerifyPixiTask)
    {
        if (!string.IsNullOrWhiteSpace(overrideCommand))
        {
            return FromCommandLine(overrideCommand.Trim(), VerifyCommandSource.Override);
        }

        if (ReadRepoDeclaration(workspaceDirectory) is { } repoCommand)
        {
            return FromCommandLine(repoCommand, VerifyCommandSource.RepoDeclaration);
        }

        return string.IsNullOrWhiteSpace(roleVerifyPixiTask)
            ? null
            : new ResolvedVerifyCommand("pixi", ["run", roleVerifyPixiTask], VerifyCommandSource.RoleDefault, roleVerifyPixiTask);
    }

    private static string? ReadRepoDeclaration(string? workspaceDirectory)
    {
        if (string.IsNullOrWhiteSpace(workspaceDirectory))
        {
            return null;
        }

        var path = Path.Combine(workspaceDirectory, RepoDeclarationRelativePath);
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length > 0 && !line.StartsWith('#'))
            {
                return line;
            }
        }

        return null;
    }

    // #1702: an arbitrary declared/overridden command line is run through the platform shell (this
    // project ships Windows-only, #1405) rather than hand-tokenized -- a command line can carry
    // quoting, pipes, or flags a naive whitespace split would mangle. The role's own pixi-task default
    // below stays a direct `pixi run <task>` spawn, unchanged from #1623.
    private static ResolvedVerifyCommand FromCommandLine(string commandLine, VerifyCommandSource source) =>
        new("cmd.exe", ["/d", "/c", commandLine], source, commandLine);

    /// <summary>
    /// #1702 item 2: the pre-flight check that turns "the resolved command doesn't exist in this
    /// workspace" into a distinct not-run outcome instead of a spawn failure indistinguishable from a
    /// broken gate. A <see cref="VerifyCommandSource.RoleDefault"/> command is checked against
    /// <c>pixi task list</c>'s own output (the exact shape the #1702 issue measured: <c>gates-quiet</c>
    /// baked into <c>implement</c>, absent from a foreign workspace's task list); any other source is
    /// checked by whether its command line's own first token resolves as an executable, the same
    /// "the executable resolves" check the issue's own wording asks for.
    /// </summary>
    public static async Task<(bool Runnable, string? Reason)> CheckRunnableAsync(
        ResolvedVerifyCommand command, string? workingDirectory, CancellationToken cancellationToken, string pixiProgram = "pixi")
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Source == VerifyCommandSource.RoleDefault)
        {
            // pixiProgram defaults to the real "pixi" -- overridable only so a test can point this at
            // an unspawnable name and exercise the "pixi itself is broken" fallback below without
            // needing an actually-broken pixi installation.
            return await CheckPixiTaskAsync(command.Label, workingDirectory, pixiProgram, cancellationToken).ConfigureAwait(false);
        }

        var firstToken = FirstToken(command.Label);

        // A quoted path or a cmd.exe intrinsic/shell-metacharacter line (`if`, `for`, `echo`,
        // `a && b`, ...) isn't a bare executable name this filesystem-only PATH lookup can resolve --
        // report runnable rather than mislabel a genuinely runnable line "not runnable" on a wrong
        // reason. Only an UNQUOTED, metacharacter-free first token gets an actual PATH check.
        if (firstToken.Length == 0 || ContainsShellMetacharacter(command.Label))
        {
            return (true, null);
        }

        return ExecutableResolves(firstToken, workingDirectory)
            ? (true, (string?)null)
            : (false, $"executable not found: {firstToken}");
    }

    private static readonly char[] ShellMetacharacters = ['&', '|', '<', '>', '%', '"', '^'];

    private static bool ContainsShellMetacharacter(string commandLine) =>
        commandLine.IndexOfAny(ShellMetacharacters) >= 0;

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
            return (false, $"task absent: {task} (pixi task list failed)");
        }

        var knownTasks = output.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
        return knownTasks.Any(known => string.Equals(known, task, StringComparison.Ordinal))
            ? (true, null)
            : (false, $"task absent: {task}");
    }

    private static string FirstToken(string commandLine)
    {
        var trimmed = commandLine.TrimStart();
        var spaceIndex = trimmed.IndexOfAny([' ', '\t']);
        return spaceIndex < 0 ? trimmed : trimmed[..spaceIndex];
    }

    /// <summary>
    /// A pure filesystem PATH resolution (no process spawn) for a repo-declared/overridden command's
    /// own executable -- deliberately not a <c>where</c> spawn, so the check is fast, deterministic, and
    /// trivially pointed at a fake PATH in a test.
    /// </summary>
    private static bool ExecutableResolves(string program, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(program))
        {
            return false;
        }

        if (Path.IsPathRooted(program))
        {
            return File.Exists(program);
        }

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Append(string.Empty);
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            directories = [workingDirectory, .. directories];
        }

        return directories
            .SelectMany(_ => extensions, (dir, ext) => Path.Combine(dir, program + ext))
            .Any(File.Exists);
    }
}
