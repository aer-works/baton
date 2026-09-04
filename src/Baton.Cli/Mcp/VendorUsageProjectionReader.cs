using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Mcp;

/// <summary>
/// Issue #1391's fleet-wide <c>vendors[]</c> wire block — one entry per adapter that has ever been
/// harvested. Read by BOTH <see cref="FleetStatusTool"/> (the live MCP tool) and
/// <c>Baton.Cli.Daemon.FleetProjectionWriter</c> (the daemon-written projection file), each already
/// computing its own room list per call/tick; <see cref="ReadAll"/> takes that room list's derived
/// <paramref name="liveLanesByVendor"/> tally rather than re-deriving it, so this reader touches only
/// the harvested snapshot files (<see cref="BatonPaths.VendorUsageSnapshotFile"/>), never the rooms
/// directory itself.
/// </summary>
public static class VendorUsageProjectionReader
{
    /// <summary>Every adapter tag a snapshot file can exist for (issue #1391's scope: claude/agy —
    /// Codex is explicitly out of scope, see the issue's "Decisions already made").</summary>
    private static readonly string[] KnownVendors = ["claude", "agy"];

    private static readonly JsonSerializerOptions PersistedSnapshotOptions = new();

    /// <summary>
    /// Reads every vendor's persisted snapshot file that exists and parses cleanly, pairing each with
    /// <paramref name="liveLanesByVendor"/>'s own count (0 when the vendor is absent from that
    /// dictionary). Returns null — never an empty list — when no snapshot file exists yet or none
    /// parses, matching every other optional field's <c>JsonIgnoreCondition.WhenWritingNull</c>
    /// absence convention on this wire shape: a fleet that has never harvested emits no <c>vendors</c>
    /// key at all rather than an empty array.
    /// </summary>
    /// <param name="liveLanesByVendor">Adapter tag to count of that adapter's currently-Running rooms
    /// — the caller's own already-built room list, tallied once and passed in rather than re-scanned
    /// here.</param>
    public static IReadOnlyList<VendorUsageProjectionView>? ReadAll(IReadOnlyDictionary<string, int> liveLanesByVendor)
    {
        List<VendorUsageProjectionView> entries = [];

        foreach (var vendor in KnownVendors)
        {
            var path = BatonPaths.VendorUsageSnapshotFile(vendor);
            if (!File.Exists(path))
            {
                continue;
            }

            VendorUsageSnapshot? snapshot;
            try
            {
                var json = File.ReadAllText(path);
                snapshot = JsonSerializer.Deserialize<VendorUsageSnapshot>(json, PersistedSnapshotOptions);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // Fail open, matching every other per-room read in FleetStatusTool: one unreadable or
                // corrupt snapshot degrades that vendor's own entry, never the whole call.
                continue;
            }

            if (snapshot is null)
            {
                continue;
            }

            var liveLanes = liveLanesByVendor.GetValueOrDefault(vendor);
            var windows = snapshot.Windows
                .Select(w => new VendorUsageWindowView(w.Name, w.PercentUsed, w.ResetsAt, w.RawLine))
                .ToList();
            entries.Add(new VendorUsageProjectionView(vendor, snapshot.HarvestedAt, snapshot.Caveat, windows, liveLanes));
        }

        return entries.Count > 0 ? entries : null;
    }

    /// <summary>
    /// Tallies <paramref name="rooms"/> by <see cref="FleetRoomStatusView.Adapter"/> for every room
    /// currently displayed as <c>"Running"</c> — the same reading <c>fleet_status</c>'s own
    /// <c>role</c>/<c>adapter</c> fields already resolve (spec/baton.md §6), not a second derivation.
    /// </summary>
    public static Dictionary<string, int> CountLiveLanesByVendor(IEnumerable<FleetRoomStatusView> rooms)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var room in rooms)
        {
            if (room.State != "Running" || room.Adapter is not { } adapter)
            {
                continue;
            }

            counts[adapter] = counts.GetValueOrDefault(adapter) + 1;
        }

        return counts;
    }
}

/// <summary>One vendor's projected usage windows plus its current live-lane count (issue #1391).</summary>
public sealed record VendorUsageProjectionView(
    [property: JsonPropertyName("adapter")] string Adapter,
    [property: JsonPropertyName("harvestedAt")] DateTimeOffset HarvestedAt,
    [property: JsonPropertyName("caveat")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Caveat,
    [property: JsonPropertyName("windows")] IReadOnlyList<VendorUsageWindowView> Windows,
    [property: JsonPropertyName("liveLanes")] int LiveLanes);

/// <summary>One harvested usage window on the wire (issue #1391) — see
/// <see cref="Baton.Vendors.VendorUsageWindow"/>'s own doc comment for what each field means and when
/// it is absent.</summary>
public sealed record VendorUsageWindowView(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("percentUsed")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? PercentUsed,
    [property: JsonPropertyName("resetsAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? ResetsAt,
    [property: JsonPropertyName("rawLine")] string RawLine);

/// <summary>
/// <c>fleet_status</c>'s top-level response shape since issue #1391 — was a bare JSON array of
/// <see cref="FleetRoomStatusView"/> before this issue; <c>tools/fleet-glass/pusher.py</c>'s own
/// <c>drop_stale_rooms</c>/<c>derive_snapshot_and_timelines</c> already tolerated a <c>{"rooms": [...]}</c>
/// wrapper in anticipation of exactly this migration (their own comments name it). <see cref="Vendors"/>
/// is omitted, never an empty array, whenever <see cref="VendorUsageProjectionReader.ReadAll"/> finds
/// nothing to report.
/// </summary>
public sealed record FleetStatusResponse(
    [property: JsonPropertyName("rooms")] IReadOnlyList<FleetRoomStatusView> Rooms,
    [property: JsonPropertyName("vendors")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<VendorUsageProjectionView>? Vendors);
