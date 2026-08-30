namespace Baton.Cli;

/// <summary>
/// Parses <c>baton supply</c>'s arguments: <c>baton supply &lt;room-dir&gt; --worker &lt;role&gt;
/// --output &lt;name&gt; --file &lt;source-path&gt; --bindings &lt;bindings-file&gt;
/// [--workflow-id &lt;id&gt;]</c>. Mirrors <see cref="CancelOptionsParser"/>'s conventions — every
/// failure here is a <see cref="CliArgumentException"/>, never a bare
/// <see cref="InvalidOperationException"/>.
/// </summary>
public static class SupplyOptionsParser
{
    private const string Usage =
        "Usage: baton supply <room-dir> --worker <role> --output <name> --file <source-path> " +
        "--bindings <bindings-file> [--workflow-id <id>]";

    public static SupplyOptions Parse(IReadOnlyList<string> args)
    {
        string? roomDirectoryPath = null;
        string? worker = null;
        string? outputName = null;
        string? sourceFilePath = null;
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
                case "--output":
                    outputName = RequireValue(args, ref i, arg);
                    break;
                case "--file":
                    sourceFilePath = RequireValue(args, ref i, arg);
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

        if (outputName is null)
        {
            throw new CliArgumentException($"Missing required option '--output <name>'. {Usage}");
        }

        if (sourceFilePath is null)
        {
            throw new CliArgumentException($"Missing required option '--file <source-path>'. {Usage}");
        }

        if (bindingsFilePath is null)
        {
            throw new CliArgumentException($"Missing required option '--bindings <bindings-file>'. {Usage}");
        }

        return new SupplyOptions(
            RoomDirectoryPath.Resolve(roomDirectoryPath), worker, outputName, sourceFilePath, bindingsFilePath, workflowId);
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
