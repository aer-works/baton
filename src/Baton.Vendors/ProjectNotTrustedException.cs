using Baton;

namespace Baton.Vendors;

/// <summary>
/// Raised by <see cref="ProjectCeilingGate.Apply"/> when a worker invocation's
/// <see cref="WorkerInvocation.WorkingDirectory"/> carries no recorded entry in
/// <see cref="ProjectCeilingStore"/> — decision 0004's project ceiling, with no interactive prompt to
/// fall back on in a headless dispatch (#1166's scope ruling). Fails closed before any worker spawns,
/// the same "refuse rather than ask" posture <see cref="IncoherentPermissionGrantException"/> already
/// takes for a different 0004 rule.
/// </summary>
public sealed class ProjectNotTrustedException : BatonFlowException
{
    public string ProjectPath { get; }

    public ProjectNotTrustedException(string projectPath)
        : base(
            $"'{projectPath}' has no recorded permission ceiling. Decision 0004's project scope has no " +
            "interactive trust prompt in a headless dispatch — the operator verb is 'baton trust' — so " +
            "dispatching against an unseen project directory fails closed rather than silently spawning " +
            "under an unbounded role grant.")
    {
        ProjectPath = projectPath;
        TryInvocation =
            $"baton trust \"{projectPath}\" --ceiling all (or a comma-separated subset of " +
            "ReadFiles,WriteFiles,RunShellCommands,NetworkAccess), then re-run the dispatch.";
    }
}
