using Baton;

namespace Baton.Vendors;

/// <summary>
/// Raised by <see cref="ProjectCeilingStore.Load"/> when the ceiling file exists but is not valid
/// JSON, or is not the flat canonical-path → <see cref="ProjectCeiling"/> map it writes. Mirrors
/// <see cref="ProfileStoreException"/>'s own reasoning for why a malformed file is never silently
/// treated as empty here: a caller resolving a project's ceiling may be depending on it, and folding
/// the failure into "no ceilings recorded" would surface as a confusing "project not trusted" refusal
/// instead of the actual, fixable root cause.
/// </summary>
public sealed class ProjectCeilingStoreException : BatonFlowException
{
    public ProjectCeilingStoreException(string message)
        : base(message)
    {
    }

    public ProjectCeilingStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
