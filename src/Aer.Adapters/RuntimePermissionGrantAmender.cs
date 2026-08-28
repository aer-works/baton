namespace Aer.Adapters;

/// <summary>
/// Reads and revokes a room's standing, pre-cleared <see cref="PermissionGrant"/> (decision 0022's
/// persisting rungs), so the next turn's grant build (<c>InteractiveSessionMaterializer</c>'s
/// per-turn read of the same bindings file) picks up a revocation automatically — the same
/// enforcement path an interactive turn already uses, not a parallel one.
/// </summary>
/// <remarks>
/// Lives in <c>Aer.Adapters</c> rather than beside <c>RoomMutationInterface</c> in <c>Aer.Flow</c>
/// because it reads/writes <c>bindings.json</c> through <see cref="WorkerBindingConfigParser"/>/
/// <see cref="WorkerBindingConfigWriter"/>, both adapter-layer types — <c>Aer.Flow</c> does not (and
/// per Architecture Rule 2, must not) depend on <c>Aer.Adapters</c>.
/// <para>
/// This type used to also carry the write half of decision 0022's ladder — <c>AmendAsync</c>, which
/// persisted a scoped grant from a mid-lane runtime-permission answer. That half was cut with the
/// mid-lane ask/answer/revoke machinery it served (#1417, spec/baton.md §5): a lane is now dispatched
/// fully pre-cleared, so nothing answers a ladder rung at runtime anymore. What remains is the
/// pre-cleared side only — reading and revoking a standing grant already written to
/// <c>bindings.json</c> before dispatch.
/// </para>
/// </remarks>
public static class RuntimePermissionGrantAmender
{
    /// <summary>
    /// Reads the standing <see cref="PermissionGrant"/> for <paramref name="workerName"/> in
    /// <paramref name="roomDirectoryPath"/>'s <c>bindings.json</c>, using the same
    /// <see cref="WorkerBindingConfigParser"/> that <see cref="RevokeAsync"/> uses.
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
    /// Takes back a standing permission from the named worker's <see cref="PermissionGrant"/> in
    /// <paramref name="roomDirectoryPath"/>'s <c>bindings.json</c>, on the same file and through the
    /// same writer <see cref="GetStandingPermissionsAsync"/> reads, so the next turn's grant build
    /// picks up a revocation automatically.
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
    /// offered against a permission that is already held and can therefore be named.
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

        // Ordinal: a pattern is a stored token, not prose, and a case-insensitive removal here would
        // take back a family the operator did not name on any filesystem where two spellings can both
        // be in the list.
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
}

