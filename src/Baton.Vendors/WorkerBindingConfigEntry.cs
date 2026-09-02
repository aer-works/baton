using Baton.Domain;

namespace Baton.Vendors;

/// <summary>
/// One worker role's entry in a worker-binding config file (M11 Phase 1's open question: "where
/// worker-binding config lives"). A workflow names abstract worker roles (e.g. <c>"architect"</c>);
/// this is the run-time sidecar mapping — worker name → {adapter, model, permission scope, prompt
/// template} — deliberately kept out of the frozen <see cref="WorkflowDefinitionSnapshot"/>, the
/// same way M7 Phase 7 kept a worker's <c>Timeout</c> off the step.
/// </summary>
/// <param name="Adapter">
/// The registered adapter name (e.g. <c>"claude"</c>) this entry resolves through — looked up in
/// the <see cref="IWorkerAdapter"/> registry <see cref="WorkerBindingResolver.Resolve"/> is given,
/// never hardcoded to a vendor here.
/// </param>
/// <param name="Contract">This worker role's <see cref="WorkerContract"/> — required inputs, declared outputs, optional metadata.</param>
/// <param name="PromptTemplate">Forwarded verbatim into the resolved <see cref="WorkerInvocation"/>.</param>
/// <param name="Timeout">The per-execution timeout carried on the resolved <c>Baton.Mutation.WorkerBinding.Process</c>.</param>
/// <param name="Model">Forwarded verbatim into the resolved <see cref="WorkerInvocation"/>.</param>
/// <param name="PermissionScope">Forwarded verbatim into the resolved <see cref="WorkerInvocation"/>.</param>
/// <param name="PermissionGrant">Forwarded verbatim into the resolved <see cref="WorkerInvocation"/> — see its docs for precedence over <paramref name="PermissionScope"/>.</param>
/// <param name="WorkingDirectory">
/// Where this worker role's process should run (M23 Phase 3, #272) — a rooted absolute path (used
/// directly, but not portable to a machine where that path doesn't exist) or a bare name, looked up
/// in the local per-machine profile mapping (<see cref="BatonProfileStore"/>) by
/// <see cref="WorkerBindingResolver.Resolve"/> — the same key resolves to a different real directory
/// on every machine that has its own copy of that mapping, keeping this bindings file itself
/// portable even though the project directory it points at is not. Null keeps the prior default (no
/// explicit cwd).
/// </param>
/// <param name="Effort">
/// Forwarded into the resolved <see cref="WorkerInvocation"/> unchanged as a string — but no longer
/// verbatim in effect, since #1318 widened this field's domain to also accept 0023's canonical effort
/// word; see <see cref="WorkerInvocation.Effort"/>'s own doc for where that word is resolved.
/// </param>
/// <param name="Worktree">
/// When set, the worker's workspace is a git worktree the engine provisions before dispatch and tears
/// down on Terminal (#669), rather than a pre-existing <paramref name="WorkingDirectory"/>. The two are
/// mutually exclusive — a worker runs in exactly one place — and setting both is refused before the
/// pump starts. Null (the default) keeps the referential-directory behaviour above.
/// </param>
/// <param name="IsWorktree">
/// <see cref="WorktreeWorkspaces.Provision"/>'s stamp that <paramref name="WorkingDirectory"/> now
/// points at a worktree it provisioned (#901) — NOT an author-facing setting; a hand-authored true
/// claims isolation that does not exist, and the post-run audit then fails closed against the
/// shared directory's unrelated dirt (loud, not silent — but still a lie the run pays for).
/// </param>
/// <param name="Label">
/// The operator-supplied <c>--label</c> (#1499) — full contract, including why it lives here rather
/// than a new file, is spec/baton.md §2/§6. Sanitized once at parse time
/// (<c>Baton.Cli.DispatchOptionsParser.SanitizeLabel</c>). Null when never supplied.
/// </param>
/// <param name="VerifyPixiTask">
/// #1623: <see cref="WorkerRole.VerifyPixiTask"/>, carried onto the resolved
/// <c>Baton.Mutation.WorkerBinding.Process</c> unchanged — the engine, never the worker, runs it. Since
/// #1702 this is only the lowest-precedence input to <c>Baton.Mutation.VerifyCommandResolver.Resolve</c>,
/// not the sole source of a verify step.
/// </param>
/// <param name="VerifyCommandOverride">
/// #1702: the <c>--verify</c> escape hatch (<see cref="RoleDispatch.ToBinding"/>'s
/// <c>verifyCommandOverride</c>), mirroring <paramref name="TokenBudget"/>'s override pattern —
/// highest precedence in <c>Baton.Mutation.VerifyCommandResolver.Resolve</c>. Null defers to the
/// workspace's own <c>.baton/verify</c> declaration, then <paramref name="VerifyPixiTask"/>.
/// </param>
/// <param name="TokenBudget">
/// #1623: <see cref="WorkerRole.TokenBudget"/>, or the <c>--token-budget</c> override
/// (<see cref="RoleDispatch.ToBinding"/>'s <c>tokenBudgetOverride</c>) when one was supplied.
/// </param>
/// <param name="MaxToolSteps">
/// #1682: <see cref="WorkerRole.MaxToolSteps"/>, or the <c>--max-tool-steps</c> override (#1686 review
/// F11, <see cref="RoleDispatch.ToBinding"/>'s <c>maxToolStepsOverride</c>) when one was supplied —
/// same axis shape as <paramref name="TokenBudget"/>'s <c>--token-budget</c>.
/// </param>
/// <param name="Workstream">
/// The operator-supplied <c>--workstream</c> slug (#1619, rung 1 of #1614's ruling) — a grouping key,
/// not a title: unlike <paramref name="Label"/> it IS path-written, as the directory name of a
/// <c>~/.baton/by-workstream/&lt;slug&gt;/</c> junction the CLI creates at dispatch time
/// (<c>Baton.Cli.WorkstreamJunctionLinker</c>) — see spec/baton.md §2/§6. Sanitized and slug-validated
/// once at parse time (<c>Baton.Cli.DispatchOptionsParser.SanitizeWorkstream</c>). Null when never
/// supplied.
/// </param>
/// <param name="WorktreeBaseSha">
/// N2 (#1664 re-review): the commit <paramref name="Worktree"/>'s <see cref="WorktreeWorkspace.Ref"/>
/// resolved to at provisioning time (<see cref="Workspaces.WorktreeProvisioner.ResolveBaseCommit"/>),
/// stamped by <see cref="WorktreeWorkspaces"/> in the SAME expression that nulls
/// <paramref name="Worktree"/> and sets <paramref name="IsWorktree"/> — so the value the fix reads is
/// captured before the field carrying it is cleared, unlike the symbolic ref this replaces. Null
/// whenever <paramref name="IsWorktree"/> is false, or the ref could not be resolved against the
/// source repository.
/// </param>
/// <param name="ToolSha">
/// #1668: The commit SHA of the baton binary that dispatched this room, stamped at dispatch
/// time so side-by-side tool pruning can preserve versions referenced by live rooms. Null when
/// dispatched by a binary that predates the field or when unresolved.
/// </param>
public sealed record WorkerBindingConfigEntry(
    string Adapter,
    WorkerContract Contract,
    string PromptTemplate,
    TimeSpan Timeout,
    string? Model = null,
    string? PermissionScope = null,
    PermissionGrant? PermissionGrant = null,
    string? WorkingDirectory = null,
    string? SessionId = null,
    bool ResumeSession = false,
    bool StreamJson = false,
    string? LogFilePath = null,
    string? Effort = null,
    WorktreeWorkspace? Worktree = null,
    GrantAuditMode GrantAuditMode = GrantAuditMode.Enforced,
    bool IsWorktree = false,
    string? Label = null,
    string? VerifyPixiTask = null,
    string? VerifyCommandOverride = null,
    long? TokenBudget = null,
    int? MaxToolSteps = null,
    string? Workstream = null,
    string? WorktreeBaseSha = null,
    string? ToolSha = null);


/// <summary>
/// A worktree workspace spec on a <see cref="WorkerBindingConfigEntry"/> (#669): the local
/// <paramref name="Repository"/> to make a worktree of, and the <paramref name="Ref"/> (a branch or
/// commit) to check out. The provisioning, teardown, and the local-only / Credential-Isolation
/// rationale all live on <c>Baton.Workspaces.WorktreeProvisioner</c>; this record is only the
/// declared intent.
/// </summary>
public sealed record WorktreeWorkspace(string Repository, string Ref);
