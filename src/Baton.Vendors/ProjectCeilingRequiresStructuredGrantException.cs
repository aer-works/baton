using Baton;

namespace Baton.Vendors;

/// <summary>
/// Raised by <see cref="ProjectCeilingGate.Apply"/> when a project's recorded
/// <see cref="ProjectCeiling"/> withholds at least one category (<see cref="ProjectCeiling.IsUnrestricted"/>
/// is false) but the invocation carries no structured <see cref="WorkerInvocation.PermissionGrant"/> to
/// intersect it against — only the raw, hand-typed <see cref="WorkerInvocation.PermissionScope"/>
/// escape hatch, which is an opaque vendor-specific string this type has no category vocabulary to cap.
/// Decision 0004's ceiling "always narrowing, never widening" rules out dispatching that string
/// unconstrained, so this refuses rather than silently letting an un-inspectable grant through a
/// restrictive ceiling.
/// </summary>
public sealed class ProjectCeilingRequiresStructuredGrantException : BatonFlowException
{
    public string WorkerName { get; }

    public string ProjectPath { get; }

    public ProjectCeilingRequiresStructuredGrantException(string workerName, string projectPath)
        : base(
            $"Worker-binding config entry for '{workerName}' targets project '{projectPath}', whose " +
            "recorded ceiling withholds at least one category, but the entry carries only the raw " +
            "PermissionScope escape hatch — no structured PermissionGrant for the ceiling to intersect " +
            "against. AER cannot verify an opaque permission string against a category ceiling, so it " +
            "refuses rather than dispatch unconstrained.")
    {
        WorkerName = workerName;
        ProjectPath = projectPath;
        TryInvocation =
            "Either author this binding with a structured PermissionGrant, or " +
            $"'baton trust \"{projectPath}\" --ceiling all' if this project should be unrestricted.";
    }
}
