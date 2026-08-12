using System.Text.Json;
using System.Text.Json.Nodes;
using Aer.Mcp;
using Aer.Mcp.Host;

namespace Aer.Mcp.Tests;

public class PermissionGateToolTests
{
    [Fact]
    public async Task AllowRendezvous_ClaudeCallback_ReturnsAllowJson()
    {
        var rendezvousDir = Path.Combine(Path.GetTempPath(), $"aer-perm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rendezvousDir);
        try
        {
            var tool = new PermissionGateTool(rendezvousDir, PermissionReturnShape.ClaudeCallback, TimeSpan.FromSeconds(5));
            using var argsDoc = JsonDocument.Parse("""{"tool_name":"bash","input":{"command":"ls -la"},"reason":"test"}""");

            var callTask = tool.CallAsync(argsDoc.RootElement, TestContext.Current.CancellationToken);

            var requestId = await WaitForAskFileIdAsync(rendezvousDir, TimeSpan.FromSeconds(2));
            var answerPath = Path.Combine(rendezvousDir, $"answer-{requestId}.json");
            var answerJson = JsonSerializer.Serialize(new { decisionKind = "AllowOnce", updatedInputJson = (string?)null, reason = (string?)null });
            await File.WriteAllTextAsync(answerPath, answerJson, TestContext.Current.CancellationToken);

            var result = await callTask;
            Assert.False(result.IsError);

            var node = JsonNode.Parse(result.Text)!.AsObject();
            Assert.Equal("allow", (string)node["behavior"]!);
            Assert.Equal("ls -la", (string)node["updatedInput"]!["command"]!);
        }
        finally
        {
            if (Directory.Exists(rendezvousDir))
            {
                Directory.Delete(rendezvousDir, true);
            }
        }
    }

    [Fact]
    public async Task AllowRendezvous_AgyElected_ReturnsAllowText()
    {
        var rendezvousDir = Path.Combine(Path.GetTempPath(), $"aer-perm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rendezvousDir);
        try
        {
            var tool = new PermissionGateTool(rendezvousDir, PermissionReturnShape.AgyElected, TimeSpan.FromSeconds(5));
            using var argsDoc = JsonDocument.Parse("""{"tool_name":"bash","input":{"command":"ls -la"}}""");

            var callTask = tool.CallAsync(argsDoc.RootElement, TestContext.Current.CancellationToken);

            var requestId = await WaitForAskFileIdAsync(rendezvousDir, TimeSpan.FromSeconds(2));
            var answerPath = Path.Combine(rendezvousDir, $"answer-{requestId}.json");
            var answerJson = JsonSerializer.Serialize(new { decisionKind = "AllowOnce", updatedInputJson = (string?)null, reason = (string?)null });
            await File.WriteAllTextAsync(answerPath, answerJson, TestContext.Current.CancellationToken);

            var result = await callTask;
            Assert.False(result.IsError);
            Assert.StartsWith("Permission granted", result.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(rendezvousDir))
            {
                Directory.Delete(rendezvousDir, true);
            }
        }
    }

    [Fact]
    public async Task Deny_ClaudeCallback_ReturnsDenyJson()
    {
        var rendezvousDir = Path.Combine(Path.GetTempPath(), $"aer-perm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rendezvousDir);
        try
        {
            var tool = new PermissionGateTool(rendezvousDir, PermissionReturnShape.ClaudeCallback, TimeSpan.FromSeconds(5));
            using var argsDoc = JsonDocument.Parse("""{"tool_name":"delete_all","input":{}}""");

            var callTask = tool.CallAsync(argsDoc.RootElement, TestContext.Current.CancellationToken);

            var requestId = await WaitForAskFileIdAsync(rendezvousDir, TimeSpan.FromSeconds(2));
            var answerPath = Path.Combine(rendezvousDir, $"answer-{requestId}.json");
            var answerJson = JsonSerializer.Serialize(new { decisionKind = "Deny", reason = "Operation dangerous" });
            await File.WriteAllTextAsync(answerPath, answerJson, TestContext.Current.CancellationToken);

            var result = await callTask;
            Assert.False(result.IsError);

            var node = JsonNode.Parse(result.Text)!.AsObject();
            Assert.Equal("deny", (string)node["behavior"]!);
            Assert.Equal("Operation dangerous", (string)node["message"]!);
        }
        finally
        {
            if (Directory.Exists(rendezvousDir))
            {
                Directory.Delete(rendezvousDir, true);
            }
        }
    }

    [Fact]
    public async Task Deny_AgyElected_ReturnsIsErrorWithReason()
    {
        var rendezvousDir = Path.Combine(Path.GetTempPath(), $"aer-perm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rendezvousDir);
        try
        {
            var tool = new PermissionGateTool(rendezvousDir, PermissionReturnShape.AgyElected, TimeSpan.FromSeconds(5));
            using var argsDoc = JsonDocument.Parse("""{"tool_name":"delete_all","input":{}}""");

            var callTask = tool.CallAsync(argsDoc.RootElement, TestContext.Current.CancellationToken);

            var requestId = await WaitForAskFileIdAsync(rendezvousDir, TimeSpan.FromSeconds(2));
            var answerPath = Path.Combine(rendezvousDir, $"answer-{requestId}.json");
            var answerJson = JsonSerializer.Serialize(new { decisionKind = "Deny", reason = "Operation dangerous" });
            await File.WriteAllTextAsync(answerPath, answerJson, TestContext.Current.CancellationToken);

            var result = await callTask;
            Assert.True(result.IsError);
            Assert.Equal("Operation dangerous", result.Text);
        }
        finally
        {
            if (Directory.Exists(rendezvousDir))
            {
                Directory.Delete(rendezvousDir, true);
            }
        }
    }

    [Fact]
    public async Task Timeout_WritesRevokedFile_AndReturnsFailClosedDeny()
    {
        var rendezvousDir = Path.Combine(Path.GetTempPath(), $"aer-perm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rendezvousDir);
        try
        {
            var toolClaude = new PermissionGateTool(rendezvousDir, PermissionReturnShape.ClaudeCallback, TimeSpan.FromMilliseconds(200));
            using var argsDoc = JsonDocument.Parse("""{"tool_name":"long_op","input":{}}""");

            var resultClaude = await toolClaude.CallAsync(argsDoc.RootElement, TestContext.Current.CancellationToken);
            Assert.False(resultClaude.IsError);
            var node = JsonNode.Parse(resultClaude.Text)!.AsObject();
            Assert.Equal("deny", (string)node["behavior"]!);

            var revokedFiles = Directory.GetFiles(rendezvousDir, "revoked-*.json");
            Assert.Single(revokedFiles);
            var revokedText = await File.ReadAllTextAsync(revokedFiles[0], TestContext.Current.CancellationToken);
            var revokedNode = JsonNode.Parse(revokedText)!.AsObject();
            Assert.Equal("timeout", (string)revokedNode["reason"]!);
            Assert.False(string.IsNullOrWhiteSpace((string)revokedNode["permissionRequestId"]!));
        }
        finally
        {
            if (Directory.Exists(rendezvousDir))
            {
                Directory.Delete(rendezvousDir, true);
            }
        }
    }

    [Fact]
    public async Task TimeoutControl_ReturnsFailClosedDenyNamingTimeout()
    {
        var rendezvousDir = Path.Combine(Path.GetTempPath(), $"aer-perm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rendezvousDir);
        try
        {
            var toolClaude = new PermissionGateTool(rendezvousDir, PermissionReturnShape.ClaudeCallback, TimeSpan.FromMilliseconds(300));
            using var argsDoc = JsonDocument.Parse("""{"tool_name":"long_op","input":{}}""");

            var resultClaude = await toolClaude.CallAsync(argsDoc.RootElement, TestContext.Current.CancellationToken);
            Assert.False(resultClaude.IsError);
            var node = JsonNode.Parse(resultClaude.Text)!.AsObject();
            Assert.Equal("deny", (string)node["behavior"]!);
            Assert.Contains("300ms", (string)node["message"]!);

            var toolAgy = new PermissionGateTool(rendezvousDir, PermissionReturnShape.AgyElected, TimeSpan.FromMilliseconds(300));
            var resultAgy = await toolAgy.CallAsync(argsDoc.RootElement, TestContext.Current.CancellationToken);
            Assert.True(resultAgy.IsError);
            Assert.Contains("300ms", resultAgy.Text);
        }
        finally
        {
            if (Directory.Exists(rendezvousDir))
            {
                Directory.Delete(rendezvousDir, true);
            }
        }
    }

    [Fact]
    public async Task AskFileWritten_ContainsExpectedProperties()
    {
        var rendezvousDir = Path.Combine(Path.GetTempPath(), $"aer-perm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rendezvousDir);
        try
        {
            var tool = new PermissionGateTool(rendezvousDir, PermissionReturnShape.ClaudeCallback, TimeSpan.FromSeconds(5));
            using var argsDoc = JsonDocument.Parse("""{"tool_name":"git_push","input":{"remote":"origin","branch":"main"},"reason":"deploying"}""");

            var callTask = tool.CallAsync(argsDoc.RootElement, TestContext.Current.CancellationToken);

            var requestId = await WaitForAskFileIdAsync(rendezvousDir, TimeSpan.FromSeconds(2));
            var askPath = Path.Combine(rendezvousDir, $"ask-{requestId}.json");
            Assert.True(File.Exists(askPath));

            var askText = await File.ReadAllTextAsync(askPath, TestContext.Current.CancellationToken);
            var askNode = JsonNode.Parse(askText)!.AsObject();

            Assert.Equal(requestId, (string)askNode["permissionRequestId"]!);
            Assert.Equal("git_push", (string)askNode["toolName"]!);
            Assert.Contains("origin", (string)askNode["inputJson"]!);
            Assert.Equal("deploying", (string)askNode["reason"]!);

            // Complete task to prevent hanging cleanup
            var answerPath = Path.Combine(rendezvousDir, $"answer-{requestId}.json");
            await File.WriteAllTextAsync(answerPath, """{"decisionKind":"Deny"}""", TestContext.Current.CancellationToken);
            await callTask;
        }
        finally
        {
            if (Directory.Exists(rendezvousDir))
            {
                Directory.Delete(rendezvousDir, true);
            }
        }
    }

    [Fact]
    public async Task DefaultInterfaceNoOp_YieldToolCallAsyncReturnsSameAsCall()
    {
        var captureFile1 = Path.Combine(Path.GetTempPath(), $"aer-yield-test1-{Guid.NewGuid():N}.json");
        var captureFile2 = Path.Combine(Path.GetTempPath(), $"aer-yield-test2-{Guid.NewGuid():N}.json");
        try
        {
            IMcpTool tool1 = new YieldTool(captureFile1);
            IMcpTool tool2 = new YieldTool(captureFile2);
            using var argsDoc = JsonDocument.Parse("""{"outcome":"concluded"}""");

            var syncResult = tool1.Call(argsDoc.RootElement);
            var asyncResult = await tool2.CallAsync(argsDoc.RootElement, TestContext.Current.CancellationToken);

            Assert.Equal(syncResult.Text, asyncResult.Text);
            Assert.Equal(syncResult.IsError, asyncResult.IsError);
        }
        finally
        {
            if (File.Exists(captureFile1))
            {
                FileCleanup.Delete(captureFile1);
            }
            if (File.Exists(captureFile2))
            {
                FileCleanup.Delete(captureFile2);
            }
        }
    }

    private static async Task<string> WaitForAskFileIdAsync(string rendezvousDir, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var files = Directory.GetFiles(rendezvousDir, "ask-*.json");
            if (files.Length > 0)
            {
                var fileName = Path.GetFileName(files[0]);
                return fileName["ask-".Length..^".json".Length];
            }
            // wait-ok: poll interval inside a bounded WaitForAskFile helper — the TimeoutException ceiling is the real wait
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("ask-*.json file was not written within timeout.");
    }
}
