namespace Baton.Cli.Tests;

/// <summary>
/// #649: the check that decides whether a withheld write is nonetheless allowed, because it lands in
/// the worker's own outbox rather than the workspace. A bug here is a permission hole, not a defect.
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public class OutboxPathTests
{
    private static string Outbox => Path.Combine(Path.GetTempPath(), "baton-task", "artifacts", "execution_1");

    [Fact]
    public void A_file_written_directly_into_the_outbox_is_inside_it()
    {
        // The control everything else is read against. If this fails the rest proves nothing — a
        // check that denies universally would pass every negative case below.
        Assert.True(OutboxPath.IsInside(Path.Combine(Outbox, "review.md"), Outbox));
    }

    [Fact]
    public void A_file_in_a_subdirectory_of_the_outbox_is_inside_it()
    {
        Assert.True(OutboxPath.IsInside(Path.Combine(Outbox, "nested", "review.md"), Outbox));
    }

    [Fact]
    public void A_traversal_out_of_the_outbox_is_not_inside_it()
    {
        // The case this class exists for. Prefix matching on the unresolved string would allow it,
        // and the write would land in the repo the grant was withholding.
        var escaped = Path.Combine(Outbox, "..", "..", "..", "repo", "src", "Program.cs");

        Assert.False(OutboxPath.IsInside(escaped, Outbox));
    }

    [Fact]
    public void A_sibling_directory_sharing_the_outboxs_name_as_a_prefix_is_not_inside_it()
    {
        // `execution_1-evil` beside `execution_1`. The trailing-separator comparison is what catches
        // this; without it a worker could write anywhere it could name.
        Assert.False(OutboxPath.IsInside(Outbox + "-evil" + Path.DirectorySeparatorChar + "x.md", Outbox));
    }

    [Fact]
    public void The_outbox_directory_itself_is_not_a_file_inside_it()
    {
        Assert.False(OutboxPath.IsInside(Outbox, Outbox));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unanswerable_question_denies_rather_than_allows(string? missing)
    {
        // Fails closed in both directions: no outbox to compare against, and nothing to compare.
        Assert.False(OutboxPath.IsInside(Path.Combine(Outbox, "review.md"), missing));
        Assert.False(OutboxPath.IsInside(missing, Outbox));
    }

    [Fact]
    public void A_relative_candidate_is_resolved_before_comparison_rather_than_assumed_outside()
    {
        // A worker's cwd is its working directory, so a bare `review.md` is a workspace write and must
        // be denied — but it must be denied because it *resolves* outside, not because it looks odd.
        //
        // The first assertion alone cannot tell those apart: an implementation that refused every
        // non-rooted candidate on sight satisfies it identically. The discriminating arm is the
        // opposite polarity — the same bare name, with the process cwd set to the outbox, which
        // resolves *inside* and must be allowed.
        var outbox = Directory.CreateTempSubdirectory("baton-outbox-relcwd-").FullName;
        var priorCwd = Directory.GetCurrentDirectory();
        try
        {
            Assert.False(OutboxPath.IsInside("review.md", outbox));

            Directory.SetCurrentDirectory(outbox);
            Assert.True(OutboxPath.IsInside("review.md", outbox));
        }
        finally
        {
            Directory.SetCurrentDirectory(priorCwd);
            DirectoryCleanup.DeleteRecursively(outbox);
        }
    }
}
