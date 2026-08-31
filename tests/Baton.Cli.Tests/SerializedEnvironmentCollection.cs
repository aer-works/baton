namespace Baton.Cli.Tests;

/// <summary>
/// The one collection every test class in this assembly that mutates a process-global environment
/// variable belongs to. xUnit runs collections in parallel, but a <c>DisableParallelization</c>
/// collection never overlaps the parallel pool or any other such collection — so enrolling every
/// env-mutating class here guarantees no environment mutation is ever in flight while another test
/// reads it. <c>BatonPaths.Root</c> and the worker-role / workflow-template catalog resolvers
/// re-read their env var on every access (they deliberately never cache), so an unenrolled mutator
/// racing any of those readers is the #1480 flake family — this collection plus
/// <c>SerializedEnvironmentTests</c> (the tripwire that fails the build on an unenrolled
/// <c>Environment.SetEnvironmentVariable</c>) is what closes it as a class, #1491.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerializedEnvironmentCollection
{
    public const string Name = "serialized-environment";
}
