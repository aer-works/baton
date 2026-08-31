namespace Baton.Cli;

/// <summary>
/// Parses <c>baton unkeep</c>'s arguments: <c>baton unkeep &lt;room-dir&gt;</c>. See
/// <see cref="KeepOptionsParser"/> for why this is a separate class from its counterpart rather than
/// a shared flag.
/// </summary>
public static class UnkeepOptionsParser
{
    public const string Usage = "Usage: baton unkeep <room-dir>";

    public static KeepOptions Parse(IReadOnlyList<string> args)
    {
        string? roomDirectoryPath = null;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
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
        }

        if (roomDirectoryPath is null)
        {
            throw new CliArgumentException($"Missing required <room-dir> argument. {Usage}");
        }

        return new KeepOptions(RoomDirectoryPath.Resolve(roomDirectoryPath));
    }
}
