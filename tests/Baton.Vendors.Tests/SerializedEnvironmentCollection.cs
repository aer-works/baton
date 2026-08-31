namespace Baton.Vendors.Tests;

/// <summary>
/// This assembly's own enrollment point for the mechanism <c>Baton.Cli.Tests.SerializedEnvironmentCollection</c>
/// documents in full (#1491) — every class here that flips a process-global environment variable
/// belongs in this one <c>DisableParallelization</c> group instead of the default parallel pool. The
/// concrete payoff in this assembly: the catalog lookups behind worker roles and workflow templates,
/// plus the Claude config-root path, all resolve their variable fresh on each call rather than
/// caching it, so a mutator left out of this group can bleed into any of them mid-run.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerializedEnvironmentCollection
{
    public const string Name = "serialized-environment";
}
