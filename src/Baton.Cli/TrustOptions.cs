using Baton.Vendors;

namespace Baton.Cli;

/// <summary>Which of <c>baton trust</c>'s three shapes (#1166) this invocation is.</summary>
public enum TrustMode
{
    /// <summary><c>baton trust &lt;project-path&gt; --ceiling &lt;categories&gt;</c>.</summary>
    Register,

    /// <summary><c>baton trust --list</c>.</summary>
    List,

    /// <summary><c>baton trust &lt;project-path&gt; --revoke</c>.</summary>
    Revoke,
}

/// <summary>
/// Parsed arguments for <c>baton trust</c>. <see cref="ProjectPath"/> is non-null for
/// <see cref="TrustMode.Register"/>/<see cref="TrustMode.Revoke"/> and <see cref="Ceiling"/> is
/// non-null exactly for <see cref="TrustMode.Register"/> — <see cref="TrustOptionsParser"/> is what
/// enforces that. Deliberately not named <c>RoomDirectoryPath</c>: a project path is not a room
/// directory, and <c>RoomDirectoryIsResolvedAtTheBoundaryTests</c> discovers its population by that
/// exact property name.
/// </summary>
public sealed record TrustOptions(TrustMode Mode, string? ProjectPath, ProjectCeiling? Ceiling);
