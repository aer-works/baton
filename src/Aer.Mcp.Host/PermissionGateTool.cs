using System.Text.Json;
using System.Text.Json.Nodes;
using Aer.Mcp;

namespace Aer.Mcp.Host;

/// <summary>
/// The <c>aer_permission_ask</c> MCP tool: a worker calls this before an operation it is not pre-cleared for.
/// It writes an ask file into <paramref name="rendezvousDirectoryPath"/> and blocks until an answer file appears
/// or a timeout is reached.
/// </summary>
public sealed class PermissionGateTool : IMcpTool
{
    private readonly string _rendezvousDirectoryPath;
    private readonly PermissionReturnShape _returnShape;
    private readonly TimeSpan _timeout;

    public PermissionGateTool(
        string rendezvousDirectoryPath,
        PermissionReturnShape returnShape,
        TimeSpan? timeout = null)
    {
        _rendezvousDirectoryPath = rendezvousDirectoryPath ?? throw new ArgumentNullException(nameof(rendezvousDirectoryPath));
        _returnShape = returnShape;
        _timeout = timeout ?? TimeSpan.FromSeconds(180);
    }

    public string Name => "aer_permission_ask";

    public string Description =>
        "a worker calls this before an operation it is not pre-cleared for; it blocks until a human answers.";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "tool_name": { "type": "string" },
            "input": { "type": "object" },
            "reason": { "type": "string" }
          },
          "required": ["tool_name", "input"]
        }
        """;

    public McpToolCallResult Call(JsonElement arguments) =>
        CallAsync(arguments, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<McpToolCallResult> CallAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return new McpToolCallResult("Arguments must be a JSON object.", IsError: true);
        }

        if (!arguments.TryGetProperty("tool_name", out var toolNameElem) || toolNameElem.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(toolNameElem.GetString()))
        {
            return new McpToolCallResult("'tool_name' is required and must be a non-empty string.", IsError: true);
        }

        if (!arguments.TryGetProperty("input", out var inputElem) || inputElem.ValueKind != JsonValueKind.Object)
        {
            return new McpToolCallResult("'input' is required and must be a JSON object.", IsError: true);
        }

        var toolName = toolNameElem.GetString()!;
        var inputJson = inputElem.GetRawText();
        string? reason = arguments.TryGetProperty("reason", out var reasonElem) && reasonElem.ValueKind == JsonValueKind.String
            ? reasonElem.GetString()
            : null;

        var permissionRequestId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(_rendezvousDirectoryPath);

        var askPayload = new
        {
            permissionRequestId,
            toolName,
            inputJson,
            reason,
            askedAt = DateTimeOffset.UtcNow,
            // The ask carries its own deadline so no other component has to duplicate this tool's
            // timeout value: the daemon's restart reconciliation expires an ask from askedAt +
            // timeoutSeconds, staying correct if a caller ever parameterizes the timeout.
            timeoutSeconds = (int)_timeout.TotalSeconds
        };

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        var askJson = JsonSerializer.Serialize(askPayload, jsonOptions);

        var askFilePath = Path.Combine(_rendezvousDirectoryPath, $"ask-{permissionRequestId}.json");
        var tempFilePath = Path.Combine(_rendezvousDirectoryPath, $"ask-{permissionRequestId}.json.{Guid.NewGuid():N}.tmp");

        await File.WriteAllTextAsync(tempFilePath, askJson, cancellationToken).ConfigureAwait(false);
        File.Move(tempFilePath, askFilePath, overwrite: true);

        var answerFilePath = Path.Combine(_rendezvousDirectoryPath, $"answer-{permissionRequestId}.json");
        var deadline = DateTime.UtcNow.Add(_timeout);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(answerFilePath))
            {
                try
                {
                    var answerText = await File.ReadAllTextAsync(answerFilePath, cancellationToken).ConfigureAwait(false);
                    using var answerDoc = JsonDocument.Parse(answerText);
                    var root = answerDoc.RootElement;

                    var decisionKind = GetStringProperty(root, "decisionKind");
                    var updatedInputJson = GetStringOrObjectProperty(root, "updatedInputJson");
                    var answerReason = GetStringProperty(root, "reason");

                    return BuildAnswerResult(decisionKind, updatedInputJson, answerReason, inputElem, toolName);
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    // Transient lock or incomplete write; wait and retry
                }
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var delayMs = (int)Math.Min(150, remaining.TotalMilliseconds);
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            var revokePayload = new
            {
                permissionRequestId,
                reason = "timeout"
            };
            var revokeJson = JsonSerializer.Serialize(revokePayload, jsonOptions);
            var revokedFilePath = Path.Combine(_rendezvousDirectoryPath, $"revoked-{permissionRequestId}.json");
            var tempRevokedFilePath = Path.Combine(_rendezvousDirectoryPath, $"revoked-{permissionRequestId}.json.{Guid.NewGuid():N}.tmp");

            await File.WriteAllTextAsync(tempRevokedFilePath, revokeJson, cancellationToken).ConfigureAwait(false);
            File.Move(tempRevokedFilePath, revokedFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignore transient file errors on timeout write
        }

        return BuildTimeoutResult();
    }

    private McpToolCallResult BuildAnswerResult(
        string? decisionKind,
        string? updatedInputJson,
        string? answerReason,
        JsonElement originalInput,
        string toolName)
    {
        var isAllow = decisionKind is not null && decisionKind.StartsWith("Allow", StringComparison.OrdinalIgnoreCase);

        if (_returnShape == PermissionReturnShape.ClaudeCallback)
        {
            if (isAllow)
            {
                JsonNode? updatedNode = null;
                if (!string.IsNullOrWhiteSpace(updatedInputJson))
                {
                    try
                    {
                        updatedNode = JsonNode.Parse(updatedInputJson);
                    }
                    catch (JsonException)
                    {
                        // Fall back to original input if parsing updatedInputJson fails
                    }
                }

                updatedNode ??= JsonNode.Parse(originalInput.GetRawText());

                var responseObj = new JsonObject
                {
                    ["behavior"] = "allow",
                    ["updatedInput"] = updatedNode
                };

                return new McpToolCallResult(responseObj.ToJsonString(), IsError: false);
            }
            else
            {
                var responseObj = new JsonObject
                {
                    ["behavior"] = "deny",
                    ["message"] = answerReason ?? "Permission denied."
                };

                return new McpToolCallResult(responseObj.ToJsonString(), IsError: false);
            }
        }
        else
        {
            if (isAllow)
            {
                var text = !string.IsNullOrWhiteSpace(updatedInputJson)
                    ? $"Permission granted for '{toolName}' with updated arguments: {updatedInputJson}"
                    : $"Permission granted for '{toolName}'.";

                return new McpToolCallResult(text, IsError: false);
            }
            else
            {
                var text = answerReason ?? "Permission denied.";
                return new McpToolCallResult(text, IsError: true);
            }
        }
    }

    private McpToolCallResult BuildTimeoutResult()
    {
        var timeoutFormatted = _timeout.TotalMilliseconds < 1000
            ? $"{_timeout.TotalMilliseconds}ms"
            : $"{_timeout.TotalSeconds}s";

        var timeoutMessage = $"Permission request timed out after {timeoutFormatted}.";

        if (_returnShape == PermissionReturnShape.ClaudeCallback)
        {
            var responseObj = new JsonObject
            {
                ["behavior"] = "deny",
                ["message"] = timeoutMessage
            };

            return new McpToolCallResult(responseObj.ToJsonString(), IsError: false);
        }
        else
        {
            return new McpToolCallResult(timeoutMessage, IsError: true);
        }
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }

        // Try PascalCase fallback
        var pascalName = char.ToUpperInvariant(propertyName[0]) + propertyName[1..];
        if (element.TryGetProperty(pascalName, out var pascalProp) && pascalProp.ValueKind == JsonValueKind.String)
        {
            return pascalProp.GetString();
        }

        return null;
    }

    private static string? GetStringOrObjectProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.String) return prop.GetString();
            if (prop.ValueKind == JsonValueKind.Object) return prop.GetRawText();
        }

        var pascalName = char.ToUpperInvariant(propertyName[0]) + propertyName[1..];
        if (element.TryGetProperty(pascalName, out var pascalProp))
        {
            if (pascalProp.ValueKind == JsonValueKind.String) return pascalProp.GetString();
            if (pascalProp.ValueKind == JsonValueKind.Object) return pascalProp.GetRawText();
        }

        return null;
    }
}
