using Baton.Domain;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// Unit tests for tool SHA resolution and stamping (#1668).
/// </summary>
public sealed class ToolShaBindingTests : IDisposable
{
    private readonly string _tempBatonHome = Path.Combine(Path.GetTempPath(), $"baton-paths-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempBatonHome))
        {
            DirectoryCleanup.DeleteRecursively(_tempBatonHome);
        }
    }

    [Fact]
    public void BatonPaths_tools_and_current_pointer_file_resolve_under_root()
    {
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = _tempBatonHome });

        Assert.Equal(Path.Combine(_tempBatonHome, "tools"), BatonPaths.Tools);
        Assert.Equal(Path.Combine(_tempBatonHome, "tools", "current"), BatonPaths.CurrentToolPointerFile);
    }

    [Fact]
    public void TryResolveCurrentToolSha_returns_null_when_pointer_file_is_missing()
    {
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = _tempBatonHome });

        Assert.Null(BatonPaths.TryResolveCurrentToolSha());
    }

    [Fact]
    public void TryResolveCurrentToolSha_returns_trimmed_sha_when_pointer_file_exists()
    {
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = _tempBatonHome });

        var toolsDir = Path.Combine(_tempBatonHome, "tools");
        Directory.CreateDirectory(toolsDir);
        File.WriteAllText(Path.Combine(toolsDir, "current"), "  a1b2c3d4\r\n");

        Assert.Equal("a1b2c3d4", BatonPaths.TryResolveCurrentToolSha());
    }

    [Fact]
    public void TryResolveCurrentToolSha_returns_null_when_pointer_file_is_empty()
    {
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = _tempBatonHome });

        var toolsDir = Path.Combine(_tempBatonHome, "tools");
        Directory.CreateDirectory(toolsDir);
        File.WriteAllText(Path.Combine(toolsDir, "current"), "   \r\n");

        Assert.Null(BatonPaths.TryResolveCurrentToolSha());
    }

    [Fact]
    public void InheritBinding_inherits_parent_ToolSha_when_no_current_pointer_is_set()
    {
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = _tempBatonHome });

        var parent = new WorkerBindingConfigEntry(
            Adapter: "claude",
            Contract: new WorkerContract("advise", [], [new ProducedOutput("advice.md")], []),
            PromptTemplate: "Weigh the options.",
            Timeout: TimeSpan.FromMinutes(30),
            ToolSha: "parent-sha");

        var entry = RedispatchCommand.InheritBinding(parent, new RedispatchOptions("parent-room", "new-room"));

        Assert.Equal("parent-sha", entry.ToolSha);
    }

    [Fact]
    public void InheritBinding_uses_current_ToolSha_when_pointer_is_present()
    {
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = _tempBatonHome });

        var toolsDir = Path.Combine(_tempBatonHome, "tools");
        Directory.CreateDirectory(toolsDir);
        File.WriteAllText(Path.Combine(toolsDir, "current"), "fresh-tool-sha");

        var parent = new WorkerBindingConfigEntry(
            Adapter: "claude",
            Contract: new WorkerContract("advise", [], [new ProducedOutput("advice.md")], []),
            PromptTemplate: "Weigh the options.",
            Timeout: TimeSpan.FromMinutes(30),
            ToolSha: "parent-sha");

        var entry = RedispatchCommand.InheritBinding(parent, new RedispatchOptions("parent-room", "new-room"));

        Assert.Equal("fresh-tool-sha", entry.ToolSha);
    }
}
