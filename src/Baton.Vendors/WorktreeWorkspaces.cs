using Baton.Concurrency;
using Baton.Mutation;
using Baton.Workspaces;

namespace Baton.Vendors;

/// <summary>
/// The pre-dispatch pass that turns a binding's declared <see cref="WorktreeWorkspace"/> into a real
/// directory the worker runs in (#669). For each entry declaring one it provisions a git worktree
/// under the room directory — one per worker, never shared — and rewrites that entry's
/// <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> to point at it, so
/// <see cref="WorkerBindingResolver.Resolve"/> downstream sees an ordinary directory and needs no
/// worktree knowledge. Returns the worktrees to tear down once the run reaches Terminal.
///
/// <para>
/// Idempotent across resume: a worktree that already exists on a second <c>baton run</c> is reused, not
/// re-added (which git would refuse). Refuses an entry that sets both a WorkingDirectory and a
/// worktree, because a worker runs in exactly one place — a bind-time refusal, before the pump starts.
/// </para>
/// </summary>
public static class WorktreeWorkspaces
{
    /// <summary>The room-directory-relative parent the per-worker worktrees are created under.</summary>
    public const string WorkspacesDirectoryName = "workspaces";

    /// <summary>
    /// Provisions every declared worktree and returns the bindings with each such entry's
    /// WorkingDirectory rewritten to its worktree, plus the list to hand to teardown on Terminal. When
    /// no entry declares a worktree the input dictionary is returned unchanged.
    /// <para>
    /// The strict half of the pair: it is <see cref="ProvisionLazily"/>'s walk, with the first entry
    /// that could not be provisioned rethrown rather than skipped. Two copies of the walk would be two
    /// things to keep in step, and the skip/throw choice is the only difference between them.
    /// </para>
    /// </summary>
    public static (IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings,
                   IReadOnlyList<ProvisionedWorktree> Provisioned)
        Provision(IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings, string roomDirectoryPath)
    {
        var (rewritten, provisioned, _) = Walk(bindings, roomDirectoryPath, throwOnFailure: true);
        return (rewritten, provisioned);
    }

    /// <summary>
    /// Same provisioning as <see cref="Provision"/>, but skips any entry whose worktree specification is invalid
    /// or fails to provision, leaving its binding untouched and returning it in the skipped list (#1012).
    ///
    /// <para>
    /// A skipped entry keeps no isolation stamp, so if it is ever actually dispatched the existing refusal fires —
    /// <see cref="UnisolatedGrantAuditException"/> for an audited binding — and the failure re-surfaces where it is
    /// actionable instead of blocking an unrelated cancel.
    /// </para>
    /// </summary>
    public static (IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings,
                   IReadOnlyList<ProvisionedWorktree> Provisioned,
                   IReadOnlyList<SkippedWorktreeProvisioning> Skipped)
        ProvisionLazily(IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings, string roomDirectoryPath) =>
        Walk(bindings, roomDirectoryPath, throwOnFailure: false);

    /// <summary>
    /// The reuse-or-refuse half of <c>baton resume</c>'s worktree handling (issue #1359 F1). A resume
    /// NEVER provisions — it continues in the exact workspace the execution being resumed already ran
    /// in, never a freshly-created one: <see cref="Provision"/>'s ordinary "create if missing"
    /// behavior would otherwise let a resume silently re-provision a torn-down tree at whatever
    /// <c>HEAD</c> is now, resuming the vendor session into an empty directory that describes edits
    /// that are not on disk — worse than an ordinary cold start, because the transcript claims
    /// otherwise. Refuses instead when the directory is gone, naming the missing path.
    /// <para>
    /// An entry with no <see cref="WorkerBindingConfigEntry.Worktree"/> spec (an ordinary
    /// <c>WorkingDirectory</c>, or none) passes through unchanged — nothing here applies to it.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidWorkspaceSpecException">
    /// The entry declares both a <c>WorkingDirectory</c> and a worktree, or the worktree spec itself
    /// is malformed (same checks <see cref="Provision"/> runs).
    /// </exception>
    /// <exception cref="InvalidResumeException">
    /// The worker's worktree spec is otherwise valid, but the directory it names no longer exists on
    /// disk — the prior run's workspace is gone, and a resume must not conjure a fresh one wearing
    /// its clothes.
    /// </exception>
    public static WorkerBindingConfigEntry ReuseForResume(WorkerBindingConfigEntry entry, string workerName, string roomDirectoryPath)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(roomDirectoryPath);

        if (entry.Worktree is not { } spec)
        {
            return entry;
        }

        if (entry.WorkingDirectory is not null)
        {
            throw new InvalidWorkspaceSpecException(
                $"Worker '{workerName}' declares both a WorkingDirectory and a worktree workspace; " +
                "a worker runs in exactly one place. Set one, not both.");
        }

        WorktreeProvisioner.ValidateSpec(spec.Repository, spec.Ref);
        var worktreePath = Path.Combine(roomDirectoryPath, WorkspacesDirectoryName, workerName);

