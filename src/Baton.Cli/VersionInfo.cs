using System.Reflection;

namespace Baton.Cli;

/// <summary>
/// M13 Phase 2 (#108): reads the version the SDK stamped onto the assembly from
/// <c>Directory.Build.props</c>'s <c>&lt;Version&gt;</c> — the same value release-please bumps on
/// every release — so <c>baton --version</c> matches the <c>CHANGELOG.md</c> entry it shipped with.
/// </summary>
public static class VersionInfo
{
    public static string GetVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";
    }
}
