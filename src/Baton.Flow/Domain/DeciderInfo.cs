using System.Text.Json.Serialization;

namespace Baton.Flow.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeciderKind
{
    Human,
    Worker
}

/// <summary>
/// Decider attribution recorded on external decisions. Default = Human with no IDs.
/// </summary>
public sealed record DeciderInfo(
    DeciderKind Kind = DeciderKind.Human,
    WorkerId? WorkerId = null,
    GrantId? GrantId = null)
{
    public static readonly DeciderInfo DefaultHuman = new(DeciderKind.Human);
}
