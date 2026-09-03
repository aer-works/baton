using System.Net;
using System.Text.Json;
using Baton.Cli.Tests.TestSupport;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>WatchNotifier</c> (#1488, spec/baton.md §2): the two <c>--notify</c> shapes, a URL POST and a
/// spawned command. See also <see cref="WatchFireServiceTests"/> for the surrounding claim/exactly-once
/// logic — this file only exercises the notifier's own send mechanics.
/// </summary>
public sealed class WatchNotifierTests
{
    private static WatchNotifyPayload SamplePayload() => new(
        Room: @"C:\rooms\room-1",
        State: "Succeeded",
        Verdict: null,
        Outputs: ["out.md"],
        TerminalAt: new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc));

    [Theory]
    [InlineData("https://example.invalid/hook")]
    [InlineData("http://example.invalid/hook")]
    public void IsHttpUrl_RecognizesHttpAndHttps(string url) => Assert.True(WatchNotifier.IsHttpUrl(url));

    [Theory]
    [InlineData("curl -X POST https://ntfy.sh/mytopic")]
    [InlineData(@"C:\scripts\wake.cmd")]
    [InlineData("ftp://example.invalid/x")]
    public void IsHttpUrl_RejectsEverythingElse(string target) => Assert.False(WatchNotifier.IsHttpUrl(target));

    [Fact]
    public async Task NotifyAsync_UrlTarget_PostsTheJsonPayloadToTheExactUrl()
    {
        var handler = new RecordingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var notifier = new WatchNotifier(httpClient);
        var payload = SamplePayload();

        await notifier.NotifyAsync("https://example.invalid/hook", payload, TestContext.Current.CancellationToken);

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://example.invalid/hook", request.RequestUri!.ToString());
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);

        var deserialized = JsonSerializer.Deserialize<WatchNotifyPayload>(body);
        Assert.Equal(payload.Room, deserialized!.Room);
        Assert.Equal(payload.State, deserialized.State);
        Assert.Equal(payload.Outputs, deserialized.Outputs);
    }

    [Fact]
    public async Task NotifyAsync_UrlTarget_NonSuccessStatus_DoesNotThrow()
    {
        // Best-effort delivery: a non-2xx response is logged (stderr), never surfaced as an exception
        // that would make WatchFireService think the claim itself failed.
        var handler = new RecordingHttpMessageHandler { ResponseStatusCode = HttpStatusCode.InternalServerError };
        using var httpClient = new HttpClient(handler);
        var notifier = new WatchNotifier(httpClient);

        await notifier.NotifyAsync("https://example.invalid/hook", SamplePayload(), TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task NotifyAsync_CommandTarget_ReceivesThePayloadOnStdinAndTheEnvironmentVariable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"baton-watch-notifier-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "capture.ps1");
        var stdinMarkerPath = Path.Combine(tempDir, "stdin.txt");
        var envMarkerPath = Path.Combine(tempDir, "env.txt");

        try
        {
            // Paths are baked directly into the generated script rather than passed as command-line
            // arguments, so this test carries no risk of cmd.exe/PowerShell argument-quoting drift --
            // the ONLY thing under test is whether WatchNotifier's own spawn wires stdin/the env var
            // correctly, not whether an operator's command-line quoting works.
            var errorMarkerPath = Path.Combine(tempDir, "error.txt");
            var script =
                "try {\n" +
                "$stdin = [Console]::In.ReadToEnd()\n" +
                $"[System.IO.File]::WriteAllText('{stdinMarkerPath}', $stdin)\n" +
                $"[System.IO.File]::WriteAllText('{envMarkerPath}', $env:{WatchNotifier.NotifyEventEnvironmentVariable})\n" +
                "} catch {\n" +
                $"[System.IO.File]::WriteAllText('{errorMarkerPath}', $_.Exception.ToString())\n" +
                "}\n";
            await File.WriteAllTextAsync(scriptPath, script, TestContext.Current.CancellationToken);

            var notifier = new WatchNotifier();
            var payload = SamplePayload();
            var command = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"";

            await notifier.NotifyAsync(command, payload, TestContext.Current.CancellationToken);

            var errorText = File.Exists(errorMarkerPath) ? File.ReadAllText(errorMarkerPath) : "(no error.txt)";
            Assert.True(
                File.Exists(stdinMarkerPath),
                $"the spawned command never observed stdin close, or never ran. tempDir={tempDir} error={errorText}");
            var stdinContent = await File.ReadAllTextAsync(stdinMarkerPath, TestContext.Current.CancellationToken);
            var envContent = await File.ReadAllTextAsync(envMarkerPath, TestContext.Current.CancellationToken);

            var stdinPayload = JsonSerializer.Deserialize<WatchNotifyPayload>(stdinContent);
            Assert.Equal(payload.Room, stdinPayload!.Room);
            Assert.Equal(payload.State, stdinPayload.State);

            var envPayload = JsonSerializer.Deserialize<WatchNotifyPayload>(envContent);
            Assert.Equal(payload.Room, envPayload!.Room);
            Assert.Equal(payload.State, envPayload.State);

            // The env variable and stdin carry the identical bytes -- one serialization, two transports.
            Assert.Equal(stdinContent, envContent);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }
}
