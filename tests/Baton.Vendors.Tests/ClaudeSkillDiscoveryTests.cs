using Baton.Vendors;

namespace Baton.Vendors.Tests;

public sealed class ClaudeSkillDiscoveryTests
{
    [Fact]
    public async Task DiscoverCapabilities_ProjectArm_FindsSkillsAndCommands()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"claude-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkspace);
        try
        {
            var skillsDir = Path.Combine(tempWorkspace, ".claude", "skills", "project-skill");
            Directory.CreateDirectory(skillsDir);
            File.WriteAllText(Path.Combine(skillsDir, "SKILL.md"), "description: Project skill description");

            var commandsDir = Path.Combine(tempWorkspace, ".claude", "commands");
            Directory.CreateDirectory(commandsDir);
            File.WriteAllText(Path.Combine(commandsDir, "project-cmd.md"), "# Command");

            var adapter = new ClaudeWorkerAdapter();
            var emptyUserHome = Path.Combine(Path.GetTempPath(), $"claude-empty-user-{Guid.NewGuid():N}");
            Directory.CreateDirectory(emptyUserHome);
            try
            {
                var caps = await adapter.DiscoverCapabilitiesAsync(
                    workingDirectory: tempWorkspace,
                    userHomeDirectory: emptyUserHome,
                    cancellationToken: TestContext.Current.CancellationToken);

                Assert.Equal("claude", caps.Vendor);
                Assert.Contains(caps.Items, i => i.Name == "project-skill" && i.Kind == "skill" && i.Description == "Project skill description");
                Assert.Contains(caps.Items, i => i.Name == "/project-cmd" && i.Kind == "command");
            }
            finally
            {
                DirectoryCleanup.DeleteRecursively(emptyUserHome);
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempWorkspace);
        }
    }

    [Fact]
    public async Task DiscoverCapabilities_UserArm_FindsSkillsAndCommands()
    {
        // Tests the user arm fix for issue #1512 (the #1151 proposal's doubled-path finding):
        // Probing ~/.claude/skills and ~/.claude/commands (NOT ~/.claude/.claude/skills).
        var tempUserHome = Path.Combine(Path.GetTempPath(), $"claude-user-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempUserHome);
        try
        {
            var userSkillsDir = Path.Combine(tempUserHome, ".claude", "skills", "user-skill");
            Directory.CreateDirectory(userSkillsDir);
            File.WriteAllText(Path.Combine(userSkillsDir, "SKILL.md"), "description: User personal skill");

            var userCommandsDir = Path.Combine(tempUserHome, ".claude", "commands");
            Directory.CreateDirectory(userCommandsDir);
            File.WriteAllText(Path.Combine(userCommandsDir, "user-cmd.md"), "# User command");

            var adapter = new ClaudeWorkerAdapter();
            var caps = await adapter.DiscoverCapabilitiesAsync(
                workingDirectory: null,
                userHomeDirectory: tempUserHome,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("claude", caps.Vendor);
            Assert.Contains(caps.Items, i => i.Name == "user-skill" && i.Kind == "skill" && i.Description == "User personal skill");
            Assert.Contains(caps.Items, i => i.Name == "/user-cmd" && i.Kind == "command");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempUserHome);
        }
    }

    [Fact]
    public async Task DiscoverCapabilities_BothArms_ProjectTakesPrecedenceOverUserArm()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"claude-workspace-{Guid.NewGuid():N}");
        var tempUserHome = Path.Combine(Path.GetTempPath(), $"claude-user-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkspace);
        Directory.CreateDirectory(tempUserHome);
        try
        {
            // Project arm skills
            var projSharedSkill = Path.Combine(tempWorkspace, ".claude", "skills", "shared-skill");
            Directory.CreateDirectory(projSharedSkill);
            File.WriteAllText(Path.Combine(projSharedSkill, "SKILL.md"), "description: Project version of shared skill");

            var projOnlySkill = Path.Combine(tempWorkspace, ".claude", "skills", "proj-only-skill");
            Directory.CreateDirectory(projOnlySkill);
            File.WriteAllText(Path.Combine(projOnlySkill, "SKILL.md"), "description: Project only skill");

            // User arm skills
            var userSharedSkill = Path.Combine(tempUserHome, ".claude", "skills", "shared-skill");
            Directory.CreateDirectory(userSharedSkill);
            File.WriteAllText(Path.Combine(userSharedSkill, "SKILL.md"), "description: User version of shared skill");

            var userOnlySkill = Path.Combine(tempUserHome, ".claude", "skills", "user-only-skill");
            Directory.CreateDirectory(userOnlySkill);
            File.WriteAllText(Path.Combine(userOnlySkill, "SKILL.md"), "description: User only skill");

            var adapter = new ClaudeWorkerAdapter();
            var caps = await adapter.DiscoverCapabilitiesAsync(
                workingDirectory: tempWorkspace,
                userHomeDirectory: tempUserHome,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("claude", caps.Vendor);

            // Project version takes precedence for shared skill
            var sharedItem = Assert.Single(caps.Items, i => i.Name == "shared-skill" && i.Kind == "skill");
            Assert.Equal("Project version of shared skill", sharedItem.Description);

            // Both project-only and user-only skills are present
            Assert.Contains(caps.Items, i => i.Name == "proj-only-skill" && i.Kind == "skill");
            Assert.Contains(caps.Items, i => i.Name == "user-only-skill" && i.Kind == "skill");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempWorkspace);
            DirectoryCleanup.DeleteRecursively(tempUserHome);
        }
    }
}
