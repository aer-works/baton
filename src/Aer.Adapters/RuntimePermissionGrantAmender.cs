using System.Text.Json;

namespace Aer.Adapters;

/// <summary>
/// The persistence half of decision 0022's permission ladder (M-Phase-6 #390): amends a room's
/// chat-worker binding <see cref="PermissionGrant"/> for the rungs that persist, so the next turn's
/// grant build (<c>InteractiveSessionMaterializer</c>'s per-turn read of the same bindings file) picks
/// it up automatically — the same enforcement path an interactive turn already uses, not a parallel one.
/// </summary>
/// <remarks>
/// Lives in <c>Aer.Adapters</c> rather than beside <c>RoomMutationInterface</c> in <c>Aer.Flow</c>
/// because it reads/writes <c>bindings.json</c> through <see cref="WorkerBindingConfigParser"/>/
/// <see cref="WorkerBindingConfigWriter"/>, both adapter-layer types — <c>Aer.Flow</c> does not (and
/// per Architecture Rule 2, must not) depend on <c>Aer.Adapters</c>. Called by the daemon's
/// <c>/api/rooms/permissions/answer</c> handler as a collaborator alongside
/// <c>RoomMutationInterface.AnswerPermissionAsync</c>, which keeps recording the
/// <c>RuntimePermissionAnswered</c> event unchanged.
/// </remarks>
public static class RuntimePermissionGrantAmender
{
    /// <summary>
    /// The claude/agy tool names this can derive a shell command pattern from. Any other
    /// <paramref name="toolName"/> on a shell-scoping rung persists nothing rather than guessing —
    /// SECURITY-SENSITIVE: a rung must never persist wider than what was actually asked.
    /// </summary>
    private static readonly string[] ShellToolNames = ["Bash", "run_command"];

    /// <summary>
    /// Amends the named worker's <see cref="PermissionGrant"/> in <paramref name="roomDirectoryPath"/>'s
    /// <c>bindings.json</c> for <paramref name="decisionKind"/>, per 0022's rung mapping. The
    /// <see cref="PermissionAmendOutcome"/> distinguishes a real persist from a benign no-op and from a
    /// persisting rung that could not be honored (which applies once only — the caller must surface it).
    /// </summary>
    /// <param name="decisionKind">One of the <see cref="PermissionDecisionKind"/> constants.</param>
    /// <param name="toolName">The originally-asked tool name (e.g. <c>"Bash"</c>), from the room's <c>RuntimePermissionAsked</c> event.</param>
    /// <param name="toolInputJson">The originally-asked tool input JSON, from the same event.</param>
    public static async Task<PermissionAmendOutcome> AmendAsync(
        string roomDirectoryPath,
        string workerName,
        string decisionKind,
        string toolName,
        string toolInputJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(workerName);
        ArgumentException.ThrowIfNullOrEmpty(decisionKind);
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(toolInputJson);

        // AllowOnce, Deny: nothing persists by design (0022 §3 / the DecisionKind mapping table).
        // AllowCommandAnyRoom is the ladder's cross-room rung, and it is HELD, not built here: 0004's
        // scopes are project ∩ room ∩ step with no cross-room scope, and its likely eventual home is a
        // project-scoped grant (0034 keys AER-side state by normalized project path), not a per-room
        // binding. Persisting a per-room approximation would misrepresent its scope, so it no-ops to
        // AllowOnce behavior. The hold is recorded as an amendment to decision 0022.
        // DenyAlways (the standing "never" rung) DOES persist: it adds the asked command's family to
        // DeniedShellCommandPatterns, which is enforced next turn on both vendors -- claude via
        // --disallowedTools Bash(pattern) (BuildDisallowedTools), agy via its PreToolUse hook's IsDenied
        // check (deny-beats-allow). Deny beats a wider later allow, so a closed "no" is not reopened.
        if (decisionKind is not (PermissionDecisionKind.AllowCommandInRoom
            or PermissionDecisionKind.AllowRoom
            or PermissionDecisionKind.DenyAlways))
        {
            return PermissionAmendOutcome.NoChangeNeeded;
        }

        var bindingsFilePath = Path.Combine(roomDirectoryPath, "bindings.json");
        if (!File.Exists(bindingsFilePath))
        {
            return PermissionAmendOutcome.CouldNotPersist;
        }

        var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(bindingsFilePath, cancellationToken)
            .ConfigureAwait(false);
        if (!bindings.TryGetValue(workerName, out var entry))
        {
            return PermissionAmendOutcome.CouldNotPersist;
        }

        var existingGrant = entry.PermissionGrant ?? new PermissionGrant();
        var outcome = TryAmend(existingGrant, decisionKind, toolName, toolInputJson, out var amendedGrant);
        if (outcome != PermissionAmendOutcome.Persisted)
        {
            return outcome;
        }

        var newBindings = new Dictionary<string, WorkerBindingConfigEntry>(bindings)
        {
            [workerName] = entry with { PermissionGrant = amendedGrant }
        };

        await WorkerBindingConfigWriter.SaveToFileAsync(newBindings, bindingsFilePath, cancellationToken)
            .ConfigureAwait(false);
        return PermissionAmendOutcome.Persisted;
    }

