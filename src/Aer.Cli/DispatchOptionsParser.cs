namespace Aer.Cli;

/// <summary>
/// Parses <c>aer dispatch</c>'s arguments: <c>aer dispatch &lt;name&gt; [--spec &lt;spec-file&gt;]
/// [--adapter &lt;name&gt;] [--room-dir &lt;dir&gt;] [--workflow-id &lt;id&gt;]</c>. <c>--spec</c> is
/// optional here because whether it is required depends on whether <c>&lt;name&gt;</c> resolves to a
/// role (needs one) or a workflow template (rejects one) — a catalog question <see cref="DispatchCommand"/>
/// answers, not the parser. Every malformed invocation is a <see cref="CliArgumentException"/>
/// (CLAUDE.md's error-handling rules), never a bare <see cref="InvalidOperationException"/>.
/// </summary>
public static class DispatchOptionsParser
{
    /// <summary>The one copy of <c>aer dispatch</c>'s usage line, printed here on error and by <c>Program</c>.</summary>
    public const string Usage =
        "Usage: aer dispatch <name> [--spec <spec-file>] [--adapter <name>] [--model <name>] [--effort <name>] [--room-dir <dir>] [--workspace <dir>] [--workflow-id <id>] [--output <path>] [--timeout <minutes>]";

    /// <summary>
    /// The hard ceiling <c>--timeout</c> refuses outright (#1442) — why refuse rather than confirm:
    /// spec/baton.md §2.
    /// </summary>
    public const int MaxTimeoutMinutes = 24 * 60;

    /// <summary>
    /// The caution threshold <c>--timeout</c> accepts but flags — <see cref="Aer.Cli.DispatchCommand"/>
    /// prints the stderr warning above this; why warn rather than refuse: spec/baton.md §2.
    /// </summary>
    public const int WarnTimeoutMinutes = 120;

    public static DispatchOptions Parse(IReadOnlyList<string> args)
    {
        string? name = null;
        string? specFilePath = null;
        string? adapter = null;
        string? model = null;
        string? effort = null;
        string? roomDirectoryPath = null;
        string? workspaceDirectory = null;
        string? workflowId = null;
        string? outputPath = null;
        TimeSpan? timeout = null;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--spec":
                    specFilePath = RequireValue(args, ref i, arg);
                    break;
                case "--adapter":
                    adapter = RequireValue(args, ref i, arg);
                    break;
                case "--model":
                    model = RequireValue(args, ref i, arg);
                    break;
                case "--effort":
                    effort = RequireValue(args, ref i, arg);
                    break;
                case "--room-dir":
                    roomDirectoryPath = RequireValue(args, ref i, arg);
                    break;
                case "--workspace":
                    workspaceDirectory = RequireValue(args, ref i, arg);
                    break;
                case "--workflow-id":
                    workflowId = RequireValue(args, ref i, arg);
                    break;
                case "--output":
                    outputPath = RequireValue(args, ref i, arg);
                    break;
                case "--timeout":
                    timeout = ParseTimeout(RequireValue(args, ref i, arg));
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CliArgumentException($"Unknown option '{arg}'. {Usage}");
                    }

                    if (name is not null)
                    {
                        throw new CliArgumentException($"Unexpected extra argument '{arg}'. {Usage}");
                    }

                    name = arg;
                    i++;
                    break;
            }
        }

        if (name is null)
        {
            throw new CliArgumentException(
                $"Missing required <name> argument. {Usage}",
                "run 'aer templates' to see available role and template names.");
        }

        // Fresh and unique per invocation unless pinned: a dispatch is one-shot, and deriving a stable
        // directory from the name (the way `aer run` derives one from the workflow file) would make a
        // second `aer dispatch review` resume — and so replay — the first's terminal snapshot rather
        // than run again. The per-execution artifact dir already keeps outputs collision-free (#897);
        // this keeps the *task* fresh so the orchestrator's repeated self-dispatch (#778) actually reruns.
        //
        // R2 (#1354/#1380): the default lives OUTSIDE the workspace, under AerPaths.Rooms
        // ($AER_HOME/rooms, default ~/.aer/rooms) — never under the audited tree itself. A room dropped
        // inside the workspace it audits shows up as `?? .aer/` on that tree's own `git status`, which
        // fails the audit even on an otherwise-pristine workspace (finding 2).
        if (roomDirectoryPath is null)
        {
            var uniqueName = $"dispatch-{name}-{Guid.NewGuid().ToString("N")[..8]}";
            roomDirectoryPath = Path.Combine(Aer.Adapters.AerPaths.Rooms, uniqueName);
        }

        return new DispatchOptions(
            name, specFilePath, RoomDirectoryPath.Resolve(roomDirectoryPath), adapter, workflowId,
            workspaceDirectory is null ? null : Path.GetFullPath(workspaceDirectory),
            model, effort,
            outputPath is null ? null : Path.GetFullPath(outputPath),
            timeout);
    }

    /// <summary>
    /// Parses <c>--timeout</c>'s minutes value: rejects anything that isn't a positive whole number,
    /// and rejects (rather than merely warns on) anything above <see cref="MaxTimeoutMinutes"/> — the
    /// issue's proposed &gt;2h interactive confirmation has no non-interactive equivalent, so the
    /// simplest honest substitute is a hard ceiling here plus a caution-only warning printed by
    /// <see cref="Aer.Cli.DispatchCommand"/> above <see cref="WarnTimeoutMinutes"/>.
    /// </summary>
    private static TimeSpan ParseTimeout(string rawValue)
    {
        if (!int.TryParse(rawValue, out var minutes) || minutes <= 0)
        {
            throw new CliArgumentException(
                $"'--timeout {rawValue}' is not a positive whole number of minutes. {Usage}",
                "pass a positive integer, e.g. --timeout 90.");
        }

        if (minutes > MaxTimeoutMinutes)
        {
            throw new CliArgumentException(
                $"'--timeout {rawValue}' exceeds the {MaxTimeoutMinutes}-minute (24h) ceiling. A "
                + "non-interactive dispatch cannot ask for confirmation, so a value this large is "
                + "refused outright rather than risk a typo stranding a lane for a full day.",
                $"pass a value at or below {MaxTimeoutMinutes}, e.g. --timeout 120.");
        }

        return TimeSpan.FromMinutes(minutes);
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string optionName)
    {
        if (index + 1 >= args.Count)
        {
            throw new CliArgumentException(
                $"Option '{optionName}' requires a value. {Usage}",
                $"pass a value after '{optionName}', e.g. {optionName} <value>.");
        }

        var value = args[index + 1];
        index += 2;
        return value;
    }
}
