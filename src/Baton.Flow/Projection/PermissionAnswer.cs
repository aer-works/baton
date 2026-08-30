namespace Baton.Flow.Projection;

/// <summary>
/// Records a historical answer to a runtime permission request (0022). The only bound on this
/// history is <see cref="RoomState"/>'s newest-50 cap (drop oldest) applied at projection time —
/// journal compaction does NOT reclaim the underlying permission events (#1144 measures the
/// unbounded room.jsonl growth), so the durable log keeps everything and the cap alone limits what
/// projections carry.
/// </summary>
public sealed record PermissionAnswer(
    string PermissionRequestId,
    string ToolName,
    string Category,
    string DecisionKind,
    string? Reason,
    string DeciderIdentity,
    DateTimeOffset AnsweredAt,
    bool WasRevoked);
