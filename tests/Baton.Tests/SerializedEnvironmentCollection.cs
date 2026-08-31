namespace Baton.Tests;

/// <summary>
/// Baton.Tests' copy of <c>Baton.Cli.Tests.SerializedEnvironmentCollection</c> — see that type for the
/// full rationale (#1491). Concretely here: <c>SpawnResolutionTests</c>' <c>PATH</c> swap can no
/// longer land mid-flight under a sibling class that spawns a child expecting an unmodified
/// <c>PATH</c>, because both now sit in the one group xUnit never runs alongside anything else.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerializedEnvironmentCollection
{
    public const string Name = "serialized-environment";
}
