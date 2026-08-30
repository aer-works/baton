using System.Text.Json;
using Baton.Mcp.Host;

namespace Baton.Mcp.Tests;

public class MemoryProposalToolTests
{
    [Fact]
    public void MissingOperation_ReturnsErrorAndWritesNoCaptureFile()
    {
        var dir = TempDir();
        try
        {
            var tool = new MemoryProposalTool(dir);

            var result = tool.Call(Parse("{\"targetPath\":\"foo.md\",\"rationale\":\"why\",\"content\":\"x\"}"));

            Assert.True(result.IsError);
            Assert.Contains("operation", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);
        }
        finally
        {
            DeleteIfExists(dir);
        }
    }

    [Fact]
    public void UnknownOperation_ReturnsErrorAndWritesNoCaptureFile()
    {
        var dir = TempDir();
        try
        {
            var tool = new MemoryProposalTool(dir);

            var result = tool.Call(Parse(
                "{\"operation\":\"delete-everything\",\"targetPath\":\"foo.md\",\"rationale\":\"why\"}"));

            Assert.True(result.IsError);
            Assert.Contains("add", result.Text);
            Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);
        }
        finally
        {
            DeleteIfExists(dir);
        }
    }

    // Both platforms' rooted shapes, asserted on EVERY platform: Path.IsPathRooted alone accepts
    // 'C:/etc/passwd' on Unix (not rooted there), which is exactly the CI leg where the original
    // single-arm test could not discriminate (#801 review).
    [Theory]
    [InlineData("C:/etc/passwd")]
    [InlineData("C:\\etc\\passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\x.md")]
    public void RootedTargetPath_ReturnsErrorAndWritesNoCaptureFile(string rootedPath)
    {
        var dir = TempDir();
        try
        {
            var tool = new MemoryProposalTool(dir);

            var result = tool.Call(Parse(JsonSerializer.Serialize(new
            {
                operation = "add",
                targetPath = rootedPath,
                rationale = "why",
                content = "x",
            })));

            Assert.True(result.IsError);
            Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);
        }
        finally
        {
            DeleteIfExists(dir);
        }
    }

    [Fact]
    public void NonStringContentForDelete_ReturnsErrorAndWritesNoCaptureFile()
    {
        var dir = TempDir();
        try
        {
            var tool = new MemoryProposalTool(dir);

            var result = tool.Call(Parse(
                "{\"operation\":\"delete\",\"targetPath\":\"foo.md\",\"rationale\":\"why\",\"content\":42}"));

            Assert.True(result.IsError);
            Assert.Contains("content", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);
        }
        finally
        {
            DeleteIfExists(dir);
        }
    }

    [Fact]
    public void TraversalTargetPath_ReturnsErrorAndWritesNoCaptureFile()
    {
        var dir = TempDir();
        try
        {
            var tool = new MemoryProposalTool(dir);

            var result = tool.Call(Parse(
                "{\"operation\":\"add\",\"targetPath\":\"../outside.md\",\"rationale\":\"why\",\"content\":\"x\"}"));

            Assert.True(result.IsError);
            Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);
        }
        finally
        {
            DeleteIfExists(dir);
        }
    }

    [Fact]
    public void MissingContentForAdd_ReturnsErrorAndWritesNoCaptureFile()
    {
        var dir = TempDir();
        try
        {
            var tool = new MemoryProposalTool(dir);

            var result = tool.Call(Parse(
                "{\"operation\":\"add\",\"targetPath\":\"foo.md\",\"rationale\":\"why\"}"));

            Assert.True(result.IsError);
            Assert.Contains("content", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);
        }
        finally
        {
            DeleteIfExists(dir);
        }
    }

    [Fact]
    public void MissingRationale_ReturnsErrorAndWritesNoCaptureFile()
    {
        var dir = TempDir();
        try
        {
            var tool = new MemoryProposalTool(dir);

            var result = tool.Call(Parse(
                "{\"operation\":\"add\",\"targetPath\":\"foo.md\",\"content\":\"x\"}"));

            Assert.True(result.IsError);
            Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);
        }
        finally
        {
            DeleteIfExists(dir);
        }
    }

    [Fact]
    public void ValidDelete_DoesNotRequireContentAndCaptures()
    {
        var dir = TempDir();
        try
        {
            var tool = new MemoryProposalTool(dir);

            var result = tool.Call(Parse(
                "{\"operation\":\"delete\",\"targetPath\":\"stale-fact.md\",\"rationale\":\"superseded\"}"));

            Assert.False(result.IsError);
            var file = Assert.Single(Directory.GetFiles(dir));
            var captured = JsonSerializer.Deserialize<MemoryProposalCapture>(File.ReadAllText(file));
            Assert.NotNull(captured);
            Assert.Equal("delete", captured!.Operation);
            Assert.Equal("stale-fact.md", captured.TargetPath);
            Assert.Null(captured.Content);
            Assert.Equal("superseded", captured.Rationale);
        }
        finally
        {
            DeleteIfExists(dir);
        }
    }

    [Fact]
    public void ValidAdd_RoundTripsEveryFieldIntoTheCaptureFile()
    {
        var dir = TempDir();
        try
        {
            var tool = new MemoryProposalTool(dir);

            var result = tool.Call(Parse(
                "{\"operation\":\"add\",\"targetPath\":\"new-fact.md\",\"content\":\"the fact\",\"rationale\":\"learned it\"}"));

            Assert.False(result.IsError);
            var file = Assert.Single(Directory.GetFiles(dir));
            var captured = JsonSerializer.Deserialize<MemoryProposalCapture>(File.ReadAllText(file));
            Assert.NotNull(captured);
            Assert.Equal("add", captured!.Operation);
            Assert.Equal("new-fact.md", captured.TargetPath);
            Assert.Equal("the fact", captured.Content);
            Assert.Equal("learned it", captured.Rationale);
        }
        finally
        {
            DeleteIfExists(dir);
        }
    }

    [Fact]
    public void TwoValidCalls_BothCaptureAsDistinctFiles()
    {
        var dir = TempDir();
        try
        {
            var tool = new MemoryProposalTool(dir);

            var first = tool.Call(Parse(
                "{\"operation\":\"add\",\"targetPath\":\"a.md\",\"content\":\"a\",\"rationale\":\"why a\"}"));
            var second = tool.Call(Parse(
                "{\"operation\":\"add\",\"targetPath\":\"b.md\",\"content\":\"b\",\"rationale\":\"why b\"}"));

            Assert.False(first.IsError);
            Assert.False(second.IsError);
            Assert.Equal(2, Directory.GetFiles(dir).Length);
        }
        finally
        {
            DeleteIfExists(dir);
        }
    }

    /// <summary>Pins the literal (#833) -- see <see cref="MemoryProposalTool.CaptureDirectoryName"/>'s own doc comment for why this must agree with the Baton.Flow side.</summary>
    [Fact]
    public void CaptureDirectoryName_is_the_literal_mirrored_on_the_Baton_Flow_side()
    {
        Assert.Equal("memory-proposals", MemoryProposalTool.CaptureDirectoryName);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string TempDir() => Path.Combine(Path.GetTempPath(), $"baton-memory-proposal-tool-test-{Guid.NewGuid():N}");

    private static void DeleteIfExists(string path)
    {
        DirectoryCleanup.DeleteRecursively(path);
    }
}
