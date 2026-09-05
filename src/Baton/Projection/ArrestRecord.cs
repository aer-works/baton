using System.Text.Json.Serialization;
using Baton.Domain;

namespace Baton.Projection;

/// <summary>
/// One operator-visible lifecycle for a single <c>cancel.request</c>. The room journal records
/// the transition facts; <see cref="RoomProjector"/> folds them into this one entry for status,
/// Fleet Status, and session readers.
/// </summary>
public sealed record ArrestRecord(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("requestedBy")] string RequestedBy,
    [property: JsonPropertyName("requestedAt")] DateTimeOffset RequestedAt,
    [property: JsonPropertyName("state")] string State = ArrestLedgerStates.Requested,
    [property: JsonPropertyName("executionId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ExecutionId? ExecutionId = null,
    [property: JsonPropertyName("deliveredAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? DeliveredAt = null,
    [property: JsonPropertyName("rejectedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? RejectedAt = null,
    [property: JsonPropertyName("expiredAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? ExpiredAt = null,
    [property: JsonPropertyName("reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason = null);
