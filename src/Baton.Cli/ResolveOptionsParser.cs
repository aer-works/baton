namespace Baton.Cli;

/// <summary>
/// Parses <c>baton resolve</c>'s arguments: <c>baton resolve &lt;room-dir&gt;
/// [--execution &lt;execution-id&gt;] --accept-capture | --reject --reason &lt;text&gt;</c>. Never throws a
/// bare <see cref="InvalidOperationException"/> for a malformed invocation — every failure here is a
/// <see cref="CliArgumentException"/> (CLAUDE.md's error-handling rules), mirroring
/// <see cref="DecideOptionsParser"/>. Every validity rule beyond "is this a recognized flag" stays
/// <c>Mutation.MutationInterface.RecordCaptureResolutionAsync</c>'s (e.g. whether the named execution
/// actually has an unresolved capture) — this parser adds no vocabulary of its own beyond the
/// accept/reject grammar and the reject-requires-reason rule the ruling's own literal grammar states.
/// </summary>
public static class ResolveOptionsParser
{
    private const string Usage =
        "Usage: baton resolve <room-dir> [--execution <execution-id>] --accept-capture | --reject --reason <text>";

    public static ResolveOptions Parse(IReadOnlyList<string> args)
    {
        string? roomDirectoryPath = null;
        string? executionId = null;
        bool? accept = null;
        string? reason = null;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--execution":
                    executionId = RequireValue(args, ref i, arg);
                    break;
                case "--accept-capture":
                    if (accept == false)
                    {
                        throw new CliArgumentException(
                            $"Cannot pass both '--accept-capture' and '--reject'. {Usage}",
                            "pass exactly one of --accept-capture or --reject.");
                    }

                    accept = true;
                    i++;
                    break;
                case "--reject":
                    if (accept == true)
                    {
                        throw new CliArgumentException(
                            $"Cannot pass both '--accept-capture' and '--reject'. {Usage}",
                            "pass exactly one of --accept-capture or --reject.");
                    }

                    accept = false;
                    i++;
                    break;
                case "--reason":
                    reason = RequireValue(args, ref i, arg);
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

        if (accept is null)
        {
            throw new CliArgumentException(
                $"Missing required option: one of '--accept-capture' or '--reject'. {Usage}",
                "pass --accept-capture (the capture honestly satisfies its declared output(s)) or --reject --reason <text>.");
        }

        if (accept == false && string.IsNullOrWhiteSpace(reason))
        {
            throw new CliArgumentException(
                $"'--reject' requires '--reason <text>'. {Usage}",
                "pass --reason naming why the capture cannot honestly become the declared output(s).");
        }

        return new ResolveOptions(
            RoomDirectoryPath.Resolve(roomDirectoryPath),
            executionId,
            accept.Value,
            reason);
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