        if (!Directory.Exists(worktreePath))
        {
            throw new InvalidResumeException(
                $"Worker '{workerName}''s prior workspace no longer exists at '{worktreePath}' — baton " +
                "resume reuses the exact workspace the execution being resumed ran in, and never " +
                "provisions a fresh one, so a resumed worker never starts cold in an empty tree.")
            {
                TryInvocation = $"restore '{worktreePath}' from backup if the prior work is still needed; " +
                    "otherwise this worker's session cannot be continued — dispatch it fresh with `baton run` " +
                    "or `baton dispatch` instead of `baton resume`.",
            };
        }

        // N2 (#1664 re-review): resolved against spec.Repository/spec.Ref BEFORE Worktree is nulled
        // below — the same SHA-not-symbolic-ref fix Walk applies for a fresh provision.
        var baseSha = WorktreeProvisioner.ResolveBaseCommit(spec.Repository, spec.Ref);

        return entry with { WorkingDirectory = worktreePath, Worktree = null, IsWorktree = true, WorktreeBaseSha = baseSha };
    }

    /// <summary>
    /// The one walk both entry points above share. <paramref name="throwOnFailure"/> rethrows at the
    /// failing entry rather than skipping it — which also stops the walk there, so the strict caller
    /// never leaves later entries' trees provisioned behind a refusal it is about to throw.
    /// </summary>
    private static (IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings,
                    IReadOnlyList<ProvisionedWorktree> Provisioned,
                    IReadOnlyList<SkippedWorktreeProvisioning> Skipped)
        Walk(IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings, string roomDirectoryPath, bool throwOnFailure)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentException.ThrowIfNullOrWhiteSpace(roomDirectoryPath);

        // #1646 / CancelCommand's #1495 finding: this used to acquire the flow lock unconditionally,
        // even when no entry declares a worktree at all — the common case, and the exact shape
        // RunWaitEndToEndTests hit (an ordinary shell-worker bindings file with zero Worktree
        // entries). Nothing below the guard does anything for such a walk, so there is nothing here
        // to serialize and no reason to contend a live pump's lock over it.
        if (!bindings.Values.Any(entry => entry.Worktree is not null))
        {
            return (bindings, [], []);
        }

        // #1646: bounded rather than fail-fast for the rarer walk that does have a worktree to
        // provision — the same live-pump exit tail every sibling command loses to, sized once in
        // RoutineHoldBudget rather than restated here. Still bounded: a room genuinely held by a
        // second live pump must surface as a refusal, not be waited on for that pump's whole step.
        using var guard = ConcurrencyGuard.AcquireWithin(roomDirectoryPath, RoutineHoldBudget.Duration, "worktree provisioning");

        Dictionary<string, WorkerBindingConfigEntry>? rewritten = null;
        var provisioned = new List<ProvisionedWorktree>();
        var skipped = new List<SkippedWorktreeProvisioning>();

        foreach (var (workerName, entry) in bindings)
        {
            if (entry.Worktree is not { } spec)
            {
                continue;
            }

            if (entry.WorkingDirectory is not null)
            {
                var bothDeclared = new InvalidWorkspaceSpecException(
                    $"Worker '{workerName}' declares both a WorkingDirectory and a worktree workspace; " +
                    "a worker runs in exactly one place. Set one, not both.");

                if (throwOnFailure)
                {
                    throw bothDeclared;
                }

                skipped.Add(new SkippedWorktreeProvisioning(workerName, bothDeclared));
                continue;
            }

            try
            {
                // Validate on every path (a resume reuses the tree but must still refuse a bad spec).
                WorktreeProvisioner.ValidateSpec(spec.Repository, spec.Ref);
                var worktreePath = Path.Combine(roomDirectoryPath, WorkspacesDirectoryName, workerName);

                if (!Directory.Exists(worktreePath))
                {
                    WorktreeProvisioner.Provision(worktreePath, spec.Repository, spec.Ref);
                }

                provisioned.Add(new ProvisionedWorktree(spec.Repository, worktreePath));
                rewritten ??= new Dictionary<string, WorkerBindingConfigEntry>(bindings);

                // N2 (#1664 re-review): resolved BEFORE Worktree is nulled below — see
                // WorktreeProvisioner.ResolveBaseCommit's own remarks for why this has to run against
                // the source repository rather than the symbolic ref it replaces.
                var baseSha = WorktreeProvisioner.ResolveBaseCommit(spec.Repository, spec.Ref);
                rewritten[workerName] = entry with { WorkingDirectory = worktreePath, Worktree = null, IsWorktree = true, WorktreeBaseSha = baseSha };
            }
            catch (Exception ex) when (!throwOnFailure
                && ex is InvalidWorkspaceSpecException or WorktreeProvisioningException)
            {
                skipped.Add(new SkippedWorktreeProvisioning(workerName, ex));
            }
        }

        return (rewritten ?? bindings, provisioned, skipped);
    }
}

/// <summary>
/// An entry whose worktree could not be provisioned during <see cref="WorktreeWorkspaces.ProvisionLazily"/>.
/// </summary>
public sealed record SkippedWorktreeProvisioning(string WorkerName, Exception Exception);
