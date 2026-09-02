using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// Parses <c>baton deliver</c>'s arguments: <c>baton deliver &lt;file&gt; [--title &lt;text&gt;] [--room &lt;room-dir&gt;]</c> (#1669).
/// <c>--room-dir</c> is accepted as an alias for <c>--room</c> (F6, 2026-09-02 review) — the flag
/// name every other verb's own <c>&lt;room-dir&gt;</c> positional/option uses.
/// </summary>
public static class DeliverOptionsParser
{
    public const string Usage =
        "Usage: baton deliver <file> [--title <text>] [--room <room-dir>] [--room-dir <room-dir>]";

    public static DeliverOptions Parse(IReadOnlyList<string> args)
    {
        string? sourceFilePath = null;
        string? title = null;
        string? roomDirectoryPath = null;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            if (arg == "--title")
            {
                i++;
                if (i >= args.Count || args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new CliArgumentException($"Option '--title' requires a value. {Usage}");
                }
                title = args[i];
                i++;
                continue;
            }

            if (arg == "--room" || arg == "--room-dir")
            {
                i++;
                if (i >= args.Count || args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new CliArgumentException($"Option '{arg}' requires a value. {Usage}");
                }
                roomDirectoryPath = args[i];
                i++;
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new CliArgumentException($"Unknown option '{arg}'. {Usage}");
            }

            if (sourceFilePath is not null)
            {
                throw new CliArgumentException($"Unexpected extra argument '{arg}'. {Usage}");
            }

            sourceFilePath = arg;
            i++;
        }

        if (sourceFilePath is null)
        {
            throw new CliArgumentException($"Missing required <file> argument. {Usage}");
        }

        var resolvedRoomPath = roomDirectoryPath is not null
            ? RoomDirectoryPath.Resolve(roomDirectoryPath)
            : Path.Combine(BatonPaths.Rooms, "conductor");

        return new DeliverOptions(sourceFilePath, title, resolvedRoomPath);
    }
}
