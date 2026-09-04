using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// Parses <c>baton cancel</c>'s arguments: <c>baton cancel &lt;room-dir&gt; [--execution &lt;execution-id&gt;]
/// [--bindings &lt;bindings-file&gt;] [--workflow-id &lt;id&gt;]</c>. <c>--execution</c> is optional (#1495):
/// omitted, <see cref="CancelCommand"/> targets "the target lane" itself rather than a caller-named id.
/// <c>--bindings</c> is also optional (#1607 friction fix): omitted, it defaults to
/// <c>&lt;room-dir&gt;/bindings.json</c> — see spec/baton.md §2 ("cancel's --bindings is now optional
/// too") for which rooms actually have one and what a missing default surfaces as. Never throws a bare
/// <see cref="InvalidOperationException"/> for a malformed invocation — every failure here is a
/// <see cref="CliArgumentException"/> (CLAUDE.md's error-handling rules), mirroring
/// <see cref="RunOptionsParser"/>.
/// </summary>
public static class CancelOptionsParser
{
    private const string Usage =
        "Usage: baton cancel <room-dir> [--execution <execution-id>] [--bindings <bindings-file>] [--workflow-id <id>]";

    public static CancelOptions Parse(IReadOnlyList<string> args)
    {
        string? roomDirectoryPath = null;
        string? executionId = null;
        string? bindingsFilePath = null;
        string? workflowId = null;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--execution":
                    executionId = RequireValue(args, ref i, arg);
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

        var resolvedRoomDirectoryPath = RoomDirectoryPath.Resolve(roomDirectoryPath);
        bindingsFilePath ??= BatonPaths.RoomBindingsFile(resolvedRoomDirectoryPath);

        return new CancelOptions(resolvedRoomDirectoryPath, executionId, bindingsFilePath, workflowId);
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
