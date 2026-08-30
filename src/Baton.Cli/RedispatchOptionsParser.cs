namespace Baton.Cli;

/// <summary>
/// Parses <c>baton redispatch</c>'s arguments: <c>baton redispatch &lt;room-dir&gt; [--spec &lt;spec-file&gt;]
/// [--adapter &lt;name&gt;] [--model &lt;name&gt;] [--effort &lt;name&gt;] [--workspace &lt;dir&gt;]
/// [--output &lt;path&gt;] [--timeout &lt;minutes&gt;]</c>. No <c>--room-dir</c> flag: the new room's
/// directory is always freshly generated (see <see cref="Parse"/>), the same never-reused rule
/// <see cref="DispatchOptionsParser"/> documents for <c>baton dispatch</c>. Every malformed invocation is
/// a <see cref="CliArgumentException"/> (CLAUDE.md's error-handling rules).
/// </summary>
public static class RedispatchOptionsParser
{
    /// <summary><c>baton redispatch</c>'s usage string, same role as <see cref="DispatchOptionsParser"/>'s own.</summary>
    public const string Usage =
        "Usage: baton redispatch <room-dir> [--spec <amended-brief>] [--adapter <name>] [--model <name>] "
        + "[--effort <name>] [--workspace <dir>] [--output <path>] [--timeout <minutes>]";

    public static RedispatchOptions Parse(IReadOnlyList<string> args)
    {
        string? parentRoomDirectoryPath = null;
        string? specFilePath = null;
        string? adapter = null;
        string? model = null;
        string? effort = null;
        string? workspaceDirectory = null;
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
                case "--workspace":
                    workspaceDirectory = RequireValue(args, ref i, arg);
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

                    if (parentRoomDirectoryPath is not null)
                    {
                        throw new CliArgumentException($"Unexpected extra argument '{arg}'. {Usage}");
                    }

                    parentRoomDirectoryPath = arg;
                    i++;
                    break;
            }
        }

        if (parentRoomDirectoryPath is null)
        {
            throw new CliArgumentException(
                $"Missing required <room-dir> argument. {Usage}",
                "pass the terminal room directory to redispatch, e.g. baton redispatch <room-dir>.");
        }

        // Fresh and unique per invocation, never derived from the parent's name or path -- the same
        // rule DispatchOptionsParser documents: a redispatch is a NEW room, never a resume of one
        // (spec/baton.md §2).
        var uniqueName = $"redispatch-{Guid.NewGuid().ToString("N")[..8]}";
        var freshRoomDirectoryPath = Path.Combine(Baton.Status.BatonPaths.Rooms, uniqueName);

        return new RedispatchOptions(
            RoomDirectoryPath.Resolve(parentRoomDirectoryPath),
            RoomDirectoryPath.Resolve(freshRoomDirectoryPath),
            specFilePath,
            adapter, model, effort,
            workspaceDirectory is null ? null : Path.GetFullPath(workspaceDirectory),
            outputPath is null ? null : Path.GetFullPath(outputPath),
            timeout);
    }

    /// <summary>Same ceiling/warn thresholds and rationale as <see cref="DispatchOptionsParser"/>'s own <c>--timeout</c> (#1442).</summary>
    private static TimeSpan ParseTimeout(string rawValue)
    {
        if (!int.TryParse(rawValue, out var minutes) || minutes <= 0)
        {
            throw new CliArgumentException(
                $"'--timeout {rawValue}' is not a positive whole number of minutes. {Usage}",
                "pass a positive integer, e.g. --timeout 90.");
        }

        if (minutes > DispatchOptionsParser.MaxTimeoutMinutes)
        {
            throw new CliArgumentException(
                $"'--timeout {rawValue}' exceeds the {DispatchOptionsParser.MaxTimeoutMinutes}-minute (24h) "
                + "ceiling. A non-interactive dispatch cannot ask for confirmation, so a value this large is "
                + "refused outright rather than risk a typo stranding a lane for a full day.",
                $"pass a value at or below {DispatchOptionsParser.MaxTimeoutMinutes}, e.g. --timeout 120.");
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
