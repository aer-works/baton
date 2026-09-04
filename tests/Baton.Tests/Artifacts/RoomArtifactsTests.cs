using System.Text;
using Baton.Artifacts;
using Baton.Tests.TestSupport;

namespace Baton.Tests.Artifacts;

public sealed class RoomArtifactsTests : IDisposable
{
    private readonly string _roomDir;

    public RoomArtifactsTests()
    {
        _roomDir = Path.Combine(Path.GetTempPath(), $"room-artifacts-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_roomDir);
    }

    public void Dispose()
    {
        DirectoryCleanup.DeleteRecursively(_roomDir);
    }

    private static ArtifactAttribution Attribution(string role = "worker") =>
        new(ExecutionId: "exec-1", Role: role, Adapter: "claude", Model: "sonnet");

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Write_AbsentTarget_WritesCurrentAndVersion1()
    {
        var result = RoomArtifacts.Write(_roomDir, "plan.md", Bytes("v1"), Attribution());

        Assert.Equal(ArtifactWriteOutcome.Created, result.Outcome);
        Assert.Equal(1, result.Version);
        Assert.Equal("v1", File.ReadAllText(result.CurrentPath));

        var versions = RoomArtifacts.Versions(_roomDir, "plan.md");
        Assert.Single(versions);
        Assert.Equal(1, versions[0].Version);
        Assert.Equal("v1", Encoding.UTF8.GetString(RoomArtifacts.Read(_roomDir, "plan.md")!));
    }

    [Fact]
    public void Write_IdenticalRewrite_ProducesNoNewVersion()
    {
        RoomArtifacts.Write(_roomDir, "plan.md", Bytes("v1"), Attribution());
        var result = RoomArtifacts.Write(_roomDir, "plan.md", Bytes("v1"), Attribution());

        Assert.Equal(ArtifactWriteOutcome.Unchanged, result.Outcome);
        Assert.Equal(1, result.Version);
        Assert.Single(RoomArtifacts.Versions(_roomDir, "plan.md"));
    }

    [Fact]
    public void Write_ChangedRewrite_VersionsAndKeepsOldBytesIntact()
    {
        RoomArtifacts.Write(_roomDir, "plan.md", Bytes("v1"), Attribution());
        var result = RoomArtifacts.Write(_roomDir, "plan.md", Bytes("v2"), Attribution());

        Assert.Equal(ArtifactWriteOutcome.Versioned, result.Outcome);
        Assert.Equal(2, result.Version);

        var versions = RoomArtifacts.Versions(_roomDir, "plan.md");
        Assert.Equal(2, versions.Count);
        Assert.Equal(1, versions[0].Version);
        Assert.Equal(2, versions[1].Version);

        Assert.Equal("v1", Encoding.UTF8.GetString(RoomArtifacts.Read(_roomDir, "plan.md", 1)!));
        Assert.Equal("v2", Encoding.UTF8.GetString(RoomArtifacts.Read(_roomDir, "plan.md", 2)!));
        Assert.Equal("v2", Encoding.UTF8.GetString(RoomArtifacts.Read(_roomDir, "plan.md")!));
        Assert.Equal("v2", File.ReadAllText(result.CurrentPath));
    }

    [Fact]
    public void Write_CarriesAttributionPerVersion()
    {
        RoomArtifacts.Write(_roomDir, "plan.md", Bytes("v1"), Attribution(role: "worker"));
        RoomArtifacts.Write(_roomDir, "plan.md", Bytes("v2"), Attribution(role: "reviewer"));

        var versions = RoomArtifacts.Versions(_roomDir, "plan.md");
        Assert.Equal("worker", versions[0].ProducedBy.Role);
        Assert.Equal("reviewer", versions[1].ProducedBy.Role);
        Assert.Equal("claude", versions[1].ProducedBy.Adapter);
        Assert.Equal("sonnet", versions[1].ProducedBy.Model);
        Assert.Equal("exec-1", versions[1].ProducedBy.ExecutionId);
    }

    [Fact]
    public void OrphanVersionFile_WithNoIndexLine_IsIgnoredByReadersAndReused()
    {
        // Simulates a crash between Write's version-file write and its index-line append: the file
        // exists on disk, but no index line ever named it.
        RoomArtifacts.Write(_roomDir, "plan.md", Bytes("v1"), Attribution());

        var versionsDir = Path.Combine(_roomDir, "artifacts", ".versions", "plan.md");
        File.WriteAllText(Path.Combine(versionsDir, "2"), "orphaned-content");

        // The index still reports only version 1 -- the orphan never officially happened.
        var versions = RoomArtifacts.Versions(_roomDir, "plan.md");
        Assert.Single(versions);
        Assert.Null(RoomArtifacts.Read(_roomDir, "plan.md", 2));

        // A subsequent real write reuses version number 2, overwriting the orphan file's bytes.
        var result = RoomArtifacts.Write(_roomDir, "plan.md", Bytes("v2-real"), Attribution());
        Assert.Equal(2, result.Version);
        Assert.Equal("v2-real", Encoding.UTF8.GetString(RoomArtifacts.Read(_roomDir, "plan.md", 2)!));

        var versionsAfter = RoomArtifacts.Versions(_roomDir, "plan.md");
        Assert.Equal(2, versionsAfter.Count);
    }

    [Fact]
    public void Read_UnknownName_ReturnsNull()
    {
        Assert.Null(RoomArtifacts.Read(_roomDir, "never-written.md"));
        Assert.Empty(RoomArtifacts.Versions(_roomDir, "never-written.md"));
    }

    [Fact]
    public void Write_NameWithSubdirectory_RoundTrips()
    {
        var result = RoomArtifacts.Write(_roomDir, "conductor/plan.md", Bytes("v1"), Attribution());

        Assert.Equal(Path.Combine(_roomDir, "artifacts", "conductor", "plan.md"), result.CurrentPath);
        Assert.Equal("v1", Encoding.UTF8.GetString(RoomArtifacts.Read(_roomDir, "conductor/plan.md")!));
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("/rooted.md")]
    [InlineData("a/../b.md")]
    public void Write_RejectsTraversalOrRootedNames(string name)
    {
        Assert.Throws<ArgumentException>(() => RoomArtifacts.Write(_roomDir, name, Bytes("v1"), Attribution()));
    }
}
