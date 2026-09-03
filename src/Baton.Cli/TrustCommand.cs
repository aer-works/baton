using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// <c>baton trust</c> (#1166): decision 0004's project ceiling has no interactive first-use trust
/// prompt in a headless dispatch, so this is the explicit operator verb the scope ruling calls for —
/// list/register/revoke against <see cref="ProjectCeilingStore"/>. Not a
/// <see cref="CommandResult"/>/<see cref="FlowStateReporter"/> command (no workflow pump, no projected
/// state to report): joins <c>watch</c>/<c>keep</c>/<c>unkeep</c> in <c>Program.cs</c>'s own carve-out
/// for exactly that shape.
/// </summary>
public static class TrustCommand
{
    public static Task<int> ExecuteAsync(TrustOptions options, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();

        return options.Mode switch
        {
            TrustMode.List => Task.FromResult(List(output)),
            TrustMode.Register => Task.FromResult(Register(options, output)),
            TrustMode.Revoke => Task.FromResult(Revoke(options, output)),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
    }

    private static int List(TextWriter output)
    {
        var ceilings = ProjectCeilingStore.Load(ProjectCeilingStore.DefaultPath);
        if (ceilings.Count == 0)
        {
            output.WriteLine("No project ceilings recorded.");
            return 0;
        }

        foreach (var (path, ceiling) in ceilings.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            output.WriteLine($"{path}  {Describe(ceiling)}");
        }

        return 0;
    }

    private static int Register(TrustOptions options, TextWriter output)
    {
        ProjectCeilingStore.Set(options.ProjectPath!, options.Ceiling!, ProjectCeilingStore.DefaultPath);
        output.WriteLine($"Trusted '{options.ProjectPath}' with ceiling {Describe(options.Ceiling!)}.");
        return 0;
    }

    private static int Revoke(TrustOptions options, TextWriter output)
    {
        var revoked = ProjectCeilingStore.Revoke(options.ProjectPath!, ProjectCeilingStore.DefaultPath);
        output.WriteLine(revoked
            ? $"Revoked the ceiling for '{options.ProjectPath}'."
            : $"No ceiling was recorded for '{options.ProjectPath}' — nothing to revoke.");
        return 0;
    }

    private static string Describe(ProjectCeiling ceiling)
    {
        if (ceiling.IsUnrestricted)
        {
            return "all";
        }

        List<string> categories = [];
        if (ceiling.ReadFiles)
        {
            categories.Add("ReadFiles");
        }

        if (ceiling.WriteFiles)
        {
            categories.Add("WriteFiles");
        }

        if (ceiling.RunShellCommands)
        {
            categories.Add("RunShellCommands");
        }

        if (ceiling.NetworkAccess)
        {
            categories.Add("NetworkAccess");
        }

        return categories.Count == 0 ? "none" : string.Join(',', categories);
    }
}
