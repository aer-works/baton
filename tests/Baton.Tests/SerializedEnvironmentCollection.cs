namespace Baton.Tests;

/// <summary>
/// The one collection every test class in this assembly that mutates a process-global environment
/// variable belongs to. xUnit runs collections in parallel, but a <c>DisableParallelization</c>
/// collection never overlaps the parallel pool or any other such collection — so enrolling every
/// env-mutating class here guarantees no environment mutation is ever in flight while another test
/// reads it (e.g. a <c>PATH</c> mutation in <c>SpawnResolutionTests</c> racing a spawn that resolves
/// a program off <c>PATH</c>). The build-time guard is <c>SerializedEnvironmentTests</c> in
/// Baton.Architecture.Tests, which fails on any <c>Environment.SetEnvironmentVariable</c> outside a
/// class enrolled here, #1491.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerializedEnvironmentCollection
{
    public const string Name = "serialized-environment";
}
