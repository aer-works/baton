using System.Text.Json;
using Baton.Mcp.Host;

namespace Baton.Mcp.Tests;

public class YieldToolTests
{
    [Fact]
    public void MissingOutcome_ReturnsErrorAndWritesNoCaptureFile()
    {
        var captureFile = TempPath();
        try
        {
            var tool = new YieldTool(captureFile);

            var result = tool.Call(Parse("{}"));

            Assert.True(result.IsError);
            Assert.Contains("outcome", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(captureFile));
        }
        finally
        {
            DeleteIfExists(captureFile);
        }
    }

    [Fact]
    public void NonStringOutcome_ReturnsErrorAndWritesNoCaptureFile()
    {
        var captureFile = TempPath();
        try
        {
            var tool = new YieldTool(captureFile);

            var result = tool.Call(Parse("{\"outcome\":42}"));

            Assert.True(result.IsError);
            Assert.False(File.Exists(captureFile));
        }
        finally
        {
            DeleteIfExists(captureFile);
        }
    }

    [Fact]
    public void OutcomeNotInAllowedList_ReturnsErrorAndWritesNoCaptureFile()
    {
        var captureFile = TempPath();
        try
        {
            var tool = new YieldTool(captureFile);

            var result = tool.Call(Parse("{\"outcome\":\"nope\"}"));

            Assert.True(result.IsError);
            Assert.Contains("concluded", result.Text);
            Assert.Contains("stalemate", result.Text);
            Assert.False(File.Exists(captureFile));
        }
        finally
        {
            DeleteIfExists(captureFile);
        }
    }

    [Fact]
    public void ValidOutcomeWithoutNote_CapturesOutcomeAndNullNote()
    {
        var captureFile = TempPath();
        try
        {
            var tool = new YieldTool(captureFile);

            var result = tool.Call(Parse("{\"outcome\":\"concluded\"}"));

            Assert.False(result.IsError);
            var captured = JsonSerializer.Deserialize<YieldCapture>(File.ReadAllText(captureFile));
            Assert.NotNull(captured);
            Assert.Equal("concluded", captured!.Outcome);
            Assert.Null(captured.Note);
        }
        finally
        {
            DeleteIfExists(captureFile);
        }
    }

    [Fact]
    public void ValidOutcomeWithNote_RoundTripsTheNoteIntoTheCaptureFile()
    {
        var captureFile = TempPath();
        try
        {
            var tool = new YieldTool(captureFile);

            var result = tool.Call(Parse("{\"outcome\":\"stalemate\",\"note\":\"neither side moved\"}"));

            Assert.False(result.IsError);
            var captured = JsonSerializer.Deserialize<YieldCapture>(File.ReadAllText(captureFile));
            Assert.NotNull(captured);
            Assert.Equal("stalemate", captured!.Outcome);
            Assert.Equal("neither side moved", captured.Note);
        }
        finally
        {
            DeleteIfExists(captureFile);
        }
    }

    [Fact]
    public void SecondCall_DoesNotOverwriteTheFirstCapturedOutcome()
    {
        var captureFile = TempPath();
        try
        {
            var tool = new YieldTool(captureFile);

            var first = tool.Call(Parse("{\"outcome\":\"concluded\",\"note\":\"first\"}"));
            var second = tool.Call(Parse("{\"outcome\":\"stalemate\",\"note\":\"second\"}"));

            Assert.False(first.IsError);
            Assert.True(second.IsError);

            var captured = JsonSerializer.Deserialize<YieldCapture>(File.ReadAllText(captureFile));
            Assert.NotNull(captured);
            Assert.Equal("concluded", captured!.Outcome);
            Assert.Equal("first", captured.Note);
        }
        finally
        {
            DeleteIfExists(captureFile);
        }
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"baton-yield-tool-test-{Guid.NewGuid():N}.json");

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            FileCleanup.Delete(path);
        }
    }
}
