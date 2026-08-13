namespace Aer.Flow.Projection;

/// <summary>
/// Records a historical answer to a runtime permission request (0022).
/// Journal compaction makes older history unreachable; <see cref="RoomState"/> bounds
/// the answer history to the newest 50 answers (drop oldest).
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
