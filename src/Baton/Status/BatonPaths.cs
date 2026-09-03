namespace Baton.Status;

/// <summary>
/// The single seam through which every reference to AER's per-machine storage root — the
/// <c>~/.baton</c> directory that holds rooms, profiles, the projects list, the daemon
/// token and paired-client records — is resolved. Before this type those seventeen sites each
/// re-derived <c>Path.Combine(UserProfile, ".baton", …)</c> inline, so nothing could point the
/// whole tree somewhere else at once; routing them here makes redirection a one-line change and
/// per-run test isolation (#318) possible.
/// </summary>
/// <remarks>
/// <para>
/// The root honours the <see cref="HomeEnvironmentVariable"/> override when set to a non-blank
/// value, and otherwise defaults to <c>%USERPROFILE%\.baton</c> on Windows / <c>$HOME/.baton</c> on
/// Unix. A blank (empty or whitespace) value is treated as unset, so a stray empty variable can
/// never silently redirect storage to a bare relative <c>.baton</c>.
/// </para>
/// <para>
/// <b>Frozen, not re-resolved (#1496).</b> <see cref="Root"/> reads <see cref="HomeEnvironmentVariable"/>
/// through <see cref="BatonEnvironmentSnapshot.Current"/>, which captures the environment once per
/// process and never re-reads it — the opposite of this type's original "resolve, never capture"
/// discipline, which forced every env-mutating test into one serialized collection (#1491) because a
/// production reader could observe a mutation mid-process. A test that needs a different root uses
/// <c>BatonEnvironmentSnapshot.BeginScope</c> to supply one explicitly instead of mutating the
/// process environment.
/// </para>
/// <para>
/// The vendor CLIs' own configuration directories (e.g. Claude Code's <c>~/.claude</c>) are
/// deliberately <b>not</b> routed through here: they belong to those tools, not to AER, and
/// redirecting them via <see cref="HomeEnvironmentVariable"/> would point the vendor CLI at a
/// throwaway directory and break its discovery/auth.
/// </para>
/// </remarks>
public static class BatonPaths
{
    /// <summary>
    /// Environment variable that overrides the storage root. A blank value (empty or whitespace)
    /// is treated as unset.
    /// </summary>
    public const string HomeEnvironmentVariable = "BATON_HOME";

    private const string DefaultDirectoryName = ".baton";

