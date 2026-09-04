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
/// <param name="BilledRateLimit">
/// #1691: <see cref="WorkerRole.BilledRateLimit"/>, or the <c>--billed-rate-limit</c> override
/// (<see cref="RoleDispatch.ToBinding"/>'s <c>billedRateLimitOverride</c>) when one was supplied —
/// same axis shape as <paramref name="TokenBudget"/>'s <c>--token-budget</c>. In practice the override
/// is the ONLY source: no role declares a default (spec/baton.md §3).
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
/// <param name="WorktreeSourceRepository">
/// #1166 review finding A: <paramref name="Worktree"/>'s <see cref="WorktreeWorkspace.Repository"/>,
/// stamped by <see cref="WorktreeWorkspaces"/> in the SAME expression as <paramref name="WorktreeBaseSha"/>
/// and for the identical reason — captured before <paramref name="Worktree"/> is nulled. This is the
/// project-ceiling lookup key <see cref="ProjectCeilingGate"/> uses in preference to
/// <paramref name="WorkingDirectory"/> whenever it is set: a worktree's <paramref name="WorkingDirectory"/>
/// is a fresh, room-scoped directory allocated at provisioning time (never the same path twice, and
/// never known to the operator ahead of dispatch), so keying the ceiling on it would make an
/// auto-provisioned worktree permanently untrustable — the operator has no stable path to run
/// <c>baton trust</c> against. The source repository is the stable, operator-known path 0004's ceiling
/// is actually about. Null whenever <paramref name="IsWorktree"/> is false.
/// </param>
/// <param name="ToolSha">
/// #1668: The commit SHA of the baton binary that dispatched this room, stamped at dispatch
/// time so side-by-side tool pruning can preserve versions referenced by live rooms. Null when
/// dispatched by a binary that predates the field or when unresolved.
/// </param>
/// <param name="ChangesTree">
/// #1622/#1390: whether this role's CONTRACT is "change the tree" -- read/write files and run shell
/// commands, the same two-predicate reading <c>OutcomeClassifier</c> derives it from at settle time.
/// Computed once, here, from the CATALOG role's own <see cref="WorkerRole.Grant"/>
/// (<see cref="RoleDispatch.ToBinding"/>) -- deliberately NOT re-derived from
/// <paramref name="PermissionGrant"/> above, which <c>ToBinding</c> can widen
/// (<c>WriteFiles: true</c>, audited-not-enforced) for a role that declares outputs but no tree-write
/// grant, purely so a non-outbox-capable adapter can still write its own declared report -- re-reading
/// that widened grant downstream would misclassify e.g. <c>review</c> as tree-changing under such an
/// adapter. False for every entry not constructed through <see cref="RoleDispatch.ToBinding"/> (a
/// hand-authored <c>bindings.json</c>, or a future front door that never sets it) -- the safe default,
/// since <c>workspaceChanged</c>/<c>hollow</c> are an additive signal, not a gate: false simply omits
/// the two settle-time fields rather than fabricating one for a role catalog this entry never named.
/// </param>
/// <param name="DeliversBranch">
/// #1788: <see cref="WorkerRole.DeliversBranch"/>, carried onto the resolved
/// <c>Baton.Mutation.WorkerBinding.Process</c> unchanged -- whether the engine's post-exit delivery
/// check (<c>Baton.Mutation.DeliveryVerifier</c>) runs at all. False for every entry not constructed
/// through <see cref="RoleDispatch.ToBinding"/>, the same safe default <paramref name="ChangesTree"/> uses.
/// </param>
/// <param name="ExpectPr">
/// #1788: the delivery check's PR-half switch, ALREADY RESOLVED by <see cref="RoleDispatch.ToBinding"/>
/// as <c>expectPrOverride ?? role.DeliversBranch</c> -- so this field, unlike most others on this
/// record, never needs its own nullable "not specified" state; a plain <see langword="false"/> here
/// means "do not check for a PR", which is also the correct reading for any entry not constructed
/// through <see cref="RoleDispatch.ToBinding"/> (the <paramref name="DeliversBranch"/> default already
/// disables the whole check in that case).
/// </param>
/// <param name="AllowsSubagents">
/// #1802: <see cref="WorkerRole.AllowsSubagents"/>, carried onto the resolved <see cref="WorkerInvocation"/>
/// unchanged -- whether the spawned worker keeps the vendor's own subagent/fan-out tool. Defaults to
/// <see langword="true"/> here, UNLIKE <paramref name="ChangesTree"/>/<paramref name="DeliversBranch"/>
/// above: those two are additive signals a false default merely omits, while this one drives an actual
/// enforcement flag on the spawn argv, so defaulting it to withhold would newly restrict every
/// hand-authored <c>bindings.json</c> entry that predates #1802 and never opted into the restriction.
/// <see cref="RoleDispatch.ToBinding"/> is the one caller that overrides this from the catalog role's own
/// value for every role dispatched through the front door.
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
    long? BilledRateLimit = null,
    string? Workstream = null,
    string? WorktreeBaseSha = null,
    string? WorktreeSourceRepository = null,
    string? ToolSha = null,
    bool ChangesTree = false,
    bool DeliversBranch = false,
    bool ExpectPr = false,
    bool AllowsSubagents = true);


/// <summary>
/// A worktree workspace spec on a <see cref="WorkerBindingConfigEntry"/> (#669): the local
/// <paramref name="Repository"/> to make a worktree of, and the <paramref name="Ref"/> (a branch or
/// commit) to check out. The provisioning, teardown, and the local-only / Credential-Isolation
/// rationale all live on <c>Baton.Workspaces.WorktreeProvisioner</c>; this record is only the
/// declared intent.
/// </summary>
public sealed record WorktreeWorkspace(string Repository, string Ref);
