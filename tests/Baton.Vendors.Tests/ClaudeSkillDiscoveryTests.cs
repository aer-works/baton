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
                    configRootDirectory: string.Empty,
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
                configRootDirectory: string.Empty,
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
                configRootDirectory: string.Empty,
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

    [Fact]
    public async Task DiscoverCapabilities_ConfigRoot_TakesPrecedenceOverUserHomeAndSkipsTheDotClaudeSegment()
    {
        // #1512 M3: BATON_CLAUDE_CONFIG_ROOT replaces ~/.claude wholesale -- the personal skills
        // directory under a redirected root is "<configRoot>/skills", NOT "<configRoot>/.claude/skills"
        // the way the plain user-home arm composes it. This also proves the config root is preferred
        // over userHomeDirectory when both are supplied.
        var tempUserHome = Path.Combine(Path.GetTempPath(), $"claude-user-{Guid.NewGuid():N}");
        var tempConfigRoot = Path.Combine(Path.GetTempPath(), $"claude-config-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempUserHome);
        Directory.CreateDirectory(tempConfigRoot);
        try
        {
            // A user-home skill that must NOT surface -- the config root takes precedence.
            var userSkillDir = Path.Combine(tempUserHome, ".claude", "skills", "user-home-skill");
            Directory.CreateDirectory(userSkillDir);
            File.WriteAllText(Path.Combine(userSkillDir, "SKILL.md"), "description: Should not surface");

            // The config-root skill, composed WITHOUT a .claude segment.
            var configRootSkillDir = Path.Combine(tempConfigRoot, "skills", "shared-root-skill");
            Directory.CreateDirectory(configRootSkillDir);
            File.WriteAllText(Path.Combine(configRootSkillDir, "SKILL.md"), "description: Shared config root skill");

            var adapter = new ClaudeWorkerAdapter();
            var caps = await adapter.DiscoverCapabilitiesAsync(
                workingDirectory: null,
                userHomeDirectory: tempUserHome,
                configRootDirectory: tempConfigRoot,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains(caps.Items, i => i.Name == "shared-root-skill" && i.Kind == "skill" && i.Description == "Shared config root skill");
            Assert.DoesNotContain(caps.Items, i => i.Name == "user-home-skill");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempUserHome);
            DirectoryCleanup.DeleteRecursively(tempConfigRoot);
        }
    }

    [Fact]
    public async Task DiscoverCapabilities_ConfigRoot_ShadowsAProjectSkillOfTheSameName()
    {
        // #1575: measured 2026-09-03 against the installed CLI -- with no `--setting-sources` flag
        // (which ClaudeWorkerAdapter never passes), a name collision between a config-root skill and
        // a project skill resolves to the config-root copy, the opposite of the project-over-user
        // precedence the plain ~/.claude fallback keeps (see the sibling test above). The roster must
        // name the same copy the CLI actually loads.
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"claude-workspace-{Guid.NewGuid():N}");
        var tempConfigRoot = Path.Combine(Path.GetTempPath(), $"claude-config-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkspace);
        Directory.CreateDirectory(tempConfigRoot);
        try
        {
            var projSharedSkill = Path.Combine(tempWorkspace, ".claude", "skills", "shared-skill");
            Directory.CreateDirectory(projSharedSkill);
            File.WriteAllText(Path.Combine(projSharedSkill, "SKILL.md"), "description: Project version of shared skill");

            var rootSharedSkill = Path.Combine(tempConfigRoot, "skills", "shared-skill");
            Directory.CreateDirectory(rootSharedSkill);
            File.WriteAllText(Path.Combine(rootSharedSkill, "SKILL.md"), "description: Config root version of shared skill");

            var adapter = new ClaudeWorkerAdapter();
            var caps = await adapter.DiscoverCapabilitiesAsync(
                workingDirectory: tempWorkspace,
                userHomeDirectory: string.Empty,
                configRootDirectory: tempConfigRoot,
                cancellationToken: TestContext.Current.CancellationToken);

            var sharedItem = Assert.Single(caps.Items, i => i.Name == "shared-skill" && i.Kind == "skill");
            Assert.Equal("Config root version of shared skill", sharedItem.Description);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempWorkspace);
            DirectoryCleanup.DeleteRecursively(tempConfigRoot);
        }
    }

    [Fact]
    public async Task DiscoverCapabilities_HonoursAnAlreadyCancelledToken()
    {
        // #1512 M7: DiscoverCapabilitiesAsync used to accept a CancellationToken and never consult it
        // at all. This does not prove the unbounded-hang scenario (a genuinely stuck UNC read cannot
        // be simulated reliably in a unit test) but it does prove the token is no longer ignored: a
        // caller that has already given up gets cancellation back, not a silently-completed scan.
        var adapter = new ClaudeWorkerAdapter();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            adapter.DiscoverCapabilitiesAsync(
                workingDirectory: null,
                userHomeDirectory: Path.GetTempPath(),
                configRootDirectory: string.Empty,
                cancellationToken: cts.Token));
    }
}
