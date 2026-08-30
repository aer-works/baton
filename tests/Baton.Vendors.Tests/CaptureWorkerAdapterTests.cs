using Baton.Domain;
using Baton.Templates;

namespace Baton.Vendors.Tests;

/// <summary>
/// The engine-run capture worker (decision 0047 §4): it resolves to <c>git diff --output=&lt;artifact&gt;
/// &lt;base&gt;</c>, reading the base ref the run entrypoint injects into
/// <see cref="WorkerInvocation.PromptTemplate"/>. These pin the exact command — git spawned directly, the
/// output written through the <c>BATON_OUTPUT_DIR</c> placeholder <c>CoreDispatcher</c> expands, the base
/// diffed against the working tree — and the loud failure when no base was injected.
/// </summary>
public class CaptureWorkerAdapterTests
{
    private static readonly WorkerContract Contract =
        new("capture", [], [new ProducedOutput(WorkflowTemplateComposer.CaptureOutputName)], []);

    private static readonly CaptureWorkerAdapter Adapter = new();

    [Fact]
    public void Resolve_diffs_the_injected_base_ref_into_the_capture_output_file()
    {
        var target = Adapter.Resolve(
            new WorkerInvocation("a1b2c3d4", WorkingDirectory: "/work/tree"), Contract);

        Assert.Equal("git", target.Program);
        Assert.Equal("diff", target.Args[0]);

        // Output rides an --output= arg built from the BATON_OUTPUT_DIR placeholder and the fixed artifact
        // name (CaptureWorkerAdapter's doc covers why that placeholder rather than a shell redirect).
        Assert.StartsWith("--output=", target.Args[1]);
        Assert.EndsWith($"/{WorkflowTemplateComposer.CaptureOutputName}", target.Args[1]);

        // The bare base ref — diffed against the working tree (base, not base..HEAD), so committed and
        // uncommitted work both land in the diff.
        Assert.Equal("a1b2c3d4", target.Args[2]);
        Assert.Equal(3, target.Args.Count);

        Assert.Equal("/work/tree", target.WorkingDirectory);
    }

    [Fact]
    public void A_base_ref_with_surrounding_whitespace_is_trimmed()
    {
        // The injected SHA arrives via git rev-parse's stdout; a stray newline must not become an arg.
        var target = Adapter.Resolve(new WorkerInvocation("  a1b2c3d4\n"), Contract);

        Assert.Equal("a1b2c3d4", target.Args[2]);
    }

    [Fact]
    public void A_missing_base_ref_throws_rather_than_diffing_nothing()
    {
        // Empty PromptTemplate means the entrypoint did not inject a base (non-git workspace, or the
        // injection was skipped). Diffing with no base is a silent wrong answer; this makes it loud.
        var ex = Assert.Throws<InvalidOperationException>(
            () => Adapter.Resolve(new WorkerInvocation("   "), Contract));
        Assert.Contains("base ref", ex.Message);
    }
}
