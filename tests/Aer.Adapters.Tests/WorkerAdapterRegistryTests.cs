using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Templates;

namespace Aer.Adapters.Tests;

/// <summary>
/// #651: a <see cref="PermissionGrant"/> only constrains a worker whose adapter reads it. Several
/// registered adapters never do — <see cref="NoOpWorkerAdapter"/> writes its output through a
/// dispatch target AER constructs itself, for one — so a grant attached to one of their bindings is inert.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IPermissionGrantTranslator"/> is how the rest of the product identifies that
/// population — see its own docs for what the bindings editor actually keys on it, which is a warning
/// and a Save guard rather than the builder. A bind-time refusal that reads a grant is meaningful for
/// exactly the same set. The correlation
/// is exact today and stated nowhere, so nothing stops a fifth adapter from breaking it in either
/// direction — reading a grant without declaring the interface, or declaring it without reading one.
/// </para>
/// <para>
/// This checks behaviour rather than the declaration, which is the point: an adapter cannot satisfy
/// it by implementing the interface and ignoring the grant. Note what the interface does and does
/// not mean. It is named for <see cref="IPermissionGrantTranslator.TryTranslatePermissionGrant"/>,
/// which builds the vendor's <em>allow</em> value — measured to be pre-approval rather than a
/// ceiling (<c>gate.allowedtools-is-preapproval-not-ceiling</c>). So the property established here
/// is "this adapter's dispatch depends on the grant", not "this adapter enforces denial".
/// </para>
/// </remarks>
[Collection(LaunchConfigCollection.Name)]
public class WorkerAdapterRegistryTests
{
    private static readonly WorkerContract Contract =
        new("worker", [], [new ProducedOutput("out")], []);

    /// <summary>
    /// Both arms are expressible by every adapter on purpose. <c>agy</c> refuses shell without
    /// network and network without shell, so a pair that moved only one of them would throw
    /// <see cref="PermissionGrantUnsupportedException"/> and turn this into a test about that
    /// refusal instead of about whether the grant is read at all.
    /// </summary>
    private static readonly PermissionGrant Granted = new(
        ReadFiles: true, WriteFiles: true, RunShellCommands: true, ShellCommandPatterns: [], NetworkAccess: true);

    private static readonly PermissionGrant Withheld = new(
        ReadFiles: false, WriteFiles: false, RunShellCommands: false, ShellCommandPatterns: [], NetworkAccess: false);

    /// <summary>
    /// Everything about a dispatch a grant could reach. Both channels are compared because a grant
    /// can arrive on either: Claude's denials ride <c>--disallowedTools</c> in
    /// <see cref="CoreDispatchTarget.Args"/>, agy's denied-tool list rides
    /// <see cref="CoreDispatchTarget.Environment"/>.
    /// <para>
    /// Measured, because the obvious claim is wrong: today the <see cref="CoreDispatchTarget.Args"/>
    /// arm alone discriminates for <em>both</em> adapters, since agy's permission mode also lands
    /// there. Deleting the <see cref="CoreDispatchTarget.Environment"/> arm leaves all three tests in
    /// this class green. It stays because an adapter carrying its grant only in the environment is a
    /// shape this is meant to catch, not because the current two need it.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> GrantReachableSurface(CoreDispatchTarget target) =>
    [
        target.Program,
        .. target.Args,
        .. (target.Environment ?? []).Select(pair => $"{pair.Name}={pair.Value}"),
    ];

    private static bool DispatchDependsOnTheGrant(IWorkerAdapter adapter)
    {
        var granted = GrantReachableSurface(
            adapter.Resolve(new WorkerInvocation("prompt", PermissionGrant: Granted), Contract));
        var withheld = GrantReachableSurface(
            adapter.Resolve(new WorkerInvocation("prompt", PermissionGrant: Withheld), Contract));

        return !granted.SequenceEqual(withheld, StringComparer.Ordinal);
    }

    [Fact]
    public void An_adapter_declares_the_translator_interface_exactly_when_its_dispatch_reads_the_grant()
    {
        foreach (var (name, adapter) in WorkerAdapterRegistry.Default)
        {
            var declares = adapter is IPermissionGrantTranslator;
            var reads = DispatchDependsOnTheGrant(adapter);

            Assert.True(
                declares == reads,
                declares
                    ? $"Adapter '{name}' declares IPermissionGrantTranslator, but its dispatch is identical " +
                      "under a full grant and a fully withheld one — so the grant constrains nothing and " +
                      "every surface that reads the interface as 'this grant is honoured' is wrong about it."
                    : $"Adapter '{name}' does not declare IPermissionGrantTranslator, but its dispatch does " +
                      "change with the grant. The bindings editor will not offer it the checkbox builder, and " +
                      "any rule scoped to the interface will skip an adapter whose grant is load-bearing.");
        }
    }

    [Fact]
    public void The_registry_holds_adapters_of_both_kinds()
    {
        // The control. The check above is satisfied vacuously by a registry that is empty, or whose
        // adapters all fall on one side — either of which would leave it passing while testing nothing.
        var adapters = WorkerAdapterRegistry.Default.Values;

        Assert.Contains(adapters, adapter => adapter is IPermissionGrantTranslator);
        Assert.Contains(adapters, adapter => adapter is not IPermissionGrantTranslator);
    }

    [Fact]
    public void The_capture_capability_resolves_to_the_engine_run_capture_adapter()
    {
        // The composer keys a diff-of-work-so-far step's binding on this capability name; template
        // dispatch is only runnable if the production registry resolves it to the git-diffing adapter.
        Assert.True(WorkerAdapterRegistry.Default.TryGetValue(
            WorkflowTemplateComposer.CaptureAdapter, out var adapter));
        Assert.IsType<CaptureWorkerAdapter>(adapter);
    }

    [Fact]
    public void The_surface_comparison_notices_a_grant_that_is_read()
    {
        // The second control, and the one that keeps the first test honest. The check above compares
        // an adapter against itself, so an instrument that had gone blind — GrantReachableSurface
        // narrowed until nothing varies — would make every adapter look grant-independent, and the
        // two vendor adapters would then be reported as ignoring their grants. Asserting the positive
        // directly means that arrives as "the instrument stopped working" rather than as a claim
        // about the product.
        Assert.True(DispatchDependsOnTheGrant(new ClaudeWorkerAdapter()), "Claude's dispatch must vary with the grant.");
        Assert.True(DispatchDependsOnTheGrant(new AgyWorkerAdapter()), "agy's dispatch must vary with the grant.");
    }
}
