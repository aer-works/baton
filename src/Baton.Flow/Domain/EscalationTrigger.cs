using System.Text.Json.Serialization;

namespace Baton.Flow.Domain;

/// <summary>
/// Triggers for handing a decision to the inbox.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EscalationTrigger
{
    Spend,
    Irreversibility,
    Direction,
    Ambiguity,
    Confidence,
    GrantEdge
}
