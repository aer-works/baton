namespace Baton.Cli;

/// <summary>
/// Parses <c>baton resume</c>'s arguments: <c>baton resume &lt;room-dir&gt; --worker &lt;role&gt;
/// (--message &lt;text&gt; | --message-file &lt;path&gt;) --bindings &lt;bindings-file&gt;
/// [--workflow-id &lt;id&gt;]</c>. Mirrors <see cref="SupplyOptionsParser"/>'s conventions — every
/// failure here is a <see cref="CliArgumentException"/>, never a bare
/// <see cref="InvalidOperationException"/>.
/// </summary>
public static class ResumeOptionsParser
{
    public const string Usage =
        "Usage: baton resume <room-dir> --worker <role> (--message <text> | --message-file <path>) " +
        "--bindings <bindings-file> [--workflow-id <id>]";

    public static ResumeOptions Parse(IReadOnlyList<string> args)
    {
        string? roomDirectoryPath = null;
        string? worker = null;
        string? message = null;
        string? messageFilePath = null;
        string? bindingsFilePath = null;
        string? workflowId = null;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--worker":
                    worker = RequireValue(args, ref i, arg);
                    break;
                case "--message":
                    message = RequireValue(args, ref i, arg);
                    break;
                case "--message-file":
                    messageFilePath = RequireValue(args, ref i, arg);
                    break;
                case "--bindings":
                    bindingsFilePath = RequireValue(args, ref i, arg);
                    break;
                case "--workflow-id":
                    workflowId = RequireValue(args, ref i, arg);
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

        if (worker is null)
        {
            throw new CliArgumentException($"Missing required option '--worker <role>'. {Usage}");
        }

        if (message is null && messageFilePath is null)
        {
            throw new CliArgumentException(
                $"Missing required option '--message <text>' or '--message-file <path>'. {Usage}",
                $"pass --message \"<text>\" (or --message-file <path> for a longer message).");
        }

        if (message is not null && messageFilePath is not null)
        {
            throw new CliArgumentException(
                $"'--message' and '--message-file' are mutually exclusive — pass exactly one. {Usage}");
        }

        if (bindingsFilePath is null)
        {
            throw new CliArgumentException(
                $"Missing required option '--bindings <bindings-file>'. {Usage}",
                "pass --bindings <path-to-bindings.json> naming the same bindings the room was dispatched with.");
        }

        return new ResumeOptions(
            RoomDirectoryPath.Resolve(roomDirectoryPath), worker, message, messageFilePath, bindingsFilePath, workflowId);
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
