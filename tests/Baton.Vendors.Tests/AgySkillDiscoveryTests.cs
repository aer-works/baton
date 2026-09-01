using Baton.Vendors;

namespace Baton.Vendors.Tests;

public sealed class AgySkillDiscoveryTests
{
    [Fact]
    public async Task DiscoverCapabilities_WorkspaceArm_FindsSkills()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"agy-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkspace);
        try
        {
            var skillsDir = Path.Combine(tempWorkspace, ".agents", "skills", "agy-test-skill");
            Directory.CreateDirectory(skillsDir);
            File.WriteAllText(Path.Combine(skillsDir, "SKILL.md"), "description: Agy skill in workspace");

            var adapter = new AgyWorkerAdapter();
            var caps = await adapter.DiscoverCapabilitiesAsync(
                workingDirectory: tempWorkspace,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("agy", caps.Vendor);
            Assert.Contains(caps.Items, i => i.Name == "agy-test-skill" && i.Kind == "skill" && i.Description == "Agy skill in workspace");
            Assert.Contains(caps.Items, i => i.Name == "/compact" && i.Kind == "command");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempWorkspace);
        }
    }

    [Fact]
    public async Task DiscoverCapabilities_NullOrEmptyWorkspace_ReturnsStandardCapabilitiesWithoutSkills()
    {
        var adapter = new AgyWorkerAdapter();
        var caps = await adapter.DiscoverCapabilitiesAsync(
            workingDirectory: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("agy", caps.Vendor);
        Assert.DoesNotContain(caps.Items, i => i.Kind == "skill");
        Assert.Contains(caps.Items, i => i.Name == "/compact" && i.Kind == "command");
    }
}
