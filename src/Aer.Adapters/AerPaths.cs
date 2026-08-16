namespace Aer.Adapters;

/// <summary>
/// The single seam through which every reference to AER's per-machine storage root — the
/// <c>~/.aer</c> directory that holds rooms, profiles, the projects list, the daemon
/// token and paired-client records — is resolved. Before this type those seventeen sites each
/// re-derived <c>Path.Combine(UserProfile, ".aer", …)</c> inline, so nothing could point the
/// whole tree somewhere else at once; routing them here makes redirection a one-line change and
/// per-run test isolation (#318) possible.
/// </summary>
/// <remarks>
/// <para>
/// The root honours the <see cref="HomeEnvironmentVariable"/> override when set to a non-blank
/// value, and otherwise defaults to <c>%USERPROFILE%\.aer</c> on Windows / <c>$HOME/.aer</c> on
/// Unix. A blank (empty or whitespace) value is treated as unset, so a stray empty variable can
/// never silently redirect storage to a bare relative <c>.aer</c>.
/// </para>
/// <para>
/// <b>Resolve, never capture.</b> <see cref="Root"/> reads the environment on every access on
/// purpose: a single process (the test suite above all) can change
/// <see cref="HomeEnvironmentVariable"/> between runs and must be honoured immediately. Assigning
/// any member of this type to a <c>static readonly</c> field re-introduces the one-shot,
/// captured-at-type-load resolution this seam exists to remove — expose a re-resolving property
/// instead.
/// </para>
/// <para>
/// The vendor CLIs' own configuration directories (e.g. Claude Code's <c>~/.claude</c>) are
/// deliberately <b>not</b> routed through here: they belong to those tools, not to AER, and
/// redirecting them via <see cref="HomeEnvironmentVariable"/> would point the vendor CLI at a
/// throwaway directory and break its discovery/auth.
/// </para>
/// </remarks>
public static class AerPaths
{
    /// <summary>
    /// Environment variable that overrides the storage root. A blank value (empty or whitespace)
    /// is treated as unset.
    /// </summary>
    public const string HomeEnvironmentVariable = "AER_HOME";

    private const string DefaultDirectoryName = ".aer";

    /// <summary>
    /// The AER storage root, resolved fresh on every access — <see cref="HomeEnvironmentVariable"/>
    /// when set to a non-blank value, otherwise <c>{UserProfile}/.aer</c>. Never cache this in a
    /// <c>static readonly</c> field (see the type remarks).
    /// </summary>
    public static string Root
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DefaultDirectoryName)
                : overridden;
        }
    }

    /// <summary>
    /// <c>{Root}/rooms</c> — <b>the</b> record root: every room lives here, whatever its kind. What a
    /// room's kind means and how its <c>.aer/room.json</c> marker (<see cref="RoomMetadataFileName"/>)
    /// records it is defined on <see cref="RoomKind"/>; this type only names where rooms are stored.
    /// </summary>
    public static string Rooms => Path.Combine(Root, RoomsDirectoryName);

    /// <summary>Directory name of <see cref="Rooms"/> relative to a root.</summary>
    public const string RoomsDirectoryName = "rooms";

    /// <summary>
    /// Filename, under a room's <c>.aer</c> directory, of the room marker whose <c>Kind</c> field
    /// distinguishes an interactive-session room from a workflow room. For an interactive room this
    /// file is the serialized session metadata (kind included); for a workflow room it is a minimal
    /// <c>{ "Kind": "Workflow" }</c> marker. Its absence is read as a workflow room.
    /// </summary>
    public const string RoomMetadataFileName = "room.json";

    /// <summary>
    /// Filename, directly in a room's directory, of the worker bindings that room runs — which
    /// workers, on which adapter, with which model and standing permissions. **The room's own copy is
    /// the register** (decision 0056): every path that needs to know a room's workers resolves this
    /// file under that room, never a remembered last-used path from somewhere else.
    /// </summary>
    /// <remarks>
    /// The literal was written out at eight sites before #1230, which is how a ninth — the decide
    /// endpoint — came to resolve a *different* room's file instead and dispatch to the wrong workers
    /// with no signal. Naming it once is not cosmetic here: it is what makes "the room's own bindings"
    /// a single expression rather than a convention each caller re-implements.
    /// </remarks>
    public const string RoomBindingsFileName = "bindings.json";

    /// <summary>The bindings file belonging to <paramref name="roomDirectoryPath"/>.</summary>
    public static string RoomBindingsFile(string roomDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        return Path.Combine(roomDirectoryPath, RoomBindingsFileName);
    }

    /// <summary>
    /// <c>{Root}/worker-launch</c> — files AER writes to pass per-spawn configuration to a vendor
    /// CLI via an explicit flag rather than the CLI's own directory-based discovery (#533). Unlike
    /// <see cref="Rooms"/> this directory holds no operator-authored content: everything under it
    /// is AER-owned and machine-written, the vendor-specific filename and content chosen by whichever
    /// adapter in <c>Aer.Adapters</c> needs it (Architecture Rule 2) — this type only names where it
    /// lives, the same role it already plays for <see cref="Rooms"/>.
    /// </summary>
    public static string WorkerLaunchConfig => Path.Combine(Root, WorkerLaunchConfigDirectoryName);

    /// <summary>Directory name of <see cref="WorkerLaunchConfig"/> relative to a root.</summary>
    public const string WorkerLaunchConfigDirectoryName = "worker-launch";

    /// <summary>
    /// <c>{Root}/settings.json</c> — daemon-side settings that apply machine-wide rather than to any
    /// one room, starting with the concurrency caps (#1298). Absent or malformed content is read as
    /// defaults, never thrown (see the settings store that consumes this path).
    /// </summary>
    public static string SettingsFile => Path.Combine(Root, SettingsFileName);

    /// <summary>Filename of <see cref="SettingsFile"/> relative to a root.</summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>
    /// The canonical key for a record directory: absolute, with any trailing separator removed, so
    /// <c>C:\x\run</c>, <c>C:\x\run\</c> and <c>C:\x\..\x\run</c> all resolve to one entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every per-record concurrency primitive keys on a directory path, and each one that derives
    /// its own key is a chance for two of them to disagree about whether two paths are the same
    /// record. #393's per-session turn lock was the first (a mismatch there loses turns); #335's
    /// per-session host state is the second (a mismatch there stops the wrong session). One
    /// normaliser, used by both, is what stops the third from drifting from the first two.
    /// </para>
    /// <para>
    /// Pair this with <see cref="RecordKeyComparer"/>. Path case-sensitivity is per-filesystem, not
    /// per-OS, so neither comparer is universally correct: an ordinal one under-locks on Windows
    /// (two casings of one directory become two records — the actual bug), an ignore-case one
    /// over-locks on a case-sensitive filesystem (two genuinely distinct records serialise against
    /// each other). Over-locking costs throughput; under-locking costs correctness, so the choice
    /// is not close.
    /// </para>
    /// </remarks>
    public static string RecordKey(string directoryPath) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));

    /// <summary>
    /// The comparer every dictionary keyed by <see cref="RecordKey"/> must use. See that method's
    /// remarks for why this errs toward treating two paths as one record.
    /// </summary>
    public static StringComparer RecordKeyComparer => StringComparer.OrdinalIgnoreCase;
}
