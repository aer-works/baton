namespace Aer.Cli;

/// <summary>
/// Parses <c>aer run</c>'s arguments: <c>aer run &lt;workflow-file&gt; --bindings &lt;bindings-file&gt;
/// [--room-dir &lt;dir&gt;] [--workflow-id &lt;id&gt;] [--echo-worker]</c>. Never throws a bare
/// <see cref="InvalidOperationException"/> for a malformed invocation — every failure here is a
/// <see cref="CliArgumentException"/> (CLAUDE.md's error-handling rules).
/// </summary>
public static class RunOptionsParser
{
    /// <summary>
    /// The one copy of <c>aer run</c>'s usage line, printed both from here on an argument error and
    /// by <c>Program</c> in the full command list.
    /// </summary>
    public const string Usage =
        "Usage: aer run <workflow-file> --bindings <bindings-file> [--room-dir <dir>] [--workflow-id <id>] [--echo-worker] [--wait]";

    /// <summary>
    /// #628: <c>&lt;workflow-file&gt;</c> reads as "this is what runs", and under
    /// <c>--room-dir</c> it often is not. Printed wherever <see cref="Usage"/> is, since a reader
    /// who needs the arguments spelled out is exactly the reader whose prior this corrects.
    /// </summary>
    public const string ResumeNote =
        "aer run resumes a --room-dir that already holds a snapshot, running the workflow that " +
        "directory was first bound to rather than <workflow-file>. It refuses when the two are " +
        "different templates. Use a fresh --room-dir to start different work.";

    public static RunOptions Parse(IReadOnlyList<string> args)
    {
        string? workflowFilePath = null;
        string? bindingsFilePath = null;
        string? roomDirectoryPath = null;
        string? workflowId = null;
        var echoWorker = false;
        var wait = false;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--bindings":
                    bindingsFilePath = RequireValue(args, ref i, arg);
                    break;
                case "--room-dir":
                    roomDirectoryPath = RequireValue(args, ref i, arg);
                    break;
                case "--workflow-id":
                    workflowId = RequireValue(args, ref i, arg);
                    break;
                case "--echo-worker":
                    echoWorker = true;
                    i++;
                    break;
                case "--wait":
                    wait = true;
                    i++;
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CliArgumentException($"Unknown option '{arg}'. {Usage} {ResumeNote}");
                    }

                    if (workflowFilePath is not null)
                    {
                        throw new CliArgumentException($"Unexpected extra argument '{arg}'. {Usage} {ResumeNote}");
                    }

                    workflowFilePath = arg;
                    i++;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(workflowFilePath))
        {
            throw new CliArgumentException($"Missing required <workflow-file> argument. {Usage} {ResumeNote}");
        }

        if (bindingsFilePath is null)
        {
            throw new CliArgumentException(
                $"Missing required option '--bindings <bindings-file>'. {Usage} {ResumeNote}",
                "pass --bindings <path-to-bindings.json> to configure the workers for this run, or use 'aer dispatch' to auto-generate them.");
        }

        // Derived from the workflow file's own name when not given, so `aer run workflow.json`
        // twice in the same directory naturally resumes the same room (§21) rather than each
        // invocation needing its own explicit --room-dir.
        roomDirectoryPath ??= Path.Combine(
            Directory.GetCurrentDirectory(), ".aer", Path.GetFileNameWithoutExtension(workflowFilePath));

        return new RunOptions(
            workflowFilePath, bindingsFilePath, RoomDirectoryPath.Resolve(roomDirectoryPath), workflowId, echoWorker,
            Wait: wait);
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string optionName)
    {
        if (index + 1 >= args.Count)
        {
            throw new CliArgumentException($"Option '{optionName}' requires a value. {Usage} {ResumeNote}");
        }

        var value = args[index + 1];
        index += 2;
        return value;
    }
}
