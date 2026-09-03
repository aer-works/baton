namespace Baton.Vendors;

/// <summary>
/// #1166: the single choke point both <see cref="ClaudeWorkerAdapter"/> and <see cref="AgyWorkerAdapter"/>
/// call at the top of <c>Resolve</c> — one shared read-store/refuse/cap sequence rather than two copies
/// that drift. Consults <see cref="ProjectCeilingStore"/> against <see cref="WorkerInvocation.WorkingDirectory"/>
/// and returns an invocation whose <see cref="WorkerInvocation.PermissionGrant"/> is capped to
/// decision 0004's effective grant = role grant ∩ project ceiling.
/// </summary>
internal static class ProjectCeilingGate
{
    /// <param name="invocation">The invocation as resolved so far — read, never mutated (records).</param>
    /// <param name="workerName">
    /// <see cref="Baton.Domain.WorkerContract.WorkerName"/> — carried only for the exceptions below;
    /// the gate itself has no other use for it.
    /// </param>
    /// <exception cref="ProjectNotTrustedException">
    /// <see cref="WorkerInvocation.WorkingDirectory"/> is set and carries no recorded
    /// <see cref="ProjectCeilingStore"/> entry.
    /// </exception>
    /// <exception cref="ProjectCeilingRequiresStructuredGrantException">
    /// The recorded ceiling withholds a category but the invocation has no structured
    /// <see cref="PermissionGrant"/> to cap.
    /// </exception>
    /// <exception cref="IncoherentPermissionGrantException">
    /// Capping the role's grant against the ceiling produces the #529 shape a granted, unscoped shell
    /// reaches every category anyway — re-checked here because a coherent role grant can become
    /// incoherent once narrowed (<see cref="PermissionGrant.CategoriesDefeatedByTheShell"/>'s own
    /// remarks are the canonical statement of the rule; this is the same predicate, not a restatement).
    /// </exception>
    public static WorkerInvocation Apply(WorkerInvocation invocation, string workerName)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        if (string.IsNullOrWhiteSpace(invocation.WorkingDirectory))
        {
            // No project to hold a ceiling against — a plain chat turn or an adapter call with no
            // WorkingDirectory (docs/agents doc: ExecuteSessionTurnAsync never sets one unless the
            // session is attached to a codebase). Decision 0004's ceiling is a project scope; there is
            // no project scope to enforce here.
            return invocation;
        }

        var ceiling = ProjectCeilingStore.TryGet(invocation.WorkingDirectory, ProjectCeilingStore.DefaultPath);
        if (ceiling is null)
        {
            throw new ProjectNotTrustedException(invocation.WorkingDirectory);
        }

        if (ceiling.IsUnrestricted)
        {
            return invocation;
        }

        if (invocation.PermissionGrant is not { } grant)
        {
            throw new ProjectCeilingRequiresStructuredGrantException(workerName, invocation.WorkingDirectory);
        }

        var capped = ceiling.Cap(grant);
        if (capped.CategoriesDefeatedByTheShell is { Count: > 0 } withheld)
        {
            throw new IncoherentPermissionGrantException(workerName, withheld);
        }

        return invocation with { PermissionGrant = capped };
    }
}
