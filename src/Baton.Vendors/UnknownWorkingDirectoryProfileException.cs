using Baton;

namespace Baton.Vendors;

/// <summary>
/// Raised by <see cref="WorkerBindingResolver.Resolve"/> when a
/// <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> is a non-rooted name (a profile
/// reference, not a literal path) with no matching entry in the supplied profile mapping (M23 Phase
/// 3, #272) — either no mapping was supplied at all, or it was supplied but doesn't name this
/// profile. Mirrors <see cref="UnknownWorkerAdapterException"/>'s role for the identical "config
/// named something the caller never registered" shape, one field over.
/// </summary>
public sealed class UnknownWorkingDirectoryProfileException : BatonFlowException
{
    public string WorkerName { get; }

    public string ProfileName { get; }

    public UnknownWorkingDirectoryProfileException(string workerName, string profileName)
        : base(
            $"Worker-binding config entry for '{workerName}' names WorkingDirectory profile " +
            $"'{profileName}', which has no entry in the local profile mapping ('{BatonProfileStore.DefaultPath}'). " +
            "Either add it there, or use a rooted absolute path instead.")
    {
        WorkerName = workerName;
        ProfileName = profileName;
    }
}
