namespace Baton.Cli.Tests;

/// <summary>
/// The one collection every test class in this assembly that mutates a process-global environment
/// variable belongs to. xUnit runs collections in parallel, but a <c>DisableParallelization</c>
/// collection never overlaps the parallel pool or any other such collection — so enrolling every
/// env-mutating class here guarantees no environment mutation is ever in flight while another test
/// reads it. This collection plus <c>SerializedEnvironmentTests</c> (the tripwire that fails the
/// build on an unenrolled <c>Environment.SetEnvironmentVariable</c>) is what closes it as a class,
/// #1491.
/// </summary>
/// <remarks>
/// #1524: <c>BatonPaths.Root</c> and the worker-role/workflow-template/Claude-config-root/
/// retention-sweep readers all resolve through <c>BatonEnvironmentSnapshot</c> now, so a test that
/// only overrides one of those no longer needs this collection — it isolates through
/// <c>BatonEnvironmentSnapshot.BeginScope</c> instead (see <c>SerializedEnvironmentTests</c>'s own
/// remarks). Four classes stay enrolled here anyway
/// (<see cref="Baton.Cli.Tests.DispatchCommandEndToEndTests"/> and its siblings
/// <c>DispatchAuditedWorktreeAcceptanceTests</c>, <c>DispatchTemplateEndToEndTests</c>,
/// <c>RedispatchCommandEndToEndTests</c>): each also swaps <c>Console.Out</c>/<c>Console.Error</c>,
/// another process-global mutable static, and this collection's cross-pool exclusivity is what
/// serializes that too — dropping the enrollment reintroduced
/// <c>ObjectDisposedException("Cannot write to a closed TextWriter")</c> races against unrelated
/// classes that swap the same console stream concurrently.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerializedEnvironmentCollection
{
    public const string Name = "serialized-environment";
}
