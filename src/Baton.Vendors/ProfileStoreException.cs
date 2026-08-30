using Baton;

namespace Baton.Vendors;

/// <summary>
/// Raised when the local per-machine profile mapping (M23 Phase 3, #272; see
/// <see cref="BatonProfileStore"/>) fails to parse — malformed JSON at its configured path. Unlike
/// the now-deleted <c>Baton.RoomSession.LocalUiConfigurationStore</c>'s own local config file (#1420),
/// a corrupt profile mapping is never silently treated as empty: a
/// <see cref="WorkerBindingConfigEntry.WorkingDirectory"/>
/// resolution that depends on it would otherwise fail with a confusing "unknown profile" error
/// instead of the actual, fixable root cause.
/// </summary>
public sealed class ProfileStoreException : BatonFlowException
{
    public ProfileStoreException(string message)
        : base(message)
    {
    }

    public ProfileStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
