namespace Baton.Tests;

/// <summary>
/// This assembly's own enrollment point for the mechanism <c>Baton.Cli.Tests.SerializedEnvironmentCollection</c>
/// documents in full (#1491) — every class here that flips a process-global environment variable
/// belongs in this one <c>DisableParallelization</c> group instead of the default parallel pool. The
/// concrete payoff in this assembly: <c>SpawnResolutionTests</c>' <c>PATH</c> swap can no longer land
/// mid-flight under a sibling class that spawns a child expecting an unmodified <c>PATH</c>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerializedEnvironmentCollection
{
    public const string Name = "serialized-environment";
}