    /// <summary>
    /// The AER storage root — <see cref="HomeEnvironmentVariable"/> when set to a non-blank value,
    /// otherwise <c>{UserProfile}/.baton</c>. Resolved against the frozen
    /// <see cref="BatonEnvironmentSnapshot.Current"/> (see the type remarks); a per-test override goes
    /// through <c>BatonEnvironmentSnapshot.BeginScope</c>, never <c>Environment.SetEnvironmentVariable</c>.
    /// </summary>
    public static string Root
    {
        get
        {
            var overridden = BatonEnvironmentSnapshot.Current.HomeOverride;
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DefaultDirectoryName)
                : overridden;
        }
    }

    /// <summary>
    /// <c>{Root}/rooms</c> — <b>the</b> record root: every room lives here, whatever its kind. What a
    /// room's kind means and how its <c>.baton/room.json</c> marker (<see cref="RoomMetadataFileName"/>)
    /// records it is defined on <see cref="RoomKind"/>; this type only names where rooms are stored.
    /// </summary>
    public static string Rooms => Path.Combine(Root, RoomsDirectoryName);

    /// <summary>Directory name of <see cref="Rooms"/> relative to a root.</summary>
    public const string RoomsDirectoryName = "rooms";

    /// <summary>
    /// <c>{Root}/by-workstream</c> — junction directories written by
    /// <c>Baton.Cli.WorkstreamJunctionLinker</c>. <b>Deliberately a sibling of <see cref="Rooms"/>,
    /// never a child</b>: spec/baton.md's dispatch section (§2) explains why.
    /// </summary>
    public static string ByWorkstream => Path.Combine(Root, ByWorkstreamDirectoryName);

    /// <summary>Directory name of <see cref="ByWorkstream"/> relative to a root.</summary>
    public const string ByWorkstreamDirectoryName = "by-workstream";

    /// <summary>
    /// Filename, under a room's <c>.baton</c> directory, of the room marker whose <c>Kind</c> field
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
    /// Filename, directly in a room's directory, of the append-only room-scoped event log — chat
    /// turns, permission grants, memory proposals — as distinct from <see cref="FlowLogFileName"/>'s
    /// engine-scoped run log (0053: the two logs take independent locks).
    /// </summary>
    public const string RoomLogFileName = "room.jsonl";

    /// <summary>
    /// Filename, directly in a room's directory, of the append-only engine event log the Flow engine
    /// writes as it executes a workflow run.
    /// </summary>
    public const string FlowLogFileName = "flow.jsonl";

    /// <summary>
    /// Filename, directly in a room's directory, of the terminal snapshot the engine writes once a
    /// workflow run reaches a terminal state.
    /// </summary>
    public const string SnapshotFileName = "snapshot.json";

    /// <summary>
    /// Filename, directly in a room's directory, of <see cref="Baton.Concurrency.ConcurrencyGuard"/>'s
    /// advisory lock file that serializes the engine against <see cref="FlowLogFileName"/> writers
    /// (0053). <see cref="Baton.Concurrency.ConcurrencyGuard.FlowLockFileName"/> mirrors this value
    /// rather than restating it, so the lock's own home stays the source of the mechanism while this
    /// stays the source of the name.
    /// </summary>
    public const string FlowLockFileName = "flow.lock";

    /// <summary>
    /// The four filenames whose presence in a directory is local evidence that directory is a room —
    /// decision 0057 rule 4's detection test: "the file is named <c>bindings.json</c> <em>and</em> its
    /// directory carries room evidence". Never a path prefix: room directories arrive as free paths on
    /// every request and can live anywhere.
    /// </summary>
    public static readonly IReadOnlyList<string> RoomEvidenceFileNames =
        [RoomLogFileName, FlowLogFileName, SnapshotFileName, FlowLockFileName];

    /// <summary>
    /// <c>{Root}/worker-launch</c> — files AER writes to pass per-spawn configuration to a vendor
    /// CLI via an explicit flag rather than the CLI's own directory-based discovery (#533). Unlike
    /// <see cref="Rooms"/> this directory holds no operator-authored content: everything under it
    /// is AER-owned and machine-written, the vendor-specific filename and content chosen by whichever
    /// adapter in <c>Baton.Vendors</c> needs it (Architecture Rule 2) — this type only names where it
    /// lives, the same role it already plays for <see cref="Rooms"/>.
    /// </summary>
    public static string WorkerLaunchConfig => Path.Combine(Root, WorkerLaunchConfigDirectoryName);

    /// <summary>Directory name of <see cref="WorkerLaunchConfig"/> relative to a root.</summary>
    public const string WorkerLaunchConfigDirectoryName = "worker-launch";

    /// <summary>
    /// <c>{Root}/settings.json</c> — see <see cref="DaemonSettingsStore"/> for what this holds and how
    /// an absent or malformed file is handled.
    /// </summary>
    public static string SettingsFile => Path.Combine(Root, SettingsFileName);

    /// <summary>Filename of <see cref="SettingsFile"/> relative to a root.</summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>
    /// <c>{Root}/room-registry.jsonl</c> — see <see cref="RoomRegistryStore"/> (spec/baton.md §8) for what
    /// this holds: one append-only line per room registration (room path, project root, created-at).
    /// The coverage guarantee this buys is stated once, in <c>FleetStatusTool</c>'s remarks.
    /// </summary>
    public static string RoomRegistryFile => Path.Combine(Root, RoomRegistryFileName);

    /// <summary>Filename of <see cref="RoomRegistryFile"/> relative to a root.</summary>
    public const string RoomRegistryFileName = "room-registry.jsonl";

    /// <summary>
    /// <c>{Root}/fleet/projection.json</c> — the daemon-written fleet projection file (#1557,
    /// spec/baton.md §7's fourth kept responsibility): the same <c>fleet_status</c> room array
    /// (spec/baton.md §6) plus per-room <c>live</c>/<c>pruned</c> and the top-level <c>derived_at</c>,
    /// rewritten atomically roughly every 30s so a local reader (a janitor sweep, the pusher once
    /// #1557 PR-B lands) never has to re-derive it by scanning every room itself.
    /// </summary>
    public static string FleetProjectionFile => Path.Combine(Root, FleetDirectoryName, FleetProjectionFileName);

    /// <summary>Directory name <see cref="FleetProjectionFile"/> lives under, relative to a root.</summary>
    public const string FleetDirectoryName = "fleet";

    /// <summary>Filename of <see cref="FleetProjectionFile"/> relative to <see cref="FleetDirectoryName"/>.</summary>
    public const string FleetProjectionFileName = "projection.json";

    /// <summary>
    /// <c>{Root}/secretpatterns.local.txt</c> — the daemon's own copy of the fail-closed secret-gate
    /// denylist <c>tools/fleet-glass/pusher.py</c>'s <c>load_secret_patterns</c>/<c>secret_hit_index</c>
    /// already define (spec/baton.md §6): one regex per line, '#' starts a comment, blank lines
    /// ignored. A NEW path, not the pusher's own <c>tools/fleet-glass/secretpatterns.local.txt</c> —
    /// the daemon and the pusher are separate processes with no shared working directory in general
    /// (the daemon runs from an installed <see cref="Tools"/> checkout, the pusher from its own repo
    /// checkout), so each keeps its own copy under its own storage root, machine-local like the
    /// pusher's own (never checked in — outside the repo entirely, so no <c>.gitignore</c> entry is
    /// needed either). Missing or unreadable fails CLOSED (every <c>stdoutTail</c> line withheld),
    /// matching the pusher's own ruling.
    /// </summary>
    public static string SecretPatternsFile => Path.Combine(Root, SecretPatternsFileName);

    /// <summary>Filename of <see cref="SecretPatternsFile"/> relative to a root.</summary>
    public const string SecretPatternsFileName = "secretpatterns.local.txt";

    /// <summary>
    /// <c>{Root}/deleted-rooms.jsonl</c> — the local record <c>baton room delete</c>/<c>baton rooms
    /// prune</c> leave behind so a deleted room's pushed deliverables can eventually be caught up on
    /// elsewhere. See <see cref="DeletedRoomsTombstoneStore"/> (#1659) for what writes it and why.
    /// </summary>
    public static string DeletedRoomsFile => Path.Combine(Root, DeletedRoomsFileName);

    /// <summary>Filename of <see cref="DeletedRoomsFile"/> relative to a root.</summary>
    public const string DeletedRoomsFileName = "deleted-rooms.jsonl";

    /// <summary>
    /// <c>{Root}/watches</c> — one JSON file per <c>baton watch</c> registration (#1488), named
    /// <c>&lt;watch-id&gt;.json</c>. <c>Baton.Cli</c>'s <c>WatchStore</c> (not referenced from here —
    /// this project has no <c>Baton.Cli</c> reference) owns what each file holds and how exactly-once
    /// firing is guaranteed. Operator-trust-level, per spec/baton.md §2's trust-model paragraph (M4,
    /// fix round) — not restated here.
    /// </summary>
    public static string Watches => Path.Combine(Root, WatchesDirectoryName);

    /// <summary>Directory name of <see cref="Watches"/> relative to a root.</summary>
    public const string WatchesDirectoryName = "watches";

    /// <summary>
    /// <c>{Root}/draining.json</c> — the tool-refresh drain marker. <see cref="DrainMarker"/> owns what
    /// it means and who refuses under it; this type only names where it lives, the same split
    /// <see cref="RoomRegistryFile"/> has with <see cref="RoomRegistryStore"/>.
    /// </summary>
    public static string DrainMarkerFile => Path.Combine(Root, DrainMarkerFileName);

    /// <summary>Filename of <see cref="DrainMarkerFile"/> relative to a root.</summary>
    public const string DrainMarkerFileName = "draining.json";

    /// <summary>
    /// <c>{Root}/tools</c> — directory holding side-by-side per-commit tool installations (#1668).
    /// </summary>
    public static string Tools => Path.Combine(Root, ToolsDirectoryName);

    /// <summary>Directory name of <see cref="Tools"/> relative to a root.</summary>
    public const string ToolsDirectoryName = "tools";

    /// <summary>
    /// <c>{Root}/tools/current</c> — atomic pointer file holding the currently active tool commit SHA (#1668).
    /// </summary>
    public static string CurrentToolPointerFile => Path.Combine(Tools, CurrentToolPointerFileName);

    /// <summary>Filename of <see cref="CurrentToolPointerFile"/> relative to <see cref="Tools"/>.</summary>
    public const string CurrentToolPointerFileName = "current";

    /// <summary>
    /// Attempts to resolve the active tool commit SHA (#1668). Checks <see cref="CurrentToolPointerFile"/>,
    /// degrading gracefully to null if missing or unreadable.
    /// </summary>
    public static string? TryResolveCurrentToolSha()
    {
        try
        {
            var pointerFile = CurrentToolPointerFile;
            if (File.Exists(pointerFile))
            {
                var sha = File.ReadAllText(pointerFile).Trim();
                if (!string.IsNullOrEmpty(sha))
                {
                    return sha;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Degrade gracefully
        }

        return null;
    }

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
