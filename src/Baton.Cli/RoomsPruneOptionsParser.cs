using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// Parses <c>baton rooms prune</c>'s arguments: <c>baton rooms prune --terminal [--older-than &lt;days&gt;]
/// [--state Succeeded|Failed|Cancelled] [--dry-run] [--yes]</c>. Follows <see cref="RoomDeleteOptionsParser"/>'s
/// own error-handling contract — see its remarks.
/// </summary>
public static class RoomsPruneOptionsParser
{
    public const string Usage =
        "Usage: baton rooms prune --terminal [--older-than <days>] [--state Succeeded|Failed|Cancelled] [--dry-run] [--yes]";

    private static readonly IReadOnlyList<string> AllowedStates =
        [WorkflowOutcome.Succeeded, WorkflowOutcome.Failed, WorkflowOutcome.Cancelled];

    public static RoomsPruneOptions Parse(IReadOnlyList<string> args)
    {
        var terminal = false;
        int? olderThanDays = null;
        string? state = null;
        var dryRun = false;
        var yes = false;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--terminal":
                    terminal = true;
                    i++;
                    continue;
                case "--dry-run":
                    dryRun = true;
                    i++;
                    continue;
                case "--yes":
                    yes = true;
                    i++;
                    continue;
                case "--older-than":
                    olderThanDays = ParseOlderThan(RequireValue(args, ref i, "--older-than"));
                    i++;
                    continue;
                case "--state":
                    state = ParseState(RequireValue(args, ref i, "--state"));
                    i++;
                    continue;
            }

            throw new CliArgumentException($"Unknown option '{arg}'. {Usage}");
        }

        if (!terminal)
        {
            throw new CliArgumentException($"Missing required --terminal flag. {Usage}");
        }

        return new RoomsPruneOptions(terminal, olderThanDays, state, dryRun, yes);
    }

    private static int ParseOlderThan(string rawValue)
    {
        if (!int.TryParse(rawValue, out var days) || days <= 0)
        {
            throw new CliArgumentException(
                $"'--older-than {rawValue}' is not a positive whole number of days. {Usage}",
                "pass a positive integer, e.g. --older-than 7.");
        }

        return days;
    }

    private static string ParseState(string rawValue)
    {
        var match = AllowedStates.FirstOrDefault(s => string.Equals(s, rawValue, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new CliArgumentException(
                $"'--state {rawValue}' is not one of {string.Join("|", AllowedStates)}. {Usage}",
                $"pass one of --state {string.Join(", --state ", AllowedStates)}.");
        }

        return match;
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string optionName)
    {
        if (index + 1 >= args.Count)
        {
            throw new CliArgumentException($"'{optionName}' requires a value. {Usage}");
        }

        index++;
        return args[index];
    }
}
