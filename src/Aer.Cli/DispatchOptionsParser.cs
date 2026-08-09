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
        "Usage: aer dispatch <name> [--spec <spec-file>] [--adapter <name>] [--model <name>] [--effort <name>] [--room-dir <dir>] [--workspace <dir>] [--workflow-id <id>]";

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
            throw new CliArgumentException($"Missing required <name> argument. {Usage}");
        }

        // Fresh and unique per invocation unless pinned: a dispatch is one-shot, and deriving a stable
        // directory from the name (the way `aer run` derives one from the workflow file) would make a
        // second `aer dispatch review` resume — and so replay — the first's terminal snapshot rather
        // than run again. The per-execution artifact dir already keeps outputs collision-free (#897);
        // this keeps the *task* fresh so the orchestrator's repeated self-dispatch (#778) actually reruns.
        if (roomDirectoryPath is null)
        {
            var uniqueName = $"dispatch-{name}-{Guid.NewGuid().ToString("N")[..8]}";
            roomDirectoryPath = Path.Combine(Directory.GetCurrentDirectory(), ".aer", uniqueName);
        }

        return new DispatchOptions(
            name, specFilePath, RoomDirectoryPath.Resolve(roomDirectoryPath), adapter, workflowId,
            workspaceDirectory is null ? null : Path.GetFullPath(workspaceDirectory),
            model, effort);
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string optionName)
    {
        if (index + 1 >= args.Count)
        {
            throw new CliArgumentException($"Option '{optionName}' requires a value. {Usage}");
        }

        var value = args[index + 1];
        index += 2;
        return value;
    }
}
