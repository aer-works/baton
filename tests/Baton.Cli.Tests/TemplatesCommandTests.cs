using System.Text.Json;
using Baton.Cli;

namespace Baton.Cli.Tests;

public class TemplatesCommandTests
{
    [Fact]
    public async Task ExecuteAsync_WithJsonFlag_EmitsValidTemplateJson()
    {
        using var writer = new StringWriter();
        var exitCode = await TemplatesCommand.ExecuteAsync(["--json"], writer, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);

        var output = writer.ToString();
        Assert.False(string.IsNullOrWhiteSpace(output));

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("advise", out var advise));
        Assert.True(root.TryGetProperty("implement", out var implement));
        Assert.True(root.TryGetProperty("review", out var review));
        Assert.True(root.TryGetProperty("fact-check", out var factCheck));
        Assert.True(root.TryGetProperty("janitor", out var janitor));

        // Structural assertions only — exact adapter/model/timeout values live in
        // WorkerRoles.json/WorkerTiers.json (the authority; swapping a tier is one edit there),
        // so pinning them here would just re-transcribe the register.
        foreach (var role in new[] { advise, implement, review, factCheck, janitor })
        {
            Assert.False(string.IsNullOrWhiteSpace(role.GetProperty("adapter").GetString()));
            Assert.InRange(role.GetProperty("timeout_minutes").GetInt32(), 1, 120);
        }

        // Durable by design (#732): the review role produces a structured verdict.
        Assert.True(review.GetProperty("verdict_schema").GetBoolean());
    }

    [Fact]
    public async Task ExecuteAsync_WithoutJsonFlag_PrintsHumanReadableSummary()
    {
        using var writer = new StringWriter();
        var exitCode = await TemplatesCommand.ExecuteAsync([], writer, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);

        var output = writer.ToString();
        Assert.Contains("Available built-in workflow templates:", output);
        Assert.Contains("advise", output);
        Assert.Contains("implement", output);
        Assert.Contains("review", output);
    }
}
