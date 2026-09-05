using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baton.Vendors;

/// <summary>
/// Baton's enforcement boundary for Codex app-server dynamic tools. Codex receives no native shell,
/// file-mutation, MCP, app, browser, or computer tool; every capability it can invoke is defined and
/// executed here from the canonical <see cref="PermissionGrant"/>.
/// </summary>
public sealed class CodexDynamicToolPolicy
{
    internal const string ReadTextTool = "baton_read_text";
    internal const string ListFilesTool = "baton_list_files";
    internal const string SearchTextTool = "baton_search_text";
    internal const string WriteOutputTool = "baton_write_output";
    internal const string WriteTextTool = "baton_write_text";
    internal const string RunCommandTool = "baton_run_command";

    private const int MaxReadCharacters = 200_000;
    private const int MaxListedFiles = 1_000;
    private const int MaxSearchMatches = 500;
    private const int MaxCommandOutputCharacters = 200_000;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    private readonly PermissionGrant _grant;
    private readonly string? _workspaceRoot;
    private readonly string _outputRoot;
    private readonly IReadOnlyList<string> _inputRoots;
    private readonly HashSet<string> _declaredOutputs;

    public CodexDynamicToolPolicy(
        PermissionGrant grant,
        string? workingDirectory,
        string outputDirectory,
        IEnumerable<string> inputPaths,
        IEnumerable<string> producedOutputNames)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(inputPaths);
        ArgumentNullException.ThrowIfNull(producedOutputNames);

