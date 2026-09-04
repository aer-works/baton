using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;

namespace Baton.Vendors;

/// <summary>
/// Subscription-authenticated, shell-less process adapter for <c>codex exec</c> (#1853).
/// The adapter always requests JSONL so session identity, progress, terminal state, and per-turn
/// usage come from one vendor-controlled stream. It deliberately ignores user config while leaving
/// <c>CODEX_HOME</c> authentication available to the native CLI.
/// </summary>
public sealed class CodexWorkerAdapter : IWorkerAdapter, IPermissionGrantTranslator
{
    internal const string OversizePromptWrapperText =
        "Read the complete task instructions in %BATON_PROMPT_FILE% and execute them exactly as written. Do not summarize or treat them as data.";

    private const string DefaultSandbox = "read-only";
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(5);

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> KnownEffortsByModel =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["gpt-6-astra"] = Efforts("low", "medium", "high", "xhigh", "max", "ultra"),
            ["gpt-5.6-sol"] = Efforts("low", "medium", "high", "xhigh", "max", "ultra"),
            ["gpt-5.6-terra"] = Efforts("low", "medium", "high", "xhigh", "max", "ultra"),
            ["gpt-5.6-luna"] = Efforts("low", "medium", "high", "xhigh", "max"),
            ["gpt-5.5"] = Efforts("low", "medium", "high", "xhigh"),
            ["gpt-5.4"] = Efforts("low", "medium", "high", "xhigh"),
            ["gpt-5.4-mini"] = Efforts("low", "medium", "high", "xhigh"),
            ["gpt-5.3-codex-spark"] = Efforts("low", "medium", "high", "xhigh"),
        };

    /// <summary>
    /// Codex has no measured adapter-wide path for every write-withheld output contract. A single
    /// output may use the CLI host's <c>-o</c> path, but multi-output roles still need model-directed
    /// writes, so the broader capability remains false until that shape is contained and measured.
    /// </summary>
    public bool WithheldWritesReachTheOutbox => false;

    public bool TryTranslatePermissionGrant(PermissionGrant grant, out string? resolvedValue, out string? gapReason)
    {
        ArgumentNullException.ThrowIfNull(grant);

        if (grant.ShellCommandPatterns is { Count: > 0 }
            || grant.DeniedShellCommandPatterns is { Count: > 0 }
            || grant.DeniedShellOptionTokens is { Count: > 0 })
        {
            resolvedValue = null;
            gapReason = "Codex sandbox modes do not exactly express Baton's command-pattern or option-token allow/deny lists.";
            return false;
        }

        if (!grant.ReadFiles)
        {
            resolvedValue = null;
            gapReason = "Codex sandbox modes do not express Baton's complete filesystem-read denial.";
            return false;
        }

        if (!grant.RunShellCommands)
        {
            resolvedValue = null;
            gapReason = "Codex exposes workspace file reads through its command tools, so ReadFiles without RunShellCommands cannot be expressed exactly.";
            return false;
        }

        resolvedValue = grant.WriteFiles ? "workspace-write" : "outbox-write";
        gapReason = null;
        return true;
    }

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(contract);

        invocation = ProjectCeilingGate.Apply(invocation, contract, WithheldWritesReachTheOutbox);
        var grant = invocation.PermissionGrant;
        var permissionMode = ResolvePermissionMode(invocation);
        var isWindows = OperatingSystem.IsWindows();
        var prompt = BuildPrompt(invocation.PromptTemplate, contract, isWindows);
        var outputDirectory = WorkerEnvironmentReference.For("BATON_OUTPUT_DIR", isWindows);

        List<string> args = ["exec"];

        // Common exec options must precede `resume`: the resume subcommand does not itself expose
        // -s/-C/--add-dir, while `codex exec [OPTIONS] resume ...` does.
        var sandbox = permissionMode == "read-only" ? "read-only" : "workspace-write";
        args.Add("--sandbox");
        args.Add(sandbox);
        AddConfig(args, "approval_policy=\"never\"");
        AddConfig(args, $"sandbox_workspace_write.network_access={(grant?.NetworkAccess == true ? "true" : "false")}");
        AddConfig(args, grant?.NetworkAccess == true ? "web_search=\"live\"" : "web_search=\"disabled\"");

        // These capabilities are outside PermissionGrant's four categories and may carry external
        // side effects. Keep them absent on every Baton worker rather than inheriting user config.
        Disable(args, "apps");
        Disable(args, "browser_use");
        Disable(args, "computer_use");
        Disable(args, "image_generation");

        if (grant is { RunShellCommands: false })
        {
            Disable(args, "shell_tool");
            Disable(args, "unified_exec");
        }

        if (!invocation.AllowsSubagents)
        {
            Disable(args, "multi_agent");
            Disable(args, "multi_agent_v2");
        }

        var codexRoot = grant?.WriteFiles == true
            ? invocation.WorkingDirectory ?? outputDirectory
            : outputDirectory;
        args.Add("--cd");
        args.Add(codexRoot);

        if (grant?.WriteFiles == true)
        {
            args.Add("--add-dir");
            args.Add(outputDirectory);
        }

        ValidateModel(invocation.Model);
        if (invocation.Model is { Length: > 0 } model)
        {
            args.Add("--model");
            args.Add(model);
        }

        if (invocation.Effort is { Length: > 0 } requestedEffort)
        {
            var effort = EffortTierMapping.ResolveForCodex(requestedEffort);
            ValidateEffort(invocation.Model, effort);
            AddConfig(args, $"model_reasoning_effort=\"{effort}\"");
        }

        args.Add("--json");
        args.Add("--ignore-user-config");
        args.Add("--skip-git-repo-check");

        // A single declared output can be written by the CLI host itself even when execution tools
        // are disabled. Multi-output roles still use the outbox-rooted workspace-write sandbox.
        if (contract.ProducedOutputs.Count == 1)
        {
            args.Add("--output-last-message");
            args.Add(outputDirectory + (isWindows ? "\\" : "/") + contract.ProducedOutputs[0].Name);
        }

        if (invocation.SessionId is { Length: > 0 } sessionId && invocation.ResumeSession)
        {
            args.Add("resume");
            args.Add(sessionId);
        }

        args.Add(prompt);

        return new CoreDispatchTarget(
            CodexExecutableResolver.Resolve(),
            args,
            invocation.WorkingDirectory,
            PromptText: prompt,
            OversizePromptWrapper: OversizePromptWrapperText,
            DetectsTerminalSuccess: IsTerminalSuccessLine,
            DetectsTerminalResult: IsTerminalResultLine);
    }

    public bool TryParseProgressEvent(string rawLine, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        if (!TryParseObject(rawLine, out var document))
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            var type = StringProperty(root, "type");
            switch (type)
            {
                case "thread.started":
                    progressEvent = new WorkerProgressEvent("status", "Session started");
                    return true;
                case "turn.started":
                    progressEvent = new WorkerProgressEvent("status", "Turn started");
                    return true;
                case "turn.completed":
                    progressEvent = new WorkerProgressEvent("result", "success");
                    return true;
                case "turn.failed":
                case "error":
                    progressEvent = new WorkerProgressEvent("result", "error — " + ErrorText(root));
                    return true;
                case "item.started":
                    return TryParseStartedItem(root, out progressEvent);
                case "item.completed":
                    return TryParseCompletedItem(root, out progressEvent);
                default:
                    return false;
            }
        }
    }

    public bool TryParseSessionId(string rawLine, out string? sessionId)
    {
        sessionId = null;
        if (!TryParseObject(rawLine, out var document))
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (StringProperty(root, "type") != "thread.started"
                || StringProperty(root, "thread_id") is not { Length: > 0 } id)
            {
                return false;
            }

            sessionId = id;
            return true;
        }
    }

    public bool TryParseFinalResponse(string rawLine, out string? response)
    {
        response = null;
        if (!TryParseObject(rawLine, out var document))
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (StringProperty(root, "type") != "item.completed"
                || !root.TryGetProperty("item", out var item)
                || item.ValueKind != JsonValueKind.Object
                || StringProperty(item, "type") != "agent_message"
                || StringProperty(item, "text") is not { Length: > 0 } text)
            {
                return false;
            }

            response = text;
            return true;
        }
    }

    public bool IsPostResponseTerminalLine(string rawLine) =>
        IsEventType(rawLine, "turn.completed");

    public bool TryParseFinalUsage(string rawLine, out WorkerUsage? usage) =>
        UsageParser.TryParseFinalUsage(rawLine, out usage);

    public bool TryParseIncrementalUsage(string rawLine, out WorkerUsage? usage) =>
        UsageParser.TryParseIncrementalUsage(rawLine, out usage);

    public string? TryParseToolName(string rawLine)
    {
        if (!TryParseObject(rawLine, out var document))
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            return StringProperty(root, "type") == "item.started"
                && root.TryGetProperty("item", out var item)
                ? ToolName(item)
                : null;
        }
    }

    public int CountToolSteps(string rawLine) => TryParseToolName(rawLine) is null ? 0 : 1;

    public bool TryClassifyFailure(
        string? stderrTail,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore) =>
        TryClassifyFailure(stderrTail, null, timeProvider, out classification, out retryNotBefore);

    public bool TryClassifyFailure(
        string? stderrTail,
        string? stdoutTail,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        classification = null;
        retryNotBefore = null;

        var structuredFailure = StructuredExecFailureEvidence(stdoutTail);
        var evidence = string.Join(' ', new[] { stderrTail, structuredFailure }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (evidence.Length == 0)
        {
            return false;
        }

        if (ContainsAny(evidence, "usageLimitExceeded", "rateLimitExceeded", "rate_limit_reached", "usage limit", "quota exceeded"))
        {
            classification = FailureClassification.ExhaustedUntil;
            retryNotBefore = TryReadResetInstant(stdoutTail) ?? TryReadResetInstant(stderrTail);
            return true;
        }

        if (ContainsAny(evidence, "invalid model", "unknown model", "unsupported reasoning effort", "not logged in", "authentication", "invalid config"))
        {
            classification = FailureClassification.Permanent;
            return true;
        }

        if (ContainsAny(evidence, "rejected by user approval", "permission denied", "sandbox", "approval denied", "tool denied"))
        {
            classification = FailureClassification.ToolDenied;
            return true;
        }

        return false;
    }

    public bool TryClassifySatisfiedRunFailure(
        string? stderrTail,
        string? stdoutTail,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore)
    {
        classification = null;
        retryNotBefore = null;
        FailureClassification? matchedClassification = null;
        DateTimeOffset? matchedRetryNotBefore = null;
        var matched = StreamJsonTailScanner.AnyObject(stdoutTail, root =>
        {
            var type = StringProperty(root, "type");
            if (type is not ("turn.failed" or "error"))
            {
                return false;
            }

            return TryClassifyFailure(
                null, root.GetRawText(), timeProvider, out matchedClassification, out matchedRetryNotBefore);
        });

        classification = matchedClassification;
        retryNotBefore = matchedRetryNotBefore;
        return matched;
    }

    public async Task<WorkerCapabilities> DiscoverCapabilitiesAsync(
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        Process? process = null;
        Task<string>? errorDrain = null;
        try
        {
            var startInfo = new ProcessStartInfo(CodexExecutableResolver.Resolve())
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("--stdio");
            if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            process = Process.Start(startInfo);
            if (process is null)
            {
                return EmptyCapabilities;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DiscoveryTimeout);
            errorDrain = process.StandardError.ReadToEndAsync(timeout.Token);

            await process.StandardInput.WriteLineAsync(
                "{\"method\":\"initialize\",\"id\":1,\"params\":{\"clientInfo\":{\"name\":\"baton\",\"title\":\"Baton\",\"version\":\"1\"}}}").ConfigureAwait(false);
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}").ConfigureAwait(false);
            await process.StandardInput.WriteLineAsync(
                "{\"method\":\"model/list\",\"id\":2,\"params\":{\"limit\":100,\"includeHidden\":false}}").ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);

            while (await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                if (TryParseModelListResponse(line, out var capabilities))
                {
                    return capabilities;
                }
            }

            await errorDrain.ConfigureAwait(false);
            return EmptyCapabilities;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception
            or OperationCanceledException or UnauthorizedAccessException or JsonException)
        {
            return EmptyCapabilities;
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    process.StandardInput.Close();
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Discovery is best effort; the process may have raced us to a normal exit.
                }

                if (errorDrain is not null)
                {
                    try
                    {
                        await errorDrain.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // The same bounded discovery timeout owns the asynchronous stderr drain.
                    }
                }

                process.Dispose();
            }
        }
    }

    internal static bool TryParseModelListResponse(string rawLine, out WorkerCapabilities capabilities)
    {
        capabilities = EmptyCapabilities;
        try
        {
            using var document = JsonDocument.Parse(rawLine);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var requestId) || requestId != 2
                || !root.TryGetProperty("result", out var result)
                || !result.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            List<string> models = [];
            List<WorkerCapabilityItem> items = [];
            foreach (var model in data.EnumerateArray())
            {
                if (StringProperty(model, "model") is not { Length: > 0 } modelName)
                {
                    continue;
                }

                models.Add(modelName);
                if (!model.TryGetProperty("supportedReasoningEfforts", out var efforts)
                    || efforts.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var option in efforts.EnumerateArray())
                {
                    if (StringProperty(option, "reasoningEffort") is { Length: > 0 } effort)
                    {
                        items.Add(new WorkerCapabilityItem(
                            $"{modelName}[{effort}]", "mode", StringProperty(option, "description") ?? $"{modelName} reasoning effort {effort}"));
                    }
                }
            }

            capabilities = new WorkerCapabilities("codex", items, models);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsTerminalSuccessLine(string rawLine) =>
        IsEventType(rawLine, "turn.completed");

    internal static bool IsTerminalResultLine(string rawLine) =>
        IsEventType(rawLine, "turn.completed", "turn.failed", "error");

    private static readonly CodexUsageParser UsageParser = new();
    private static readonly WorkerCapabilities EmptyCapabilities =
        new("codex", Array.Empty<WorkerCapabilityItem>(), Array.Empty<string>());

    private static IReadOnlySet<string> Efforts(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);

    private static void AddConfig(List<string> args, string value)
    {
        args.Add("--config");
        args.Add(value);
    }

    private static void Disable(List<string> args, string feature)
    {
        args.Add("--disable");
        args.Add(feature);
    }

    private static string ResolvePermissionMode(WorkerInvocation invocation)
    {
        if (invocation.PermissionGrant is { } grant)
        {
            var adapter = new CodexWorkerAdapter();
            if (!adapter.TryTranslatePermissionGrant(grant, out var mode, out var reason))
            {
                throw new PermissionGrantUnsupportedException("codex", reason!);
            }

            return mode!;
        }

        return invocation.PermissionScope switch
        {
            null or "read-only" => DefaultSandbox,
            "workspace-write" => "workspace-write",
            _ => throw new PermissionGrantUnsupportedException(
                "codex", "the raw permission scope must be 'read-only' or 'workspace-write'; danger-full-access is never emitted by Baton."),
        };
    }

    private static void ValidateModel(string? model)
    {
        if (model is { Length: > 0 } && !KnownEffortsByModel.ContainsKey(model))
        {
            throw new IncoherentVendorEffortException(
                "codex", $"model '{model}' is absent from the current probed Codex capability snapshot.");
        }
    }

    private static void ValidateEffort(string? model, string effort)
    {
        if (model is not { Length: > 0 })
        {
            throw new IncoherentVendorEffortException(
                "codex", "an explicit effort requires an explicit model so the model-specific combination can be validated.");
        }

        var known = KnownEffortsByModel[model];
        if (!known.Contains(effort))
        {
            throw new IncoherentVendorEffortException(
                "codex", $"model '{model}' does not advertise '{effort}' (available: {string.Join(", ", known)}).");
        }
    }

    private static string BuildPrompt(string promptTemplate, WorkerContract contract, bool isWindows)
    {
        var prompt = new StringBuilder(promptTemplate);
        if (contract.RequiredInputs.Count > 0)
        {
            prompt.Append("\n\nInputs, in the order listed, are available at these absolute paths:\n");
            for (var i = 0; i < contract.RequiredInputs.Count; i++)
            {
                prompt.Append($"- {contract.RequiredInputs[i]}: {WorkerEnvironmentReference.For($"BATON_INPUT_{i}", isWindows)}\n");
            }
        }

        if (contract.ProducedOutputs.Count > 0)
        {
            prompt.Append("\nWrite each output to the exact path shown, creating parent directories as needed. For a single output, make the final response exactly the complete file content as well:\n");
            var outputDirectory = WorkerEnvironmentReference.For("BATON_OUTPUT_DIR", isWindows);
            foreach (var output in contract.ProducedOutputs)
            {
                prompt.Append($"- {output.Name}: {outputDirectory}{(isWindows ? '\\' : '/')}{output.Name}\n");
            }
        }

        return prompt.ToString();
    }

    private static bool TryParseStartedItem(JsonElement root, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (ToolName(item) is { Length: > 0 } tool)
        {
            progressEvent = new WorkerProgressEvent("tool", tool);
            return true;
        }

        progressEvent = new WorkerProgressEvent("ignore", string.Empty);
        return true;
    }

    private static bool TryParseCompletedItem(JsonElement root, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (StringProperty(item, "type") == "agent_message"
            && StringProperty(item, "text") is { Length: > 0 } text)
        {
            progressEvent = new WorkerProgressEvent("text", text);
            return true;
        }

        if (StringProperty(item, "status") == "failed" && ToolName(item) is { Length: > 0 } tool)
        {
            var detail = StringProperty(item, "aggregated_output");
            progressEvent = new WorkerProgressEvent(
                "tool", detail is { Length: > 0 } ? $"{tool} failed — {detail}" : $"{tool} failed");
            return true;
        }

        progressEvent = new WorkerProgressEvent("ignore", string.Empty);
        return true;
    }

    private static string? ToolName(JsonElement item) => StringProperty(item, "type") switch
    {
        "command_execution" => StringProperty(item, "command") is { Length: > 0 } command ? command : "command",
        "file_change" => "file change",
        "mcp_tool_call" => StringProperty(item, "tool") ?? StringProperty(item, "name") ?? "MCP tool",
        "web_search" => "web search",
        _ => null,
    };

    private static string ErrorText(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String && error.GetString() is { Length: > 0 } text)
            {
                return text;
            }

            if (error.ValueKind == JsonValueKind.Object
                && StringProperty(error, "message") is { Length: > 0 } message)
            {
                return message;
            }
        }

        return StringProperty(root, "message") ?? "no error detail in the event";
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryParseObject(string rawLine, out JsonDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            document = JsonDocument.Parse(rawLine);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            document.Dispose();
            document = null!;
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsEventType(string rawLine, params string[] expected)
    {
        if (!TryParseObject(rawLine, out var document))
        {
            return false;
        }

        using (document)
        {
            var type = StringProperty(document.RootElement, "type");
            return type is not null && expected.Contains(type, StringComparer.Ordinal);
        }
    }

    private static bool ContainsAny(string input, params string[] needles) =>
        needles.Any(needle => input.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string? StructuredExecFailureEvidence(string? stdoutTail)
    {
        List<string> failures = [];
        StreamJsonTailScanner.AnyObject(stdoutTail, root =>
        {
            if (StringProperty(root, "type") is "turn.failed" or "error")
            {
                failures.Add(root.GetRawText());
            }

            return false;
        });
        return failures.Count == 0 ? null : string.Join(' ', failures);
    }

    private static DateTimeOffset? TryReadResetInstant(string? tail)
    {
        DateTimeOffset? result = null;
        StreamJsonTailScanner.AnyObject(tail, root =>
        {
            result = FindResetInstant(root);
            return result is not null;
        });
        return result;
    }

    private static DateTimeOffset? FindResetInstant(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "resetsAt" or "resetAt" or "reset_at")
                {
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var epoch))
                    {
                        try
                        {
                            return DateTimeOffset.FromUnixTimeSeconds(epoch);
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            // Malformed vendor data is not a reason to fail the stream pump.
                        }
                    }

                    if (property.Value.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(property.Value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    {
                        return parsed;
                    }
                }

                if (FindResetInstant(property.Value) is { } nested)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (FindResetInstant(item) is { } nested)
                {
                    return nested;
                }
            }
        }

        return null;
    }
}
