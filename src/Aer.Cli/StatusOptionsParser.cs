namespace Aer.Cli;

/// <summary>
/// Parses <c>aer status</c>'s arguments: <c>aer status &lt;room-dir&gt; [--follow]</c>. Never
/// throws a bare <see cref="InvalidOperationException"/> for a malformed invocation — every
/// failure here is a <see cref="CliArgumentException"/> (CLAUDE.md's error-handling rules),
/// mirroring <see cref="RunOptionsParser"/>/<see cref="CancelOptionsParser"/>.
/// </summary>
public static class StatusOptionsParser
{
    public const string Usage = "Usage: aer status <room-dir> [--follow] [--json]";

    public static StatusOptions Parse(IReadOnlyList<string> args)
    {
        string? roomDirectoryPath = null;
        var follow = false;
        var json = false;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--follow":
                    follow = true;
                    i++;
                    break;
                case "--json":
                    json = true;
                    i++;
                    break;
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
                    break;
            }
        }

        if (roomDirectoryPath is null)
        {
            throw new CliArgumentException($"Missing required <room-dir> argument. {Usage}");
        }

        if (follow && json)
        {
            throw new CliArgumentException(
                $"'--follow' and '--json' are incompatible: --json prints exactly one object and returns, --follow " +
                $"never stops printing on its own. {Usage}",
                $"aer status {roomDirectoryPath} --json");
        }

        return new StatusOptions(RoomDirectoryPath.Resolve(roomDirectoryPath), follow, json);
    }
}
