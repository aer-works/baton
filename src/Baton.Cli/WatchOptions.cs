namespace Baton.Cli;

/// <summary>Which of <c>baton watch</c>'s three shapes (spec/baton.md §2) this invocation is.</summary>
public enum WatchMode
{
    /// <summary><c>baton watch &lt;room-dir&gt; --notify &lt;target&gt;</c>.</summary>
    Register,

    /// <summary><c>baton watch --list</c>.</summary>
    List,

    /// <summary><c>baton watch --clear-fired</c>.</summary>
    ClearFired,
}

/// <summary>Parsed arguments for <c>baton watch</c>. <see cref="RoomDirectoryPath"/> and
/// <see cref="NotifyTarget"/> are non-null exactly when <see cref="Mode"/> is
/// <see cref="WatchMode.Register"/> — <see cref="WatchOptionsParser"/> is what enforces that.</summary>
public sealed record WatchOptions(WatchMode Mode, string? RoomDirectoryPath, string? NotifyTarget);
