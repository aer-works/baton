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
    /// Reads the standing <see cref="PermissionGrant"/> for <paramref name="workerName"/> in
    /// <paramref name="roomDirectoryPath"/>'s <c>bindings.json</c>, using the same
    /// <see cref="WorkerBindingConfigParser"/> that <see cref="AmendAsync"/> and <see cref="RevokeAsync"/> use.
    /// </summary>
    public static async Task<StandingPermissionReadResult> GetStandingPermissionsAsync(
        string roomDirectoryPath,
        string workerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(workerName);

        var bindingsFilePath = AerPaths.RoomBindingsFile(roomDirectoryPath);
        if (!File.Exists(bindingsFilePath))
        {
            return StandingPermissionReadResult.NoWorkerSetup();
        }

        var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(bindingsFilePath, cancellationToken)
            .ConfigureAwait(false);
        if (!bindings.TryGetValue(workerName, out var entry))
        {
            return StandingPermissionReadResult.WorkerNotConfigured();
        }

        var grant = entry.PermissionGrant ?? new PermissionGrant();
        return StandingPermissionReadResult.Configured(grant);
    }

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
        // DeniedShellCommandPatterns (see that field for each vendor's enforcement).
        if (decisionKind is not (PermissionDecisionKind.AllowCommandInRoom
            or PermissionDecisionKind.AllowRoom
            or PermissionDecisionKind.DenyAlways))
        {
            return PermissionAmendOutcome.NoChangeNeeded;
        }

        var bindingsFilePath = AerPaths.RoomBindingsFile(roomDirectoryPath);
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

    /// <summary>
    /// Takes back a standing permission from the named worker's <see cref="PermissionGrant"/> in
    /// <paramref name="roomDirectoryPath"/>'s <c>bindings.json</c> — the other direction of
    /// <see cref="AmendAsync"/>, on the same file, through the same writer, so the next turn's grant
    /// build picks it up automatically.
    /// </summary>
    /// <remarks>
    /// #1238: until this existed, answering a persisting rung once was irreversible from every
    /// surface — the only way out was hand-editing <c>bindings.json</c>. A ladder built on the idea
    /// that a standing permission is a considered choice cannot leave that choice one-way.
    /// <para>
    /// It never touches <see cref="PermissionGrant.DeniedShellCommandPatterns"/>. See
    /// <see cref="PermissionRevokeKind"/> for why lifting a refusal is a different operation, and see
    /// the polarity test that pins it.
    /// </para>
    /// <para>
    /// <b>It takes effect on the next turn, not on the one already running.</b> A worker's grant is
    /// translated into vendor flags when its process is spawned, so a turn in flight keeps what it was
    /// given — the same property <c>/api/sessions/{id}/mode</c> already relies on in the other
    /// direction. Withdrawing is the case where that matters, so it is said here rather than left to
    /// be discovered: to stop something already running, cancel the turn. Reaching into a live worker
    /// would need an interrupt, which is a different mechanism entirely.
    /// </para>
    /// </remarks>
    /// <param name="revokeKind">One of the <see cref="PermissionRevokeKind"/> constants.</param>
    /// <param name="shellCommandPattern">
    /// The exact pattern to remove, for <see cref="PermissionRevokeKind.CommandInRoom"/> only — as it
    /// is stored (e.g. <c>"rm *"</c>), not a command line to re-derive one from. A revocation is
    /// offered against a permission that is already held and can therefore be named, unlike an amend,
    /// which has only the ask it is answering.
    /// </param>
    public static async Task<PermissionRevokeOutcome> RevokeAsync(
        string roomDirectoryPath,
        string workerName,
        string revokeKind,
        string? shellCommandPattern = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(workerName);
        ArgumentException.ThrowIfNullOrEmpty(revokeKind);

        if (revokeKind is not (PermissionRevokeKind.RoomShell or PermissionRevokeKind.CommandInRoom))
        {
            throw new ArgumentOutOfRangeException(
                nameof(revokeKind), revokeKind, "Unknown revoke kind. See PermissionRevokeKind.");
        }

        if (revokeKind == PermissionRevokeKind.CommandInRoom && string.IsNullOrEmpty(shellCommandPattern))
        {
            throw new ArgumentException(
                "Revoking one command's standing permission needs the pattern to remove.",
                nameof(shellCommandPattern));
        }

        var bindingsFilePath = AerPaths.RoomBindingsFile(roomDirectoryPath);
        if (!File.Exists(bindingsFilePath))
        {
            return PermissionRevokeOutcome.CouldNotPersist;
        }

        var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(bindingsFilePath, cancellationToken)
            .ConfigureAwait(false);
        if (!bindings.TryGetValue(workerName, out var entry))
        {
            return PermissionRevokeOutcome.CouldNotPersist;
        }

        // No grant at all is the strongest form of "nothing to take back", and it must not read as a
        // failure: a surface offering revocation cannot be made to prove first what is held.
        if (entry.PermissionGrant is not { } existingGrant)
        {
            return PermissionRevokeOutcome.NothingToRevoke;
        }

        var outcome = TryRevoke(existingGrant, revokeKind, shellCommandPattern, out var revokedGrant);
        if (outcome != PermissionRevokeOutcome.Revoked)
        {
            return outcome;
        }

        var newBindings = new Dictionary<string, WorkerBindingConfigEntry>(bindings)
        {
            [workerName] = entry with { PermissionGrant = revokedGrant }
        };

        await WorkerBindingConfigWriter.SaveToFileAsync(newBindings, bindingsFilePath, cancellationToken)
            .ConfigureAwait(false);
        return PermissionRevokeOutcome.Revoked;
    }

    private static PermissionRevokeOutcome TryRevoke(
        PermissionGrant existingGrant, string revokeKind, string? shellCommandPattern,
        out PermissionGrant revokedGrant)
    {
        revokedGrant = existingGrant;

        if (revokeKind == PermissionRevokeKind.RoomShell)
        {
            if (!existingGrant.RunShellCommands && (existingGrant.ShellCommandPatterns?.Count ?? 0) == 0)
            {
                return PermissionRevokeOutcome.NothingToRevoke;
            }

            // Both, per PermissionRevokeKind.RoomShell. Note the deny list is passed through
            // untouched by the `with` — that is the whole point, not an oversight.
            revokedGrant = existingGrant with { RunShellCommands = false, ShellCommandPatterns = [] };
            return PermissionRevokeOutcome.Revoked;
        }

        var patterns = existingGrant.ShellCommandPatterns ?? [];
        if (!patterns.Contains(shellCommandPattern!, StringComparer.Ordinal))
        {
            return PermissionRevokeOutcome.NothingToRevoke;
        }

        // Ordinal, matching how AmendAsync added it. A pattern is a stored token, not prose, and a
        // case-insensitive removal here would take back a family the operator did not name on any
        // filesystem where two spellings can both be in the list.
        var remaining = patterns
            .Where(p => !string.Equals(p, shellCommandPattern, StringComparison.Ordinal))
            .ToArray();

        // #1256: taking the LAST pattern out has to take the shell with it. An empty pattern list is
        // not "nothing is allowed" — PermissionGrant.ShellCommandPatterns' own contract says an empty
        // list beside RunShellCommands means ANY command, and ClaudeWorkerAdapter.BuildAllowedTools
        // implements exactly that, emitting a bare `Bash` instead of the scoped `Bash(pattern)` forms.
        // So filtering the list alone would turn "you may run rm" into "you may run anything" — a
        // revoke that widens, which is the one thing 0004 says this surface must never do.
        revokedGrant = remaining.Length == 0
            ? existingGrant with { RunShellCommands = false, ShellCommandPatterns = [] }
            : existingGrant with { ShellCommandPatterns = remaining };
        return PermissionRevokeOutcome.Revoked;
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
        if (!ShellCommandPatternMatcher.TryReadCommandLine(toolName, toolInputJson, out var commandLine))
        {
            return null;
        }

        var family = ShellCommandPatternMatcher.ExtractCommandFamily(commandLine);
        return family is null ? null : $"{family} *";
    }
}
