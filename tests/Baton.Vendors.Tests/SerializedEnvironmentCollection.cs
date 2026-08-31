namespace Baton.Vendors.Tests;

/// <summary>
/// The one collection every test class in this assembly that mutates a process-global environment
/// variable belongs to. xUnit runs collections in parallel, but a <c>DisableParallelization</c>
/// collection never overlaps the parallel pool or any other such collection — so enrolling every
/// env-mutating class here guarantees no environment mutation is ever in flight while another test
/// reads it. The worker-role / workflow-template catalog resolvers and the claude config-root path
/// re-read their env var on every access, so an unenrolled mutator racing a reader is the #1480
/// flake family. The build-time guard is <c>SerializedEnvironmentTests</c> in
/// Baton.Architecture.Tests, which fails on any <c>Environment.SetEnvironmentVariable</c> outside a
/// class enrolled here, #1491.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerializedEnvironmentCollection
{
    public const string Name = "serialized-environment";
}
