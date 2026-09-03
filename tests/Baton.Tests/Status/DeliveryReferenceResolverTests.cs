using Baton.Status;

namespace Baton.Tests.Status;

/// <summary>
/// #734: the "a workflow step declares a delivery reference as a produced output" half. A step's
/// <c>WorkflowStepDefinition.Outputs</c> resolves to a list of file paths the same way
/// <see cref="StepOutputResolver"/> already resolves every other declared output; this is what reads
/// the two well-known names (<see cref="DeliveryReferenceOutputNames"/>) back off that resolved list.
/// </summary>
public sealed class DeliveryReferenceResolverTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"baton-delivery-ref-test-{Guid.NewGuid():N}");

    public DeliveryReferenceResolverTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            DirectoryCleanup.DeleteRecursively(_tempDir);
        }
    }

    private string Write(string fileName, string content)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_step_declaring_both_keys_resolves_both()
    {
        var outputs = new[]
        {
            Write(DeliveryReferenceOutputNames.Branch, "734-lane"),
            Write(DeliveryReferenceOutputNames.PullRequest, "1799"),
        };

        var reference = DeliveryReferenceResolver.Resolve(outputs);

        Assert.NotNull(reference);
        Assert.Equal(1799, reference!.PullRequestNumber);
        Assert.Equal("734-lane", reference.Branch);
    }

    [Fact]
    public void A_step_declaring_only_the_pr_key_resolves_a_null_branch()
    {
        var outputs = new[] { Write(DeliveryReferenceOutputNames.PullRequest, "42") };

        var reference = DeliveryReferenceResolver.Resolve(outputs);

        Assert.NotNull(reference);
        Assert.Equal(42, reference!.PullRequestNumber);
        Assert.Null(reference.Branch);
    }

    [Fact]
    public void A_step_declaring_only_the_branch_key_resolves_a_null_pr_number()
    {
        var outputs = new[] { Write(DeliveryReferenceOutputNames.Branch, "wip-branch") };

        var reference = DeliveryReferenceResolver.Resolve(outputs);

        Assert.NotNull(reference);
        Assert.Null(reference!.PullRequestNumber);
        Assert.Equal("wip-branch", reference.Branch);
    }

    /// <summary>
    /// The control case: without either declared key, nothing resolves -- this is what makes
    /// <c>Baton.Cli.Daemon.DeliveryPoller</c> never start polling for a room whose step declared no
    /// delivery output.
    /// </summary>
    [Fact]
    public void A_step_declaring_neither_key_resolves_nothing()
    {
        var outputs = new[] { Write("plan.md", "unrelated output") };

        Assert.Null(DeliveryReferenceResolver.Resolve(outputs));
    }

    [Fact]
    public void No_outputs_at_all_resolves_nothing()
    {
        Assert.Null(DeliveryReferenceResolver.Resolve([]));
        Assert.Null(DeliveryReferenceResolver.Resolve(null));
    }

    [Fact]
    public void A_non_numeric_pr_file_resolves_a_null_pr_number_rather_than_throwing()
    {
        var outputs = new[] { Write(DeliveryReferenceOutputNames.PullRequest, "not-a-number") };

        Assert.Null(DeliveryReferenceResolver.Resolve(outputs));
    }
}