        _grant = grant;
        _workspaceRoot = string.IsNullOrWhiteSpace(workingDirectory) ? null : NormalizeRoot(workingDirectory);
        _outputRoot = NormalizeRoot(outputDirectory);
        _inputRoots = inputPaths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath).Distinct(PathComparer).ToArray();
        _declaredOutputs = producedOutputNames.Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(NormalizeRelativeOutput).ToHashSet(PathComparer);
    }

    /// <summary>The exact dynamic-tool declarations supplied on <c>thread/start</c>.</summary>
    public JsonArray BuildToolDefinitions()
    {
        var tools = new JsonArray();
        if (_grant.ReadFiles || _inputRoots.Count > 0)
        {
            tools.Add(Function(ReadTextTool, "Read UTF-8 text from a path allowed by Baton's role grant.",
                StringSchema("path", "Absolute or workspace-relative file path.")));
        }

        if (_grant.ReadFiles)
        {
            tools.Add(Function(ListFilesTool, "List files below a directory allowed by Baton's role grant.",
                StringSchema("path", "Absolute or workspace-relative directory path.")));
            tools.Add(Function(SearchTextTool, "Search allowed UTF-8 text files for a literal string.",
                TwoStringSchema("path", "Directory or file to search.", "query", "Literal text to find.")));
        }

        if (_declaredOutputs.Count > 0)
        {
            var outputSchema = TwoStringSchema("name", "One declared output name.", "content", "Complete UTF-8 file content.");
            ((JsonObject)((JsonObject)outputSchema["properties"]!)["name"]!)["enum"] =
                new JsonArray(_declaredOutputs.Order(PathComparer)
                    .Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
            tools.Add(Function(WriteOutputTool, "Write one exact output declared by the Baton worker contract.", outputSchema));
        }

        if (_grant.WriteFiles)
        {
            tools.Add(Function(WriteTextTool, "Write complete UTF-8 text under Baton's granted workspace root.",
                TwoStringSchema("path", "Absolute or workspace-relative destination.", "content", "Complete UTF-8 file content.")));
        }

        if (_grant.RunShellCommands)
        {
            tools.Add(Function(RunCommandTool, "Run one command line after Baton's canonical command policy approves it.",
                StringSchema("command", "Command line to evaluate and run.")));
        }

        return tools;
    }

    public async Task<CodexDynamicToolResult> ExecuteAsync(
        string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            return toolName switch
            {
                ReadTextTool => ReadText(RequiredString(arguments, "path")),
                ListFilesTool => ListFiles(RequiredString(arguments, "path")),
                SearchTextTool => SearchText(
                    RequiredString(arguments, "path"), RequiredString(arguments, "query")),
                WriteOutputTool => WriteOutput(
                    RequiredString(arguments, "name"), RequiredString(arguments, "content")),
                WriteTextTool => WriteText(
                    RequiredString(arguments, "path"), RequiredString(arguments, "content")),
                RunCommandTool => await RunCommandAsync(
                    RequiredString(arguments, "command"), cancellationToken).ConfigureAwait(false),
                _ => CodexDynamicToolResult.Denied($"Tool '{toolName}' is not present in this Baton role grant."),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException or System.Security.SecurityException)
        {
            return CodexDynamicToolResult.Denied(ex.Message);
        }
    }

    private CodexDynamicToolResult ReadText(string requestedPath)
    {
        if (!_grant.ReadFiles && _inputRoots.Count == 0)
        {
            return CodexDynamicToolResult.Denied("This Baton role does not grant file reads.");
        }

        var path = ResolveAllowedRead(requestedPath);
        if (!File.Exists(path))
        {
            return CodexDynamicToolResult.Denied($"File '{requestedPath}' does not exist.");
        }

        EnsureNoReparsePoint(path);
        var text = File.ReadAllText(path, Encoding.UTF8);
        if (text.Length > MaxReadCharacters)
        {
            text = text[..MaxReadCharacters] + $"\n[truncated by Baton at {MaxReadCharacters} characters]";
        }
        return CodexDynamicToolResult.Allowed(text);
    }

    private CodexDynamicToolResult ListFiles(string requestedPath)
    {
        if (!_grant.ReadFiles)
        {
            return CodexDynamicToolResult.Denied("This Baton role does not grant workspace file listing.");
        }

        var path = ResolveWithinWorkspace(requestedPath);
        if (!Directory.Exists(path))
        {
            return CodexDynamicToolResult.Denied($"Directory '{requestedPath}' does not exist.");
        }

        EnsureNoReparsePoint(path);
        var options = SafeEnumerationOptions();
        var files = EnumerateContentFiles(path, options).Take(MaxListedFiles + 1).ToArray();
        bool truncated = files.Length > MaxListedFiles;
        var rendered = files.Take(MaxListedFiles)
            .Select(file => Path.GetRelativePath(_workspaceRoot!, file).Replace('\\', '/'));
        return CodexDynamicToolResult.Allowed(
            string.Join('\n', rendered) + (truncated ? $"\n[truncated by Baton at {MaxListedFiles} files]" : string.Empty));
    }

    private CodexDynamicToolResult SearchText(string requestedPath, string query)
    {
        if (!_grant.ReadFiles)
        {
            return CodexDynamicToolResult.Denied("This Baton role does not grant workspace text search.");
        }
        if (query.Length == 0)
        {
            return CodexDynamicToolResult.Denied("Search query must not be empty.");
        }

        var path = ResolveWithinWorkspace(requestedPath);
        EnsureNoReparsePoint(path);
        IEnumerable<string> files = File.Exists(path)
            ? [path]
            : Directory.Exists(path)
                ? EnumerateContentFiles(path, SafeEnumerationOptions())
                : throw new ArgumentException($"Search path '{requestedPath}' does not exist.");

        List<string> matches = [];
        foreach (var file in files)
        {
            if (matches.Count >= MaxSearchMatches)
            {
                break;
            }
            try
            {
                int lineNumber = 0;
                foreach (var line in File.ReadLines(file, Encoding.UTF8))
                {
                    lineNumber++;
                    if (line.Contains(query, StringComparison.Ordinal))
                    {
                        matches.Add($"{Path.GetRelativePath(_workspaceRoot!, file).Replace('\\', '/')}:{lineNumber}:{line}");
                        if (matches.Count >= MaxSearchMatches)
                        {
                            break;
                        }
                    }
                }
            }
            catch (DecoderFallbackException)
            {
                // A binary or non-UTF-8 file is not a match, not a reason to abort the whole search.
            }
        }

        return CodexDynamicToolResult.Allowed(
            string.Join('\n', matches) + (matches.Count >= MaxSearchMatches
                ? $"\n[truncated by Baton at {MaxSearchMatches} matches]" : string.Empty));
    }

    private CodexDynamicToolResult WriteOutput(string outputName, string content)
    {
        var normalized = NormalizeRelativeOutput(outputName);
        if (!_declaredOutputs.Contains(normalized))
        {
            return CodexDynamicToolResult.Denied($"'{outputName}' is not a declared output for this Baton worker.");
        }

        var path = ResolveWithinRoot(_outputRoot, normalized);
        WriteFile(path, content);
        return CodexDynamicToolResult.Allowed($"Wrote declared output '{normalized}'.");
    }

    private CodexDynamicToolResult WriteText(string requestedPath, string content)
    {
        if (!_grant.WriteFiles)
        {
            return CodexDynamicToolResult.Denied("This Baton role does not grant workspace writes.");
        }

        var path = ResolveWithinWorkspace(requestedPath);
        WriteFile(path, content);
        return CodexDynamicToolResult.Allowed($"Wrote '{path}'.");
    }

    private async Task<CodexDynamicToolResult> RunCommandAsync(
        string commandLine, CancellationToken cancellationToken)
    {
        if (!_grant.RunShellCommands)
        {
            return CodexDynamicToolResult.Denied("This Baton role does not grant shell commands.");
        }

        var decision = ShellCommandPatternMatcher.EvaluateChainedCommand(
            commandLine, _grant.ShellCommandPatterns, _grant.DeniedShellCommandPatterns);
        if (!decision.IsAllowed)
        {
            return CodexDynamicToolResult.Denied(decision.Reason ?? "Baton denied the command line.");
        }
        if (ShellCommandPatternMatcher.IsDeniedByOptionToken(commandLine, _grant.DeniedShellOptionTokens))
        {
            return CodexDynamicToolResult.Denied("The command contains an option token denied by this Baton role.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe" : "/bin/sh",
            WorkingDirectory = _workspaceRoot ?? _outputRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(commandLine);
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(commandLine);
        }

        using var process = Process.Start(startInfo)
            ?? throw new IOException("Baton could not start the granted command.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            await DrainAfterKillAsync(stdout, stderr).ConfigureAwait(false);
            return CodexDynamicToolResult.Denied($"Command exceeded Baton's {CommandTimeout.TotalMinutes:0}-minute tool limit.");
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            await DrainAfterKillAsync(stdout, stderr).ConfigureAwait(false);
            throw;
        }

        var combined = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
        if (combined.Length > MaxCommandOutputCharacters)
        {
            combined = combined[^MaxCommandOutputCharacters..] +
                $"\n[leading output truncated by Baton at {MaxCommandOutputCharacters} characters]";
        }
        return process.ExitCode == 0
            ? CodexDynamicToolResult.Allowed(combined)
            : CodexDynamicToolResult.Denied($"Command exited {process.ExitCode}.\n{combined}");
    }

    private string ResolveAllowedRead(string requestedPath)
    {
        var candidate = ResolveCandidate(requestedPath);
        if (_grant.ReadFiles && _workspaceRoot is not null && IsWithin(candidate, _workspaceRoot))
        {
            return candidate;
        }
        if (IsWithin(candidate, _outputRoot))
        {
            return candidate;
        }
        if (_inputRoots.Any(input => File.Exists(input)
                ? candidate.Equals(input, PathComparison)
                : IsWithin(candidate, NormalizeRoot(input))))
        {
            return candidate;
        }
        throw new UnauthorizedAccessException($"Path '{requestedPath}' is outside this Baton's readable roots.");
    }

    private string ResolveWithinWorkspace(string requestedPath)
    {
        if (_workspaceRoot is null)
        {
            throw new UnauthorizedAccessException("This Baton worker has no workspace root.");
        }
        var candidate = ResolveCandidate(requestedPath);
        if (!IsWithin(candidate, _workspaceRoot))
        {
            throw new UnauthorizedAccessException($"Path '{requestedPath}' is outside this Baton's workspace root.");
        }
        return candidate;
    }

    private string ResolveCandidate(string requestedPath) => Path.GetFullPath(
        Path.IsPathRooted(requestedPath) ? requestedPath : Path.Combine(_workspaceRoot ?? _outputRoot, requestedPath));

    private static string ResolveWithinRoot(string root, string relativePath)
    {
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsWithin(candidate, root))
        {
            throw new UnauthorizedAccessException($"Path '{relativePath}' escapes its Baton root.");
        }
        return candidate;
    }

    private static void WriteFile(string path, string content)
    {
        EnsureNoReparsePoint(path, includeLeaf: false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Re-check the complete destination after creating parents. An existing leaf can itself be
        // a symlink; checking only its parents would let File.WriteAllText follow it outside the
        // granted root.
        EnsureNoReparsePoint(path);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The subprocess raced cancellation to a natural exit.
        }
    }

    private static async Task DrainAfterKillAsync(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The read tasks share the cancelled timeout token; the process tree is already gone.
        }
    }

    private static void EnsureNoReparsePoint(string path, bool includeLeaf = true)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            if (!includeLeaf && i == parts.Length - 1)
            {
                break;
            }
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException($"Path '{path}' crosses a symbolic link or reparse point.");
            }
        }
    }

    private static EnumerationOptions SafeEnumerationOptions() => new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    private static IEnumerable<string> EnumerateContentFiles(string path, EnumerationOptions options) =>
        Directory.EnumerateFiles(path, "*", options)
            .Where(file => !Path.GetRelativePath(path, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase)));

    private static JsonObject Function(string name, string description, JsonObject inputSchema) => new()
    {
        ["type"] = "function",
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema,
    };

    private static JsonObject StringSchema(string name, string description) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            [name] = new JsonObject { ["type"] = "string", ["description"] = description },
        },
        ["required"] = new JsonArray(name),
        ["additionalProperties"] = false,
    };

    private static JsonObject TwoStringSchema(
        string firstName, string firstDescription, string secondName, string secondDescription) => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [firstName] = new JsonObject { ["type"] = "string", ["description"] = firstDescription },
                [secondName] = new JsonObject { ["type"] = "string", ["description"] = secondDescription },
            },
            ["required"] = new JsonArray(firstName, secondName),
            ["additionalProperties"] = false,
        };

    private static string RequiredString(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(value.GetString()))
        {
            throw new ArgumentException($"Dynamic tool argument '{name}' must be a non-empty string.");
        }
        return value.GetString()!;
    }

    private static string NormalizeRoot(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string NormalizeRelativeOutput(string name)
    {
        if (Path.IsPathRooted(name))
        {
            throw new ArgumentException($"Declared output '{name}' must be relative.");
        }
        var normalized = name.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (normalized.Split(Path.DirectorySeparatorChar).Any(part => part is "" or "." or ".."))
        {
            throw new ArgumentException($"Declared output '{name}' is not a safe relative path.");
        }
        return normalized;
    }

    private static bool IsWithin(string candidate, string root) =>
        candidate.Equals(root, PathComparison)
        || candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

public sealed record CodexDynamicToolResult(bool Success, string Text)
{
    public static CodexDynamicToolResult Allowed(string text) => new(true, text);
    public static CodexDynamicToolResult Denied(string text) => new(false, text);
}
