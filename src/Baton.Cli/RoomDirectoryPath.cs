namespace Baton.Cli;

/// <summary>
/// Resolves a room directory to an absolute path at the CLI boundary (#668).
/// </summary>
/// <remarks>
/// A relative <c>--room-dir</c> is meaningful only against the process that read it, and the worker
/// is a different process with a different working directory. AER derived <c>BATON_OUTPUT_DIR</c> from
/// the relative form, the worker resolved it against its own cwd, and wrote its declared output
/// somewhere AER never looked — reported as <c>Contract not satisfied</c>, after the run was paid
/// for in full and with nothing naming the real cause.
/// <para>
/// Resolved rather than refused, because the derived default was already absolute
/// (<see cref="RunOptionsParser"/> builds it from the current directory) — so an operator passing a
/// relative one is asking for the same thing the default already does, and refusing would be the
/// surprising half of the pair.
/// </para>
/// <para>
/// Every entry point taking one calls this, and <c>RoomDirectoryIsResolvedAtTheBoundaryTests</c>
/// discovers that population by reflection rather than listing it, so a fifth parser fails the test
/// until it is covered.
/// </para>
/// </remarks>
public static class RoomDirectoryPath
{
    /// <summary>Absolute form of <paramref name="path"/>, resolved against the CLI's own directory.</summary>
    public static string Resolve(string path) => Path.GetFullPath(path);
}
