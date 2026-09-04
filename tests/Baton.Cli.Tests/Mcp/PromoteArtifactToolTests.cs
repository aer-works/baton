using System.Text;
using System.Text.Json;
using Baton.Artifacts;
using Baton.Cli.Mcp;

namespace Baton.Cli.Tests.Mcp;

public class PromoteArtifactToolTests
{
    private static readonly ArtifactAttribution Attribution = new("exec-1", null, null, null);

    [Fact]
    public void Promote_WritesVersionOneWithAttribution()
    {
        var roomDir = TempDir();
        var sourcePath = Path.Combine(Path.GetTempPath(), $"baton-promote-source-{Guid.NewGuid():N}.md");
        File.WriteAllText(sourcePath, "hello");
        try
        {
            var tool = new PromoteArtifactTool(roomDir, Attribution);

            var result = tool.Call(Parse(JsonSerializer.Serialize(new { sourcePath, artifactName = "report.md" })));

            Assert.False(result.IsError);
            Assert.Contains("version 1", result.Text);

            var versions = RoomArtifacts.Versions(roomDir, "report.md");
            Assert.Single(versions);
            Assert.Equal(1, versions[0].Version);
            Assert.Equal("exec-1", versions[0].ProducedBy.ExecutionId);

            var written = RoomArtifacts.Read(roomDir, "report.md");
            Assert.Equal("hello", Encoding.UTF8.GetString(written!));
        }
        finally
        {
            DeleteIfExists(roomDir);
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public void SecondPromote_OfSameName_WritesVersionTwo()
    {
        var roomDir = TempDir();
        var sourcePath = Path.Combine(Path.GetTempPath(), $"baton-promote-source-{Guid.NewGuid():N}.md");
        try
        {
            var tool = new PromoteArtifactTool(roomDir, Attribution);

            File.WriteAllText(sourcePath, "first");
            var first = tool.Call(Parse(JsonSerializer.Serialize(new { sourcePath, artifactName = "report.md" })));
            Assert.False(first.IsError);

            File.WriteAllText(sourcePath, "second");
            var second = tool.Call(Parse(JsonSerializer.Serialize(new { sourcePath, artifactName = "report.md" })));

            Assert.False(second.IsError);
            Assert.Contains("version 2", second.Text);

            var versions = RoomArtifacts.Versions(roomDir, "report.md");
            Assert.Equal(2, versions.Count);
        }
        finally
        {
            DeleteIfExists(roomDir);
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public void MissingSource_ReturnsErrorAndWritesNothing()
    {
        var roomDir = TempDir();
        try
        {
            var tool = new PromoteArtifactTool(roomDir, Attribution);
            var sourcePath = Path.Combine(Path.GetTempPath(), $"baton-promote-missing-{Guid.NewGuid():N}.md");

            var result = tool.Call(Parse(JsonSerializer.Serialize(new { sourcePath, artifactName = "report.md" })));

            Assert.True(result.IsError);
            Assert.Empty(RoomArtifacts.Versions(roomDir, "report.md"));
        }
        finally
        {
            DeleteIfExists(roomDir);
        }
    }

    [Fact]
    public void RelativeSourcePath_ReturnsError()
    {
        var roomDir = TempDir();
        try
        {
            var tool = new PromoteArtifactTool(roomDir, Attribution);

            var result = tool.Call(Parse(JsonSerializer.Serialize(new
            {
                sourcePath = "relative/report.md",
                artifactName = "report.md",
            })));

            Assert.True(result.IsError);
            Assert.Contains("absolute", result.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteIfExists(roomDir);
        }
    }

    [Theory]
    [InlineData("sub/report.md")]
    [InlineData("sub\\report.md")]
    [InlineData("..")]
    [InlineData("a/../b")]
    public void BadArtifactName_ReturnsErrorAndWritesNothing(string artifactName)
    {
        var roomDir = TempDir();
        var sourcePath = Path.Combine(Path.GetTempPath(), $"baton-promote-source-{Guid.NewGuid():N}.md");
        File.WriteAllText(sourcePath, "hello");
        try
        {
            var tool = new PromoteArtifactTool(roomDir, Attribution);

            var result = tool.Call(Parse(JsonSerializer.Serialize(new { sourcePath, artifactName })));

            Assert.True(result.IsError);
            Assert.False(Directory.Exists(Path.Combine(roomDir, "artifacts")));
        }
        finally
        {
            DeleteIfExists(roomDir);
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public void OversizedSource_ReturnsErrorAndWritesNothing()
    {
        var roomDir = TempDir();
        var sourcePath = Path.Combine(Path.GetTempPath(), $"baton-promote-source-{Guid.NewGuid():N}.md");
        try
        {
            using (var stream = File.Create(sourcePath))
            {
                stream.SetLength(PromoteArtifactTool.MaxSourceBytes + 1);
            }

            var tool = new PromoteArtifactTool(roomDir, Attribution);

            var result = tool.Call(Parse(JsonSerializer.Serialize(new { sourcePath, artifactName = "report.md" })));

            Assert.True(result.IsError);
            Assert.Empty(RoomArtifacts.Versions(roomDir, "report.md"));
        }
        finally
        {
            DeleteIfExists(roomDir);
            File.Delete(sourcePath);
        }
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string TempDir() => Path.Combine(Path.GetTempPath(), $"baton-promote-artifact-tool-test-{Guid.NewGuid():N}");

    private static void DeleteIfExists(string path) => DirectoryCleanup.DeleteRecursively(path);
}
