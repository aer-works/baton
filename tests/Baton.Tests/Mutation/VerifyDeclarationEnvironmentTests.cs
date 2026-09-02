using Baton.Mutation;
using Baton.Tests.TestSupport;
using Xunit;

namespace Baton.Tests.Mutation;

/// <summary>
/// #1708 L3: <c>git</c>'s stdout decides WHAT COMMAND grades a run, so the spawn that produces it must
/// not be steerable from outside — see <c>VerifyCommandResolver</c>'s own hardened-spawn doc for the
/// full list of what is scrubbed and why.
/// <para>
/// Enrolled in <see cref="SerializedEnvironmentCollection"/> (#1491) because it mutates process-wide
/// state: an ambient <c>GIT_DIR</c> live for even a few milliseconds would otherwise be inherited by
/// every other class's concurrent <c>git init</c>/<c>git commit</c>.
/// </para>
/// </summary>
[Collection(SerializedEnvironmentCollection.Name)]
public sealed class VerifyDeclarationEnvironmentTests
{
    /// <summary>
    /// Red-first, with its own control arm. A <c>GIT_DIR</c> pointing at a DIFFERENT repository — one
    /// whose reviewed base declares <c>exit 0</c> — is the cheapest positive control for the scrub: the
    /// control assertion first proves that an UNSCRUBBED spawn really is redirected (otherwise the test
    /// asserts nothing, because git might simply ignore the variable), then the real read is asserted to
    /// return the dispatched workspace's own line regardless.
    /// </summary>
    [Fact]
    public async Task ReadCommittedRepoDeclarationAsync_is_not_redirected_by_an_ambient_GIT_DIR()
    {
        var workspace = VerifyDeclarationWorkspace.CreateTemp();
        var decoy = VerifyDeclarationWorkspace.CreateTemp();
        var originalGitDir = Environment.GetEnvironmentVariable("GIT_DIR");
        try
        {
            VerifyDeclarationWorkspace.WriteDeclaration(workspace, "python -c \"import sys; sys.exit(1)\"");
            TempGitRepository.InitWithEverythingCommitted(workspace);
            TempGitRepository.SetReviewedBaselineAtHead(workspace);

            VerifyDeclarationWorkspace.WriteDeclaration(decoy, "exit 0");
            TempGitRepository.InitWithEverythingCommitted(decoy);
            TempGitRepository.SetReviewedBaselineAtHead(decoy);

            Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(decoy, ".git"));

            // Control: an ordinary, environment-inheriting `git show` run from the dispatched workspace
            // DOES come back with the decoy's line. Without this the assertion below could pass on a
            // host where GIT_DIR happened to do nothing.
            Assert.Equal("exit 0", VerifyDeclarationWorkspace.ShowAtHead(workspace));

            var committed = await VerifyCommandResolver.ReadCommittedRepoDeclarationAsync(
                workspace, TestContext.Current.CancellationToken);

            Assert.Equal("python -c \"import sys; sys.exit(1)\"", committed.CommandLine);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_DIR", originalGitDir);
            DirectoryCleanup.DeleteRecursively(workspace);
            DirectoryCleanup.DeleteRecursively(decoy);
        }
    }
}
