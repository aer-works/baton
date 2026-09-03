namespace Baton.Cli;

/// <summary>
/// Parses <c>baton watch</c>'s arguments: <c>baton watch &lt;room-dir&gt; --notify &lt;command|url&gt;</c>,
/// <c>baton watch --list</c>, or <c>baton watch --clear-fired</c>. Follows
/// <see cref="RoomsPruneOptionsParser"/>'s own error-handling contract — every failure is a
/// <see cref="CliArgumentException"/>, never a bare framework exception.
/// </summary>
public static class WatchOptionsParser
{
    public const string Usage =
        "Usage: baton watch <room-dir> --notify <command|url> | baton watch --list | baton watch --clear-fired";

    public static WatchOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 1 && args[0] == "--list")
        {
            return new WatchOptions(WatchMode.List, null, null);
        }

        if (args.Count == 1 && args[0] == "--clear-fired")
        {
            return new WatchOptions(WatchMode.ClearFired, null, null);
        }

        string? roomDirectoryPath = null;
        string? notifyTarget = null;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--notify":
                    if (i + 1 >= args.Count)
                    {
                        throw new CliArgumentException(
                            $"Option '--notify' requires a value. {Usage}",
                            "pass a value after '--notify', e.g. --notify 'curl -X POST https://ntfy.sh/mytopic'.");
                    }

                    notifyTarget = args[i + 1];
                    i += 2;
                    continue;
                case "--list":
                case "--clear-fired":
                    throw new CliArgumentException(
                        $"'{arg}' cannot be combined with a room directory or other options. {Usage}");
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CliArgumentException($"Unknown option '{arg}'. {Usage}");
                    }

                    if (roomDirectoryPath is not null)
                    {
                        throw new CliArgumentException($"Unexpected extra argument '{arg}'. {Usage}");
                    }

                    roomDirectoryPath = arg;
                    i++;
                    continue;
            }
        }

        if (roomDirectoryPath is null)
        {
            throw new CliArgumentException($"Missing required <room-dir> argument. {Usage}");
        }

        if (string.IsNullOrWhiteSpace(notifyTarget))
        {
            throw new CliArgumentException($"Missing required --notify <command|url>. {Usage}");
        }

        return new WatchOptions(WatchMode.Register, RoomDirectoryPath.Resolve(roomDirectoryPath), notifyTarget);
    }
}