    private static PermissionAmendOutcome TryAmend(
        PermissionGrant existingGrant, string decisionKind, string toolName, string toolInputJson,
        out PermissionGrant amendedGrant)
    {
        amendedGrant = existingGrant;

        if (decisionKind == PermissionDecisionKind.AllowRoom)
        {
            if (existingGrant.RunShellCommands && (existingGrant.ShellCommandPatterns?.Count ?? 0) == 0)
            {
                return PermissionAmendOutcome.NoChangeNeeded; // already unscoped
            }

            // Unscoped for the room (04:82-94): RunShellCommands=true with an EMPTY pattern list, not
            // merely true -- PermissionGrant treats a non-empty ShellCommandPatterns as still scoped
            // regardless of the boolean, so leaving a prior scoped list in place would silently keep
            // the room narrower than what was just granted.
            amendedGrant = existingGrant with { RunShellCommands = true, ShellCommandPatterns = [] };
            return PermissionAmendOutcome.Persisted;
        }

        var pattern = DeriveShellCommandPattern(toolName, toolInputJson);
        if (pattern is null)
        {
            // Cannot derive a scoped pattern from this ask -- fail closed and persist nothing rather
            // than guess at what "this command" refers to. CouldNotPersist, not NoChangeNeeded: the
            // operator asked for a standing scoped grant and is getting once-only, which must surface.
            return PermissionAmendOutcome.CouldNotPersist;
        }

        if (decisionKind == PermissionDecisionKind.AllowCommandInRoom)
        {
            var existing = existingGrant.ShellCommandPatterns ?? [];
            if (existing.Contains(pattern, StringComparer.Ordinal))
            {
                return PermissionAmendOutcome.NoChangeNeeded; // family already granted for the room
            }

            amendedGrant = existingGrant with
            {
                RunShellCommands = true,
                ShellCommandPatterns = [.. existing, pattern],
            };
            return PermissionAmendOutcome.Persisted;
        }

        // DenyAlways (0022's standing "never" rung): the family joins the subtractive
        // DeniedShellCommandPatterns list, which deny-beats-allow on both vendors next turn. Note it does
        // NOT touch RunShellCommands -- a deny is not an implicit grant of the shell, and a match here is
        // refused regardless of whether the shell is otherwise granted (PermissionGrant's own contract).
        var existingDenied = existingGrant.DeniedShellCommandPatterns ?? [];
        if (existingDenied.Contains(pattern, StringComparer.Ordinal))
        {
            return PermissionAmendOutcome.NoChangeNeeded; // family already denied
        }

        amendedGrant = existingGrant with { DeniedShellCommandPatterns = [.. existingDenied, pattern] };
        return PermissionAmendOutcome.Persisted;
    }

    /// <summary>
    /// Derives a <c>ShellCommandPatternMatcher</c>-shaped prefix pattern (e.g. <c>"rm *"</c>) from a
    /// shell tool's asked input, or <see langword="null"/> when the tool isn't a recognized shell tool
    /// or the command line cannot be read/parsed safely.
    /// </summary>
    internal static string? DeriveShellCommandPattern(string toolName, string toolInputJson)
    {
        if (!ShellToolNames.Contains(toolName, StringComparer.Ordinal))
        {
            return null;
        }

        string? commandLine = null;
        try
        {
            using var doc = JsonDocument.Parse(toolInputJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                // "command" is claude's Bash tool_input key; "CommandLine" is agy's run_command arg key
                // (AgyHookCheckCommand reads the same name for the same tool).
                if (doc.RootElement.TryGetProperty("command", out var commandProp) &&
                    commandProp.ValueKind == JsonValueKind.String)
                {
                    commandLine = commandProp.GetString();
                }
                else if (doc.RootElement.TryGetProperty("CommandLine", out var commandLineProp) &&
                    commandLineProp.ValueKind == JsonValueKind.String)
                {
                    commandLine = commandLineProp.GetString();
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        var family = ShellCommandPatternMatcher.ExtractCommandFamily(commandLine);
        return family is null ? null : $"{family} *";
    }
}
